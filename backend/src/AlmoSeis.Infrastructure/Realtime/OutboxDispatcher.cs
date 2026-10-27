using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Infrastructure.Realtime;

/// Entrega um envelope do outbox aos clientes conectados (06 §6.14). Implementado pela Api (SignalR).
public interface IRealtimePublisher
{
    Task PublishAsync(Guid tenantId, string eventType, string payload, CancellationToken cancellationToken);
}

/// Evento do outbox tratado dentro do backend em vez de ir para o SignalR (ex.: apagar a imagem antiga no R2).
/// Mesma garantia do outbox: roda depois do commit, pelo menos uma vez, com novas tentativas em falha.
public interface IOutboxConsumer
{
    string EventType { get; }

    Task HandleAsync(Guid tenantId, System.Text.Json.JsonElement envelope, CancellationToken cancellationToken);
}

public sealed class OutboxOptions
{
    public const string Section = "Outbox";

    public bool Enabled { get; set; } = true;

    public int PollMilliseconds { get; set; } = 250;

    public int BatchSize { get; set; } = 100;

    /// Depois disso a mensagem fica parada com `last_error` (log de erro), sem novas tentativas.
    public int MaxAttempts { get; set; } = 10;
}

/// 07 §7.10: lê o outbox com `FOR UPDATE SKIP LOCKED` (seguro com várias réplicas), publica e marca processado.
/// Entrega pelo menos uma vez; o app deduplica por `eventId`/`seq` (ADR-0031).
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopes,
    IRealtimePublisher publisher,
    IEnumerable<IOutboxConsumer> consumers,
    IOptions<OutboxOptions> options,
    TimeProvider time,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private readonly Dictionary<string, IOutboxConsumer> _consumers = consumers.ToDictionary(c => c.EventType);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.Value.PollMilliseconds), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                while (await DispatchBatchAsync(stoppingToken) == options.Value.BatchSize)
                {
                    // Lote cheio: há mais esperando, sem aguardar o próximo tick.
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falha no dispatcher do outbox.");
            }
        }
    }

    /// Um lote numa transação. Público para os testes dispararem sem esperar o timer.
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var o = options.Value;
        var batch = await db.Set<OutboxMessage>()
            .FromSql($"""
                SELECT * FROM outbox_messages
                 WHERE processed_at IS NULL AND attempts < {o.MaxAttempts}
                 ORDER BY occurred_at
                 LIMIT {o.BatchSize}
                   FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        foreach (var message in batch)
        {
            try
            {
                if (_consumers.TryGetValue(message.EventType, out var consumer))
                {
                    using var envelope = System.Text.Json.JsonDocument.Parse(message.Payload);
                    await consumer.HandleAsync(message.TenantId, envelope.RootElement, cancellationToken);
                }
                else
                {
                    await publisher.PublishAsync(message.TenantId, message.EventType, message.Payload, cancellationToken);
                }

                message.ProcessedAt = time.GetUtcNow();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.Attempts++;
                message.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                if (message.Attempts >= o.MaxAttempts)
                {
                    logger.LogError(ex, "Mensagem {MessageId} do outbox desistida após {Attempts} tentativas.", message.Id, message.Attempts);
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return batch.Count;
    }
}

/// 07 §7.11: limpeza diária do feed (7 dias), da idempotência expirada e do outbox processado.
/// Advisory lock: uma execução por vez, mesmo com várias réplicas.
public sealed class SyncRetentionJob(IServiceScopeFactory scopes, TimeProvider time, ILogger<SyncRetentionJob> logger) : BackgroundService
{
    public static readonly TimeSpan FeedRetention = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24), time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falha na limpeza de retenção.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// Devolve false quando outra réplica já está limpando.
    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var locked = (await db.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended('job:sync-retention', 0)) AS \"Value\"")
            .ToListAsync(cancellationToken)).Single();
        if (!locked)
        {
            return false;
        }

        var now = time.GetUtcNow();
        var cutoff = now - FeedRetention;
        await db.Database.ExecuteSqlAsync($"DELETE FROM change_feed_entries WHERE occurred_at < {cutoff}", cancellationToken);
        await db.Database.ExecuteSqlAsync($"DELETE FROM idempotency_records WHERE expires_at < {now}", cancellationToken);
        await db.Database.ExecuteSqlAsync($"DELETE FROM outbox_messages WHERE processed_at < {cutoff}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
