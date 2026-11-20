using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Api.Infrastructure;

/// App do restaurante no navegador (ADR-0053, 06 §6.3 "App no navegador"). Só `X-App-Platform: WEB` usa cookies;
/// ANDROID/IOS seguem com o refresh no corpo e o segredo no header, e os cookies são ignorados.
public static class WebSession
{
    public const string PlatformHeader = "X-App-Platform";
    public const string WebPlatform = "WEB";
    public const string RefreshCookie = "__Secure-almoseis-rt";
    public const string DeviceSecretCookie = "__Secure-almoseis-ds";
    public const string CookiePath = "/api/v1/auth";

    /// O Chrome limita a validade de um cookie a 400 dias; cada `pin-login` pelo cookie o reemite.
    public static readonly TimeSpan DeviceSecretLifetime = TimeSpan.FromDays(400);

    public static bool IsWeb(HttpRequest request) =>
        string.Equals(request.Headers[PlatformHeader].FirstOrDefault(), WebPlatform, StringComparison.OrdinalIgnoreCase);

    /// Refresh token da requisição: na web, do cookie (corpo com refresh → 422); fora dela, do corpo (obrigatório).
    public static string? RefreshToken(HttpRequest request, string? fromBody, bool required)
    {
        if (!IsWeb(request))
        {
            return required && string.IsNullOrEmpty(fromBody)
                ? throw BusinessException.Validation("refreshToken", "Informe o refresh token.")
                : fromBody;
        }

        EnsureAllowedOrigin(request);
        return fromBody is not null
            ? throw BusinessException.Validation("refreshToken", "Na web, o refresh token vem do cookie.")
            : ReadCookie(request, RefreshCookie);
    }

    /// Segredo do aparelho: fora da web, `X-Device-Secret`; na web, **só** o cookie (ADR-0053 §3.8: o segredo fica fora
    /// do JavaScript), e o header é ignorado. `FromCookie` decide se a resposta renova ou apaga o cookie.
    public static (string? Secret, bool FromCookie) DeviceSecret(HttpRequest request, string? fromHeader)
    {
        if (!IsWeb(request))
        {
            return (fromHeader, false);
        }

        var cookie = ReadCookie(request, DeviceSecretCookie);
        return (cookie, !string.IsNullOrEmpty(cookie));
    }

    /// Anti-CSRF (ADR-0053 §3.4): `X-App-Platform: WEB` sem `Origin` ou com `Origin` fora de `Cors:RestaurantAppOrigins`
    /// → 403 OriginNotAllowed. Sem `WEB` os cookies não são lidos, então não há o que conferir.
    public static void EnsureAllowedOrigin(HttpRequest request)
    {
        if (IsWeb(request)
            && !request.HttpContext.RequestServices.GetRequiredService<IOptions<RestaurantAppCorsOptions>>().Value.IsAllowed(request.Headers.Origin.FirstOrDefault()))
        {
            throw new BusinessException(ErrorCodes.OriginNotAllowed);
        }
    }

    /// Único ponto da API que lê cookie da requisição (regra de arquitetura 8, 07 §7.12): confere o `Origin` antes.
    private static string? ReadCookie(HttpRequest request, string name)
    {
        EnsureAllowedOrigin(request);
        return request.Cookies[name];
    }

    public static void SetRefreshCookie(HttpResponse response, string token, TimeSpan maxAge) =>
        response.Cookies.Append(RefreshCookie, token, Options(maxAge));

    public static void SetDeviceSecretCookie(HttpResponse response, string secret) =>
        response.Cookies.Append(DeviceSecretCookie, secret, Options(DeviceSecretLifetime));

    public static void ClearCookie(HttpResponse response, string name) =>
        response.Cookies.Append(name, string.Empty, Options(TimeSpan.Zero, DateTimeOffset.UnixEpoch));

    /// Apaga o cookie mesmo quando a resposta vira erro: o tratador de exceções limpa os headers da resposta,
    /// mas os callbacks de OnStarting rodam depois disso.
    public static void ClearCookieOnResponse(HttpResponse response, string name) =>
        response.OnStarting(() =>
        {
            ClearCookie(response, name);
            return Task.CompletedTask;
        });

    private static CookieOptions Options(TimeSpan maxAge, DateTimeOffset? expires = null) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = CookiePath,
        MaxAge = maxAge,
        Expires = expires,
        IsEssential = true,
    };
}

/// `Cors:RestaurantAppOrigins`: origens do app do restaurante no navegador, por ambiente (ADR-0053 §4).
public sealed class RestaurantAppCorsOptions
{
    public const string Section = "Cors";
    public const string Policy = "RestaurantApp";

    public string[] RestaurantAppOrigins { get; set; } = [];

    public IReadOnlyList<string> NormalizedOrigins =>
        RestaurantAppOrigins.Select(o => o.Trim().TrimEnd('/')).Where(o => o.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public bool IsAllowed(string? origin) =>
        !string.IsNullOrEmpty(origin) && NormalizedOrigins.Contains(origin.TrimEnd('/'), StringComparer.OrdinalIgnoreCase);
}

public static class RestaurantAppCors
{
    /// Headers que o app web envia (ADR-0053 §4.3); tudo o que não está aqui falha no preflight. Sem `X-Device-Secret`:
    /// na web o segredo vem só do cookie. Sem os headers do cliente SignalR (`X-Requested-With`, `X-SignalR-User-Agent`):
    /// o app conecta direto por WebSocket (`skipNegotiation`), que não passa por preflight.
    public static readonly string[] AllowedHeaders =
    [
        "Authorization", "Content-Type", "Idempotency-Key", "If-Match", "X-App-Version", "X-App-Platform", "X-Tenant-Id",
    ];

    /// Sem `ETag` o JavaScript não lê a versão do If-Match (ADR-0034); `Idempotent-Replayed` é lido pelo app (ADR-0033).
    public static readonly string[] ExposedHeaders = ["ETag", "Location", "Retry-After", "Idempotent-Replayed"];

    public static IServiceCollection AddRestaurantAppCors(this IServiceCollection services)
    {
        services.AddOptions<RestaurantAppCorsOptions>()
            .BindConfiguration(RestaurantAppCorsOptions.Section)
            .Validate(o => o.NormalizedOrigins.All(IsValidOrigin),
                "Cors:RestaurantAppOrigins aceita só origens completas em https:// (http:// só para localhost e 127.0.0.1), sem curinga nem caminho.")
            .ValidateOnStart();

        services.AddCors();
        services.AddOptions<CorsOptions>().Configure<IOptions<RestaurantAppCorsOptions>>((cors, app) =>
            cors.AddPolicy(RestaurantAppCorsOptions.Policy, policy =>
            {
                // Lista vazia → política sem origens: nenhuma resposta leva Access-Control-Allow-Origin.
                policy.WithOrigins([.. app.Value.NormalizedOrigins])
                    .AllowCredentials()
                    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
                    .WithHeaders(AllowedHeaders)
                    .WithExposedHeaders(ExposedHeaders)
                    .SetPreflightMaxAge(TimeSpan.FromSeconds(600));
            }));
        return services;
    }

    /// Origem completa (esquema + host + porta opcional, sem caminho, query nem credenciais): `https://`, ou `http://` só
    /// em `localhost`/`127.0.0.1` (desenvolvimento, qualquer porta).
    public static bool IsValidOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0
        && !origin.EndsWith('/') && !uri.Host.Contains('*', StringComparison.Ordinal)
        && (uri.Scheme == Uri.UriSchemeHttps
            || (uri.Scheme == Uri.UriSchemeHttp && (uri.Host == "localhost" || uri.Host == "127.0.0.1")));
}

/// [WebOriginCheck] nas rotas que leem ou apagam cookie (anti-CSRF, ADR-0053 §3.4): a mesma checagem de
/// `WebSession.EnsureAllowedOrigin`, mas antes do model binding e da validação (o 403 vem antes de um 422). A leitura do
/// cookie confere de novo, então esquecer o atributo não abre a rota.
[AttributeUsage(AttributeTargets.Method)]
public sealed class WebOriginCheckAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context) => WebSession.EnsureAllowedOrigin(context.HttpContext.Request);

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}

/// Na web, tira o refresh token e o `deviceSecret` do corpo e os entrega em cookies `HttpOnly` (06 §6.3). Global: vale
/// para toda resposta com `Tokens` (signup, login, refresh, switch-tenant, pin-login, aceite de convite) e para o
/// registro do aparelho compartilhado.
internal sealed class WebSessionCookieFilter(TimeProvider time) : IResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not ObjectResult { Value: { } value } result || !WebSession.IsWeb(context.HttpContext.Request))
        {
            return;
        }

        var response = context.HttpContext.Response;
        switch (value)
        {
            case AuthResultDto auth:
                result.Value = auth with { Tokens = MoveRefresh(response, auth.Tokens) };
                break;
            case TokensDto tokens:
                result.Value = MoveRefresh(response, tokens);
                break;
            case RegisteredSharedDeviceDto { DeviceSecret: { } secret } device:
                WebSession.SetDeviceSecretCookie(response, secret);
                result.Value = device with { DeviceSecret = null };
                break;
        }
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }

    private TokensDto MoveRefresh(HttpResponse response, TokensDto tokens)
    {
        if (tokens.RefreshToken is { } refresh)
        {
            var maxAge = tokens.RefreshTokenExpiresAt - time.GetUtcNow();
            WebSession.SetRefreshCookie(response, refresh, maxAge > TimeSpan.Zero ? maxAge : TimeSpan.Zero);
        }

        return tokens with { RefreshToken = null };
    }
}

/// O hub não passa pelo CORS no WebSocket: a conexão confere o `Origin` (ADR-0053 §4.2). Sem `Origin` (cliente nativo)
/// ou com o próprio host da API (o WebSocket do React Native manda esse) passa.
internal sealed class HubOriginMiddleware(RequestDelegate next, IOptions<RestaurantAppCorsOptions> options)
{
    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.Path.StartsWithSegments("/hubs") && request.Headers.Origin.FirstOrDefault() is { Length: > 0 } origin
            && !options.Value.IsAllowed(origin) && !IsSameHost(origin, request))
        {
            throw new BusinessException(ErrorCodes.OriginNotAllowed);
        }

        return next(context);
    }

    private static bool IsSameHost(string origin, HttpRequest request) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
}
