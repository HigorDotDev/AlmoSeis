using System.Buffers.Text;
using System.Text.Json;
using AlmoSeis.Infrastructure.Audit;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Persistence.Entities;
using AlmoSeis.Modules.Billing.Contracts;
using AlmoSeis.Modules.Billing.Domain;
using AlmoSeis.Modules.Billing.Infrastructure;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Modules.Billing.Application;

/// `/admin/*` (06 §6.15, ADR-0025, ADR-0043). Papel de plataforma: enxerga todos os tenants, por isso as consultas
/// trazem só cadastro, assinatura e contagens, nunca pedidos ou clientes. Toda ação é auditada como PLATFORM_ADMIN.
internal sealed class PlatformAdminService(
    PlatformDbContext db,
    SubscriptionChangeRecorder recorder,
    IIdGenerator ids,
    IOptions<BillingOptions> options,
    TimeProvider time) : IPlatformAdminService
{
    private const int MaxLimit = 100;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PageDto<AdminTenantDto>> ListTenantsAsync(string? status, string? search, int limit, string? cursor, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > MaxLimit)
        {
            throw BusinessException.Validation("limit", $"Use um limite entre 1 e {MaxLimit}.");
        }

        var after = cursor is null ? (Guid?)null : DecodeCursor(cursor);
        var pattern = string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
        var rows = await db.Database.SqlQuery<TenantRow>(
            $"""
            SELECT t.id, t.name, t.document_number, t.created_at, s.status, s.trial_ends_at, s.current_period_ends_at,
                   (SELECT count(*) FROM tenant_employees e WHERE e.tenant_id = t.id AND e.status = 'ACTIVE')::int AS active_employee_count,
                   (SELECT max(o.created_at) FROM orders o WHERE o.tenant_id = t.id) AS last_order_at
              FROM tenants t
              JOIN subscriptions s ON s.tenant_id = t.id
             WHERE ({status}::text IS NULL OR s.status = {status}::text)
               AND ({pattern}::text IS NULL OR t.name ILIKE {pattern}::text OR t.document_number LIKE {pattern}::text)
               AND ({after}::uuid IS NULL OR t.id < {after}::uuid)
             ORDER BY t.id DESC
             LIMIT {limit + 1}
            """).ToListAsync(cancellationToken);

        var page = rows.Take(limit).Select(ToDto).ToList();
        return new PageDto<AdminTenantDto>(page, rows.Count > limit ? Base64Url.EncodeToString(page[^1].Id.ToByteArray()) : null);
    }

    public async Task<AdminTenantDetailDto> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var since = time.GetUtcNow().AddDays(-30);
        var row = (await db.Database.SqlQuery<TenantDetailRow>(
            $"""
            SELECT t.id, t.name, t.document_number, t.created_at, s.status, s.trial_ends_at, s.current_period_ends_at,
                   (SELECT count(*) FROM tenant_employees e WHERE e.tenant_id = t.id AND e.status = 'ACTIVE')::int AS active_employee_count,
                   (SELECT max(o.created_at) FROM orders o WHERE o.tenant_id = t.id) AS last_order_at,
                   (SELECT count(*) FROM orders o WHERE o.tenant_id = t.id AND o.created_at >= {since})::int AS orders_last30days, -- snake_case do EF: sem "_" antes do número
                   d.id AS open_day_id, d.business_date AS open_day_date, d.opened_at AS open_day_opened_at
              FROM tenants t
              JOIN subscriptions s ON s.tenant_id = t.id
              LEFT JOIN business_days d ON d.tenant_id = t.id AND d.status = 'OPEN'
             WHERE t.id = {tenantId}
            """).ToListAsync(cancellationToken)).SingleOrDefault()
            ?? throw new NotFoundException("TENANT", tenantId);

        var b = ToDto(row.Base);
        return new AdminTenantDetailDto(b.Id, b.Name, b.DocumentNumber, b.CreatedAt, b.Subscription, b.ActiveEmployeeCount, b.LastOrderAt,
            row.OrdersLast30Days,
            row.OpenDayId is { } dayId ? new AdminOpenBusinessDayDto(dayId, row.OpenDayDate!.Value, row.OpenDayOpenedAt!.Value) : null);
    }

    public Task<SubscriptionDto> ExtendTrialAsync(Guid adminUserId, Guid tenantId, ExtendTrialRequest request, CancellationToken cancellationToken)
    {
        if (request.Days is < 1 or > 60)
        {
            throw BusinessException.Validation("days", "Use de 1 a 60 dias.");
        }

        return ChangeAsync(adminUserId, tenantId, "TrialExtended", request.Reason, new { request.Days }, (s, now) => s.ExtendTrial(request.Days, now), cancellationToken);
    }

    public Task<SubscriptionDto> SuspendAsync(Guid adminUserId, Guid tenantId, AdminReasonRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(adminUserId, tenantId, "SubscriptionSuspendedByAdmin", request.Reason, null, (s, now) => s.Suspend(now), cancellationToken);

    /// Pagamento confirmado fora do gateway: ativa por um mês a partir de agora.
    public Task<SubscriptionDto> ReactivateAsync(Guid adminUserId, Guid tenantId, AdminReasonRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(adminUserId, tenantId, "SubscriptionReactivatedByAdmin", request.Reason, null, (s, now) => s.Activate(now.AddMonths(1), now), cancellationToken);

    public async Task ReprocessWebhookAsync(Guid adminUserId, Guid webhookEventId, CancellationToken cancellationToken)
    {
        var webhook = await db.Set<BillingWebhookEvent>().SingleOrDefaultAsync(e => e.Id == webhookEventId, cancellationToken)
            ?? throw new NotFoundException("BILLING_WEBHOOK_EVENT", webhookEventId);
        webhook.ProcessedAt = null;
        Audit(adminUserId, webhook.TenantId, "BillingWebhookReprocessed", "BILLING_WEBHOOK_EVENT", webhook.Id, new { webhook.EventType, webhook.GatewayEventId });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<SubscriptionDto> ChangeAsync(
        Guid adminUserId, Guid tenantId, string operation, string reason, object? details, Action<Subscription, DateTimeOffset> change,
        CancellationToken cancellationToken)
    {
        if (reason.Trim().Length < 5)
        {
            throw BusinessException.Validation("reason", "Descreva o motivo com pelo menos 5 caracteres.");
        }

        var subscription = await db.Set<Subscription>().SingleOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken)
            ?? throw new NotFoundException("TENANT", tenantId);
        var now = time.GetUtcNow();
        var before = subscription.Status;
        try
        {
            change(subscription, now);
        }
        catch (InvalidOperationException ex)
        {
            throw BusinessException.Validation("status", ex.Message);
        }

        Audit(adminUserId, tenantId, operation, "SUBSCRIPTION", subscription.Id, new { reason = reason.Trim(), details, before, after = subscription.Status });
        recorder.Record(db, subscription, AuditOrigins.PlatformAdmin, operation, before, now, adminUserId);
        await db.SaveChangesAsync(cancellationToken);
        return SubscriptionDtos.Build(subscription, options.Value.GracePeriod, now);
    }

    private void Audit(Guid adminUserId, Guid? tenantId, string operation, string entityType, Guid entityId, object after) =>
        db.Add(new AuditLog
        {
            Id = ids.New(),
            TenantId = tenantId,
            Origin = AuditOrigins.PlatformAdmin,
            ActorUserId = adminUserId,
            OperationType = operation,
            EntityType = entityType,
            EntityId = entityId,
            After = JsonSerializer.Serialize(after, Json),
            CorrelationId = System.Diagnostics.Activity.Current?.Id ?? ids.New().ToString("N"),
            OccurredAt = time.GetUtcNow(),
        });

    private static AdminTenantDto ToDto(TenantRow r) => new(
        r.Id, r.Name, r.DocumentNumber, r.CreatedAt, new AdminSubscriptionSummaryDto(r.Status, r.TrialEndsAt, r.CurrentPeriodEndsAt),
        r.ActiveEmployeeCount, r.LastOrderAt);

    private static Guid DecodeCursor(string cursor)
    {
        try
        {
            var bytes = Base64Url.DecodeFromChars(cursor);
            return bytes.Length == 16 ? new Guid(bytes) : throw new FormatException();
        }
        catch (FormatException)
        {
            throw new BusinessException(ErrorCodes.MalformedRequest, "Cursor inválido.");
        }
    }

    private sealed record TenantRow(
        Guid Id, string Name, string DocumentNumber, DateTimeOffset CreatedAt, string Status, DateTimeOffset TrialEndsAt,
        DateTimeOffset? CurrentPeriodEndsAt, int ActiveEmployeeCount, DateTimeOffset? LastOrderAt);

    private sealed record TenantDetailRow(
        Guid Id, string Name, string DocumentNumber, DateTimeOffset CreatedAt, string Status, DateTimeOffset TrialEndsAt,
        DateTimeOffset? CurrentPeriodEndsAt, int ActiveEmployeeCount, DateTimeOffset? LastOrderAt, int OrdersLast30Days,
        Guid? OpenDayId, DateOnly? OpenDayDate, DateTimeOffset? OpenDayOpenedAt)
    {
        public TenantRow Base => new(Id, Name, DocumentNumber, CreatedAt, Status, TrialEndsAt, CurrentPeriodEndsAt, ActiveEmployeeCount, LastOrderAt);
    }
}
