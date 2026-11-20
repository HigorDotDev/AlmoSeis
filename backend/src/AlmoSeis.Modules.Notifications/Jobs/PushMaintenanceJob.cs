using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Notifications.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Modules.Notifications.Jobs;

/// ADR-0049 itens 4 e 8, a cada `Push:MaintenanceIntervalMinutes` (advisory lock: uma réplica por vez):
/// 1. recibos da Expo dos pushes enviados há `ReceiptDelayMinutes`: o `PlatformDbContext` só acha os tickets entre os
///    tenants; a conferência (e o `DeviceNotRegistered` que apaga o registro) é no contexto de cada tenant;
/// 2. no papel de plataforma, só dado técnico vencido: registros sem uso há `RegistrationRetentionDays` e entregas com
///    mais de `DeliveryRetentionDays`.
internal sealed class PushMaintenanceJob(
    IServiceScopeFactory scopes,
    IOptions<PushOptions> options,
    TimeProvider time,
    ILogger<PushMaintenanceJob> logger) : BackgroundService
{
    public const int ReceiptBatch = 1000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.MaintenanceJobEnabled || options.Value.Provider == PushProvider.None)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.MaintenanceIntervalMinutes), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falha na manutenção do push.");
            }
        }
    }

    /// Quantos recibos conferiu; `null` quando outra réplica já está rodando. Público para os testes.
    public async Task<int?> RunOnceAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        await using var scope = scopes.CreateAsyncScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await using var transaction = await platform.Database.BeginTransactionAsync(cancellationToken);

        var locked = (await platform.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended('job:push-maintenance', 0)) AS \"Value\"")
            .ToListAsync(cancellationToken)).Single();
        if (!locked)
        {
            return null;
        }

        var now = time.GetUtcNow();
        var receiptsDue = now.AddMinutes(-o.ReceiptDelayMinutes);
        var due = await platform.Database.SqlQuery<DueTicket>(
            $"""
            SELECT tenant_id, ticket_id
              FROM push_deliveries
             WHERE ticket_id IS NOT NULL AND receipt_checked_at IS NULL AND sent_at <= {receiptsDue}
             ORDER BY sent_at
             LIMIT {ReceiptBatch}
            """).ToListAsync(cancellationToken);

        var checkedCount = 0;
        foreach (var tenantTickets in due.GroupBy(d => d.TenantId))
        {
            await using var tenantScope = scopes.CreateAsyncScope();
            tenantScope.ServiceProvider.GetRequiredService<ITenantContext>().InitializeForSystem(tenantTickets.Key);
            try
            {
                checkedCount += await tenantScope.ServiceProvider.GetRequiredService<PushReceiptChecker>()
                    .CheckAsync(tenantTickets.Select(t => t.TicketId).ToList(), cancellationToken);
            }
            catch (PushUnavailableException ex)
            {
                // Fica para a próxima rodada; o recibo vale 24 h na Expo.
                logger.LogWarning("Recibos de push não conferidos agora: {Reason}", ex.Message);
                break;
            }
        }

        var registrationCutoff = now.AddDays(-o.RegistrationRetentionDays);
        var deliveryCutoff = now.AddDays(-o.DeliveryRetentionDays);
        await platform.Database.ExecuteSqlAsync($"DELETE FROM push_registrations WHERE updated_at < {registrationCutoff}", cancellationToken);
        await platform.Database.ExecuteSqlAsync($"DELETE FROM push_deliveries WHERE sent_at < {deliveryCutoff}", cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return checkedCount;
    }

    private sealed record DueTicket(Guid TenantId, string TicketId);
}
