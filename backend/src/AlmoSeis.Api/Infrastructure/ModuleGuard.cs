using AlmoSeis.SharedKernel.Security;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AlmoSeis.Api.Infrastructure;

/// Escrita de cadastro de um módulo (ADR-0054 item 14): sem o módulo no tenant → `403 ModuleNotEnabled` com `module`.
/// Roda antes dos filtros globais (assinatura, validação) e da idempotência, para a resposta não depender do corpo.
/// Quando o módulo depende do corpo (o `orderType` de `POST /orders`), a checagem fica no handler, pelo mesmo
/// `ITenantModules`.
///
/// Com mais de um módulo, basta **um** deles (o catálogo de produtos serve a `MARMITA`, `PF`, `KILO` e `DAILY_MENU`);
/// sem nenhum, o `403` leva o primeiro da lista em `module` (06 §6.5).
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequiresModuleAttribute : Attribute, IFilterFactory, IOrderedFilter
{
    public RequiresModuleAttribute(TenantModule module, params TenantModule[] alternatives)
    {
        Modules = [module, .. alternatives];
    }

    public IReadOnlyList<TenantModule> Modules { get; }

    public bool IsReusable => true;

    /// Antes dos filtros globais (ordem 0) e dos de ação.
    public int Order => -1000;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) => new ModuleFilter(Modules);

    private sealed class ModuleFilter(IReadOnlyList<TenantModule> modules) : IAsyncActionFilter, IOrderedFilter
    {
        public int Order => -1000;

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var tenantModules = context.HttpContext.RequestServices.GetRequiredService<ITenantModules>();
            var enabled = await tenantModules.GetAsync(context.HttpContext.RequestAborted);
            if (!modules.Any(enabled.Contains))
            {
                throw ITenantModules.ModuleNotEnabled(modules[0]);
            }

            await next();
        }
    }
}
