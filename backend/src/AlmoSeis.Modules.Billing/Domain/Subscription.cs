using AlmoSeis.SharedKernel.Domain;

namespace AlmoSeis.Modules.Billing.Domain;

/// Máquina de estados do ADR-0017. Nesta etapa: início do teste grátis e leitura de situação.
internal sealed class Subscription : AggregateRoot, ITenantOwned
{
    public static readonly TimeSpan TrialLength = TimeSpan.FromDays(7);
    public static readonly TimeSpan GracePeriod = TimeSpan.FromDays(5);

    private Subscription()
    {
    }

    public Guid TenantId { get; private set; }
    public string Status { get; private set; } = SubscriptionStatus.Trialing;
    public string PlanCode { get; private set; } = null!;
    public Money MonthlyPrice { get; private set; }
    public DateTimeOffset TrialEndsAt { get; private set; }
    public DateTimeOffset? CurrentPeriodEndsAt { get; private set; }
    public DateTimeOffset? PastDueSince { get; private set; }
    public DateTimeOffset? SuspendedAt { get; private set; }
    public DateTimeOffset? CanceledAt { get; private set; }
    public string? GatewayCustomerId { get; private set; }
    public string? GatewaySubscriptionId { get; private set; }

    public bool CanWrite => Status is not (SubscriptionStatus.Suspended or SubscriptionStatus.Canceled);

    public static Subscription StartTrial(Guid id, Guid tenantId, string planCode, Money monthlyPrice, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        PlanCode = planCode,
        MonthlyPrice = monthlyPrice,
        TrialEndsAt = now + TrialLength,
    };

    /// Dias restantes do teste ou da carência; nulo quando não se aplica.
    public int? DaysLeft(DateTimeOffset now) => Status switch
    {
        SubscriptionStatus.Trialing => Math.Max(0, (int)Math.Ceiling((TrialEndsAt - now).TotalDays)),
        SubscriptionStatus.PastDue when PastDueSince is { } since => Math.Max(0, (int)Math.Ceiling((since + GracePeriod - now).TotalDays)),
        _ => null,
    };
}

internal static class SubscriptionStatus
{
    public const string Trialing = "TRIALING";
    public const string Active = "ACTIVE";
    public const string PastDue = "PAST_DUE";
    public const string Suspended = "SUSPENDED";
    public const string Canceled = "CANCELED";
}

/// Um teste grátis por documento ou telefone (hash SHA-256, ADR-0017/0024).
internal sealed class TrialClaim
{
    private TrialClaim()
    {
    }

    public string ClaimKey { get; private set; } = null!;
    public Guid TenantId { get; private set; }
    public DateTimeOffset ClaimedAt { get; private set; }

    public static TrialClaim Create(string claimKey, Guid tenantId, DateTimeOffset now) =>
        new() { ClaimKey = claimKey, TenantId = tenantId, ClaimedAt = now };
}
