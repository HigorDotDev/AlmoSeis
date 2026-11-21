using System.Text.Json;
using System.Threading.RateLimiting;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Api.Infrastructure;

public sealed class AppVersionOptions
{
    public const string Section = "App";

    public Dictionary<string, string> MinSupportedAppVersion { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> StoreUrl { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// ADR-0035: `X-App-Version` abaixo da mínima da plataforma → 426 AppVersionUnsupported. Sem o header (ferramentas,
/// webhooks), a requisição passa. O hub recebe a versão pela query `appVersion` (06 §6.14).
internal sealed class AppVersionMiddleware(RequestDelegate next, IOptionsMonitor<AppVersionOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!request.Path.StartsWithSegments("/health") && !request.Path.StartsWithSegments("/webhooks"))
        {
            var version = request.Headers["X-App-Version"].FirstOrDefault() ?? request.Query["appVersion"].FirstOrDefault();
            var platform = request.Headers["X-App-Platform"].FirstOrDefault() ?? request.Query["appPlatform"].FirstOrDefault();
            if (version is not null && platform is not null
                && options.CurrentValue.MinSupportedAppVersion.TryGetValue(platform, out var minimum)
                && Version.TryParse(minimum, out var min)
                && (!Version.TryParse(version, out var current) || current < min))
            {
                throw new BusinessException(
                    ErrorCodes.AppVersionUnsupported,
                    extensions: new Dictionary<string, object?>
                    {
                        ["minVersion"] = minimum,
                        ["storeUrl"] = options.CurrentValue.StoreUrl.GetValueOrDefault(platform),
                    });
            }
        }

        await next(context);
    }
}

public sealed class RateLimitSettings
{
    public const string Section = "RateLimiting";

    public bool Enabled { get; set; } = true;

    /// Cotações de entrega por **estabelecimento** e dia (ADR-0045 item 11), contadas depois de o slug virar tenant
    /// (`PublicStoreQuotaMiddleware`): slug antigo, maiúsculas e slug inexistente não abrem cotas novas.
    public int PublicQuotesPerStorePerDay { get; set; } = 300;
}

/// ADR-0041: limites dos endpoints públicos, em memória, por réplica, com janela deslizante. Endpoints autenticados
/// não têm limite no MVP. Um limitador global encadeado: cada elo só limita as rotas da sua política (as outras
/// recebem "sem limite"), e o particionamento do ASP.NET Core descarta as partições ociosas.
internal static class RateLimitPolicies
{
    /// Chaves do corpo usadas pelos limites por login e por instalação (lidas antes do limiter).
    public const string LoginKey = "rate-limit:login";
    public const string InstallationKey = "rate-limit:installation";

    private static readonly string[] AuthPaths = ["/api/v1/auth/login", "/api/v1/auth/admin", "/api/v1/auth/signup", "/api/v1/auth/refresh", "/api/v1/auth/password"];

    public static IServiceCollection AddAlmoSeisRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(o =>
        {
            o.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                Limit(http => IsAuth(http) ? $"auth-ip:{Ip(http)}" : null, 10, TimeSpan.FromMinutes(1)),
                Limit(http => IsAuth(http) && http.Items[LoginKey] is string login ? $"auth-login:{login}" : null, 5, TimeSpan.FromMinutes(1)),
                Limit(http => IsPin(http) ? $"pin:{http.Items[InstallationKey] as string ?? InstallationFromPath(http) ?? Ip(http)}" : null, 30, TimeSpan.FromMinutes(1)),
                Limit(http => Starts(http, "/api/v1/invitations") ? $"inv-min:{Ip(http)}" : null, 10, TimeSpan.FromMinutes(1)),
                Limit(http => Starts(http, "/api/v1/invitations") ? $"inv-day:{Ip(http)}" : null, 50, TimeSpan.FromDays(1)),
                Limit(http => Starts(http, "/webhooks/asaas") ? $"webhook:{Ip(http)}" : null, 120, TimeSpan.FromMinutes(1)),
                // Página de pedidos (ADR-0045 item 11). O servidor da página (X-Web-Renderer-Key) fica fora do limite de leitura.
                Limit(http => IsPublicRead(http) && !PublicRoutes.IsRenderer(http) ? $"public-read:{Ip(http)}" : null, 120, TimeSpan.FromMinutes(1)),
                Limit(http => IsPublicQuote(http) ? $"public-quote-min:{Ip(http)}" : null, 10, TimeSpan.FromMinutes(1)),
                Limit(http => IsPublicQuote(http) ? $"public-quote-day:{Ip(http)}" : null, 60, TimeSpan.FromDays(1)),
                Limit(http => IsPublicOrder(http) ? $"public-order-min:{Ip(http)}" : null, 5, TimeSpan.FromMinutes(1)),
                Limit(http => IsPublicOrder(http) ? $"public-order-hour:{Ip(http)}" : null, 20, TimeSpan.FromHours(1)),
                Limit(http => IsPublicTracking(http) ? $"public-tracking:{Ip(http)}" : null, 60, TimeSpan.FromMinutes(1)));

            o.OnRejected = (context, cancellationToken) => WriteTooManyRequestsAsync(context.HttpContext, context.Lease);
        });
        services.AddSingleton<PublicStoreQuotaLimiter>();
        return services;
    }

    /// `429 TooManyRequests` com `Retry-After` (do limitador; sem a informação, 60 s).
    public static async ValueTask WriteTooManyRequestsAsync(HttpContext http, RateLimitLease lease)
    {
        var retry = lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : TimeSpan.FromSeconds(60);
        http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.StatusCode = ErrorCodes.TooManyRequests.HttpStatus;
        var problem = new ProblemDetails
        {
            Type = ErrorCodes.TooManyRequests.TypeUri,
            Title = ErrorCodes.TooManyRequests.Title,
            Status = ErrorCodes.TooManyRequests.HttpStatus,
            Instance = http.Request.Path,
        };
        problem.Extensions["errorCode"] = ErrorCodes.TooManyRequests.Code;
        problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? http.TraceIdentifier;
        await http.RequestServices.GetRequiredService<IProblemDetailsService>()
            .WriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem });
    }

    /// Chave nula = a rota não é desta política.
    private static PartitionedRateLimiter<HttpContext> Limit(Func<HttpContext, string?> key, int permits, TimeSpan window) =>
        PartitionedRateLimiter.Create<HttpContext, string>(http =>
        {
            var partition = http.RequestServices.GetRequiredService<IOptionsMonitor<RateLimitSettings>>().CurrentValue.Enabled ? key(http) : null;
            return partition is null
                ? RateLimitPartition.GetNoLimiter(string.Empty)
                : RateLimitPartition.GetSlidingWindowLimiter(partition, _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = permits,
                    Window = window,
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                });
        });

    private static bool IsAuth(HttpContext http) => AuthPaths.Any(p => Starts(http, p));

    private static bool IsPublicRead(HttpContext http) => HttpMethods.IsGet(http.Request.Method) && Starts(http, PublicRoutes.Stores);

    public static bool IsPublicQuote(HttpContext http) =>
        HttpMethods.IsPost(http.Request.Method) && Starts(http, PublicRoutes.Stores) && Ends(http, "/delivery-quotes");

    private static bool IsPublicOrder(HttpContext http) =>
        HttpMethods.IsPost(http.Request.Method)
        && ((Starts(http, PublicRoutes.Stores) && Ends(http, "/orders")) || (Starts(http, PublicRoutes.Tracking) && Ends(http, "/withdraw")));

    private static bool IsPublicTracking(HttpContext http) => HttpMethods.IsGet(http.Request.Method) && Starts(http, PublicRoutes.Tracking);

    private static bool Ends(HttpContext http, string suffix) =>
        http.Request.Path.Value!.TrimEnd('/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    private static bool IsPin(HttpContext http) =>
        Starts(http, "/api/v1/auth/pin-login")
        || Starts(http, "/api/v1/auth/shared-device/employees")
        || (Starts(http, "/api/v1/devices/shared") && http.Request.Path.Value!.EndsWith("/employees", StringComparison.OrdinalIgnoreCase));

    /// Mobile: `/devices/shared/{installationId}/employees`; web: `/auth/shared-device/employees?installationId=` (ADR-0053).
    private static string? InstallationFromPath(HttpContext http) =>
        Starts(http, "/api/v1/auth/shared-device/employees")
            ? http.Request.Query["installationId"].FirstOrDefault()
            : http.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries) is [.., var installation, "employees"] ? installation : null;

    private static bool Starts(HttpContext http, string prefix) => http.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase);

    private static string Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

/// Cota diária de cotações por estabelecimento (ADR-0045 item 11), em memória e por réplica como os outros limites
/// (ADR-0041). Partição pelo tenant já resolvido.
internal sealed class PublicStoreQuotaLimiter(IOptionsMonitor<RateLimitSettings> settings) : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> _limiter = PartitionedRateLimiter.Create<Guid, Guid>(tenantId =>
        RateLimitPartition.GetSlidingWindowLimiter(tenantId, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = settings.CurrentValue.PublicQuotesPerStorePerDay,
            Window = TimeSpan.FromDays(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0,
        }));

    public RateLimitLease Acquire(Guid tenantId) => _limiter.AttemptAcquire(tenantId);

    public void Dispose() => _limiter.Dispose();
}

/// Depois do `PublicTenantMiddleware`: a cota da cotação conta por tenant, não pelo texto do slug (07 §7.2).
internal sealed class PublicStoreQuotaMiddleware(RequestDelegate next, PublicStoreQuotaLimiter limiter, IOptionsMonitor<RateLimitSettings> settings)
{
    public async Task InvokeAsync(HttpContext context, AlmoSeis.Infrastructure.Tenancy.ITenantContext tenant)
    {
        if (settings.CurrentValue.Enabled && RateLimitPolicies.IsPublicQuote(context) && tenant.TenantId is { } tenantId)
        {
            using var lease = limiter.Acquire(tenantId);
            if (!lease.IsAcquired)
            {
                await RateLimitPolicies.WriteTooManyRequestsAsync(context, lease);
                return;
            }
        }

        await next(context);
    }
}

/// Lê `login`/`installationId` do corpo JSON das rotas públicas limitadas, antes do rate limiter (ADR-0041:
/// "5/min por login", "30/min por installationId"). O corpo volta ao início para o model binding.
internal sealed class RateLimitKeyMiddleware(RequestDelegate next)
{
    private static readonly string[] BodyPaths = ["/api/v1/auth/login", "/api/v1/auth/admin/login", "/api/v1/auth/pin-login"];

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (HttpMethods.IsPost(request.Method) && BodyPaths.Any(p => request.Path.Equals(p, StringComparison.OrdinalIgnoreCase))
            && request.ContentLength is > 0 and < 16_384)
        {
            request.EnableBuffering();
            try
            {
                using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: context.RequestAborted);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("login", out var login) && login.ValueKind == JsonValueKind.String)
                    {
                        context.Items[RateLimitPolicies.LoginKey] = login.GetString()!.Trim().ToUpperInvariant();
                    }

                    if (doc.RootElement.TryGetProperty("installationId", out var installation) && installation.ValueKind == JsonValueKind.String)
                    {
                        context.Items[RateLimitPolicies.InstallationKey] = installation.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // Corpo inválido: o model binding responde 400 depois.
            }
            finally
            {
                request.Body.Position = 0;
            }
        }

        await next(context);
    }
}
