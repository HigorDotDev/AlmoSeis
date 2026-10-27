using System.Text.Json;
using AlmoSeis.SharedKernel.Application;

namespace AlmoSeis.Modules.Audit.Contracts;

/// 06 §6.13.
public sealed record AuditDeviceDto(Guid Id, string Name);

public sealed record AuditLogDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Origin,
    string OperationType,
    string EntityType,
    Guid EntityId,
    Guid? OrderId,
    AuthorDto? Actor,
    AuditDeviceDto? SharedDevice,
    JsonElement? Before,
    JsonElement? After,
    string CorrelationId);

public sealed record ListAuditLogsQuery(
    string? EntityType, Guid? EntityId, Guid? ActorEmployeeId, DateTimeOffset? From, DateTimeOffset? To, int Limit, string? Cursor);
