using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Ordering.Application;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Modules.Ordering.Jobs;

/// ADR-0046 item 14: a cada 30 s, os pedidos do site com o prazo de aceite vencido passam a `EXPIRED`.
/// O `PlatformDbContext` só acha os candidatos entre os tenants e segura o advisory lock (uma réplica por vez). Cada
/// pedido é expirado por um `ICommandHandler` no contexto do próprio tenant (`InitializeForSystem`), com o ciclo de
/// escrita normal: RLS, auditoria `SYSTEM`, feed e outbox na mesma transação (07 §7.3), um pedido por transação.
internal sealed class WebOrderExpiryJob(
    IServiceScopeFactory scopes,
    IOptions<WebOrderOptions> options,
    TimeProvider time,
    ILogger<WebOrderExpiryJob> logger) : BackgroundService
{
    public const int BatchSize = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ExpiryJobEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.ExpiryIntervalSeconds), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falha no job de expiração dos pedidos do site.");
            }
        }
    }

    /// Quantos pedidos expirou; `null` quando outra réplica já está rodando. Público para os testes.
    public async Task<int?> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await using var transaction = await platform.Database.BeginTransactionAsync(cancellationToken);

        var locked = (await platform.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended('job:web-order-expiry', 0)) AS \"Value\"")
            .ToListAsync(cancellationToken)).Single();
        if (!locked)
        {
            return null;
        }

        var now = time.GetUtcNow();
        var due = await platform.Database.SqlQuery<DueOrder>(
            $"""
            SELECT tenant_id, id
              FROM orders
             WHERE acceptance_status = 'AWAITING_ACCEPTANCE' AND acceptance_deadline <= {now}
             ORDER BY acceptance_deadline
             LIMIT {BatchSize}
            """).ToListAsync(cancellationToken);

        var expired = 0;
        foreach (var tenantOrders in due.GroupBy(d => d.TenantId))
        {
            await using var tenantScope = scopes.CreateAsyncScope();
            tenantScope.ServiceProvider.GetRequiredService<ITenantContext>().InitializeForSystem(tenantOrders.Key);
            var handler = tenantScope.ServiceProvider.GetRequiredService<ICommandHandler<ExpireWebOrderCommand, bool>>();

            foreach (var order in tenantOrders)
            {
                try
                {
                    if (await handler.HandleAsync(new ExpireWebOrderCommand(order.Id), cancellationToken))
                    {
                        expired++;
                    }
                }
                catch (BusinessException ex)
                {
                    // VersionConflict: o aceite (ou a recusa) gravou primeiro e venceu a disputa (item 16).
                    logger.LogInformation("Pedido {OrderId} não expirado: {ErrorCode}.", order.Id, ex.ErrorCode.Code);
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return expired;
    }

    private sealed record DueOrder(Guid TenantId, Guid Id);
}
