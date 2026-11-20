using AlmoSeis.Modules.Storefront.Domain;

namespace AlmoSeis.Modules.Storefront.Application;

/// Precisão do resultado do geocodificador (ADR-0048 item 9). Só `Exact` (o número da casa) vira taxa.
internal enum GeocodePrecision
{
    /// Número da casa (telhado ou interpolado na quadra).
    Exact,

    /// Só a rua, o bairro ou a cidade.
    Approximate,
}

/// Endereço como vai para o provedor: só o endereço, nunca nome ou telefone (ADR-0052 item 8).
internal sealed record GeocodeQuery(string Street, string? Number, string? Neighborhood, string? City, string? State, string? ZipCode)
{
    public static GeocodeQuery From(DeliveryAddressKey key) => new(key.Street, key.Number, key.Neighborhood, key.City, key.State, key.ZipCode);
}

internal sealed record GeocodeResult(GeoPoint Point, GeocodePrecision Precision, string FormattedAddress);

/// Endereço → coordenada (ADR-0048 item 15). Nulo = não encontrado. Falha, tempo esgotado (3 s) ou cota estourada →
/// `MapsUnavailableException`. Não expõe tipos do provedor.
internal interface IGeocoder
{
    Task<GeocodeResult?> GeocodeAsync(GeocodeQuery query, CancellationToken cancellationToken);
}

/// Distância de carro, sem trânsito (ADR-0048 item 4), em metros. Nulo = não há rota. Falha → `MapsUnavailableException`.
internal interface IRouteDistanceProvider
{
    Task<int?> DrivingDistanceMetersAsync(GeoPoint origin, GeoPoint destination, CancellationToken cancellationToken);
}

/// Provedor fora do ar, lento ou sem cota (ADR-0048 item 13). A mensagem nunca leva o endereço.
internal sealed class MapsUnavailableException(string provider, string reason, Exception? inner = null)
    : Exception($"Provedor de mapas {provider} indisponível: {reason}.", inner)
{
    public string Provider { get; } = provider;

    public string Reason { get; } = reason;
}

/// Provedor escolhido por `Maps:Provider`.
internal interface IMapsAvailability
{
    /// Há provedor (`Fake`, `Osm` ou `Google`). `None` → a página não oferece entrega por distância.
    bool IsConfigured { get; }

    /// O endereço sai da API para um terceiro (`Osm`, `Google`): entra `MAPS` no aviso de privacidade (ADR-0052 item 1).
    bool IsExternal { get; }
}
