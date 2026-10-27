using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AlmoSeis.Infrastructure.Realtime;
using AlmoSeis.IntegrationTests.Ordering;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Sync;

/// Outbox → dispatcher → SignalR (ADR-0009, ADR-0031, 06 §6.14) e limpeza de retenção, contra a API real.
[Collection(nameof(PostgresCollection))]
public sealed class RealtimeFlowTests(PostgresFixture db) : IDisposable
{
    private readonly ApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Events_reach_the_groups_allowed_by_permissions()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var kitchenToken = await _api.EmployeeTokenAsync(db, s.Owner, "KITCHEN");
        await using var owner = await ConnectAsync(s.Owner.AccessToken());
        await using var kitchen = await ConnectAsync(kitchenToken);

        var order = await s.CreateOrderAsync(s.Pf());
        await (await s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/payments", new { amount = "10.00", paymentMethod = "PIX" }, 1))
            .ExpectAsync(HttpStatusCode.Created, _api);

        var created = await kitchen.WaitForAsync("OrderCreated");
        created.GetProperty("entityId").GetString().ShouldBe(order.Id().ToString());
        created.GetProperty("entityVersion").GetInt32().ShouldBe(1);
        created.GetProperty("seq").GetInt64().ShouldBeGreaterThan(0);
        created.GetProperty("data").GetProperty("label").GetString().ShouldBe("Mesa 5");

        var registered = await owner.WaitForAsync("OrderPaymentRegistered");
        registered.GetProperty("data").GetProperty("balance").GetString().ShouldBe("38.00");
        (await owner.WaitForAsync("ClosingUpdated")).GetProperty("data").ValueKind.ShouldBe(JsonValueKind.Null);

        // A cozinha não está no grupo do fechamento.
        await Task.Delay(500, Ct);
        kitchen.Received.ShouldNotContain(e => e.GetProperty("eventType").GetString() == "ClosingUpdated");
        (await s.ScalarAsync<long>("SELECT count(*) FROM outbox_messages WHERE tenant_id = @t AND processed_at IS NULL")).ShouldBe(0);
    }

    [Fact]
    public async Task Closing_updates_are_coalesced_per_tenant()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        await using var owner = await ConnectAsync(s.Owner.AccessToken());

        for (var i = 0; i < 3; i++)
        {
            await (await s.Client.PostIdempotentAsync("/api/v1/closing/cash-movements", new { movementType = "SUPPLY", amount = "1.00", description = "Moedas" }, Guid.NewGuid()))
                .ExpectAsync(HttpStatusCode.Created, _api);
        }

        await owner.WaitForAsync("ClosingUpdated");
        await Task.Delay(TimeSpan.FromSeconds(6), Ct); // passa a janela de 5 s

        // Três movimentações: a primeira sai na hora, as outras viram um único aviso no fim da janela.
        owner.Received.Count(e => e.GetProperty("eventType").GetString() == "ClosingUpdated").ShouldBe(2);
    }

    [Fact]
    public async Task Retention_removes_old_feed_and_processed_outbox()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        await s.SqlAsync("UPDATE change_feed_entries SET occurred_at = now() - interval '8 days' WHERE tenant_id = @t AND entity_type = 'PRODUCT'");
        await s.SqlAsync("UPDATE outbox_messages SET processed_at = now() - interval '8 days' WHERE tenant_id = @t AND event_type = 'CatalogUpdated'");
        var products = await s.ScalarAsync<long>("SELECT count(*) FROM change_feed_entries WHERE tenant_id = @t AND entity_type = 'PRODUCT'");
        products.ShouldBeGreaterThan(0);

        (await _api.Services.GetRequiredService<SyncRetentionJob>().RunOnceAsync(Ct)).ShouldBeTrue();

        (await s.ScalarAsync<long>("SELECT count(*) FROM change_feed_entries WHERE tenant_id = @t AND entity_type = 'PRODUCT'")).ShouldBe(0);
        (await s.ScalarAsync<long>("SELECT count(*) FROM outbox_messages WHERE tenant_id = @t AND event_type = 'CatalogUpdated'")).ShouldBe(0);
        (await s.ScalarAsync<long>("SELECT count(*) FROM change_feed_entries WHERE tenant_id = @t AND entity_type = 'MENU'")).ShouldBeGreaterThan(0);
    }

    private async Task<Listener> ConnectAsync(string accessToken)
    {
        var server = _api.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/hubs/realtime"), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .Build();
        var listener = new Listener(connection);
        await connection.StartAsync(Ct);
        return listener;
    }

    private sealed class Listener : IAsyncDisposable
    {
        private readonly HubConnection _connection;

        public Listener(HubConnection connection)
        {
            _connection = connection;
            connection.On<JsonElement>("event", e => Received.Enqueue(e));
        }

        public ConcurrentQueue<JsonElement> Received { get; } = new();

        public async Task<JsonElement> WaitForAsync(string eventType)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var found = Received.FirstOrDefault(e => e.GetProperty("eventType").GetString() == eventType);
                if (found.ValueKind != JsonValueKind.Undefined)
                {
                    return found;
                }

                await Task.Delay(50, Ct);
            }

            throw new TimeoutException($"{eventType} não chegou. Recebidos: {string.Join(", ", Received.Select(e => e.GetProperty("eventType").GetString()))}");
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}
