namespace AlmoSeis.Modules.Billing.Contracts;

public sealed record SubscriptionSummary(string Status, DateTimeOffset TrialEndsAt, int? DaysLeft, bool CanWrite);

/// O que outros módulos podem pedir ao Billing (07 §7.7).
public interface IBillingPort
{
    /// Grava os TrialClaims (hash de documento/telefone) e a assinatura TRIALING. TrialAlreadyClaimed se repetido.
    Task StartTrialAsync(Guid tenantId, IReadOnlyCollection<string> claimSources, CancellationToken cancellationToken);

    Task<SubscriptionSummary?> GetSummaryAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// Situação comercial para o bloqueio de escrita (ADR-0017 §6), com cache curto.
public interface ISubscriptionAccess
{
    Task<SubscriptionAccessState> GetAsync(Guid tenantId, CancellationToken cancellationToken);

    void Invalidate(Guid tenantId);
}

public sealed record SubscriptionAccessState(string Status, bool CanWrite);

/// GET /subscription (06 §6.4). **Sem preço, plano ou link de compra** (ADR-0017).
public sealed record SubscriptionDto(
    string Status,
    DateTimeOffset TrialEndsAt,
    DateTimeOffset? CurrentPeriodEndsAt,
    DateTimeOffset? PastDueSince,
    DateTimeOffset? GraceEndsAt,
    int? DaysLeft,
    bool CanWrite,
    string? Message);

public sealed record GetSubscriptionQuery;

public sealed record WebhookReceipt(bool Accepted);

/// POST /webhooks/asaas: valida o token e grava o evento; o processamento é assíncrono (06 §6.15).
public interface IAsaasWebhookReceiver
{
    /// `Accepted = false` → token inválido (401). Evento repetido é aceito e ignorado.
    Task<WebhookReceipt> ReceiveAsync(string? accessToken, string payload, CancellationToken cancellationToken);
}
