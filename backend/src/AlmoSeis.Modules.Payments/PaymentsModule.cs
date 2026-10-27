using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Payments.Application;
using AlmoSeis.Modules.Payments.Infrastructure;
using AlmoSeis.SharedKernel.Application;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace AlmoSeis.Modules.Payments;

public static class PaymentsModule
{
    /// Registrar depois do Ordering: substitui o `IOrderPaymentsReader` vazio de lá.
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleModelConfiguration, PaymentsModelConfiguration>();
        services.AddScoped<PaymentDtoBuilder>();
        services.AddScoped<IOrderPaymentsReader, OrderPaymentsReader>();

        services.Scan(scan => scan
            .FromAssemblyOf<PaymentDtoBuilder>()
            .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(IQueryHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime());

        services.AddValidatorsFromAssemblyContaining<PaymentDtoBuilder>(includeInternalTypes: true);
        return services;
    }
}
