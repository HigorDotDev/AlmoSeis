using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.Modules.Menu.Application;
using AlmoSeis.Modules.Menu.Contracts;
using AlmoSeis.Modules.Menu.Infrastructure;
using AlmoSeis.SharedKernel.Application;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace AlmoSeis.Modules.Menu;

public static class MenuModule
{
    public static IServiceCollection AddMenuModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleModelConfiguration, MenuModelConfiguration>();
        services.AddScoped<MenuDtoBuilder>();
        services.AddScoped<IProductUsageCheck, MenuProductUsageCheck>();
        services.AddScoped<IMenuMaterializer, MenuMaterializer>();

        services.Scan(scan => scan
            .FromAssemblyOf<MenuDtoBuilder>()
            .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(IQueryHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo<IIntegrationEventMapper>(), publicOnly: false).As<IIntegrationEventMapper>().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo<ICurrentResourceReader>(), publicOnly: false).As<ICurrentResourceReader>().WithScopedLifetime());

        services.AddValidatorsFromAssemblyContaining<MenuDtoBuilder>(includeInternalTypes: true);
        return services;
    }
}
