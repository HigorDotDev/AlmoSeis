using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Security;

namespace AlmoSeis.Modules.Identity.Domain;

/// De onde veio o comportamento (05 §5.17 0008).
internal enum BehaviorOrigin
{
    /// Escolhido no autocadastro (exatamente um).
    Signup,

    /// Acrescentado pelo administrador da plataforma (ADR-0025, ADR-0054 item 5).
    PlatformAdmin,

    /// Migração 0008 dos tenants existentes, ou a correção da janela do deploy (sem autor).
    System,
}

/// tenant_behaviors: comportamento habilitado, filho do agregado `Tenant`. Nunca alterado nem removido nesta fase.
internal sealed class TenantBehavior : ITenantOwned
{
    private TenantBehavior()
    {
    }

    public Guid TenantId { get; private set; }
    public EstablishmentBehavior Behavior { get; private set; }
    public BehaviorOrigin Origin { get; private set; }
    public DateTimeOffset EnabledAt { get; private set; }

    /// Proprietário (autocadastro) ou administrador da plataforma; nulo ⇔ `SYSTEM` (`ck_tenant_behaviors_enabled_by`).
    public Guid? EnabledByUserId { get; private set; }

    public static TenantBehavior Create(Guid tenantId, EstablishmentBehavior behavior, BehaviorOrigin origin, Guid? enabledBy, DateTimeOffset now) =>
        (origin == BehaviorOrigin.System) == (enabledBy is null)
            ? new TenantBehavior { TenantId = tenantId, Behavior = behavior, Origin = origin, EnabledAt = now, EnabledByUserId = enabledBy }
            : throw new ArgumentException("Só o comportamento de origem SYSTEM não tem autor.", nameof(enabledBy));
}

/// tenant_labels: rótulo informativo, filho do agregado `Tenant` (a versão é a do tenant). Nenhuma regra lê rótulo.
internal sealed class TenantLabel : ITenantOwned
{
    private TenantLabel()
    {
    }

    public Guid TenantId { get; private set; }
    public EstablishmentLabel Label { get; private set; }

    /// Derivado do rótulo; a FK composta para `tenant_behaviors` garante que o comportamento está habilitado.
    public EstablishmentBehavior Behavior { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static TenantLabel Create(Guid tenantId, EstablishmentLabel label, DateTimeOffset now) =>
        new() { TenantId = tenantId, Label = label, Behavior = EstablishmentLabels.BehaviorOf(label), CreatedAt = now };
}

/// O administrador acrescentou um comportamento: os módulos mudaram (Q-324). Vira `TenantModulesChanged` (06 §6.14).
internal sealed record TenantModulesChanged(
    Guid TenantId,
    IReadOnlyList<EstablishmentBehavior> Behaviors,
    IReadOnlyList<TenantModule> Modules,
    DateTimeOffset OccurredAt) : IDomainEvent;
