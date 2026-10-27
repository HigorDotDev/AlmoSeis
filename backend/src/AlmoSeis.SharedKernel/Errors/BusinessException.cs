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

    /// Regra de negócio por campo, no mesmo formato do 422 do FluentValidation (06 §6.1).
    /// `field` já vem no caminho da API: "defaultPrice", "items[1].productId".
    public static BusinessException Validation(string field, string message) =>
        Validation(new Dictionary<string, string[]> { [field] = [message] });

    public static BusinessException Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new(ErrorCodes.ValidationFailed, extensions: new Dictionary<string, object?> { ["errors"] = errors });
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
