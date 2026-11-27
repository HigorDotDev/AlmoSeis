using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using AlmoSeis.Infrastructure.Observability;
using AlmoSeis.Infrastructure.Realtime;
using AlmoSeis.IntegrationTests.Ordering;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace AlmoSeis.IntegrationTests;

/// Métricas próprias (Meter `AlmoSeis`) e o atraso do outbox no `/health/ready` (07 §7.4, 09 §9.8), contra a API real.
/// O ouvinte só escuta o `Meter` deste host: as outras fábricas do mesmo processo não interferem.
[Collection(nameof(PostgresCollection))]
public sealed class ObservabilityFlowTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Action<IServiceCollection> Outbox(Action<OutboxOptions> configure) =>
        services => services.Configure<OutboxOptions>(o =>
        {
            o.BacklogCacheSeconds = 0;
            configure(o);
        });

    [Fact]
    public async Task Ready_is_degraded_when_the_oldest_pending_message_waits_too_long()
    {
        // Dispatcher parado: a mensagem antiga fica pendente.
        using var api = new ApiFactory(db, Outbox(o => o.Enabled = false));
        using var metrics = new MetricsRecorder(api.Services.GetRequiredService<AlmoSeisMetrics>().Meter);
        var tenantId = Guid.Parse((await api.SignUpAsync()).TenantId());
        var id = await InsertPendingAsync(tenantId, TimeSpan.FromMinutes(5));
        try
        {
            var response = await api.CreateClient().GetAsync("/health/ready", Ct);

            // Degraded não é Unhealthy: 200 (o Compose e o smoke do deploy seguem verdes), mas sem a palavra `Healthy`.
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("Degraded");

            metrics.Collect();
            metrics.Last("almoseis.outbox.pending").ShouldBeGreaterThanOrEqualTo(1);
            metrics.Last("almoseis.outbox.oldest_age_ms").ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(4).TotalMilliseconds);
        }
        finally
        {
            await db.ExecuteAsSuperuserAsync($"UPDATE outbox_messages SET processed_at = now() WHERE id = '{id}'");
        }
    }

    [Fact]
    public async Task Ready_is_degraded_when_too_many_messages_are_pending()
    {
        using var api = new ApiFactory(db, Outbox(o =>
        {
            o.Enabled = false;
            o.DegradedPendingThreshold = 2;
            o.DegradedOldestSeconds = 3600;
        }));
        var tenantId = Guid.Parse((await api.SignUpAsync()).TenantId());
        var ids = new[]
        {
            await InsertPendingAsync(tenantId, TimeSpan.Zero),
            await InsertPendingAsync(tenantId, TimeSpan.Zero),
            await InsertPendingAsync(tenantId, TimeSpan.Zero),
        };
        try
        {
            var response = await api.CreateClient().GetAsync("/health/ready", Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("Degraded");
        }
        finally
        {
            await db.ExecuteAsSuperuserAsync(
                $"UPDATE outbox_messages SET processed_at = now() WHERE id IN ({string.Join(',', ids.Select(i => $"'{i}'"))})");
        }
    }

    [Fact]
    public async Task Scheduled_and_abandoned_messages_do_not_count_as_pending()
    {
        using var api = new ApiFactory(db, Outbox(o => o.Enabled = false));
        var monitor = api.Services.GetRequiredService<OutboxBacklogMonitor>();
        var tenantId = Guid.Parse((await api.SignUpAsync()).TenantId());
        var before = await monitor.GetAsync(Ct);

        var scheduled = Guid.CreateVersion7();
        var abandoned = Guid.CreateVersion7();
        await db.ExecuteAsSuperuserAsync($$"""
            INSERT INTO outbox_messages (id, tenant_id, event_type, payload, occurred_at, available_at)
            VALUES ('{{scheduled}}', '{{tenantId}}', 'MetricsTest', '{}', now() - interval '10 minutes', now() + interval '1 hour');
            INSERT INTO outbox_messages (id, tenant_id, event_type, payload, occurred_at, attempts)
            VALUES ('{{abandoned}}', '{{tenantId}}', 'MetricsTest', '{}', now() - interval '10 minutes', 10);
            """);
        try
        {
            var after = await monitor.GetAsync(Ct);
            after.Pending.ShouldBe(before.Pending);
        }
        finally
        {
            await db.ExecuteAsSuperuserAsync($"UPDATE outbox_messages SET processed_at = now() WHERE id IN ('{scheduled}', '{abandoned}')");
        }
    }

    [Fact]
    public async Task Orders_replays_and_outbox_lag_are_measured_and_ready_is_healthy_once_drained()
    {
        using var api = new ApiFactory(db, Outbox(_ => { }));
        using var metrics = new MetricsRecorder(api.Services.GetRequiredService<AlmoSeisMetrics>().Meter);
        var s = await OrderingScenario.CreateAsync(api, db);

        var key = Guid.NewGuid();
        await (await s.Client.PostIdempotentAsync("/api/v1/orders", s.Pf(), key)).ExpectAsync(HttpStatusCode.Created, api);
        var replay = await s.Client.PostIdempotentAsync("/api/v1/orders", s.Pf(), key);
        await replay.ExpectAsync(HttpStatusCode.Created, api);
        replay.Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);

        // Um pedido só, contado depois do commit, sem tenant nas tags; o reenvio conta como replay e não como pedido.
        var created = metrics.All("almoseis.orders.created");
        created.Count.ShouldBe(1);
        created[0].Tags["order.origin"].ShouldBe("APP");
        created[0].Tags["order.type"].ShouldBe("PF");
        created[0].Tags.ShouldNotContainKey("tenant.id");
        var replays = metrics.All("almoseis.idempotency.replays");
        replays.Count.ShouldBe(1);
        replays[0].Tags["http.route"].ShouldBe("api/v1/orders");
        replays[0].Tags["http.request.method"].ShouldBe("POST");

        // O dispatcher deste host entrega o OrderCreated (e o que mais estiver pendente) e o ready volta a Healthy.
        var body = "";
        for (var i = 0; i < 60 && body != "Healthy"; i++)
        {
            await Task.Delay(250, Ct);
            body = await (await api.CreateClient().GetAsync("/health/ready", Ct)).Content.ReadAsStringAsync(Ct);
        }

        body.ShouldBe("Healthy");
        metrics.All("almoseis.outbox.lag_ms").ShouldContain(m => (string?)m.Tags["outbox.loop"] == "realtime" && m.Value >= 0);
    }

    [Fact]
    public async Task Accepted_web_order_counts_as_created_on_acceptance()
    {
        using var api = new ApiFactory(db);
        using var metrics = new MetricsRecorder(api.Services.GetRequiredService<AlmoSeisMetrics>().Meter);
        var s = await OrderingScenario.CreateAsync(api, db);
        var orderId = await s.SubmitWebOrderAsync();
        metrics.All("almoseis.orders.created").ShouldBeEmpty("o pedido da página nasce para a cozinha no aceite");

        var order = await s.GetOrderAsync(orderId);
        await (await s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{orderId}/accept", new { estimatedReadyMinutes = 30 }, order.Version()))
            .ExpectAsync(HttpStatusCode.OK, api);

        var created = metrics.All("almoseis.orders.created").ShouldHaveSingleItem();
        created.Tags["order.origin"].ShouldBe("WEB");
        created.Tags["order.type"].ShouldBe("MARMITA");
    }

    [Fact]
    public async Task Expired_sync_cursor_is_counted()
    {
        using var api = new ApiFactory(db);
        using var metrics = new MetricsRecorder(api.Services.GetRequiredService<AlmoSeisMetrics>().Meter);
        var s = await OrderingScenario.CreateAsync(api, db);
        await s.SqlAsync("DELETE FROM change_feed_entries WHERE tenant_id = @t AND seq <= 3");

        await (await s.Client.GetAsync("/api/v1/sync/changes?since=1", Ct)).ExpectAsync(HttpStatusCode.Gone, api);
        await (await s.Client.GetAsync("/api/v1/sync/changes?since=0", Ct)).ExpectAsync(HttpStatusCode.OK, api);

        metrics.All("almoseis.sync.cursor_expired").Count.ShouldBe(1);
    }

    private async Task<Guid> InsertPendingAsync(Guid tenantId, TimeSpan age)
    {
        var id = Guid.CreateVersion7();
        await db.ExecuteAsSuperuserAsync($$"""
            INSERT INTO outbox_messages (id, tenant_id, event_type, payload, occurred_at)
            VALUES ('{{id}}', '{{tenantId}}', 'MetricsTest', '{}', now() - interval '{{(int)age.TotalSeconds}} seconds')
            """);
        return id;
    }
}

/// Ouvinte de um `Meter` específico (o do host do teste), com as medições guardadas.
internal sealed class MetricsRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<RecordedMeasurement> _measurements = new();

    public MetricsRecorder(Meter meter)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, meter))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i, v, tags));
        _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i, v, tags));
        _listener.Start();
    }

    public List<RecordedMeasurement> All(string name) => [.. _measurements.Where(m => m.Name == name)];

    public double Last(string name) => All(name).Last().Value;

    /// Roda os callbacks dos gauges (o que o leitor periódico do OpenTelemetry faria).
    public void Collect() => _listener.RecordObservableInstruments();

    public void Dispose() => _listener.Dispose();

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>();
        foreach (var tag in tags)
        {
            copy[tag.Key] = tag.Value;
        }

        _measurements.Enqueue(new RecordedMeasurement(instrument.Name, value, copy));
    }
}

internal sealed record RecordedMeasurement(string Name, double Value, Dictionary<string, object?> Tags);
