using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Infrastructure.Security;

/// Cloudflare Turnstile (ADR-0045 item 12). Seção `Turnstile`. A chave secreta vem do Key Vault; em desenvolvimento,
/// `Enabled = false` ou a chave de teste oficial que sempre passa (`1x0000000000000000000000000000000AA`), recusada fora de
/// Development (`TurnstileOptionsValidator`).
public sealed class TurnstileOptions
{
    public const string Section = "Turnstile";

    public bool Enabled { get; set; } = true;

    public string SecretKey { get; set; } = string.Empty;

    public string SiteVerifyUrl { get; set; } = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    /// Sem resposta do Cloudflare nesse prazo, o pedido é aceito com `RiskFlag = CAPTCHA_UNAVAILABLE`.
    public int TimeoutSeconds { get; set; } = 3;
}

public enum TurnstileOutcome
{
    /// Desafio resolvido (ou Turnstile desligado por configuração).
    Passed,

    /// Token ausente, inválido, vencido ou repetido → `403 CaptchaFailed`.
    Failed,

    /// Cloudflare fora do ar ou lento: aceita e marca o pedido (o aceite humano é a segunda barreira).
    Unavailable,
}

public interface ITurnstileVerifier
{
    Task<TurnstileOutcome> VerifyAsync(string? token, string? remoteIp, CancellationToken cancellationToken);
}

/// O token nunca vai para log (ADR-0052 item 7).
internal sealed class TurnstileVerifier(HttpClient http, IOptions<TurnstileOptions> options, ILogger<TurnstileVerifier> logger)
    : ITurnstileVerifier
{
    public const string HttpClientName = "turnstile";

    public async Task<TurnstileOutcome> VerifyAsync(string? token, string? remoteIp, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return TurnstileOutcome.Passed;
        }

        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048)
        {
            return TurnstileOutcome.Failed;
        }

        var form = new Dictionary<string, string> { ["secret"] = settings.SecretKey, ["response"] = token };
        if (!string.IsNullOrEmpty(remoteIp))
        {
            form["remoteip"] = remoteIp;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            using var response = await http.PostAsync(settings.SiteVerifyUrl, new FormUrlEncodedContent(form), timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Turnstile respondeu {Status}: pedido aceito com CAPTCHA_UNAVAILABLE.", (int)response.StatusCode);
                return TurnstileOutcome.Unavailable;
            }

            var result = await response.Content.ReadFromJsonAsync<SiteVerifyResponse>(timeout.Token);
            if (result is null)
            {
                return TurnstileOutcome.Unavailable;
            }

            if (!result.Success && result.ErrorCodes?.Contains("internal-error") == true)
            {
                return TurnstileOutcome.Unavailable;
            }

            return result.Success ? TurnstileOutcome.Passed : TurnstileOutcome.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                   && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Turnstile indisponível ({Error}): pedido aceito com CAPTCHA_UNAVAILABLE.", ex.GetType().Name);
            return TurnstileOutcome.Unavailable;
        }
    }

    private sealed record SiteVerifyResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("error-codes")] string[]? ErrorCodes);
}

/// Fora de Development, `Turnstile:Enabled` exige a chave secreta de verdade: as chaves de teste oficiais do Cloudflare
/// (`1x0000…AA` sempre passa, `2x0000…AA` sempre falha, `3x0000…AA` dá token já usado) desligariam a barreira sem aviso.
internal sealed class TurnstileOptionsValidator(IHostEnvironment environment) : IValidateOptions<TurnstileOptions>
{
    private static readonly string[] TestKeyPrefixes = ["1x0000", "2x0000", "3x0000"];

    public ValidateOptionsResult Validate(string? name, TurnstileOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.SecretKey))
        {
            return ValidateOptionsResult.Fail("Turnstile:SecretKey é obrigatório com Turnstile:Enabled.");
        }

        if (!environment.IsDevelopment() && TestKeyPrefixes.Any(p => options.SecretKey.Trim().StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return ValidateOptionsResult.Fail("Turnstile:SecretKey é uma chave de teste do Cloudflare: só em Development.");
        }

        return ValidateOptionsResult.Success;
    }
}

/// Seção `PublicPage`: segredos e chaves da página pública (ADR-0045, ADR-0047, ADR-0051).
public sealed class PublicPageOptions
{
    public const string Section = "PublicPage";

    /// Mínimo dos segredos fora de Development (256 bits em base64 ou hex passam).
    public const int MinSecretLength = 32;

    /// Sal do hash do IP (ADR-0045 item 14). Obrigatório fora de Development; vazio em Development → um sal aleatório por
    /// processo (o limite de pendentes por IP reinicia a cada reinício da API).
    public string IpHashSecret { get; set; } = string.Empty;

    /// Chave do token de acompanhamento (ADR-0047): o token é derivado dela e do pedido, e por isso não precisa ficar
    /// guardado em lugar nenhum, nem na resposta guardada da idempotência. Obrigatória fora de Development; vazia em
    /// Development → aleatória por processo (um reenvio depois de reiniciar a API devolveria outro token).
    public string TrackingTokenSecret { get; set; } = string.Empty;

    /// `X-Web-Renderer-Key` do servidor da página (ADR-0051): isenta as leituras do limite por IP. Vazio → sem isenção.
    public string RendererKey { get; set; } = string.Empty;
}

/// A API não sobe fora de Development sem os segredos da página: com o sal aleatório por processo, cada réplica (e cada
/// reinício) teria um hash de IP diferente, e o limite de 3 pendentes por IP deixaria de valer.
internal sealed class PublicPageOptionsValidator(IHostEnvironment environment) : IValidateOptions<PublicPageOptions>
{
    public ValidateOptionsResult Validate(string? name, PublicPageOptions options)
    {
        if (environment.IsDevelopment())
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        foreach (var (key, value) in new[] { ("IpHashSecret", options.IpHashSecret), ("TrackingTokenSecret", options.TrackingTokenSecret) })
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length < PublicPageOptions.MinSecretLength)
            {
                failures.Add($"PublicPage:{key} é obrigatório fora de Development (Key Vault), com pelo menos {PublicPageOptions.MinSecretLength} caracteres.");
            }
        }

        if (!string.IsNullOrWhiteSpace(options.IpHashSecret) && options.IpHashSecret == options.TrackingTokenSecret)
        {
            failures.Add("PublicPage:IpHashSecret e PublicPage:TrackingTokenSecret precisam ser diferentes.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// IP só como hash com sal **diário** (ADR-0045 item 14, ADR-0052 item 5): o mesmo IP no mesmo dia dá o mesmo hash
/// (limite de pendentes por IP), e o hash não serve para cruzar dias. O IP em claro nunca é gravado.
public interface IClientIpHasher
{
    string Hash(string? ip, DateTimeOffset at);
}

internal sealed class ClientIpHasher(IOptions<PublicPageOptions> options) : IClientIpHasher
{
    /// Só em Development (o validador recusa o segredo vazio nos outros ambientes).
    private static readonly byte[] ProcessSecret = RandomNumberGenerator.GetBytes(32);

    public string Hash(string? ip, DateTimeOffset at)
    {
        var configured = options.Value.IpHashSecret;
        var secret = string.IsNullOrEmpty(configured) ? ProcessSecret : Encoding.UTF8.GetBytes(configured);
        var day = at.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var salt = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes("ip-salt:" + day));
        return Convert.ToHexStringLower(HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(ip ?? "unknown")));
    }
}

/// Constantes compartilhadas do token de acompanhamento (ADR-0047 item 1): 128 bits em base64url (22 caracteres).
public static class TrackingTokens
{
    /// Token aleatório no mesmo formato (testes: um token que não existe).
    public static string New() => Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(16));

    /// Formato de um token possível: evita consultar o banco com lixo.
    public static bool LooksValid(string? token) =>
        token is { Length: 22 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// Token de acompanhamento do pedido (ADR-0047): `base64url(HMAC-SHA-256(PublicPage:TrackingTokenSecret, tenant:pedido)[..16])`.
/// Imprevisível sem o segredo (128 bits), e o mesmo para o mesmo pedido: o banco guarda só o SHA-256 dele, e o reenvio
/// idempotente o recalcula a partir do pedido em vez de guardá-lo na resposta (`IReplaySecretSource`).
public interface ITrackingTokenIssuer
{
    string For(Guid tenantId, Guid orderId);
}

internal sealed class TrackingTokenIssuer(IOptions<PublicPageOptions> options) : ITrackingTokenIssuer
{
    /// Só em Development (o validador recusa o segredo vazio nos outros ambientes).
    private static readonly byte[] ProcessSecret = RandomNumberGenerator.GetBytes(32);

    public string For(Guid tenantId, Guid orderId)
    {
        var configured = options.Value.TrackingTokenSecret;
        var secret = string.IsNullOrEmpty(configured) ? ProcessSecret : Encoding.UTF8.GetBytes(configured);
        var mac = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"tracking-token:v1:{tenantId:N}:{orderId:N}"));
        return Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(mac.AsSpan(0, 16).ToArray());
    }
}
