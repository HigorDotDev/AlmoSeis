using System.Diagnostics;
using System.Text.Json;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Persistence.Entities;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.SharedKernel.Application;

namespace AlmoSeis.Infrastructure.Audit;

public static class AuditOrigins
{
    public const string App = "APP";
    public const string PlatformAdmin = "PLATFORM_ADMIN";
    public const string System = "SYSTEM";
    public const string BillingWebhook = "BILLING_WEBHOOK";
}

/// Auditoria explícita de negócio, gravada na mesma transação da operação (ADR-0031).
/// `before`/`after` são objetos escolhidos pelo handler, nunca a entidade inteira, e sem telefone,
/// endereço ou token (ADR-0024).
public interface IAuditWriter
{
    void Add(
        string operationType,
        string entityType,
        Guid entityId,
        Guid? orderId = null,
        object? before = null,
        object? after = null,
        string origin = AuditOrigins.App);
}

internal sealed class AuditWriter(AppDbContext db, ITenantContext tenant, IIdGenerator ids, TimeProvider time) : IAuditWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Add(
        string operationType,
        string entityType,
        Guid entityId,
        Guid? orderId = null,
        object? before = null,
        object? after = null,
        string origin = AuditOrigins.App)
    {
        db.Add(new AuditLog
        {
            Id = ids.New(),
            TenantId = tenant.TenantId,
            Origin = origin,
            ActorUserId = tenant.UserId,
            ActorEmployeeId = tenant.EmployeeId,
            SharedDeviceId = tenant.SharedDeviceId,
            OperationType = operationType,
            EntityType = entityType,
            EntityId = entityId,
            OrderId = orderId,
            Before = before is null ? null : JsonSerializer.Serialize(before, Json),
            After = after is null ? null : JsonSerializer.Serialize(after, Json),
            CorrelationId = Activity.Current?.Id ?? Activity.Current?.TraceId.ToString() ?? ids.New().ToString("N"),
            OccurredAt = time.GetUtcNow(),
        });
    }
}
