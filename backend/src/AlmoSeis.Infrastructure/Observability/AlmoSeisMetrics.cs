using System.Diagnostics.Metrics;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Infrastructure.Observability;

/// Métricas próprias da API (07 §7.4, 09 §9.8), no `Meter` `AlmoSeis`, que o `Telemetry` registra com `AddMeter`.
/// Sem exportador (OTLP/Application Insights desligados) não há ouvinte: `Add`/`Record` não fazem nada e o callback dos
/// gauges nunca roda (nenhuma consulta ao banco).
/// **Sem `tenant.id` nas tags**: cada tenant multiplicaria as séries (o plano gratuito do destino limita séries ativas).
/// O recorte por estabelecimento fica nos traces (`tenant.id` no span, `TenantContextMiddleware`) e nos logs. As tags
/// daqui são de conjunto fechado e pequeno (rota, método, origem, modalidade, laço do dispatcher).
public sealed class AlmoSeisMetrics
{
    public const string MeterName = "AlmoSeis";

    private readonly Counter<long> _idempotencyReplays;
    private readonly Counter<long> _ordersCreated;
    private readonly Counter<long> _syncCursorExpired;
    private readonly Histogram<double> _outboxLag;

    public AlmoSeisMetrics(IMeterFactory meterFactory, OutboxBacklogMonitor outbox)
    {
        Meter = meterFactory.Create(MeterName);
        _idempotencyReplays = Meter.CreateCounter<long>(
            "almoseis.idempotency.replays", "{request}", "Reenvios respondidos com a resposta guardada (ADR-0033)");
        _ordersCreated = Meter.CreateCounter<long>(
            "almoseis.orders.created", "{order}", "Pedidos criados (app) ou aceitos (página), depois do commit");
        _syncCursorExpired = Meter.CreateCounter<long>(
            "almoseis.sync.cursor_expired", "{request}", "GET /sync/changes com cursor além da retenção (410 SyncCursorExpired)");
        _outboxLag = Meter.CreateHistogram<double>(
            "almoseis.outbox.lag_ms", "ms", "Espera de uma mensagem do outbox entre ficar disponível e ser entregue");
        Meter.CreateObservableGauge(
            "almoseis.outbox.pending",
            () => Pending(outbox.Observe()),
            "{message}",
            "Mensagens do outbox disponíveis e ainda não entregues (todas as réplicas e tenants)");
        Meter.CreateObservableGauge(
            "almoseis.outbox.oldest_age_ms",
            () => OldestAge(outbox.Observe()),
            "ms",
            "Idade da mensagem pendente mais antiga do outbox (0 sem pendentes)");
    }

    public Meter Meter { get; }

    private static IEnumerable<Measurement<long>> Pending(OutboxBacklog? backlog) =>
        backlog is null ? [] : [new Measurement<long>(backlog.Pending)];

    private static IEnumerable<Measurement<double>> OldestAge(OutboxBacklog? backlog) =>
        backlog is null ? [] : [new Measurement<double>(backlog.OldestAge.TotalMilliseconds)];

    /// `route` = template da rota (`api/v1/orders/{id}/payments`), nunca o caminho com ids.
    public void IdempotencyReplayed(string method, string route) =>
        _idempotencyReplays.Add(1, new("http.request.method", method), new("http.route", route));

    /// `origin` = `APP` | `WEB`; `type` = `MARMITA` | `PF` | `KILO` | `A_LA_CARTE`.
    public void OrderCreated(string origin, string type) =>
        _ordersCreated.Add(1, new("order.origin", origin), new("order.type", type));

    public void SyncCursorExpired() => _syncCursorExpired.Add(1);

    /// `loop` = `realtime` | `consumer` (os dois laços do `OutboxDispatcher`).
    public void OutboxDelivered(string loop, TimeSpan lag) =>
        _outboxLag.Record(Math.Max(0, lag.TotalMilliseconds), new KeyValuePair<string, object?>("outbox.loop", loop));
}

/// Retrato do outbox: `Pending` = `processed_at IS NULL`, `attempts < Outbox:MaxAttempts` e já disponível
/// (`available_at` nulo ou vencido). Fora: agendadas (lembrete do push), reservadas por um consumidor (lease) e
/// desistidas (estas aparecem no log de erro, não aqui, senão deixariam o `/health/ready` degradado para sempre).
public sealed record OutboxBacklog(long Pending, DateTimeOffset? OldestDueAt, DateTimeOffset MeasuredAt)
{
    public TimeSpan OldestAge => OldestDueAt is { } due && due < MeasuredAt ? MeasuredAt - due : TimeSpan.Zero;
}

/// Uma contagem barata do outbox (índice parcial `ix_outbox_messages_pending`) pelo `PlatformDbContext` (papel
/// `almoseis_platform`, o mesmo do dispatcher: enxerga todos os tenants por política própria, sem `BYPASSRLS`), com cache
/// de `Outbox:BacklogCacheSeconds`. Lido pelo gauge e pelo `/health/ready`; consultas simultâneas viram uma só.
public sealed class OutboxBacklogMonitor(
    IServiceScopeFactory scopes,
    IOptions<OutboxOptions> options,
    TimeProvider time,
    ILogger<OutboxBacklogMonitor> logger)
{
    private static readonly TimeSpan ObserveTimeout = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile OutboxBacklog? _cached;

    public async Task<OutboxBacklog> GetAsync(CancellationToken cancellationToken)
    {
        if (Fresh() is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Fresh() is { } again)
            {
                return again;
            }

            var backlog = await QueryAsync(cancellationToken);
            _cached = backlog;
            return backlog;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// Para o callback síncrono do gauge (thread do leitor periódico do OpenTelemetry, sem contexto de sincronização).
    /// Falha ou demora → sem medição neste ciclo (e um aviso), sem derrubar a coleta das outras métricas.
    public OutboxBacklog? Observe()
    {
        try
        {
            using var timeout = new CancellationTokenSource(ObserveTimeout);
            return GetAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Não foi possível medir o outbox para as métricas.");
            return null;
        }
    }

    private OutboxBacklog? Fresh() =>
        _cached is { } cached && time.GetUtcNow() - cached.MeasuredAt < TimeSpan.FromSeconds(Math.Max(0, options.Value.BacklogCacheSeconds))
            ? cached
            : null;

    private async Task<OutboxBacklog> QueryAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var now = time.GetUtcNow();
        var max = options.Value.MaxAttempts;
        var row = (await db.Database.SqlQuery<BacklogRow>(
            $"""
            SELECT count(*) AS pending, min(COALESCE(available_at, occurred_at)) AS oldest_due_at
              FROM outbox_messages
             WHERE processed_at IS NULL AND attempts < {max}
               AND (available_at IS NULL OR available_at <= {now})
            """).ToListAsync(cancellationToken)).Single();
        return new OutboxBacklog(row.Pending, row.OldestDueAt, now);
    }

    private sealed record BacklogRow(long Pending, DateTimeOffset? OldestDueAt);
}
