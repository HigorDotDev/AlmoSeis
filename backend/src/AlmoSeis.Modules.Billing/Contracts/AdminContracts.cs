using AlmoSeis.SharedKernel.Application;

namespace AlmoSeis.Modules.Billing.Contracts;

// ---------- Administração da plataforma (06 §6.15, ADR-0025, ADR-0043) ----------

public sealed record AdminSubscriptionSummaryDto(string Status, DateTimeOffset TrialEndsAt, DateTimeOffset? CurrentPeriodEndsAt);

public sealed record AdminOpenBusinessDayDto(Guid Id, DateOnly BusinessDate, DateTimeOffset OpenedAt);

public sealed record AdminTenantDto(
    Guid Id,
    string Name,
    string DocumentNumber,
    DateTimeOffset CreatedAt,
    AdminSubscriptionSummaryDto Subscription,
    int ActiveEmployeeCount,
    DateTimeOffset? LastOrderAt);

/// Detalhe: sem dados de pedidos ou clientes, só contagens.
public sealed record AdminTenantDetailDto(
    Guid Id,
    string Name,
    string DocumentNumber,
    DateTimeOffset CreatedAt,
    AdminSubscriptionSummaryDto Subscription,
    int ActiveEmployeeCount,
    DateTimeOffset? LastOrderAt,
    int OrdersLast30Days,
    AdminOpenBusinessDayDto? OpenBusinessDay);

public sealed record ExtendTrialRequest(int Days, string Reason);

public sealed record AdminReasonRequest(string Reason);

/// Ações de `/admin/*`. Rodam no papel de plataforma, fora do contexto de estabelecimento.
public interface IPlatformAdminService
{
    Task<PageDto<AdminTenantDto>> ListTenantsAsync(string? status, string? search, int limit, string? cursor, CancellationToken cancellationToken);

    Task<AdminTenantDetailDto> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<SubscriptionDto> ExtendTrialAsync(Guid adminUserId, Guid tenantId, ExtendTrialRequest request, CancellationToken cancellationToken);

    Task<SubscriptionDto> SuspendAsync(Guid adminUserId, Guid tenantId, AdminReasonRequest request, CancellationToken cancellationToken);

    Task<SubscriptionDto> ReactivateAsync(Guid adminUserId, Guid tenantId, AdminReasonRequest request, CancellationToken cancellationToken);

    Task ReprocessWebhookAsync(Guid adminUserId, Guid webhookEventId, CancellationToken cancellationToken);
}
