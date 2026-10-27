namespace AlmoSeis.Modules.Sync.Contracts;

/// 06 §6.12.
public sealed record SyncChangeDto(long Seq, string EntityType, Guid EntityId, int EntityVersion, string ChangeKind);

public sealed record SyncChangesDto(IReadOnlyList<SyncChangeDto> Changes, long LastSeq, bool HasMore);

public sealed record GetSyncChangesQuery(long Since, int Limit);

public sealed record OperationDto(
    Guid Key, string Status, string? Endpoint, int? ResponseStatus, Guid? ResourceId, DateTimeOffset? CreatedAt, DateTimeOffset? CompletedAt);

public sealed record GetOperationQuery(Guid Key);
