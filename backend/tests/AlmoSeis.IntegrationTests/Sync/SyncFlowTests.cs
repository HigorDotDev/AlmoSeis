using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlmoSeis.IntegrationTests.Ordering;
using Npgsql;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Sync;

/// Feed de sincronização (ADR-0032) e consulta de operação (ADR-0033 §5) contra a API real.
[Collection(nameof(PostgresCollection))]
public sealed class SyncFlowTests(PostgresFixture db) : IDisposable
{
    private readonly ApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Feed_is_compacted_ordered_and_resumable()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var order = await s.CreateOrderAsync(s.Pf());
        await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{order.Id()}", new { notes = "a" }, 1)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{order.Id()}", new { notes = "b" }, 2)).ExpectAsync(HttpStatusCode.OK, _api);

        var all = await Changes(s.Client, 0, 500);
        var changes = all.GetProperty("changes").EnumerateArray().ToList();
        all.GetProperty("hasMore").GetBoolean().ShouldBeFalse();
        changes.Select(c => c.GetProperty("seq").GetInt64()).ShouldBe(changes.Select(c => c.GetProperty("seq").GetInt64()).Order());
        var orderChanges = changes.Where(c => c.GetProperty("entityId").GetString() == order.Id().ToString()).ToList();
        orderChanges.Count.ShouldBe(1);
        orderChanges[0].GetProperty("entityVersion").GetInt32().ShouldBe(3);
        changes.Select(c => c.GetProperty("entityType").GetString()).ShouldContain("PRODUCT");
        changes.Select(c => c.GetProperty("entityType").GetString()).ShouldContain("MENU");
        changes.Select(c => c.GetProperty("entityType").GetString()).ShouldContain("BUSINESS_DAY");

        var lastSeq = all.GetProperty("lastSeq").GetInt64();
        var nothing = await Changes(s.Client, lastSeq, 500);
        nothing.GetProperty("changes").GetArrayLength().ShouldBe(0);
        nothing.GetProperty("lastSeq").GetInt64().ShouldBe(lastSeq);

        // Paginação: limit pequeno, hasMore e continuação a partir do lastSeq da página.
        var page = await Changes(s.Client, 0, 2);
        page.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
        var rest = await Changes(s.Client, page.GetProperty("lastSeq").GetInt64(), 500);
        (page.GetProperty("changes").GetArrayLength() + rest.GetProperty("changes").GetArrayLength()).ShouldBe(changes.Count);
    }

    [Fact]
    public async Task Feed_only_lists_what_the_caller_can_read()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        await (await s.Client.PostIdempotentAsync("/api/v1/closing/cash-movements", new { movementType = "SUPPLY", amount = "10.00", description = "Troco" }, Guid.NewGuid()))
            .ExpectAsync(HttpStatusCode.Created, _api);
        var kitchen = _api.Authenticated(await _api.EmployeeTokenAsync(db, s.Owner, "KITCHEN"));

        var ownerTypes = (await Changes(s.Client, 0, 500)).GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("entityType").GetString()).ToList();
        var kitchenTypes = (await Changes(kitchen, 0, 500)).GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("entityType").GetString()).ToList();

        ownerTypes.ShouldContain("CASH_MOVEMENT");
        kitchenTypes.ShouldNotContain("CASH_MOVEMENT");
        kitchenTypes.ShouldContain("MENU");
        // Funcionários alheios só para quem gerencia funcionários; o próprio sempre (o vínculo foi criado por SQL, sem feed).
        kitchenTypes.Count(t => t == "EMPLOYEE").ShouldBe(0);
    }

    [Fact]
    public async Task Cursor_older_than_retention_is_gone()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var current = (await Changes(s.Client, 0, 500)).GetProperty("lastSeq").GetInt64();
        await s.SqlAsync("DELETE FROM change_feed_entries WHERE tenant_id = @t AND seq <= 3");

        var problem = await (await s.Client.GetAsync("/api/v1/sync/changes?since=1", Ct)).ExpectAsync(HttpStatusCode.Gone, _api);

        problem.GetProperty("errorCode").GetString().ShouldBe("SyncCursorExpired");
        problem.GetProperty("currentSeq").GetInt64().ShouldBe(current);
        await (await s.Client.GetAsync("/api/v1/sync/changes?since=3", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await s.Client.GetAsync("/api/v1/sync/changes?since=0", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
    }

    [Fact]
    public async Task Operation_status_resolves_unknown_outcomes()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var key = Guid.NewGuid();
        var order = await (await s.Client.PostIdempotentAsync("/api/v1/orders", s.Pf(), key)).ExpectAsync(HttpStatusCode.Created, _api);

        var completed = await Operation(s.Client, key);
        completed.GetProperty("status").GetString().ShouldBe("COMPLETED");
        completed.GetProperty("endpoint").GetString().ShouldBe("POST /api/v1/orders");
        completed.GetProperty("responseStatus").GetInt32().ShouldBe(201);
        completed.GetProperty("resourceId").GetString().ShouldBe(order.Id().ToString());

        (await Operation(s.Client, Guid.NewGuid())).GetProperty("status").GetString().ShouldBe("NOT_FOUND");
        var colleague = _api.Authenticated(await _api.EmployeeTokenAsync(db, s.Owner, "CASHIER"));
        (await Operation(colleague, key)).GetProperty("status").GetString().ShouldBe("NOT_FOUND"); // só as próprias chaves

        // Uma transação em curso segura a chave: o estado é IN_PROGRESS.
        var pending = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(db.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO idempotency_records (id, tenant_id, key, user_id, endpoint, request_hash, status, created_at, expires_at)
            SELECT gen_random_uuid(), tenant_id, @k, user_id, 'POST /api/v1/orders', repeat('a', 64), 'IN_PROGRESS', now(), now() + interval '1 hour'
              FROM tenant_employees WHERE tenant_id = @t LIMIT 1
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("k", pending);
            insert.Parameters.AddWithValue("t", s.TenantId);
            await insert.ExecuteNonQueryAsync(Ct);
        }

        (await Operation(s.Client, pending)).GetProperty("status").GetString().ShouldBe("IN_PROGRESS");
        await transaction.RollbackAsync(Ct);
        (await Operation(s.Client, pending)).GetProperty("status").GetString().ShouldBe("NOT_FOUND");
    }

    private async Task<JsonElement> Changes(HttpClient client, long since, int limit) =>
        await (await client.GetAsync($"/api/v1/sync/changes?since={since}&limit={limit}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);

    private async Task<JsonElement> Operation(HttpClient client, Guid key) =>
        await (await client.GetAsync($"/api/v1/operations/{key}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
}
