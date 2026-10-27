using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.Modules.Ordering.Application;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Ordering.Infrastructure;
using AlmoSeis.SharedKernel.Application;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AlmoSeis.Modules.Ordering;

public static class OrderingModule
{
    public static IServiceCollection AddOrderingModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleModelConfiguration, OrderingModelConfiguration>();
        services.AddScoped<OrderItemResolver>();
        services.AddScoped<CustomerResolver>();
        services.AddScoped<OrderNumberGenerator>();
        services.AddScoped<OrderDtoBuilder>();
        services.AddScoped<CustomerDtoBuilder>();
        services.AddScoped<AlertCounter>();
        services.AddScoped<IProductUsageCheck, OrderProductUsageCheck>();
        services.AddScoped<IOrderPaymentPort, OrderPaymentPort>();
        // O Payments registra a implementação real; até lá, pedidos sem pagamentos.
        services.TryAddScoped<IOrderPaymentsReader, NoOrderPayments>();

        services.Scan(scan => scan
            .FromAssemblyOf<OrderDtoBuilder>()
            .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(IQueryHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo<IIntegrationEventMapper>(), publicOnly: false).As<IIntegrationEventMapper>().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo<ICurrentResourceReader>(), publicOnly: false).As<ICurrentResourceReader>().WithScopedLifetime());

        services.AddValidatorsFromAssemblyContaining<OrderDtoBuilder>(includeInternalTypes: true);
        return services;
    }
}
