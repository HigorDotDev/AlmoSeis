using AlmoSeis.SharedKernel.Domain;

namespace AlmoSeis.Modules.Billing.Domain;

/// Máquina de estados do ADR-0017. O estado vem só dos webhooks do gateway e dos prazos (job), nunca do app.
/// Transição inválida lança InvalidOperationException; o processador de webhook a registra e segue.
internal sealed class Subscription : AggregateRoot, ITenantOwned
{
    public static readonly TimeSpan TrialLength = TimeSpan.FromDays(7);
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromDays(5);

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

    public DateTimeOffset? GraceEndsAt(TimeSpan grace) => PastDueSince is { } since ? since + grace : null;

    /// Pagamento confirmado (webhook): ACTIVE até `periodEnd`. Vale a partir de teste, atraso ou suspensão.
    public bool Activate(DateTimeOffset periodEnd, DateTimeOffset now)
    {
        EnsureNot(SubscriptionStatus.Canceled, "ativar");
        var changed = Status != SubscriptionStatus.Active || CurrentPeriodEndsAt != periodEnd;
        (Status, CurrentPeriodEndsAt, PastDueSince, SuspendedAt) = (SubscriptionStatus.Active, periodEnd, null, null);
        return Changed(changed, now);
    }

    /// Fatura vencida (webhook): começa a carência. Repetido não muda a data de início.
    public bool MarkPastDue(DateTimeOffset now)
    {
        if (Status is SubscriptionStatus.PastDue or SubscriptionStatus.Suspended)
        {
            return false;
        }

        EnsureNot(SubscriptionStatus.Canceled, "marcar em atraso");
        (Status, PastDueSince) = (SubscriptionStatus.PastDue, now);
        return Changed(true, now);
    }

    /// Fim do teste sem contratação ou fim da carência (job).
    public bool Suspend(DateTimeOffset now)
    {
        if (Status == SubscriptionStatus.Suspended)
        {
            return false;
        }

        EnsureNot(SubscriptionStatus.Canceled, "suspender");
        (Status, SuspendedAt) = (SubscriptionStatus.Suspended, now);
        return Changed(true, now);
    }

    /// Cancelamento no gateway (webhook) ou suspensão longa (job). Final.
    public bool Cancel(DateTimeOffset now)
    {
        if (Status == SubscriptionStatus.Canceled)
        {
            return false;
        }

        (Status, CanceledAt) = (SubscriptionStatus.Canceled, now);
        return Changed(true, now);
    }

    /// Administração (06 §6.15): em teste, soma dias; suspensa sem nenhum período pago, volta ao teste por `days`.
    public void ExtendTrial(int days, DateTimeOffset now)
    {
        if (Status == SubscriptionStatus.Trialing)
        {
            TrialEndsAt = (TrialEndsAt > now ? TrialEndsAt : now) + TimeSpan.FromDays(days);
            Touch();
            return;
        }

        if (Status == SubscriptionStatus.Suspended && CurrentPeriodEndsAt is null)
        {
            (Status, TrialEndsAt, SuspendedAt) = (SubscriptionStatus.Trialing, now + TimeSpan.FromDays(days), null);
            Changed(true, now);
            return;
        }

        throw new InvalidOperationException($"Não é possível estender o teste de uma assinatura {Status}.");
    }

    public void LinkGateway(string customerId, string subscriptionId)
    {
        GatewayCustomerId = customerId;
        GatewaySubscriptionId = subscriptionId;
        Touch();
    }

    private void EnsureNot(string status, string action)
    {
        if (Status == status)
        {
            throw new InvalidOperationException($"Não é possível {action} uma assinatura {Status}.");
        }
    }

    private bool Changed(bool changed, DateTimeOffset now)
    {
        if (changed)
        {
            Touch(new SubscriptionStatusChanged(Id, TenantId, Status, CanWrite, now));
        }

        return changed;
    }

    /// Dias restantes do teste ou da carência; nulo quando não se aplica.
    public int? DaysLeft(DateTimeOffset now) => Status switch
    {
        SubscriptionStatus.Trialing => Math.Max(0, (int)Math.Ceiling((TrialEndsAt - now).TotalDays)),
        SubscriptionStatus.PastDue when PastDueSince is { } since => Math.Max(0, (int)Math.Ceiling((since + DefaultGracePeriod - now).TotalDays)),
        _ => null,
    };
}

internal sealed record SubscriptionStatusChanged(Guid SubscriptionId, Guid TenantId, string Status, bool CanWrite, DateTimeOffset OccurredAt) : IDomainEvent;

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
