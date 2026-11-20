using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Infrastructure.Security;

/// Cloudflare Turnstile (ADR-0045 item 12). Seção `Turnstile`. A chave secreta vem do Key Vault; em desenvolvimento,
/// `Enabled = false` ou a chave de teste oficial que sempre passa (`1x0000000000000000000000000000000AA`).
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

/// Seção `PublicPage`: segredos e chaves da página pública (ADR-0045, ADR-0051).
public sealed class PublicPageOptions
{
    public const string Section = "PublicPage";

    /// Sal do hash do IP (ADR-0045 item 14). Vazio → um sal aleatório por processo (desenvolvimento e testes).
    public string IpHashSecret { get; set; } = string.Empty;

    /// `X-Web-Renderer-Key` do servidor da página (ADR-0051): isenta as leituras do limite por IP. Vazio → sem isenção.
    public string RendererKey { get; set; } = string.Empty;
}

/// IP só como hash com sal **diário** (ADR-0045 item 14, ADR-0052 item 5): o mesmo IP no mesmo dia dá o mesmo hash
/// (limite de pendentes por IP), e o hash não serve para cruzar dias. O IP em claro nunca é gravado.
public interface IClientIpHasher
{
    string Hash(string? ip, DateTimeOffset at);
}

internal sealed class ClientIpHasher(IOptions<PublicPageOptions> options) : IClientIpHasher
{
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

/// Constantes compartilhadas do token de acompanhamento (ADR-0047 item 1): 128 bits aleatórios em base64url (22 caracteres).
public static class TrackingTokens
{
    public static string New() => Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(16));

    /// Formato de um token possível: evita consultar o banco com lixo.
    public static bool LooksValid(string? token) =>
        token is { Length: 22 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
