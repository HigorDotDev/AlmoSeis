using AlmoSeis.SharedKernel.Security;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AlmoSeis.Api.Infrastructure;

/// Escrita de cadastro de um módulo (ADR-0054 item 14): sem o módulo no tenant → `403 ModuleNotEnabled` com `module`.
/// Roda antes dos filtros globais (assinatura, validação) e da idempotência, para a resposta não depender do corpo.
/// Quando o módulo depende do corpo (o `orderType` de `POST /orders`), a checagem fica no handler, pelo mesmo
/// `ITenantModules`.
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequiresModuleAttribute(TenantModule module) : Attribute, IFilterFactory, IOrderedFilter
{
    public TenantModule Module { get; } = module;

    public bool IsReusable => true;

    /// Antes dos filtros globais (ordem 0) e dos de ação.
    public int Order => -1000;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) => new ModuleFilter(Module);

    private sealed class ModuleFilter(TenantModule module) : IAsyncActionFilter, IOrderedFilter
    {
        public int Order => -1000;

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var modules = context.HttpContext.RequestServices.GetRequiredService<ITenantModules>();
            await modules.EnsureAsync(module, context.HttpContext.RequestAborted);
            await next();
        }
    }
}
