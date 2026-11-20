using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Modules.Storefront.Domain;
using AlmoSeis.Modules.Storefront.Infrastructure.Maps;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Modules.Storefront.Jobs;

/// Retenção da taxa por distância (ADR-0052 item 5), de hora em hora, no papel de plataforma (todos os tenants):
/// cotações 24 h depois de vencer e entradas do cache por tenant depois dos 30 dias. Só apaga dado técnico; nada é
/// gravado no contexto de um tenant.
internal sealed class DeliveryQuoteRetentionJob(
    IServiceScopeFactory scopes,
    IOptions<MapsOptions> options,
    TimeProvider time,
    ILogger<DeliveryQuoteRetentionJob> logger) : BackgroundService
{
    public sealed record Result(int QuotesDeleted, int CacheEntriesDeleted);

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
                logger.LogError(ex, "Falha no job de retenção das cotações de entrega.");
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
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended('job:delivery-quote-retention', 0)) AS \"Value\"")
            .ToListAsync(cancellationToken)).Single();
        if (!locked)
        {
            return null;
        }

        var now = time.GetUtcNow();
        var quotesCutoff = now - DeliveryQuote.Retention;
        var quotes = await platform.Database.ExecuteSqlAsync($"DELETE FROM delivery_quotes WHERE expires_at <= {quotesCutoff}", cancellationToken);
        var cache = await platform.Database.ExecuteSqlAsync($"DELETE FROM geocode_cache_entries WHERE expires_at <= {now}", cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new Result(quotes, cache);
    }
}
