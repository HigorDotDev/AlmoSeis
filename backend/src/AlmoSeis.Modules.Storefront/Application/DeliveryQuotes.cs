using System.Diagnostics.Metrics;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Storefront.Contracts;
using AlmoSeis.Modules.Storefront.Domain;
using AlmoSeis.Modules.Storefront.Infrastructure.Maps;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Modules.Storefront.Application;

/// Cálculo da cotação (ADR-0048 itens 4 a 13), sempre no servidor: geocodificação (cache por tenant ou provedor) → rota
/// do ponto confirmado do restaurante (cache ou provedor) → km com uma casa → raio → `max(round(km × preço/km), mínimo)`.
/// Não grava a cotação: quem chama decide (a rota pública grava na transação; o envio do pedido, à parte).
internal sealed class DeliveryQuoteCalculator(
    AppDbContext db,
    ITenantContext tenant,
    IGeocoder geocoder,
    IRouteDistanceProvider routes,
    IMapsAvailability maps,
    ITenantSettingsReader tenantSettings,
    GeocodeCacheWriter cache,
    IIdGenerator ids,
    IOptions<MapsOptions> options,
    TimeProvider time,
    ILogger<DeliveryQuoteCalculator> logger)
{
    /// Alerta operacional do ADR-0048 item 14 (mais de 5 em 10 min, 09-devops): `almoseis.maps.failures`.
    private static readonly Meter Meter = new("AlmoSeis.Maps");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("almoseis.maps.failures", description: "Falhas do provedor de mapas");

    public const string ProviderDownMessage = "Não conseguimos calcular a entrega agora. Você pode retirar no local.";

    /// `field`: prefixo dos erros de endereço ("address" na cotação, "deliveryAddress" no envio).
    public async Task<DeliveryQuote> QuoteAsync(StorefrontSettings settings, PublicAddressInput address, string field, CancellationToken cancellationToken)
    {
        if (!settings.IsDeliveryAvailableOnPage(maps.IsConfigured))
        {
            throw new BusinessException(ErrorCodes.DeliveryUnavailable);
        }

        var tenantId = tenant.TenantId!.Value;
        var key = DeliveryAddressKey.Create(tenantId, address, settings.AddressCity, settings.AddressState);
        var now = time.GetUtcNow();
        if (settings.DeliveryFeeMode == DeliveryFeeMode.Fixed)
        {
            var fixedFee = (await tenantSettings.GetAsync(cancellationToken)).DefaultDeliveryFee;
            return DeliveryQuote.ForFixed(ids.New(), tenantId, key, fixedFee, settings.Version, now);
        }

        var origin = settings.ConfirmedLocation!;
        var cached = await db.Set<GeocodeCacheEntry>().AsNoTracking()
            .SingleOrDefaultAsync(c => c.AddressHash == key.Hash && c.ExpiresAt > now, cancellationToken);

        GeoPoint destination;
        string formattedAddress;
        DateTimeOffset geocodedAt;
        if (cached is not null)
        {
            // O cache não guarda o texto (ADR-0052 item 5): o endereço mostrado é o digitado, que tem o mesmo hash.
            (destination, formattedAddress, geocodedAt) = (cached.Point, key.Display, cached.GeocodedAt);
        }
        else
        {
            var result = await CallAsync(() => geocoder.GeocodeAsync(GeocodeQuery.From(key), cancellationToken), "geocode");
            if (result is null)
            {
                throw BusinessException.Validation($"{field}.street", "Não encontramos esse endereço. Confira a rua, o número e o bairro.");
            }

            if (result.Precision != GeocodePrecision.Exact)
            {
                // Resultado aproximado nunca vira taxa (ADR-0048 item 9; RF-204).
                throw BusinessException.Validation($"{field}.number", "Não achamos esse número nessa rua. Confira o número e o bairro.");
            }

            (destination, formattedAddress, geocodedAt) = (result.Point, result.FormattedAddress, now);
        }

        var meters = cached?.CachedMeters(origin, now, RouteValidity);
        if (meters is null)
        {
            meters = await CallAsync(() => routes.DrivingDistanceMetersAsync(origin, destination, cancellationToken), "route");
            await cache.UpsertAsync(tenantId, key.Hash, destination, geocodedAt, meters is { } m ? (origin, m, now) : null, cancellationToken);
            if (meters is null)
            {
                throw new BusinessException(ErrorCodes.OutOfDeliveryArea);
            }
        }

        var quote = DeliveryQuote.ForDistance(
            ids.New(), tenantId, key, formattedAddress, destination, meters.Value, settings.PricePerKm!.Value, settings.MinimumDeliveryFee,
            settings.Version, now);
        if (!DeliveryFeeRule.WithinRadius(quote.DistanceKm!.Value, settings.MaxDeliveryDistanceKm))
        {
            throw new BusinessException(
                ErrorCodes.OutOfDeliveryArea,
                extensions: new Dictionary<string, object?>
                {
                    ["distanceKm"] = DeliveryFeeRule.FormatKm(quote.DistanceKm.Value),
                    ["maxDistanceKm"] = DeliveryFeeRule.FormatKm(settings.MaxDeliveryDistanceKm!.Value),
                });
        }

        return quote;
    }

    /// Endereço do restaurante (ADR-0048 item 3), geocodificado uma vez ao salvar. Falha ou não encontrado → nulo: o
    /// proprietário marca o ponto no mapa.
    public async Task<GeoPoint?> GeocodeStoreAsync(StorefrontSettings settings, CancellationToken cancellationToken)
    {
        if (!maps.IsConfigured || !settings.HasCompleteAddress)
        {
            return null;
        }

        try
        {
            var result = await CallAsync(
                () => geocoder.GeocodeAsync(
                    new GeocodeQuery(settings.AddressStreet!, settings.AddressNumber, settings.AddressNeighborhood, settings.AddressCity, settings.AddressState, settings.AddressZipCode),
                    cancellationToken),
                "geocode-store");
            return result?.Point;
        }
        catch (BusinessException)
        {
            return null;
        }
    }

    private TimeSpan RouteValidity => TimeSpan.FromDays(options.Value.RouteCacheDays);

    private async Task<T> CallAsync<T>(Func<Task<T>> call, string operation)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is MapsUnavailableException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Failures.Add(1, new KeyValuePair<string, object?>("provider", options.Value.Provider.ToString()), new KeyValuePair<string, object?>("operation", operation));
            // Nunca o endereço, a coordenada ou a chave (ADR-0052 item 7).
            logger.LogWarning("Provedor de mapas {Provider} falhou em {Operation}: {Reason}", options.Value.Provider, operation,
                ex is MapsUnavailableException maps ? maps.Reason : ex.GetType().Name);
            throw new BusinessException(ErrorCodes.DeliveryUnavailable, ProviderDownMessage);
        }
    }
}

/// Cache por tenant (ADR-0048 item 11), gravado **fora** da transação da requisição: uma cotação que termina em erro
/// (fora da área, por exemplo) não paga o provedor de novo na próxima tentativa. `INSERT … ON CONFLICT` porque duas
/// cotações simultâneas do mesmo endereço não podem abortar.
internal sealed class GeocodeCacheWriter(DetachedTenantWork detached, IOptions<MapsOptions> options)
{
    public Task UpsertAsync(
        Guid tenantId, string addressHash, GeoPoint point, DateTimeOffset geocodedAt, (GeoPoint Origin, int Meters, DateTimeOffset At)? route,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.GeocodeCacheDays == 0)
        {
            return Task.CompletedTask;
        }

        var expiresAt = geocodedAt + TimeSpan.FromDays(settings.GeocodeCacheDays);
        var routed = settings.RouteCacheDays > 0 ? route : null;
        decimal? originLatitude = routed?.Origin.Latitude, originLongitude = routed?.Origin.Longitude;
        int? meters = routed?.Meters;
        DateTimeOffset? routedAt = routed?.At;
        return detached.RunAsync(tenantId, (db, ct) => db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO geocode_cache_entries
                   (tenant_id, address_hash, latitude, longitude, geocoded_at, origin_latitude, origin_longitude, distance_meters, routed_at, expires_at)
            VALUES ({tenantId}, {addressHash}, {point.Latitude}, {point.Longitude}, {geocodedAt}, {originLatitude}, {originLongitude}, {meters}, {routedAt}, {expiresAt})
            ON CONFLICT (tenant_id, address_hash) DO UPDATE
               SET latitude = EXCLUDED.latitude, longitude = EXCLUDED.longitude, geocoded_at = EXCLUDED.geocoded_at,
                   origin_latitude = EXCLUDED.origin_latitude, origin_longitude = EXCLUDED.origin_longitude,
                   distance_meters = EXCLUDED.distance_meters, routed_at = EXCLUDED.routed_at, expires_at = EXCLUDED.expires_at
            """, ct), cancellationToken);
    }
}

/// Gravação no tenant da página numa conexão e transação próprias (escopo novo, mesmo tenant): cache e a cotação nova
/// do `409 DeliveryQuoteChanged`, que precisam sobreviver ao rollback da requisição.
internal sealed class DetachedTenantWork(IServiceScopeFactory scopes)
{
    public async Task RunAsync(Guid tenantId, Func<AppDbContext, CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().InitializeForPublic(tenantId, TenantSource.PublicSlug);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await work(db, cancellationToken);
    }
}

/// Conferência da cotação no envio do pedido (ADR-0048 item 10): o cliente nunca paga uma taxa que não viu.
internal sealed class DeliveryQuoteRedeemer(
    AppDbContext db,
    ITenantContext tenant,
    IMapsAvailability maps,
    DeliveryQuoteCalculator calculator,
    DetachedTenantWork detached,
    TimeProvider time) : IDeliveryQuoteRedeemer
{
    public async Task<RedeemedDeliveryQuote> RedeemAsync(Guid? quoteId, PublicAddressInput address, CancellationToken cancellationToken)
    {
        if (quoteId is not { } id)
        {
            throw BusinessException.Validation("deliveryQuoteId", "Calcule a taxa de entrega antes de enviar o pedido.");
        }

        // Query filter + RLS: a cotação de outro restaurante não existe aqui.
        var quote = await db.Set<DeliveryQuote>().AsNoTracking().SingleOrDefaultAsync(q => q.Id == id, cancellationToken)
            ?? throw BusinessException.Validation("deliveryQuoteId", "Esta cotação de entrega não vale mais. Calcule a entrega de novo.");

        var tenantId = tenant.TenantId!.Value;
        var settings = await db.Set<StorefrontSettings>().AsNoTracking().SingleAsync(s => s.Id == tenantId, cancellationToken);
        if (!settings.IsDeliveryAvailableOnPage(maps.IsConfigured))
        {
            throw new BusinessException(ErrorCodes.DeliveryUnavailable);
        }

        var key = DeliveryAddressKey.Create(tenantId, address, settings.AddressCity, settings.AddressState);
        if (quote.IsValidAt(time.GetUtcNow()) && quote.AddressHash == key.Hash && quote.SettingsVersion == settings.Version)
        {
            return quote.ToRedeemed();
        }

        // Vencida, de outro endereço ou de outra configuração: recalcula. Mesma taxa → segue com o cálculo novo.
        var fresh = await calculator.QuoteAsync(settings, address, "deliveryAddress", cancellationToken);
        if (fresh.DeliveryFee == quote.DeliveryFee)
        {
            return fresh.ToRedeemed();
        }

        // A cotação nova precisa existir depois do rollback deste envio: a página confirma e reenvia com ela.
        await detached.RunAsync(tenantId, async (context, ct) =>
        {
            context.Add(fresh);
            await context.SaveChangesAsync(ct);
        }, cancellationToken);
        throw new BusinessException(
            ErrorCodes.DeliveryQuoteChanged,
            $"A taxa de entrega agora é R$ {fresh.DeliveryFee.ToString().Replace('.', ',')}. Confira e envie de novo.",
            new Dictionary<string, object?> { ["quote"] = fresh.ToDto() });
    }
}
