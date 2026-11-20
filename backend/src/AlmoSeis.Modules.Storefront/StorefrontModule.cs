using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Storefront.Application;
using AlmoSeis.Modules.Storefront.Contracts;
using AlmoSeis.Modules.Storefront.Infrastructure;
using AlmoSeis.SharedKernel.Application;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace AlmoSeis.Modules.Storefront;

/// Página de pedidos: perfil público, slug, estado e cardápio da página (ADR-0044, ADR-0045). Fica entre o Closing e o
/// Ordering (07 §7.1): lê catálogo, cardápio, expediente e assinatura; o Ordering o usa para o envio e o acompanhamento.
public static class StorefrontModule
{
    public static IServiceCollection AddStorefrontModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleModelConfiguration, StorefrontModelConfiguration>();
        services.AddScoped<SlugWriter>();
        services.AddScoped<StorefrontStatusCalculator>();
        services.AddScoped<PublicMenuBuilder>();
        services.AddScoped<IStorefrontGate, StorefrontGate>();
        services.AddScoped<IStoreSlugResolver, StoreSlugResolver>();
        services.AddScoped<ITenantCreatedListener, StorefrontProvisioner>();

        services.Scan(scan => scan
            .FromAssemblyOf<SlugWriter>()
            .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(IQueryHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo<ICurrentResourceReader>(), publicOnly: false).As<ICurrentResourceReader>().WithScopedLifetime());

        services.AddValidatorsFromAssemblyContaining<SlugWriter>(includeInternalTypes: true);
        return services;
    }
}
