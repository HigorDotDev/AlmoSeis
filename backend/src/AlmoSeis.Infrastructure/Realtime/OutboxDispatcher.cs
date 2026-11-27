using AlmoSeis.Infrastructure.Observability;
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

    /// Tentativas antes de desistir. Nulo = `Outbox:MaxAttempts` (a mensagem fica parada com `last_error`). Com valor, ao
    /// desistir a mensagem é marcada processada com o `last_error` da última falha e um log de erro (ex.: o push, que não
    /// reenvia indefinidamente, ADR-0049 item 8).
    int? MaxAttempts => null;

    /// Espera antes da próxima tentativa, gravada em `available_at`. Zero = no próximo ciclo.
    TimeSpan RetryDelay => TimeSpan.Zero;

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

    /// Reserva de uma mensagem de consumidor interno enquanto ele roda fora da transação (`available_at = agora + isto`).
    /// Precisa passar do pior caso do consumidor (o push: lotes de 100 com até 5 s cada). Se a réplica cair no meio, outra
    /// pega a mensagem depois disso, e o consumidor deduplica (`push_deliveries`; apagar a imagem antiga de novo é inócuo).
    public int ConsumerLeaseSeconds { get; set; } = 120;

    /// `/health/ready` fica `Degraded` (HTTP 200, corpo `Degraded`) com mais pendentes que isto (07 §7.4, 09 §9.8).
    public int DegradedPendingThreshold { get; set; } = 100;

    /// ... ou com a pendente mais antiga esperando há mais que isto.
    public int DegradedOldestSeconds { get; set; } = 60;

    /// Validade da contagem do `OutboxBacklogMonitor` (gauge e health check leem a mesma).
    public int BacklogCacheSeconds { get; set; } = 5;
}

/// 07 §7.10: dois laços independentes sobre o outbox, cada um com `FOR UPDATE SKIP LOCKED` (seguro com várias réplicas).
/// - **Tempo real** (eventos sem consumidor interno): publica no SignalR e marca processado numa transação curta.
/// - **Consumidores internos** (`IOutboxConsumer`: o push pela Expo, a imagem antiga no R2): reserva o lote numa
///   transação curta (`attempts + 1`, `available_at = agora + lease`), confirma, chama o consumidor **fora** de qualquer
///   transação e grava o resultado de cada mensagem numa instrução própria. Uma chamada HTTP lenta não segura locks nem
///   atrasa os eventos de tempo real, que têm o próprio laço.
/// Entrega pelo menos uma vez; o app deduplica por `eventId`/`seq` (ADR-0031) e cada consumidor deduplica o seu efeito.
/// Mensagem com `available_at` no futuro (agendada, reservada ou nova tentativa com espera) fica para depois.
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopes,
    IRealtimePublisher publisher,
    IEnumerable<IOutboxConsumer> consumers,
    IOptions<OutboxOptions> options,
    TimeProvider time,
    AlmoSeisMetrics metrics,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private readonly Dictionary<string, IOutboxConsumer> _consumers = consumers.ToDictionary(c => c.EventType);

    private string[] ConsumerTypes => [.. _consumers.Keys];

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return Task.CompletedTask;
        }

        return Task.WhenAll(
            LoopAsync(DispatchRealtimeBatchAsync, "tempo real", stoppingToken),
            LoopAsync(DispatchConsumerBatchAsync, "consumidores", stoppingToken));
    }

    private async Task LoopAsync(Func<CancellationToken, Task<int>> dispatch, string name, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.Value.PollMilliseconds), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                while (await dispatch(stoppingToken) == options.Value.BatchSize)
                {
                    // Lote cheio: há mais esperando, sem aguardar o próximo tick.
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falha no dispatcher do outbox ({Loop}).", name);
            }
        }
    }

    /// Os dois laços, um depois do outro. Público para os testes dispararem sem esperar o timer.
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken) =>
        await DispatchRealtimeBatchAsync(cancellationToken) + await DispatchConsumerBatchAsync(cancellationToken);

    /// Eventos sem consumidor interno: SignalR e `processed_at` numa transação curta (a publicação é em memória).
    public async Task<int> DispatchRealtimeBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var o = options.Value;
        var now = time.GetUtcNow();
        var types = ConsumerTypes;
        var batch = await db.Set<OutboxMessage>()
            .FromSql($"""
                SELECT * FROM outbox_messages
                 WHERE processed_at IS NULL AND attempts < {o.MaxAttempts}
                   AND (available_at IS NULL OR available_at <= {now})
                   AND event_type <> ALL ({types})
                 ORDER BY occurred_at
                 LIMIT {o.BatchSize}
                   FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        foreach (var message in batch)
        {
            try
            {
                await publisher.PublishAsync(message.TenantId, message.EventType, message.Payload, cancellationToken);
                message.ProcessedAt = time.GetUtcNow();
                metrics.OutboxDelivered("realtime", message.ProcessedAt.Value - (message.AvailableAt ?? message.OccurredAt));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.Attempts++;
                message.LastError = Truncate(ex.Message);
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

    /// Mensagens dos consumidores internos: reserva curta, consumidor fora da transação, resultado por mensagem.
    public async Task<int> DispatchConsumerBatchAsync(CancellationToken cancellationToken)
    {
        if (_consumers.Count == 0)
        {
            return 0;
        }

        var reserved = await ReserveAsync(cancellationToken);
        foreach (var message in reserved)
        {
            var consumer = _consumers[message.EventType];
            Exception? failure = null;
            try
            {
                using var envelope = System.Text.Json.JsonDocument.Parse(message.Payload);
                await consumer.HandleAsync(message.TenantId, envelope.RootElement, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = ex;
            }

            // O que já aconteceu (o push saiu) é gravado mesmo com a parada pedida: sem o token de cancelamento.
            await CompleteAsync(message, consumer, failure, CancellationToken.None);
        }

        return reserved.Count;
    }

    private async Task<List<ReservedMessage>> ReserveAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var o = options.Value;
        var now = time.GetUtcNow();
        var types = ConsumerTypes;
        var batch = await db.Set<OutboxMessage>()
            .FromSql($"""
                SELECT * FROM outbox_messages
                 WHERE processed_at IS NULL AND attempts < {o.MaxAttempts}
                   AND (available_at IS NULL OR available_at <= {now})
                   AND event_type = ANY ({types})
                 ORDER BY occurred_at
                 LIMIT {o.BatchSize}
                   FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        var lease = now + TimeSpan.FromSeconds(o.ConsumerLeaseSeconds);
        // Desde quando cada uma estava disponível (antes de o lease sobrescrever `available_at`), para o `lag_ms`.
        var dueAt = batch.ToDictionary(m => m.Id, m => m.AvailableAt ?? m.OccurredAt);
        foreach (var message in batch)
        {
            // A tentativa conta na reserva: se a réplica cair no meio, a mensagem não volta para sempre.
            message.Attempts++;
            message.AvailableAt = lease;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return batch.Select(m => new ReservedMessage(m.Id, m.TenantId, m.EventType, m.Payload, m.Attempts, dueAt[m.Id])).ToList();
    }

    /// Uma instrução por mensagem (transação implícita e curta), só se ninguém a concluiu nesse meio-tempo.
    private async Task CompleteAsync(ReservedMessage message, IOutboxConsumer consumer, Exception? failure, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var now = time.GetUtcNow();

        if (failure is null)
        {
            var completed = await db.Database.ExecuteSqlAsync(
                $"UPDATE outbox_messages SET processed_at = {now}, available_at = NULL WHERE id = {message.Id} AND processed_at IS NULL",
                cancellationToken);
            if (completed > 0)
            {
                metrics.OutboxDelivered("consumer", now - message.DueAt);
            }

            return;
        }

        var error = Truncate(failure.Message);
        DateTimeOffset? processedAt = null;
        DateTimeOffset? availableAt = now + consumer.RetryDelay;
        if (consumer.MaxAttempts is { } consumerMax && message.Attempts >= consumerMax)
        {
            // Desistência do próprio consumidor: sai da fila (a retenção apaga em 7 dias) com o erro guardado.
            processedAt = now;
            availableAt = null;
            logger.LogError(failure, "Mensagem {MessageId} ({EventType}) do outbox desistida após {Attempts} tentativas.", message.Id, message.EventType, message.Attempts);
        }
        else if (message.Attempts >= options.Value.MaxAttempts)
        {
            logger.LogError(failure, "Mensagem {MessageId} do outbox desistida após {Attempts} tentativas.", message.Id, message.Attempts);
        }

        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE outbox_messages SET last_error = {error}, processed_at = {processedAt}, available_at = {availableAt}
             WHERE id = {message.Id} AND processed_at IS NULL
            """,
            cancellationToken);
    }

    private static string Truncate(string message) => message.Length > 500 ? message[..500] : message;

    private sealed record ReservedMessage(Guid Id, Guid TenantId, string EventType, string Payload, int Attempts, DateTimeOffset DueAt);
}

// 07 §7.11: limpeza diária do feed (7 dias), da idempotência expirada e do outbox processado.
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
