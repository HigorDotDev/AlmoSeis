using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AlmoSeis.Infrastructure.Security;

/// SHA-256 em hex minúsculo: refresh token, deviceSecret, ClaimKey de teste grátis, hash de idempotência.
public interface ISecretHasher
{
    string Hash(string value);
}

internal sealed class Sha256SecretHasher : ISecretHasher
{
    public string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public static class SecureRandom
{
    /// 32 bytes aleatórios em base64url com prefixo (ex.: "rt_...", "ds_...").
    public static string Token(string prefix) =>
        prefix + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
}

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "almoseis";

    public string Audience { get; set; } = "almoseis-app";

    /// Chave privada RSA em PEM (Key Vault em nuvem, user-secrets local) (07 §7.3).
    public string? SigningKeyPem { get; set; }

    /// Só desenvolvimento e testes: gera uma chave efêmera se SigningKeyPem estiver vazio.
    public bool AllowEphemeralKey { get; set; }

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 30;
}

/// Claims do access token (ADR-0027).
public static class AlmoSeisClaims
{
    public const string Subject = JwtRegisteredClaimNames.Sub;
    public const string TenantId = "tenant_id";
    public const string EmployeeId = "employee_id";
    public const string SharedDeviceId = "device_id";
    public const string SecurityStamp = "security_stamp";
    public const string PlatformAdmin = "platform_admin";
    public const string InstallationId = "installation_id";
}

public sealed record AccessTokenRequest(
    Guid UserId,
    Guid? TenantId,
    Guid? EmployeeId,
    Guid? EmployeeSecurityStamp,
    Guid? SharedDeviceId,
    string? InstallationId,
    bool IsPlatformAdmin = false);

public sealed record IssuedAccessToken(string Token, DateTimeOffset ExpiresAt);

public interface IJwtTokenIssuer
{
    IssuedAccessToken Issue(AccessTokenRequest request);

    SecurityKey ValidationKey { get; }
}

internal sealed class JwtTokenIssuer : IJwtTokenIssuer
{
    private readonly JwtOptions _options;
    private readonly TimeProvider _time;
    private readonly RsaSecurityKey _key;

    public JwtTokenIssuer(IOptions<JwtOptions> options, TimeProvider time, ILogger<JwtTokenIssuer> logger)
    {
        _options = options.Value;
        _time = time;

        var rsa = RSA.Create(2048);
        if (!string.IsNullOrWhiteSpace(_options.SigningKeyPem))
        {
            rsa.ImportFromPem(_options.SigningKeyPem);
        }
        else if (_options.AllowEphemeralKey)
        {
            logger.LogWarning("Jwt:SigningKeyPem vazio: usando chave RSA efêmera (só desenvolvimento/testes).");
        }
        else
        {
            throw new InvalidOperationException("Jwt:SigningKeyPem não configurada.");
        }

        _key = new RsaSecurityKey(rsa) { KeyId = "almoseis-1" };
    }

    public SecurityKey ValidationKey => _key;

    public IssuedAccessToken Issue(AccessTokenRequest request)
    {
        var now = _time.GetUtcNow();
        var expires = now.AddMinutes(_options.AccessTokenMinutes);

        var claims = new Dictionary<string, object>
        {
            [AlmoSeisClaims.Subject] = request.UserId.ToString(),
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
        };
        if (request.TenantId is { } tenantId)
        {
            claims[AlmoSeisClaims.TenantId] = tenantId.ToString();
        }

        if (request.EmployeeId is { } employeeId)
        {
            claims[AlmoSeisClaims.EmployeeId] = employeeId.ToString();
        }

        if (request.EmployeeSecurityStamp is { } stamp)
        {
            claims[AlmoSeisClaims.SecurityStamp] = stamp.ToString();
        }

        if (request.SharedDeviceId is { } deviceId)
        {
            claims[AlmoSeisClaims.SharedDeviceId] = deviceId.ToString();
        }

        if (request.InstallationId is { } installationId)
        {
            claims[AlmoSeisClaims.InstallationId] = installationId;
        }

        if (request.IsPlatformAdmin)
        {
            claims[AlmoSeisClaims.PlatformAdmin] = "true";
        }

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        });

        return new IssuedAccessToken(token, expires);
    }
}

public static class ClaimsPrincipalExtensions
{
    public static Guid? GetGuid(this ClaimsPrincipal principal, string claimType) =>
        Guid.TryParse(principal.FindFirst(claimType)?.Value, out var value) ? value : null;
}
