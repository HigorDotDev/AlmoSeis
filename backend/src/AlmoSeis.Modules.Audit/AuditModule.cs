using System.Buffers.Text;
using System.Text.Json;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Persistence.Entities;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Audit.Contracts;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlmoSeis.Modules.Audit;

public static class AuditModule
{
    public static IServiceCollection AddAuditModule(this IServiceCollection services)
    {
        services.AddScoped<IQueryHandler<ListAuditLogsQuery, PageDto<AuditLogDto>>, ListAuditLogsQueryHandler>();
        return services;
    }
}

/// GET /audit-logs (06 §6.13). audit_logs não é ITenantOwned (tenant anulável): o filtro de tenant é explícito,
/// e a RLS é a segunda barreira. Mais recentes primeiro; cursor = último id (UUID v7).
internal sealed class ListAuditLogsQueryHandler(AppDbContext db, ITenantContext tenant, IEmployeeDirectory directory)
    : IQueryHandler<ListAuditLogsQuery, PageDto<AuditLogDto>>
{
    private const int MaxLimit = 100;

    public async Task<PageDto<AuditLogDto>> HandleAsync(ListAuditLogsQuery query, CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > MaxLimit)
        {
            throw BusinessException.Validation("limit", $"Use um limite entre 1 e {MaxLimit}.");
        }

        var tenantId = tenant.TenantId;
        var logs = db.Set<AuditLog>().AsNoTracking().Where(a => a.TenantId == tenantId);
        if (query.EntityType is { } entityType)
        {
            logs = logs.Where(a => a.EntityType == entityType);
        }

        if (query.EntityId is { } entityId)
        {
            logs = logs.Where(a => a.EntityId == entityId);
        }

        if (query.ActorEmployeeId is { } actor)
        {
            logs = logs.Where(a => a.ActorEmployeeId == actor);
        }

        if (query.From is { } from)
        {
            logs = logs.Where(a => a.OccurredAt >= from);
        }

        if (query.To is { } to)
        {
            logs = logs.Where(a => a.OccurredAt <= to);
        }

        if (query.Cursor is { } cursor)
        {
            var after = DecodeCursor(cursor);
            logs = logs.Where(a => a.Id.CompareTo(after) < 0);
        }

        var rows = await logs.OrderByDescending(a => a.Id).Take(query.Limit + 1).ToListAsync(cancellationToken);
        var page = rows.Take(query.Limit).ToList();
        var names = await directory.GetDisplayNamesAsync(page.Where(r => r.ActorEmployeeId is not null).Select(r => r.ActorEmployeeId!.Value), cancellationToken);
        var devices = await directory.GetDeviceNamesAsync(page.Where(r => r.SharedDeviceId is not null).Select(r => r.SharedDeviceId!.Value), cancellationToken);

        var items = page.Select(r => new AuditLogDto(
                r.Id, r.OccurredAt, r.Origin, r.OperationType, r.EntityType, r.EntityId, r.OrderId,
                r.ActorEmployeeId is { } a ? new AuthorDto(a, names.GetValueOrDefault(a, string.Empty)) : null,
                r.SharedDeviceId is { } d ? new AuditDeviceDto(d, devices.GetValueOrDefault(d, string.Empty)) : null,
                Json(r.Before), Json(r.After), r.CorrelationId))
            .ToList();
        return new PageDto<AuditLogDto>(items, rows.Count > query.Limit ? Base64Url.EncodeToString(page[^1].Id.ToByteArray()) : null);
    }

    private static JsonElement? Json(string? value) => value is null ? null : JsonDocument.Parse(value).RootElement.Clone();

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
}
