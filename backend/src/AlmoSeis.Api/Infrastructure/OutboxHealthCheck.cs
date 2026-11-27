using AlmoSeis.Infrastructure.Observability;
using AlmoSeis.Infrastructure.Realtime;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Api.Infrastructure;

/// Atraso do outbox no `/health/ready` (07 §7.4, 09 §9.8): sem Azure Monitor na VM (ADR-0060), é o que o monitor externo
/// (palavra-chave `Healthy`) enxerga. Atraso é `Degraded` (HTTP 200, corpo `Degraded`), nunca `Unhealthy`: a API segue
/// atendendo, o healthcheck do Compose e o smoke do deploy (`curl -f`) não derrubam nada, e só o monitor por palavra-chave
/// avisa. Falha ou tempo-limite da consulta também é `Degraded` (`failureStatus` do registro); o banco fora do ar já
/// deixa o `ready` `Unhealthy` pelo `DatabaseHealthCheck`. A contagem vem do `OutboxBacklogMonitor` (cache de poucos s).
public sealed class OutboxHealthCheck(OutboxBacklogMonitor monitor, IOptions<OutboxOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Evaluate(await monitor.GetAsync(cancellationToken), options.Value);

    /// Sem tenant nem conteúdo na descrição: só números (o corpo padrão do health check nem a mostra).
    public static HealthCheckResult Evaluate(OutboxBacklog backlog, OutboxOptions options)
    {
        var data = new Dictionary<string, object>
        {
            ["pending"] = backlog.Pending,
            ["oldestAgeSeconds"] = (long)backlog.OldestAge.TotalSeconds,
        };
        if (backlog.Pending > options.DegradedPendingThreshold)
        {
            return HealthCheckResult.Degraded($"Outbox com {backlog.Pending} mensagens pendentes.", data: data);
        }

        return backlog.OldestAge > TimeSpan.FromSeconds(options.DegradedOldestSeconds)
            ? HealthCheckResult.Degraded($"Mensagem do outbox esperando há {(long)backlog.OldestAge.TotalSeconds} s.", data: data)
            : HealthCheckResult.Healthy(data: data);
    }
}
