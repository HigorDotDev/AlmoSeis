using System.Security.Claims;
using AlmoSeis.Infrastructure.Security;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.SharedKernel.Errors;
using AlmoSeis.SharedKernel.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AlmoSeis.Api.Infrastructure;

public static class AuthenticationSetup
{
    public static IServiceCollection AddAlmoSeisAuthentication(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddScoped<IPermissionChecker, HttpPermissionChecker>();
        services.AddScoped<EmployeeAccessCache>();
        services.AddScoped<IEmployeeAccessCache>(sp => sp.GetRequiredService<EmployeeAccessCache>());
        services.AddScoped<IEmployeeAccessInvalidator>(sp => sp.GetRequiredService<EmployeeAccessCache>());

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IJwtTokenIssuer, IOptions<JwtOptions>>((options, issuer, jwt) =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Value.Issuer,
                    ValidAudience = jwt.Value.Audience,
                    IssuerSigningKey = issuer.ValidationKey,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = AlmoSeisClaims.Subject,
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = EmployeeAccessValidator.ValidateAsync,
                    OnMessageReceived = ctx =>
                    {
                        // SignalR envia o token na query string (06 §6.14).
                        if (ctx.Request.Path.StartsWithSegments("/hubs") && ctx.Request.Query.TryGetValue("access_token", out var token))
                        {
                            ctx.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemDetailsAuthorizationResultHandler>();
        services.AddScoped<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
        services.AddAuthorization(o => o.AddPolicy(PlatformAdminRequirement.Policy, p => p.RequireAuthenticatedUser().AddRequirements(new PlatformAdminRequirement())));
        return services;
    }
}

/// Funcionário desativado ou com perfis alterados perde o acesso em até 30 s (ADR-0027).
public interface IEmployeeAccessCache
{
    Task<EmployeeAccess?> GetAsync(Guid employeeId, CancellationToken cancellationToken);

    void Invalidate(Guid employeeId);
}

internal sealed class EmployeeAccessCache(IMemoryCache cache, IEmployeeAccessReader reader) : IEmployeeAccessCache, IEmployeeAccessInvalidator
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public async Task<EmployeeAccess?> GetAsync(Guid employeeId, CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync(Key(employeeId), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            return reader.GetAsync(employeeId, cancellationToken);
        });

    public void Invalidate(Guid employeeId) => cache.Remove(Key(employeeId));

    private static string Key(Guid employeeId) => $"employee-access:{employeeId}";
}

internal static class EmployeeAccessValidator
{
    public const string ItemKey = "almoseis.employee-access";

    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        var principal = context.Principal!;
        var userId = principal.GetGuid(AlmoSeisClaims.Subject);
        var tenantId = principal.GetGuid(AlmoSeisClaims.TenantId);
        var employeeId = principal.GetGuid(AlmoSeisClaims.EmployeeId);
        if (tenantId is null && principal.FindFirstValue(AlmoSeisClaims.PlatformAdmin) == "true")
        {
            await ValidatePlatformAdminAsync(context, userId, principal.FindFirstValue(AlmoSeisClaims.SecurityStamp));
            return;
        }

        var stamp = principal.GetGuid(AlmoSeisClaims.SecurityStamp);
        if (userId is null || tenantId is null || employeeId is null || stamp is null)
        {
            context.Fail("Token sem tenant/funcionário.");
            return;
        }

        // O tenant vem só do token (ADR-0026); a partir daqui toda conexão da requisição usa a RLS dele.
        var services = context.HttpContext.RequestServices;
        services.GetRequiredService<ITenantContext>().Initialize(
            tenantId.Value, userId.Value, employeeId.Value,
            principal.GetGuid(AlmoSeisClaims.SharedDeviceId),
            principal.FindFirstValue(AlmoSeisClaims.InstallationId));

        var access = await services.GetRequiredService<IEmployeeAccessCache>().GetAsync(employeeId.Value, context.HttpContext.RequestAborted);
        if (access is null || !access.IsActive || access.SecurityStamp != stamp || access.TenantId != tenantId)
        {
            context.Fail("Acesso revogado.");
            return;
        }

        context.HttpContext.Items[ItemKey] = access;
    }

    /// ADR-0043: token sem tenant só vale em /api/v1/admin e só enquanto o usuário for admin com o mesmo stamp.
    private static async Task ValidatePlatformAdminAsync(TokenValidatedContext context, Guid? userId, string? stamp)
    {
        if (userId is null || stamp is null || !context.HttpContext.Request.Path.StartsWithSegments("/api/v1/admin"))
        {
            context.Fail("Token de administrador fora das rotas de administração.");
            return;
        }

        var check = context.HttpContext.RequestServices.GetRequiredService<IPlatformAdminCheck>();
        if (!await check.IsValidAsync(userId.Value, stamp, context.HttpContext.RequestAborted))
        {
            context.Fail("Administrador revogado.");
            return;
        }

        context.HttpContext.Items[PlatformAdminItemKey] = userId.Value;
    }

    public const string PlatformAdminItemKey = "almoseis.platform-admin";
}

/// [PlatformAdmin]: só tokens de `POST /auth/admin/login` (ADR-0043).
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class PlatformAdminAttribute() : AuthorizeAttribute(PlatformAdminRequirement.Policy);

internal sealed class PlatformAdminRequirement : IAuthorizationRequirement
{
    public const string Policy = "PlatformAdmin";
}

internal sealed class PlatformAdminAuthorizationHandler(IHttpContextAccessor accessor) : AuthorizationHandler<PlatformAdminRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PlatformAdminRequirement requirement)
    {
        if (accessor.HttpContext?.Items[EmployeeAccessValidator.PlatformAdminItemKey] is Guid)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// Mesmas permissões do token validado, para regras que dependem do valor enviado.
internal sealed class HttpPermissionChecker(IHttpContextAccessor accessor) : IPermissionChecker
{
    public bool Has(string permission) =>
        accessor.HttpContext?.Items[EmployeeAccessValidator.ItemKey] is EmployeeAccess access && access.Permissions.Contains(permission);
}

/// [HasPermission("orders.create")] → política "perm:orders.create" (07 §7.4).
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class HasPermissionAttribute(string permission) : AuthorizeAttribute(PermissionPolicyProvider.Prefix + permission);

internal sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

internal sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public const string Prefix = "perm:";

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) =>
        policyName.StartsWith(Prefix, StringComparison.Ordinal)
            ? new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(policyName[Prefix.Length..])).Build()
            : await base.GetPolicyAsync(policyName);
}

internal sealed class PermissionAuthorizationHandler(IHttpContextAccessor accessor) : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (accessor.HttpContext?.Items[EmployeeAccessValidator.ItemKey] is EmployeeAccess access && access.Permissions.Contains(requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// 401/403 no formato de 06 §6.1 (Unauthenticated / PermissionDenied com a permissão que faltou).
internal sealed class ProblemDetailsAuthorizationResultHandler(IProblemDetailsService problemDetails) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Succeeded)
        {
            await _default.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        var (code, extensions) = authorizeResult.Challenged
            ? (ErrorCodes.Unauthenticated, new Dictionary<string, object?>())
            : (ErrorCodes.PermissionDenied, new Dictionary<string, object?>
            {
                ["permission"] = policy.Requirements.OfType<PermissionRequirement>().FirstOrDefault()?.Permission,
            });

        context.Response.StatusCode = code.HttpStatus;
        var problem = new ProblemDetails { Type = code.TypeUri, Title = code.Title, Status = code.HttpStatus, Instance = context.Request.Path };
        problem.Extensions["errorCode"] = code.Code;
        problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier;
        foreach (var (key, value) in extensions)
        {
            problem.Extensions[key] = value;
        }

        await problemDetails.WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
    }
}
