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
                Limit(http => Starts(http, "/webhooks/asaas") ? $"webhook:{Ip(http)}" : null, 120, TimeSpan.FromMinutes(1)));

            o.OnRejected = async (context, cancellationToken) =>
            {
                var http = context.HttpContext;
                var retry = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : TimeSpan.FromSeconds(60);
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
            };
        });
        return services;
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

    private static bool IsPin(HttpContext http) =>
        Starts(http, "/api/v1/auth/pin-login")
        || (Starts(http, "/api/v1/devices/shared") && http.Request.Path.Value!.EndsWith("/employees", StringComparison.OrdinalIgnoreCase));

    private static string? InstallationFromPath(HttpContext http) =>
        http.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries) is [.., var installation, "employees"] ? installation : null;

    private static bool Starts(HttpContext http, string prefix) => http.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase);

    private static string Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
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
