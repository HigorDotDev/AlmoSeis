using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Infrastructure.Storage;

/// Cloudflare R2 pela API compatível com S3 (ADR-0037, 07 §7.11, 09 §9.x: `Storage:R2:*`).
/// As credenciais vêm de user-secrets (local) ou do Key Vault; nunca do appsettings versionado.
public sealed class R2Options
{
    public const string Section = "Storage:R2";

    /// Monta o endpoint `https://{AccountId}.r2.cloudflarestorage.com`, se `ServiceUrl` não vier preenchido.
    public string? AccountId { get; set; }

    public string? ServiceUrl { get; set; }

    public string Bucket { get; set; } = "almoseis-images";

    public string? AccessKeyId { get; set; }

    public string? SecretAccessKey { get; set; }

    /// Domínio público das imagens, sem barra final: "https://img.almoseis.app" ou "https://pub-<hash>.r2.dev".
    /// Sem o bucket no caminho: o domínio público já aponta para o bucket.
    public string PublicBaseUrl { get; set; } = "https://img.almoseis.app";

    public int UploadUrlMinutes { get; set; } = 15;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccessKeyId) && !string.IsNullOrWhiteSpace(SecretAccessKey) && Endpoint is not null;

    /// Só protocolo e host. Um `ServiceUrl` com o bucket no caminho (`.../almoseis-images`) faria o SDK, em
    /// path-style, gravar as chaves como `<bucket>/t/...` dentro do bucket (visto no teste contra o R2 real).
    public string? Endpoint => !string.IsNullOrWhiteSpace(ServiceUrl)
        ? Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : ServiceUrl
        : string.IsNullOrWhiteSpace(AccountId) ? null : $"https://{AccountId}.r2.cloudflarestorage.com";
}

public sealed record UploadUrl(string Url, string Method, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);

public sealed record StoredObject(long SizeBytes, string? ContentType);

/// Imagens dos produtos. O app envia direto ao R2 por URL pré-assinada (06 §6.5); a API só assina e confere.
public interface IObjectStorage
{
    /// Sem credenciais (desenvolvimento e testes), a API não confere o objeto antes de aceitar a chave.
    bool IsConfigured { get; }

    string? PublicUrl(string? storageKey);

    UploadUrl CreateUploadUrl(string storageKey, string contentType);

    /// Metadados do objeto enviado, ou null se ele não existe.
    Task<StoredObject?> GetAsync(string storageKey, CancellationToken cancellationToken);
}

/// Mesma configuração de cliente que funciona contra o R2 no UrbanCarePlatform: path-style e checksum só quando
/// exigido (o R2 não implementa a assinatura em streaming com checksum no fim, `STREAMING-...-TRAILER`).
internal sealed class R2ObjectStorage(IOptions<R2Options> options, TimeProvider time) : IObjectStorage, IDisposable
{
    private readonly Lazy<AmazonS3Client> _client = new(() => CreateClient(options.Value));

    public bool IsConfigured => options.Value.IsConfigured;

    public string? PublicUrl(string? storageKey) =>
        storageKey is null ? null : $"{options.Value.PublicBaseUrl.TrimEnd('/')}/{storageKey}";

    public UploadUrl CreateUploadUrl(string storageKey, string contentType)
    {
        var o = options.Value;
        if (!o.IsConfigured)
        {
            throw new InvalidOperationException("Storage:R2 não configurado: defina AccountId (ou ServiceUrl), AccessKeyId e SecretAccessKey.");
        }

        // Assinar é local: não há chamada de rede aqui.
        var expiresAt = time.GetUtcNow().AddMinutes(o.UploadUrlMinutes);
        var url = _client.Value.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = o.Bucket,
            Key = storageKey,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = expiresAt.UtcDateTime,
            Protocol = Protocol.HTTPS,
        });
        return new UploadUrl(url, "PUT", new Dictionary<string, string> { ["Content-Type"] = contentType }, expiresAt);
    }

    public async Task<StoredObject?> GetAsync(string storageKey, CancellationToken cancellationToken)
    {
        try
        {
            var head = await _client.Value.GetObjectMetadataAsync(options.Value.Bucket, storageKey, cancellationToken);
            return new StoredObject(head.ContentLength, head.Headers.ContentType);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    private static AmazonS3Client CreateClient(R2Options o) =>
        new(
            new BasicAWSCredentials(o.AccessKeyId, o.SecretAccessKey),
            new AmazonS3Config
            {
                ServiceURL = o.Endpoint,
                ForcePathStyle = true,
                AuthenticationRegion = "auto",
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            });
}
