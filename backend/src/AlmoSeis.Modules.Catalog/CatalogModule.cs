using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.Modules.Catalog.Infrastructure;
using AlmoSeis.SharedKernel.Application;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace AlmoSeis.Modules.Catalog;

public static class CatalogModule
{
    public static IServiceCollection AddCatalogModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleModelConfiguration, CatalogModelConfiguration>();
        services.AddScoped<ICatalogReader, CatalogReader>();
        services.AddSingleton<AlmoSeis.Infrastructure.Realtime.IOutboxConsumer, ProductImageCleanup>();

        // Cardápio fixo (ADR-0056): carregamento único, porta de leitura para o pedido (fatia 3) e limpeza das imagens.
        services.AddScoped<AlmoSeis.Modules.Catalog.Application.FixedMenuLoader>();
        services.AddScoped<IFixedMenuReader, AlmoSeis.Modules.Catalog.Application.FixedMenuReader>();
        services.AddSingleton<AlmoSeis.Infrastructure.Realtime.IOutboxConsumer, FixedMenuImageCleanup>();

        services.Scan(scan => scan
            .FromAssemblyOf<CatalogReader>()
            .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(IQueryHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo<IIntegrationEventMapper>(), publicOnly: false).As<IIntegrationEventMapper>().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo<ICurrentResourceReader>(), publicOnly: false).As<ICurrentResourceReader>().WithScopedLifetime());

        services.AddValidatorsFromAssemblyContaining<CatalogReader>(includeInternalTypes: true);
        return services;
    }
}
