using System.Security.Cryptography;
using System.Text;
using AlmoSeis.Infrastructure.Security;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Storefront.Contracts;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Api.Infrastructure;

/// Rotas anônimas da página de pedidos (ADR-0045): `/api/v1/public/*`.
public static class PublicRoutes
{
    public const string Prefix = "/api/v1/public";
    public const string Stores = "/api/v1/public/stores";
    public const string Tracking = "/api/v1/public/tracking";
    public const string RendererKeyHeader = "X-Web-Renderer-Key";
    public const string TurnstileHeader = "X-Turnstile-Token";

    public static bool IsPublic(HttpContext http) => http.Request.Path.StartsWithSegments(Prefix, StringComparison.OrdinalIgnoreCase);

    /// Leitura feita pelo servidor da página (ADR-0051), isenta do limite por IP: `X-Web-Renderer-Key` igual à configurada.
    public static bool IsRenderer(HttpContext http)
    {
        var expected = http.RequestServices.GetRequiredService<IOptionsMonitor<PublicPageOptions>>().CurrentValue.RendererKey;
        var received = http.Request.Headers[RendererKeyHeader].FirstOrDefault();
        return !string.IsNullOrEmpty(expected) && received is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(received));
    }
}

/// `Cors:PublicPageOrigins`: origens da página pública, por ambiente (ADR-0053 §4.6). Política **separada** da do app do
/// restaurante: sem credenciais, só `/api/v1/public/*`. Desenvolvimento: `http://localhost:3000`.
public sealed class PublicPageCorsOptions
{
    public const string Section = "Cors";
    public const string Policy = "PublicPage";

    public string[] PublicPageOrigins { get; set; } = [];

    public IReadOnlyList<string> NormalizedOrigins =>
        PublicPageOrigins.Select(o => o.Trim().TrimEnd('/')).Where(o => o.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

public static class PublicPageCors
{
    /// O que a página envia: corpo JSON, a chave de idempotência, o token do Turnstile e o `If-None-Match` da consulta de
    /// acompanhamento (o `no-store` impede o navegador de mandar sozinho).
    public static readonly string[] AllowedHeaders = ["Content-Type", "Idempotency-Key", PublicRoutes.TurnstileHeader, "If-None-Match"];

    public static readonly string[] ExposedHeaders = ["ETag", "Location", "Retry-After", "Idempotent-Replayed"];

    public static IServiceCollection AddPublicPageCors(this IServiceCollection services)
    {
        services.AddOptions<PublicPageCorsOptions>()
            .BindConfiguration(PublicPageCorsOptions.Section)
            .Validate(o => o.NormalizedOrigins.All(RestaurantAppCors.IsValidOrigin),
                "Cors:PublicPageOrigins aceita só origens completas em https:// (http:// só para localhost e 127.0.0.1), sem curinga nem caminho.")
            .ValidateOnStart();

        services.AddOptions<CorsOptions>().Configure<IOptions<PublicPageCorsOptions>>((cors, page) =>
            cors.AddPolicy(PublicPageCorsOptions.Policy, policy =>
            {
                // Sem AllowCredentials: a página não tem sessão nem cookie (ADR-0045, ADR-0053 §4.6).
                policy.WithOrigins([.. page.Value.NormalizedOrigins])
                    .WithMethods("GET", "POST")
                    .WithHeaders(AllowedHeaders)
                    .WithExposedHeaders(ExposedHeaders)
                    .SetPreflightMaxAge(TimeSpan.FromSeconds(600));
            }));
        return services;
    }
}

/// Resolve o tenant das rotas públicas no servidor (ADR-0045 itens 3 a 6), antes da autenticação:
/// - `/public/stores/{slug}/…` → `store_slugs`. Inexistente → `404 StoreNotFound`; slug antigo (ou fora do formato
///   canônico) → `301` (GET) ou `308` (demais métodos) para o atual;
/// - `/public/tracking/{token}/…` → hash em `order_tracking_tokens`. Inexistente → `404 NotFound`; vencido → `410`.
/// Depois disso, toda leitura e gravação passa pelo query filter e pela RLS do tenant, como numa rota autenticada.
internal sealed class PublicTenantMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context, ITenantContext tenant, IStoreSlugResolver slugs, ITrackingTokenResolver tokens, TimeProvider time)
    {
        var request = context.Request;
        if (request.Path.StartsWithSegments(PublicRoutes.Stores, StringComparison.OrdinalIgnoreCase, out var storeRest)
            && FirstSegment(storeRest) is { } slug)
        {
            var resolved = await slugs.ResolveAsync(slug, context.RequestAborted) ?? throw new BusinessException(ErrorCodes.StoreNotFound);
            if (!string.Equals(resolved.CurrentSlug, slug, StringComparison.Ordinal))
            {
                var raw = storeRest.Value!;
                var slash = raw.IndexOf('/', 1);
                var remaining = slash < 0 ? string.Empty : raw[slash..];
                context.Response.StatusCode = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
                    ? StatusCodes.Status301MovedPermanently
                    : StatusCodes.Status308PermanentRedirect;
                context.Response.Headers.Location = $"{PublicRoutes.Stores}/{resolved.CurrentSlug}{remaining}{request.QueryString}";
                return;
            }

            tenant.InitializeForPublic(resolved.TenantId, TenantSource.PublicSlug);
        }
        else if (request.Path.StartsWithSegments(PublicRoutes.Tracking, StringComparison.OrdinalIgnoreCase, out var trackingRest)
                 && FirstSegment(trackingRest) is { } token)
        {
            // O token está no caminho: nunca vai para log (ADR-0052 item 7). Inválido e inexistente dão o mesmo 404.
            var resolved = await tokens.ResolveAsync(token, context.RequestAborted) ?? throw new NotFoundException("ORDER");
            if (resolved.ExpiresAt <= time.GetUtcNow())
            {
                throw new BusinessException(ErrorCodes.TrackingExpired);
            }

            tenant.InitializeForPublic(resolved.TenantId, TenantSource.TrackingToken);
        }

        await next(context);
    }

    private static string? FirstSegment(PathString rest) =>
        rest.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) is [var first, ..] ? Uri.UnescapeDataString(first) : null;
}

/// Acompanhamento (ADR-0047 item 9): sem cache, sem `Referer` (o token está no caminho) e fora dos buscadores.
public static class TrackingHeaders
{
    public static void Apply(HttpResponse response)
    {
        response.Headers.CacheControl = "private, no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Robots-Tag"] = "noindex";
    }
}

/// Os cabeçalhos do acompanhamento valem para toda resposta de `/public/tracking`, inclusive `404`/`410`: o tratador de
/// exceções limpa os headers da resposta, e os callbacks de OnStarting rodam depois disso.
internal sealed class TrackingHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(PublicRoutes.Tracking, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.OnStarting(() =>
            {
                TrackingHeaders.Apply(context.Response);
                return Task.CompletedTask;
            });
        }

        return next(context);
    }
}
