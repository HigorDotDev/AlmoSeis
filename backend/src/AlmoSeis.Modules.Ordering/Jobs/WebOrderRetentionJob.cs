using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Ordering.Application;
using AlmoSeis.Modules.Ordering.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Modules.Ordering.Jobs;

/// Retenção da página de pedidos (ADR-0052 itens 3 e 5, ADR-0047 item 4), de hora em hora:
/// - pedidos da página que não viraram venda (`REJECTED`, `EXPIRED`, `WITHDRAWN`) há 7 dias têm nome, telefone, endereço e
///   observações anonimizados, cada um por um `ICommandHandler` no contexto do próprio tenant (auditoria `SYSTEM`, feed e
///   outbox), como o `WebOrderExpiryJob`;
/// - tokens de acompanhamento vencidos e hashes de IP com mais de 7 dias são apagados (papel de plataforma).
internal sealed class WebOrderRetentionJob(
    IServiceScopeFactory scopes,
    IOptions<WebOrderOptions> options,
    TimeProvider time,
    ILogger<WebOrderRetentionJob> logger) : BackgroundService
{
    public const int BatchSize = 500;

    public sealed record Result(int Anonymized, int TokensDeleted, int IntakesDeleted);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RetentionJobEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.RetentionIntervalMinutes), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falha no job de retenção dos pedidos do site.");
            }
        }
    }

    /// `null` quando outra réplica já está rodando. Público para os testes.
    public async Task<Result?> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await using var transaction = await platform.Database.BeginTransactionAsync(cancellationToken);

        var locked = (await platform.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended('job:web-order-retention', 0)) AS \"Value\"")
            .ToListAsync(cancellationToken)).Single();
        if (!locked)
        {
            return null;
        }

        var now = time.GetUtcNow();
        var cutoff = now - WebOrderIntake.Retention;
        var due = await platform.Database.SqlQuery<DueOrder>(
            $"""
            SELECT tenant_id, id
              FROM orders
             WHERE origin = 'WEB' AND acceptance_status IN ('REJECTED', 'EXPIRED', 'WITHDRAWN') AND anonymized_at IS NULL
               AND coalesce(rejected_at, expired_at, withdrawn_at) <= {cutoff}
             ORDER BY coalesce(rejected_at, expired_at, withdrawn_at)
             LIMIT {BatchSize}
            """).ToListAsync(cancellationToken);

        var anonymized = 0;
        foreach (var tenantOrders in due.GroupBy(d => d.TenantId))
        {
            await using var tenantScope = scopes.CreateAsyncScope();
            tenantScope.ServiceProvider.GetRequiredService<ITenantContext>().InitializeForSystem(tenantOrders.Key);
            var handler = tenantScope.ServiceProvider.GetRequiredService<ICommandHandler<AnonymizeWebOrderCommand, bool>>();
            foreach (var order in tenantOrders)
            {
                try
                {
                    if (await handler.HandleAsync(new AnonymizeWebOrderCommand(order.Id), cancellationToken))
                    {
                        anonymized++;
                    }
                }
                catch (BusinessException ex)
                {
                    logger.LogInformation("Pedido {OrderId} não anonimizado agora: {ErrorCode}.", order.Id, ex.ErrorCode.Code);
                }
            }
        }

        var tokens = await platform.Database.ExecuteSqlAsync(
            $"DELETE FROM order_tracking_tokens WHERE expires_at <= {now}", cancellationToken);
        var intakes = await platform.Database.ExecuteSqlAsync(
            $"DELETE FROM web_order_intakes WHERE created_at <= {cutoff}", cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new Result(anonymized, tokens, intakes);
    }

    private sealed record DueOrder(Guid TenantId, Guid Id);
}
