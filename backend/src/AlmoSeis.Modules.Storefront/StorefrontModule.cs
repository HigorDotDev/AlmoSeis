using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Storefront.Application;
using AlmoSeis.Modules.Storefront.Contracts;
using AlmoSeis.Modules.Storefront.Infrastructure;
using AlmoSeis.Modules.Storefront.Infrastructure.Maps;
using AlmoSeis.Modules.Storefront.Jobs;
using AlmoSeis.SharedKernel.Application;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Modules.Storefront;

/// Página de pedidos: perfil público, slug, estado e cardápio da página (ADR-0044, ADR-0045) e a taxa de entrega por
/// distância (ADR-0048). Fica entre o Closing e o Ordering (07 §7.1): lê catálogo, cardápio, expediente e assinatura; o
/// Ordering o usa para o envio, a cotação e o acompanhamento.
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

        AddMaps(services);
        services.AddScoped<DeliveryQuoteCalculator>();
        services.AddScoped<GeocodeCacheWriter>();
        services.AddSingleton<DetachedTenantWork>();
        services.AddScoped<IDeliveryQuoteRedeemer, DeliveryQuoteRedeemer>();
        services.AddSingleton<DeliveryQuoteRetentionJob>();
        services.AddHostedService(sp => sp.GetRequiredService<DeliveryQuoteRetentionJob>());

        services.Scan(scan => scan
            .FromAssemblyOf<SlugWriter>()
            .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(IQueryHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo<ICurrentResourceReader>(), publicOnly: false).As<ICurrentResourceReader>().WithScopedLifetime());

        services.AddValidatorsFromAssemblyContaining<SlugWriter>(includeInternalTypes: true);
        return services;
    }

    /// `Maps:Provider` escolhe o par geocodificador + rota (ADR-0048 item 15). Os clientes HTTP não registram log de
    /// requisição: a URL leva o endereço do cliente e, no Geocoding do Google, a chave.
    private static void AddMaps(IServiceCollection services)
    {
        services.AddOptions<MapsOptions>().BindConfiguration(MapsOptions.Section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<MapsOptions>, MapsOptionsValidator>();
        services.AddSingleton<IMapsAvailability, MapsAvailability>();
        services.AddSingleton<NominatimThrottle>();
        services.AddSingleton<FakeMaps>();
        services.AddSingleton<NoMaps>();
        services.AddHttpClient<GoogleGeocoder>().RemoveAllLoggers();
        services.AddHttpClient<GoogleRouteDistance>().RemoveAllLoggers();
        services.AddHttpClient<NominatimGeocoder>().RemoveAllLoggers();
        services.AddHttpClient<OsrmRouteDistance>().RemoveAllLoggers();

        services.AddTransient<IGeocoder>(sp => sp.GetRequiredService<IOptions<MapsOptions>>().Value.Provider switch
        {
            MapsProvider.Google => sp.GetRequiredService<GoogleGeocoder>(),
            MapsProvider.Osm => sp.GetRequiredService<NominatimGeocoder>(),
            MapsProvider.Fake => sp.GetRequiredService<FakeMaps>(),
            _ => sp.GetRequiredService<NoMaps>(),
        });
        services.AddTransient<IRouteDistanceProvider>(sp => sp.GetRequiredService<IOptions<MapsOptions>>().Value.Provider switch
        {
            MapsProvider.Google => sp.GetRequiredService<GoogleRouteDistance>(),
            MapsProvider.Osm => sp.GetRequiredService<OsrmRouteDistance>(),
            MapsProvider.Fake => sp.GetRequiredService<FakeMaps>(),
            _ => sp.GetRequiredService<NoMaps>(),
        });
    }
}
