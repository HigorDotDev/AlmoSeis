namespace AlmoSeis.SharedKernel.Errors;

/// Erro de negócio. O ProblemDetailsExceptionHandler o converte em RFC 7807 com `errorCode` (07 §7.4).
public class BusinessException(
    ErrorCode errorCode,
    string? detail = null,
    IReadOnlyDictionary<string, object?>? extensions = null)
    : Exception(detail ?? errorCode.Title)
{
    public ErrorCode ErrorCode { get; } = errorCode;

    public string? Detail { get; } = detail;

    public IReadOnlyDictionary<string, object?> Extensions { get; } = extensions ?? new Dictionary<string, object?>();
}

public sealed class NotFoundException(string entityType, Guid? entityId = null)
    : BusinessException(ErrorCodes.NotFound, $"{entityType} não encontrado.")
{
    public string EntityType { get; } = entityType;

    public Guid? EntityId { get; } = entityId;
}

/// `entityType`/`entityId` dizem ao Api qual recurso recarregar para o `current` do 409 (07 §7.4).
public sealed class VersionConflictException(string entityType, Guid entityId, int expectedVersion, int currentVersion)
    : BusinessException(
        ErrorCodes.VersionConflict,
        extensions: new Dictionary<string, object?>
        {
            ["expectedVersion"] = expectedVersion,
            ["currentVersion"] = currentVersion,
        })
{
    public string EntityType { get; } = entityType;

    public Guid EntityId { get; } = entityId;

    public int ExpectedVersion { get; } = expectedVersion;

    public int CurrentVersion { get; } = currentVersion;
}
