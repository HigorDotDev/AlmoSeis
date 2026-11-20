using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AlmoSeis.Modules.Storefront.Application;
using AlmoSeis.Modules.Storefront.Domain;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Modules.Storefront.Infrastructure.Maps;

/// `Maps:Provider = Fake` (desenvolvimento e testes). Sem rede e determinístico:
/// - o endereço vira um ponto a até `RadiusKm` de `Center*`, sorteado pelo SHA-256 do endereço normalizado;
/// - com número que tenha dígito, o resultado é exato; sem número (ou "S/N"), aproximado (a página pede revisão);
/// - rua com a palavra "inexistente" → não encontrado;
/// - distância = Haversine × `RouteFactor` (1,3), em metros.
internal sealed class FakeMaps(IOptions<MapsOptions> options) : IGeocoder, IRouteDistanceProvider
{
    private const double EarthRadiusKm = 6371.0088;
    private const double KmPerDegree = 111.32;

    public Task<GeocodeResult?> GeocodeAsync(GeocodeQuery query, CancellationToken cancellationToken)
    {
        var folded = Fold(query.Street);
        if (folded.Contains("inexistente", StringComparison.Ordinal))
        {
            return Task.FromResult<GeocodeResult?>(null);
        }

        var fake = options.Value.Fake;
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', folded, Fold(query.Number), Fold(query.Neighborhood), Fold(query.City), Fold(query.State))));
        var u1 = BitConverter.ToUInt32(seed, 0) / (double)uint.MaxValue;
        var u2 = BitConverter.ToUInt32(seed, 4) / (double)uint.MaxValue;
        var bearing = u1 * 2 * Math.PI;
        var distanceKm = Math.Sqrt(u2) * (double)fake.RadiusKm;

        var centerLatitude = (double)fake.CenterLatitude;
        var latitude = centerLatitude + distanceKm / KmPerDegree * Math.Cos(bearing);
        var longitude = (double)fake.CenterLongitude + distanceKm / (KmPerDegree * Math.Cos(centerLatitude * Math.PI / 180)) * Math.Sin(bearing);
        var point = new GeoPoint(Round(latitude), Round(longitude));

        var exact = query.Number?.Any(char.IsAsciiDigit) == true;
        var formatted = Format(query);
        return Task.FromResult<GeocodeResult?>(new GeocodeResult(point, exact ? GeocodePrecision.Exact : GeocodePrecision.Approximate, formatted));
    }

    public Task<int?> DrivingDistanceMetersAsync(GeoPoint origin, GeoPoint destination, CancellationToken cancellationToken) =>
        Task.FromResult<int?>((int)Math.Round(HaversineKm(origin, destination) * (double)options.Value.Fake.RouteFactor * 1000, MidpointRounding.AwayFromZero));

    public static double HaversineKm(GeoPoint a, GeoPoint b)
    {
        static double Radians(decimal degrees) => (double)degrees * Math.PI / 180;
        var dLat = Radians(b.Latitude - a.Latitude);
        var dLng = Radians(b.Longitude - a.Longitude);
        var h = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(Radians(a.Latitude)) * Math.Cos(Radians(b.Latitude)) * Math.Pow(Math.Sin(dLng / 2), 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private static decimal Round(double coordinate) => Math.Round((decimal)coordinate, 6, MidpointRounding.AwayFromZero);

    private static string Format(GeocodeQuery q)
    {
        var text = new StringBuilder(q.Street.Trim());
        if (!string.IsNullOrWhiteSpace(q.Number))
        {
            text.Append(", ").Append(q.Number.Trim());
        }

        if (!string.IsNullOrWhiteSpace(q.Neighborhood))
        {
            text.Append(" - ").Append(q.Neighborhood.Trim());
        }

        if (!string.IsNullOrWhiteSpace(q.City))
        {
            text.Append(", ").Append(q.City.Trim());
            if (!string.IsNullOrWhiteSpace(q.State))
            {
                text.Append(" - ").Append(q.State.Trim().ToUpperInvariant());
            }
        }

        return text.ToString();
    }

    private static string Fold(string? text) =>
        text is null
            ? string.Empty
            : new string(text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD)
                .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
}

/// `Maps:Provider = None`: nunca é chamado (a disponibilidade barra antes); se for, responde como provedor fora do ar.
internal sealed class NoMaps : IGeocoder, IRouteDistanceProvider
{
    public Task<GeocodeResult?> GeocodeAsync(GeocodeQuery query, CancellationToken cancellationToken) =>
        throw new MapsUnavailableException("None", "nenhum provedor configurado");

    public Task<int?> DrivingDistanceMetersAsync(GeoPoint origin, GeoPoint destination, CancellationToken cancellationToken) =>
        throw new MapsUnavailableException("None", "nenhum provedor configurado");
}
