using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.Modules.Ordering.Application;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Ordering.Infrastructure;
using AlmoSeis.Modules.Ordering.Jobs;
using AlmoSeis.Modules.Closing.Contracts;
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

        // Pedido da página: aceite, recusa, expiração e o fechamento (ADR-0046).
        services.AddOptions<WebOrderOptions>().BindConfiguration(WebOrderOptions.Section);
        services.AddScoped<WebOrderCustomerLinker>();
        services.AddScoped<IAwaitingWebOrdersRejector, AwaitingWebOrdersRejector>();
        services.AddSingleton<WebOrderExpiryJob>();
        services.AddHostedService(sp => sp.GetRequiredService<WebOrderExpiryJob>());

        // Página de pedidos: envio, acompanhamento e retenção (ADR-0044, ADR-0045, ADR-0047, ADR-0052).
        services.AddScoped<WebOrderSubmitter>();
        services.AddScoped<WebOrderWithdrawal>();
        services.AddScoped<TrackedOrderLoader>();
        services.AddScoped<ITrackingTokenResolver, TrackingTokenResolver>();
        services.AddSingleton<WebOrderRetentionJob>();
        services.AddHostedService(sp => sp.GetRequiredService<WebOrderRetentionJob>());
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
