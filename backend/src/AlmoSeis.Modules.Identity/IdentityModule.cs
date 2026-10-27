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

        services.Scan(scan => scan
            .FromAssemblyOf<AuthSessionIssuer>()
            .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(IQueryHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime());

        services.AddValidatorsFromAssemblyContaining<AuthSessionIssuer>(includeInternalTypes: true);
        return services;
    }
}
