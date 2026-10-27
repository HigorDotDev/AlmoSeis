using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Closing;

/// Expediente (06 §6.6, ADR-0019/0038/0039) e idempotência (ADR-0033) contra a API real.
[Collection(nameof(PostgresCollection))]
public sealed class BusinessDayFlowTests(PostgresFixture db) : IDisposable
{
    private const string Url = "/api/v1/business-days";
    private static readonly object OpenBody = new { openingBalance = "100.00", confirmSameDate = false };

    private readonly ApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// Data local do autocadastro de teste (America/Sao_Paulo).
    private static DateOnly Today =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")).DateTime);

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Open_requires_an_idempotency_key()
    {
        var client = await NewStoreAsync();

        var problem = await (await client.PostAsJsonAsync(Url, OpenBody, Ct)).ExpectAsync(HttpStatusCode.PreconditionRequired, _api);

        problem.GetProperty("errorCode").GetString().ShouldBe("IdempotencyKeyRequired");
        await (await client.GetAsync($"{Url}/current", Ct)).ExpectAsync(HttpStatusCode.NotFound, _api);
    }

    [Fact]
    public async Task Open_creates_the_business_day_for_the_local_date()
    {
        var client = await NewStoreAsync();

        var response = await client.PostIdempotentAsync(Url, OpenBody, Guid.NewGuid());
        var result = await response.ExpectAsync(HttpStatusCode.Created, _api);

        var day = result.GetProperty("businessDay");
        day.GetProperty("status").GetString().ShouldBe("OPEN");
        day.GetProperty("version").GetInt32().ShouldBe(1);
        day.GetProperty("businessDate").GetString().ShouldBe(Today.ToString("yyyy-MM-dd"));
        day.GetProperty("openingBalance").GetString().ShouldBe("100.00");
        day.GetProperty("openedBy").GetProperty("displayName").GetString().ShouldNotBeNullOrEmpty();
        day.GetProperty("closedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        day.GetProperty("dailyClosingId").ValueKind.ShouldBe(JsonValueKind.Null);
        result.GetProperty("materializedMenus").GetArrayLength().ShouldBe(0);
        response.Headers.Location!.ToString().ShouldBe("/api/v1/business-days/current");

        var currentResponse = await client.GetAsync($"{Url}/current", Ct);
        var current = await currentResponse.ExpectAsync(HttpStatusCode.OK, _api);
        current.GetProperty("id").GetString().ShouldBe(day.GetProperty("id").GetString());
        currentResponse.Headers.ETag!.Tag.ShouldBe("\"1\"");
    }

    [Fact]
    public async Task Repeating_the_key_with_the_same_body_replays_the_original_response()
    {
        var client = await NewStoreAsync();
        var key = Guid.NewGuid();

        var first = await (await client.PostIdempotentAsync(Url, OpenBody, key)).ExpectAsync(HttpStatusCode.Created, _api);
        // Mesmo corpo com outra formatação e ordem de campos: mesmo hash.
        var replayResponse = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, Url)
            {
                Headers = { { "Idempotency-Key", key.ToString() } },
                Content = new StringContent("""{ "confirmSameDate": false,  "openingBalance": "100.00" }""", System.Text.Encoding.UTF8, "application/json"),
            },
            Ct);
        var replay = await replayResponse.ExpectAsync(HttpStatusCode.Created, _api);

        replayResponse.Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);
        replay.GetProperty("businessDay").GetProperty("id").GetString().ShouldBe(first.GetProperty("businessDay").GetProperty("id").GetString());
        replay.GetProperty("businessDay").GetProperty("openingBalance").GetString().ShouldBe("100.00");
        (await ListAsync(client)).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Repeating_the_key_with_another_body_is_rejected()
    {
        var client = await NewStoreAsync();
        var key = Guid.NewGuid();
        await (await client.PostIdempotentAsync(Url, OpenBody, key)).ExpectAsync(HttpStatusCode.Created, _api);

        var problem = await (await client.PostIdempotentAsync(Url, new { openingBalance = "50.00", confirmSameDate = false }, key))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);

        problem.GetProperty("errorCode").GetString().ShouldBe("IdempotencyKeyReused");
    }

    [Fact]
    public async Task Failed_operation_keeps_no_record_so_the_key_can_be_retried()
    {
        var client = await NewStoreAsync();
        var key = Guid.NewGuid();

        await (await client.PostIdempotentAsync(Url, new { openingBalance = "-1.00", confirmSameDate = false }, key))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);
        (await CountAsync("SELECT count(*) FROM idempotency_records WHERE key = @k", ("k", key))).ShouldBe(0);

        // Corpo corrigido com a mesma chave: executa normalmente, já que nada foi guardado.
        await (await client.PostIdempotentAsync(Url, OpenBody, key)).ExpectAsync(HttpStatusCode.Created, _api);
        (await CountAsync("SELECT count(*) FROM idempotency_records WHERE key = @k AND status = 'COMPLETED' AND resource_id IS NOT NULL", ("k", key)))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_key_open_a_single_business_day()
    {
        var client = await NewStoreAsync();
        var key = Guid.NewGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.PostIdempotentAsync(Url, OpenBody, key)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Created, string.Join(" | ", _api.ServerErrors));
        responses.Count(r => r.Headers.Contains("Idempotent-Replayed")).ShouldBe(2);
        (await ListAsync(client)).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Only_one_business_day_can_be_open()
    {
        var client = await NewStoreAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => client.PostIdempotentAsync(Url, OpenBody, Guid.NewGuid())));

        responses.Select(r => r.StatusCode).Order().ShouldBe([HttpStatusCode.Created, HttpStatusCode.Conflict]);
        var opened = await responses.Single(r => r.StatusCode == HttpStatusCode.Created).ExpectAsync(HttpStatusCode.Created, _api);
        var problem = await responses.Single(r => r.StatusCode == HttpStatusCode.Conflict).ExpectAsync(HttpStatusCode.Conflict, _api);
        problem.GetProperty("errorCode").GetString().ShouldBe("BusinessDayAlreadyOpen");
        problem.GetProperty("current").GetProperty("id").GetString().ShouldBe(opened.GetProperty("businessDay").GetProperty("id").GetString());
        problem.GetProperty("current").GetProperty("openingBalance").GetString().ShouldBe("100.00");
    }

    [Fact]
    public async Task Reopening_on_the_same_date_requires_confirmation()
    {
        var auth = await _api.SignUpAsync();
        var client = _api.Authenticated(auth.AccessToken());
        await (await client.PostIdempotentAsync(Url, OpenBody, Guid.NewGuid())).ExpectAsync(HttpStatusCode.Created, _api);
        await CloseOpenDayAsync(auth.TenantId());

        var problem = await (await client.PostIdempotentAsync(Url, OpenBody, Guid.NewGuid())).ExpectAsync(HttpStatusCode.Conflict, _api);
        problem.GetProperty("errorCode").GetString().ShouldBe("BusinessDayClosedToday");

        var second = await (await client.PostIdempotentAsync(Url, new { openingBalance = "0.00", confirmSameDate = true }, Guid.NewGuid()))
            .ExpectAsync(HttpStatusCode.Created, _api);
        second.GetProperty("businessDay").GetProperty("businessDate").GetString().ShouldBe(Today.ToString("yyyy-MM-dd"));

        var days = await ListAsync(client);
        days.GetArrayLength().ShouldBe(2);
        days[0].GetProperty("status").GetString().ShouldBe("OPEN"); // mais recente primeiro
        days[1].GetProperty("status").GetString().ShouldBe("CLOSED");
    }

    [Fact]
    public async Task List_pages_with_an_opaque_cursor()
    {
        var auth = await _api.SignUpAsync();
        var client = _api.Authenticated(auth.AccessToken());
        for (var i = 0; i < 3; i++)
        {
            await (await client.PostIdempotentAsync(Url, new { openingBalance = $"{i}.00", confirmSameDate = true }, Guid.NewGuid()))
                .ExpectAsync(HttpStatusCode.Created, _api);
            await CloseOpenDayAsync(auth.TenantId());
        }

        var first = await (await client.GetAsync($"{Url}?limit=2", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        first.GetProperty("items").GetArrayLength().ShouldBe(2);
        first.GetProperty("items")[0].GetProperty("openingBalance").GetString().ShouldBe("2.00");
        var cursor = first.GetProperty("nextCursor").GetString()!;

        var second = await (await client.GetAsync($"{Url}?limit=2&cursor={cursor}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        second.GetProperty("items").GetArrayLength().ShouldBe(1);
        second.GetProperty("items")[0].GetProperty("openingBalance").GetString().ShouldBe("0.00");
        second.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);

        (await (await client.GetAsync($"{Url}?limit=101", Ct)).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api))
            .GetProperty("errors").GetProperty("limit").GetArrayLength().ShouldBe(1);
        await (await client.GetAsync($"{Url}?cursor=%%%", Ct)).ExpectAsync(HttpStatusCode.BadRequest, _api);
    }

    [Fact]
    public async Task Weekly_template_becomes_the_daily_menu_when_the_day_opens()
    {
        var client = await NewStoreAsync();
        var catalog = await SeedCatalogAsync(client);
        var dayOfWeek = Today.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)Today.DayOfWeek;
        var template = await CreateMenuAsync(client, "MARMITA", "WEEKLY_TEMPLATE", null, dayOfWeek, catalog.Frango, catalog.Arroz);
        var pfYesterday = await CreateMenuAsync(client, "PF", "DAILY", Today.AddDays(-1).ToString("yyyy-MM-dd"), null, catalog.Frango);

        // Sem expediente, não há "cardápio atual".
        (await (await client.GetAsync("/api/v1/menus/current?menuType=MARMITA", Ct)).ExpectAsync(HttpStatusCode.Conflict, _api))
            .GetProperty("errorCode").GetString().ShouldBe("BusinessDayRequired");

        var opened = await (await client.PostIdempotentAsync(Url, OpenBody, Guid.NewGuid())).ExpectAsync(HttpStatusCode.Created, _api);
        var materialized = opened.GetProperty("materializedMenus");
        materialized.GetArrayLength().ShouldBe(1);
        materialized[0].GetProperty("menuType").GetString().ShouldBe("MARMITA");
        materialized[0].GetProperty("sourceMenuId").GetString().ShouldBe(template);

        var menu = await (await client.GetAsync("/api/v1/menus/current?menuType=MARMITA", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        menu.GetProperty("id").GetString().ShouldBe(materialized[0].GetProperty("id").GetString());
        menu.GetProperty("kind").GetString().ShouldBe("DAILY");
        menu.GetProperty("date").GetString().ShouldBe(Today.ToString("yyyy-MM-dd"));
        menu.GetProperty("createdBy").ValueKind.ShouldBe(JsonValueKind.Null);
        menu.GetProperty("items").GetArrayLength().ShouldBe(2);
        (await CountAsync("SELECT count(*) FROM audit_logs WHERE operation_type = 'MenuMaterialized' AND origin = 'SYSTEM' AND entity_id = @m",
            ("m", Guid.Parse(menu.GetProperty("id").GetString()!)))).ShouldBe(1);

        var problem = await (await client.GetAsync("/api/v1/menus/current?menuType=PF", Ct)).ExpectAsync(HttpStatusCode.NotFound, _api);
        problem.GetProperty("errorCode").GetString().ShouldBe("MenuNotFound");
        problem.GetProperty("detail").GetString().ShouldBe("Ainda não há cardápio de PF para hoje.");
        problem.GetProperty("menuType").GetString().ShouldBe("PF");
        problem.GetProperty("businessDate").GetString().ShouldBe(Today.ToString("yyyy-MM-dd"));
        var sources = problem.GetProperty("sources");
        sources.GetProperty("yesterdayMenuId").GetString().ShouldBe(pfYesterday);
        sources.GetProperty("lastWeekSameDayMenuId").ValueKind.ShouldBe(JsonValueKind.Null);
        sources.GetProperty("weeklyTemplateId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Daily_menu_created_in_advance_wins_over_the_template()
    {
        var client = await NewStoreAsync();
        var catalog = await SeedCatalogAsync(client);
        var dayOfWeek = Today.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)Today.DayOfWeek;
        await CreateMenuAsync(client, "MARMITA", "WEEKLY_TEMPLATE", null, dayOfWeek, catalog.Frango, catalog.Arroz);
        var daily = await CreateMenuAsync(client, "MARMITA", "DAILY", Today.ToString("yyyy-MM-dd"), null, catalog.Arroz);

        var opened = await (await client.PostIdempotentAsync(Url, OpenBody, Guid.NewGuid())).ExpectAsync(HttpStatusCode.Created, _api);

        opened.GetProperty("materializedMenus").GetArrayLength().ShouldBe(0);
        (await (await client.GetAsync("/api/v1/menus/current?menuType=MARMITA", Ct)).ExpectAsync(HttpStatusCode.OK, _api))
            .GetProperty("id").GetString().ShouldBe(daily);
    }

    [Fact]
    public async Task Attendant_sees_the_open_day_but_cannot_open_one()
    {
        var auth = await _api.SignUpAsync();
        var attendant = _api.Authenticated(await _api.EmployeeTokenAsync(db, auth, "ATTENDANT"));

        (await (await attendant.PostIdempotentAsync(Url, OpenBody, Guid.NewGuid())).ExpectAsync(HttpStatusCode.Forbidden, _api))
            .GetProperty("errorCode").GetString().ShouldBe("PermissionDenied");

        var cashier = _api.Authenticated(await _api.EmployeeTokenAsync(db, auth, "CASHIER"));
        await (await cashier.PostIdempotentAsync(Url, OpenBody, Guid.NewGuid())).ExpectAsync(HttpStatusCode.Created, _api);
        await (await attendant.GetAsync($"{Url}/current", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await attendant.GetAsync(Url, Ct)).ExpectAsync(HttpStatusCode.Forbidden, _api);
    }

    [Fact]
    public async Task Opening_writes_feed_and_outbox_in_the_same_transaction()
    {
        var auth = await _api.SignUpAsync();
        var client = _api.Authenticated(auth.AccessToken());

        var opened = await (await client.PostIdempotentAsync(Url, OpenBody, Guid.NewGuid())).ExpectAsync(HttpStatusCode.Created, _api);
        var id = Guid.Parse(opened.GetProperty("businessDay").GetProperty("id").GetString()!);

        (await CountAsync("SELECT count(*) FROM change_feed_entries WHERE entity_type = 'BUSINESS_DAY' AND entity_id = @id", ("id", id))).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM outbox_messages WHERE event_type = 'BusinessDayOpened' AND payload->>'entityId' = @id::text", ("id", id)))
            .ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM audit_logs WHERE operation_type = 'BusinessDayOpened' AND entity_id = @id", ("id", id))).ShouldBe(1);
    }

    private async Task<HttpClient> NewStoreAsync() => _api.Authenticated((await _api.SignUpAsync()).AccessToken());

    private async Task<JsonElement> ListAsync(HttpClient client) =>
        (await (await client.GetAsync(Url, Ct)).ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("items");

    /// O fechamento ainda não existe na API: simula a finalização direto no banco.
    private async Task CloseOpenDayAsync(string tenantId)
    {
        await using var connection = new NpgsqlConnection(db.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            UPDATE business_days SET status = 'CLOSED', closed_at = now(), closed_by_employee_id = opened_by_employee_id, version = version + 1
             WHERE tenant_id = @t AND status = 'OPEN'
            """, connection);
        command.Parameters.AddWithValue("t", Guid.Parse(tenantId));
        (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
    }

    private async Task<long> CountAsync(string sql, (string Name, object Value) parameter)
    {
        await using var connection = new NpgsqlConnection(db.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private sealed record Catalog(Guid Frango, Guid Arroz);

    private async Task<Catalog> SeedCatalogAsync(HttpClient client)
    {
        async Task<Guid> Create(string kind, string name, string? price)
        {
            var product = await (await client.PostAsJsonAsync("/api/v1/products",
                new { kind, name, description = (string?)null, category = (string?)null, defaultPrice = price, requiresPreparation = (bool?)null }, Ct))
                .ExpectAsync(HttpStatusCode.Created, _api);
            return Guid.Parse(product.GetProperty("id").GetString()!);
        }

        return new Catalog(await Create("MAIN_DISH", "Frango grelhado", "22.00"), await Create("SIDE_DISH", "Arroz", null));
    }

    private async Task<string> CreateMenuAsync(HttpClient client, string menuType, string kind, string? date, int? dayOfWeek, params Guid[] products)
    {
        var menu = await (await client.PostAsJsonAsync("/api/v1/menus", new
        {
            menuType,
            kind,
            date,
            dayOfWeek,
            items = products.Select((p, i) => new { productId = p, pfPrice = (string?)null, isAvailable = true, sortOrder = i }).ToArray(),
        }, Ct)).ExpectAsync(HttpStatusCode.Created, _api);
        return menu.GetProperty("id").GetString()!;
    }
}
