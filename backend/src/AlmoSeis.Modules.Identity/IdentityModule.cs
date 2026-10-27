using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Modules.Identity.Application;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Identity.Domain;
using AlmoSeis.Modules.Identity.Infrastructure;
using AlmoSeis.SharedKernel.Application;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace AlmoSeis.Modules.Identity;

public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleModelConfiguration, IdentityModelConfiguration>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddScoped<AuthSessionIssuer>();
        services.AddScoped<IEmployeeAccessReader, EmployeeAccessReader>();
        services.AddScoped<IEmployeeDirectory, EmployeeDirectory>();
        services.AddScoped<ITenantCalendar, TenantCalendar>();
        services.AddScoped<ITenantSettingsReader, TenantSettingsReader>();
        services.AddOptions<InvitationOptions>().BindConfiguration(InvitationOptions.Section);
        services.AddScoped<InvitationLookup>();
        services.AddScoped<InvitationWriter>();
        services.AddScoped<ManagementDtos>();
        services.AddScoped<EmployeeAccessRevoker>();

        services.Scan(scan => scan
            .FromAssemblyOf<AuthSessionIssuer>()
            .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(IQueryHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo<IIntegrationEventMapper>(), publicOnly: false).As<IIntegrationEventMapper>().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo<ICurrentResourceReader>(), publicOnly: false).As<ICurrentResourceReader>().WithScopedLifetime());

        services.AddValidatorsFromAssemblyContaining<AuthSessionIssuer>(includeInternalTypes: true);
        return services;
    }
}
