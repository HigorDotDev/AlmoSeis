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

        return fromBody is not null
            ? throw BusinessException.Validation("refreshToken", "Na web, o refresh token vem do cookie.")
            : request.Cookies[RefreshCookie];
    }

    /// Segredo do aparelho: `X-Device-Secret`, ou o cookie na web. `FromCookie` decide se a resposta renova ou apaga o cookie.
    public static (string? Secret, bool FromCookie) DeviceSecret(HttpRequest request, string? fromHeader)
    {
        if (!string.IsNullOrEmpty(fromHeader) || !IsWeb(request))
        {
            return (fromHeader, false);
        }

        var cookie = request.Cookies[DeviceSecretCookie];
        return (cookie, !string.IsNullOrEmpty(cookie));
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
    /// Headers que o app (e o cliente SignalR) enviam; tudo o que não está aqui falha no preflight.
    public static readonly string[] AllowedHeaders =
    [
        "Authorization", "Content-Type", "Idempotency-Key", "If-Match", "X-App-Version", "X-App-Platform", "X-Tenant-Id", "X-Device-Secret",
        "X-Requested-With", "X-SignalR-User-Agent",
    ];

    /// Sem `ETag` o JavaScript não lê a versão do If-Match (ADR-0034).
    public static readonly string[] ExposedHeaders = ["ETag", "Location", "Retry-After", "Idempotent-Replayed"];

    public static IServiceCollection AddRestaurantAppCors(this IServiceCollection services)
    {
        services.AddOptions<RestaurantAppCorsOptions>()
            .BindConfiguration(RestaurantAppCorsOptions.Section)
            .Validate(o => o.NormalizedOrigins.All(x => x != "*" && Uri.TryCreate(x, UriKind.Absolute, out var u) && u.AbsolutePath == "/"),
                "Cors:RestaurantAppOrigins aceita só origens completas (https://app.exemplo), sem curinga.")
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
}

/// [WebOriginCheck] nas rotas que leem cookie (anti-CSRF, ADR-0053 §3.4): `X-App-Platform: WEB` sem `Origin` ou com
/// `Origin` fora da lista → 403 OriginNotAllowed, antes do model binding e de qualquer leitura de cookie. Sem `WEB`
/// os cookies não são lidos, então não há o que conferir.
[AttributeUsage(AttributeTargets.Method)]
public sealed class WebOriginCheckAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var request = context.HttpContext.Request;
        if (WebSession.IsWeb(request)
            && !context.HttpContext.RequestServices.GetRequiredService<IOptions<RestaurantAppCorsOptions>>().Value.IsAllowed(request.Headers.Origin.FirstOrDefault()))
        {
            throw new BusinessException(ErrorCodes.OriginNotAllowed);
        }
    }

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
