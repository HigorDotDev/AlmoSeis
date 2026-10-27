using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Billing.Contracts;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AlmoSeis.Api.Infrastructure;

/// 07 §7.4 / ADR-0017 §6: escrita com assinatura SUSPENDED ou CANCELED → 402 SubscriptionInactive.
/// Fora do bloqueio: leituras, /auth, /admin e /webhooks. Suspenso ainda pode finalizar o fechamento em aberto.
internal sealed class SubscriptionWriteGuardFilter(ITenantContext tenant, ISubscriptionAccess subscriptions) : IAsyncActionFilter
{
    private static readonly string[] OpenPrefixes = ["/api/v1/auth", "/api/v1/admin", "/webhooks"];

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method)
            || OpenPrefixes.Any(p => request.Path.StartsWithSegments(p)) || tenant.TenantId is not { } tenantId)
        {
            await next();
            return;
        }

        var state = await subscriptions.GetAsync(tenantId, context.HttpContext.RequestAborted);
        var finalizingWhileSuspended = state.Status == "SUSPENDED" && request.Path.StartsWithSegments("/api/v1/closing/finalize");
        if (!state.CanWrite && !finalizingWhileSuspended)
        {
            throw new BusinessException(
                ErrorCodes.SubscriptionInactive,
                extensions: new Dictionary<string, object?> { ["subscriptionStatus"] = state.Status });
        }

        await next();
    }
}
