using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Modules.Closing.Application;
using AlmoSeis.Modules.Closing.Contracts;
using AlmoSeis.Modules.Closing.Infrastructure;
using AlmoSeis.Modules.Menu.Contracts;
using AlmoSeis.SharedKernel.Application;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace AlmoSeis.Modules.Closing;

public static class ClosingModule
{
    public static IServiceCollection AddClosingModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleModelConfiguration, ClosingModelConfiguration>();
        services.AddScoped<BusinessDayDtoBuilder>();
        services.AddScoped<ClosingDtoBuilder>();
        services.AddScoped<ClosingInputsQuery>();
        services.AddScoped<IBusinessDayReader, BusinessDayReader>();
        services.AddScoped<IOpenBusinessDate, OpenBusinessDate>();

        services.Scan(scan => scan
            .FromAssemblyOf<BusinessDayDtoBuilder>()
            .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(IQueryHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo<IIntegrationEventMapper>(), publicOnly: false).As<IIntegrationEventMapper>().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo<ICurrentResourceReader>(), publicOnly: false).As<ICurrentResourceReader>().WithScopedLifetime());

        services.AddValidatorsFromAssemblyContaining<BusinessDayDtoBuilder>(includeInternalTypes: true);
        return services;
    }
}
