using Microsoft.Extensions.Options;

namespace AlmoSeis.Infrastructure.Storage;

public sealed class ObjectStorageOptions
{
    public const string Section = "ObjectStorage";

    /// Domínio público das imagens (ADR-0037), sem barra final: "https://img.almoseis.app".
    public string PublicBaseUrl { get; set; } = "https://img.almoseis.app";
}

/// Imagens no R2 (ADR-0037). A URL pré-assinada de upload entra com `POST /products/{id}/image-upload`.
public interface IObjectStorage
{
    string? PublicUrl(string? storageKey);
}

internal sealed class PublicUrlObjectStorage(IOptions<ObjectStorageOptions> options) : IObjectStorage
{
    public string? PublicUrl(string? storageKey) =>
        storageKey is null ? null : $"{options.Value.PublicBaseUrl.TrimEnd('/')}/{storageKey}";
}
