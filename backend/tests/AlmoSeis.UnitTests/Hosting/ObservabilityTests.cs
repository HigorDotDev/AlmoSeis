using System.Diagnostics.Metrics;
using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Infrastructure.Observability;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace AlmoSeis.UnitTests.Hosting;

/// Métricas próprias e atraso do outbox no `/health/ready` (07 §7.4, 09 §9.8).
public sealed class ObservabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 15, 0, 0, TimeSpan.Zero);

    private static readonly OutboxOptions Defaults = new();

    [Theory]
    [InlineData(0, 0, HealthStatus.Healthy)]
    [InlineData(100, 0, HealthStatus.Healthy)]      // no limite: ainda saudável
    [InlineData(101, 0, HealthStatus.Degraded)]     // pendente > 100
    [InlineData(5, 60, HealthStatus.Healthy)]       // 60 s exatos: ainda saudável
    [InlineData(5, 61, HealthStatus.Degraded)]      // mais antiga > 60 s
    [InlineData(500, 3600, HealthStatus.Degraded)]
    public void Outbox_backlog_degrades_ready_but_never_makes_it_unhealthy(long pending, int oldestSeconds, HealthStatus expected)
    {
        var backlog = new OutboxBacklog(pending, pending == 0 ? null : Now.AddSeconds(-oldestSeconds), Now);

        var result = OutboxHealthCheck.Evaluate(backlog, Defaults);

        result.Status.ShouldBe(expected);
        result.Data["pending"].ShouldBe(pending);
    }

    [Fact]
    public void Thresholds_come_from_outbox_options()
    {
        var options = new OutboxOptions { DegradedPendingThreshold = 2, DegradedOldestSeconds = 10 };

        OutboxHealthCheck.Evaluate(new OutboxBacklog(3, Now, Now), options).Status.ShouldBe(HealthStatus.Degraded);
        OutboxHealthCheck.Evaluate(new OutboxBacklog(1, Now.AddSeconds(-11), Now), options).Status.ShouldBe(HealthStatus.Degraded);
        OutboxHealthCheck.Evaluate(new OutboxBacklog(2, Now.AddSeconds(-10), Now), options).Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public void Oldest_age_is_zero_without_pending_or_with_clock_skew()
    {
        new OutboxBacklog(0, null, Now).OldestAge.ShouldBe(TimeSpan.Zero);
        new OutboxBacklog(1, Now.AddSeconds(2), Now).OldestAge.ShouldBe(TimeSpan.Zero);
        new OutboxBacklog(1, Now.AddSeconds(-90), Now).OldestAge.ShouldBe(TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void Counters_carry_only_low_cardinality_tags()
    {
        using var provider = MetricsProvider(Substitute.For<IServiceScopeFactory>());
        var metrics = provider.GetRequiredService<AlmoSeisMetrics>();
        using var recorder = new Recorder(metrics.Meter);

        metrics.OrderCreated("APP", "KILO");
        metrics.IdempotencyReplayed("POST", "api/v1/orders/{id}/payments");
        metrics.SyncCursorExpired();
        metrics.OutboxDelivered("consumer", TimeSpan.FromMilliseconds(1500));
        metrics.OutboxDelivered("realtime", TimeSpan.FromMilliseconds(-3)); // relógio de outra réplica adiantado

        recorder.Single("almoseis.orders.created").Tags.ShouldBe([new("order.origin", "APP"), new("order.type", "KILO")], ignoreOrder: true);
        recorder.Single("almoseis.idempotency.replays").Tags
            .ShouldBe([new("http.request.method", "POST"), new("http.route", "api/v1/orders/{id}/payments")], ignoreOrder: true);
        recorder.Single("almoseis.sync.cursor_expired").Tags.ShouldBeEmpty();
        var lags = recorder.All("almoseis.outbox.lag_ms");
        lags.Select(l => l.Value).ShouldBe([1500d, 0d]);
        lags[0].Tags.ShouldBe([new("outbox.loop", "consumer")]);
        recorder.Measurements.ShouldAllBe(m => m.Tags.All(t => t.Key != "tenant.id"));
    }

    [Fact]
    public void Outbox_gauge_queries_nothing_without_a_listener_and_skips_a_failed_measurement()
    {
        var scopes = Substitute.For<IServiceScopeFactory>();
        scopes.CreateScope().Returns(_ => throw new InvalidOperationException("banco fora"));
        using var provider = MetricsProvider(scopes);
        var metrics = provider.GetRequiredService<AlmoSeisMetrics>();

        // OTLP desligado = ninguém ouvindo: os contadores não fazem nada e o gauge nunca consulta o banco.
        metrics.OrderCreated("APP", "PF");
        scopes.ReceivedCalls().ShouldBeEmpty();

        using var recorder = new Recorder(metrics.Meter);
        recorder.Collect();

        scopes.ReceivedCalls().ShouldNotBeEmpty();
        recorder.All("almoseis.outbox.pending").ShouldBeEmpty();
        recorder.All("almoseis.outbox.oldest_age_ms").ShouldBeEmpty();
    }

    [Fact]
    public void Commit_callbacks_run_only_after_commit()
    {
        var callbacks = new CommitCallbacks(NullLogger<CommitCallbacks>.Instance);
        var runs = 0;

        callbacks.Add(() => runs++);
        callbacks.TransactionRolledBack(null!, null!);
        callbacks.TransactionCommitted(null!, null!);
        runs.ShouldBe(0, "rollback descarta");

        callbacks.Add(() => runs++);
        callbacks.TransactionFailed(null!, null!);
        callbacks.TransactionCommitted(null!, null!);
        runs.ShouldBe(0, "falha da transação descarta");

        callbacks.Add(() => throw new InvalidOperationException("falha no contador"));
        callbacks.Add(() => runs++);
        callbacks.TransactionCommitted(null!, null!);
        runs.ShouldBe(1, "uma ação com erro não impede as outras");

        callbacks.TransactionCommitted(null!, null!);
        runs.ShouldBe(1, "cada ação roda uma vez");
    }

    private static ServiceProvider MetricsProvider(IServiceScopeFactory scopes)
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new OutboxOptions()));
        services.AddSingleton(sp => new OutboxBacklogMonitor(
            scopes, sp.GetRequiredService<IOptions<OutboxOptions>>(), TimeProvider.System, NullLogger<OutboxBacklogMonitor>.Instance));
        services.AddSingleton<AlmoSeisMetrics>();
        return services.BuildServiceProvider();
    }

    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();

        public Recorder(Meter meter)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Measurements.Add(new(i.Name, v, [.. tags.ToArray()])));
            _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Measurements.Add(new(i.Name, v, [.. tags.ToArray()])));
            _listener.Start();
        }

        public List<Measured> Measurements { get; } = [];

        public List<Measured> All(string name) => [.. Measurements.Where(m => m.Name == name)];

        public Measured Single(string name) => All(name).ShouldHaveSingleItem();

        public void Collect() => _listener.RecordObservableInstruments();

        public void Dispose() => _listener.Dispose();
    }

    private sealed record Measured(string Name, double Value, KeyValuePair<string, object?>[] Tags);
}
