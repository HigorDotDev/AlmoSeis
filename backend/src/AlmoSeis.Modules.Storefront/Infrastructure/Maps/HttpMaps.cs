using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AlmoSeis.Modules.Storefront.Application;
using AlmoSeis.Modules.Storefront.Domain;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Modules.Storefront.Infrastructure.Maps;

/// Chamada HTTP a um provedor de mapas com o tempo máximo de `Maps:TimeoutSeconds` (ADR-0048 item 13). Erro de rede,
/// tempo esgotado, HTTP fora de 2xx ou JSON inválido → `MapsUnavailableException`. Os clientes são registrados sem os
/// loggers do `IHttpClientFactory`: a URL leva o endereço (e, no Geocoding do Google, a chave) e nunca vai para log
/// (ADR-0052 item 7).
internal static class MapsHttp
{
    public static async Task<JsonElement> SendAsync(
        HttpClient http, string provider, Func<HttpRequestMessage> request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            using var message = request();
            using var response = await http.SendAsync(message, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new MapsUnavailableException(provider, "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            }

            return await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            throw new MapsUnavailableException(provider, ex is TaskCanceledException ? "tempo esgotado" : ex.GetType().Name, ex);
        }
    }

    public static decimal Coordinate(JsonElement value) =>
        Math.Round(value.ValueKind == JsonValueKind.String
            ? decimal.Parse(value.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture)
            : value.GetDecimal(), 6, MidpointRounding.AwayFromZero);

    public static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static string AddressLine(GeocodeQuery q)
    {
        var parts = new List<string>();
        var street = string.IsNullOrWhiteSpace(q.Number) ? q.Street.Trim() : $"{q.Street.Trim()}, {q.Number.Trim()}";
        parts.Add(street);
        foreach (var part in new[] { q.Neighborhood, q.City, q.State, q.ZipCode })
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                parts.Add(part.Trim());
            }
        }

        parts.Add("Brasil");
        return string.Join(", ", parts);
    }
}

/// Google Geocoding API (ADR-0048 item 16). Exato = `ROOFTOP` ou `RANGE_INTERPOLATED` num resultado do tipo
/// `street_address`, `premise` ou `subpremise`; o resto (rua, bairro, cidade) é aproximado.
internal sealed class GoogleGeocoder(HttpClient http, IOptions<MapsOptions> options) : IGeocoder
{
    public const string Provider = "Google";

    private static readonly string[] ExactTypes = ["street_address", "premise", "subpremise"];

    public async Task<GeocodeResult?> GeocodeAsync(GeocodeQuery query, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var url = $"{settings.Google.GeocodingUrl}?address={Uri.EscapeDataString(MapsHttp.AddressLine(query))}" +
                  $"&components=country:BR&language=pt-BR&region=br&key={Uri.EscapeDataString(settings.Google.ApiKey)}";
        var json = await MapsHttp.SendAsync(http, Provider, () => new HttpRequestMessage(HttpMethod.Get, url), settings.Timeout, cancellationToken);

        var status = MapsHttp.Text(json, "status");
        if (status == "ZERO_RESULTS")
        {
            return null;
        }

        if (status != "OK" || !json.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
        {
            // OVER_QUERY_LIMIT, REQUEST_DENIED, INVALID_REQUEST, UNKNOWN_ERROR: o status vai para o log, a chave não.
            throw new MapsUnavailableException(Provider, "status " + (status ?? "ausente"));
        }

        var first = results[0];
        var geometry = first.GetProperty("geometry");
        var location = geometry.GetProperty("location");
        var locationType = MapsHttp.Text(geometry, "location_type");
        var types = first.TryGetProperty("types", out var t) ? t.EnumerateArray().Select(x => x.GetString()).ToList() : [];
        var exact = locationType is "ROOFTOP" or "RANGE_INTERPOLATED" && types.Any(x => ExactTypes.Contains(x));

        return new GeocodeResult(
            new GeoPoint(MapsHttp.Coordinate(location.GetProperty("lat")), MapsHttp.Coordinate(location.GetProperty("lng"))),
            exact ? GeocodePrecision.Exact : GeocodePrecision.Approximate,
            MapsHttp.Text(first, "formatted_address") ?? MapsHttp.AddressLine(query));
    }
}

/// Google Routes API, Compute Route Matrix (1 origem × 1 destino), `DRIVE` e `TRAFFIC_UNAWARE`: SKU Essentials, sem
/// trânsito (ADR-0048 itens 4 e 16). A chave vai no header `X-Goog-Api-Key`.
internal sealed class GoogleRouteDistance(HttpClient http, IOptions<MapsOptions> options) : IRouteDistanceProvider
{
    public const string FieldMask = "originIndex,destinationIndex,status,condition,distanceMeters";

    public async Task<int?> DrivingDistanceMetersAsync(GeoPoint origin, GeoPoint destination, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var body = JsonSerializer.Serialize(new
        {
            origins = new[] { new { waypoint = Waypoint(origin) } },
            destinations = new[] { new { waypoint = Waypoint(destination) } },
            travelMode = "DRIVE",
            routingPreference = "TRAFFIC_UNAWARE",
        });
        var json = await MapsHttp.SendAsync(http, GoogleGeocoder.Provider, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, settings.Google.RouteMatrixUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("X-Goog-Api-Key", settings.Google.ApiKey);
            request.Headers.TryAddWithoutValidation("X-Goog-FieldMask", FieldMask);
            return request;
        }, settings.Timeout, cancellationToken);

        if (json.ValueKind != JsonValueKind.Array || json.GetArrayLength() == 0)
        {
            throw new MapsUnavailableException(GoogleGeocoder.Provider, "matriz vazia");
        }

        var element = json[0];
        if (element.TryGetProperty("status", out var status) && status.TryGetProperty("code", out var code) && code.GetInt32() != 0)
        {
            throw new MapsUnavailableException(GoogleGeocoder.Provider, "status " + code.GetInt32().ToString(CultureInfo.InvariantCulture));
        }

        return MapsHttp.Text(element, "condition") switch
        {
            "ROUTE_EXISTS" => element.TryGetProperty("distanceMeters", out var meters) ? meters.GetInt32() : 0,
            "ROUTE_NOT_FOUND" => null,
            var other => throw new MapsUnavailableException(GoogleGeocoder.Provider, "condição " + (other ?? "ausente")),
        };
    }

    private static object Waypoint(GeoPoint point) => new { location = new { latLng = new { latitude = point.Latitude, longitude = point.Longitude } } };
}

/// Intervalo mínimo entre chamadas ao Nominatim nesta réplica (política de uso: 1 req/s). Singleton.
internal sealed class NominatimThrottle(IOptions<MapsOptions> options, TimeProvider time)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Relógio monotônico (`GetTimestamp`), não o de parede: um ajuste do relógio do sistema (NTP) não pode encurtar o
    // intervalo exigido pela política de uso do Nominatim. `null` = nenhuma chamada ainda.
    private long? _last;

    public async Task<T> RunAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var interval = TimeSpan.FromMilliseconds(options.Value.Osm.NominatimMinIntervalMilliseconds);
            var wait = _last is { } last ? interval - time.GetElapsedTime(last) : TimeSpan.Zero;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, time, cancellationToken);
            }

            try
            {
                return await call();
            }
            finally
            {
                _last = time.GetTimestamp();
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// Nominatim (OpenStreetMap), busca estruturada. Exato = o resultado tem `house_number` (ou, com
/// `Maps:Osm:AcceptStreetLevel`, é uma rua). O bairro não entra na busca estruturada do Nominatim.
internal sealed class NominatimGeocoder(HttpClient http, IOptions<MapsOptions> options, NominatimThrottle throttle) : IGeocoder
{
    public const string Provider = "Osm";

    public async Task<GeocodeResult?> GeocodeAsync(GeocodeQuery query, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var street = string.IsNullOrWhiteSpace(query.Number) ? query.Street.Trim() : $"{query.Number.Trim()} {query.Street.Trim()}";
        var parameters = new List<string> { "street=" + Uri.EscapeDataString(street) };
        if (!string.IsNullOrWhiteSpace(query.City))
        {
            parameters.Add("city=" + Uri.EscapeDataString(query.City.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(query.State))
        {
            parameters.Add("state=" + Uri.EscapeDataString(query.State.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(query.ZipCode))
        {
            parameters.Add("postalcode=" + Uri.EscapeDataString(query.ZipCode.Trim()));
        }

        var url = $"{settings.Osm.NominatimUrl.TrimEnd('/')}/search?{string.Join('&', parameters)}&countrycodes=br&format=jsonv2&addressdetails=1&limit=1";
        var json = await throttle.RunAsync(
            () => MapsHttp.SendAsync(http, Provider, () => Request(url, settings), settings.Timeout, cancellationToken), cancellationToken);
        if (json.ValueKind != JsonValueKind.Array)
        {
            throw new MapsUnavailableException(Provider, "resposta inesperada");
        }

        if (json.GetArrayLength() == 0)
        {
            return null;
        }

        var first = json[0];
        var address = first.TryGetProperty("address", out var a) ? a : default;
        var exact = MapsHttp.Text(address, "house_number") is not null
                    || (settings.Osm.AcceptStreetLevel && MapsHttp.Text(address, "road") is not null);
        return new GeocodeResult(
            new GeoPoint(MapsHttp.Coordinate(first.GetProperty("lat")), MapsHttp.Coordinate(first.GetProperty("lon"))),
            exact ? GeocodePrecision.Exact : GeocodePrecision.Approximate,
            Format(address) ?? MapsHttp.Text(first, "display_name") ?? MapsHttp.AddressLine(query));
    }

    private static HttpRequestMessage Request(string url, MapsOptions settings)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", settings.Osm.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Language", "pt-BR");
        return request;
    }

    /// "Rua das Palmeiras, 45 - Centro, Uberlândia - MG", com o que o Nominatim devolveu.
    private static string? Format(JsonElement address)
    {
        var road = MapsHttp.Text(address, "road");
        if (road is null)
        {
            return null;
        }

        var text = new StringBuilder(road);
        if (MapsHttp.Text(address, "house_number") is { } number)
        {
            text.Append(", ").Append(number);
        }

        if ((MapsHttp.Text(address, "suburb") ?? MapsHttp.Text(address, "neighbourhood")) is { } suburb)
        {
            text.Append(" - ").Append(suburb);
        }

        if ((MapsHttp.Text(address, "city") ?? MapsHttp.Text(address, "town") ?? MapsHttp.Text(address, "village")) is { } city)
        {
            text.Append(", ").Append(city);
            if (MapsHttp.Text(address, "ISO3166-2-lvl4") is { Length: > 3 } iso && iso.StartsWith("BR-", StringComparison.Ordinal))
            {
                text.Append(" - ").Append(iso[3..]);
            }
        }

        return text.ToString();
    }
}

/// OSRM, perfil de carro (`/route/v1/driving`), sem trânsito por natureza. `NoRoute` → sem rota.
internal sealed class OsrmRouteDistance(HttpClient http, IOptions<MapsOptions> options) : IRouteDistanceProvider
{
    public async Task<int?> DrivingDistanceMetersAsync(GeoPoint origin, GeoPoint destination, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        static string Point(GeoPoint p) => string.Create(CultureInfo.InvariantCulture, $"{p.Longitude},{p.Latitude}");
        var url = $"{settings.Osm.OsrmUrl.TrimEnd('/')}/route/v1/driving/{Point(origin)};{Point(destination)}?overview=false&alternatives=false&steps=false";
        JsonElement json;
        try
        {
            json = await MapsHttp.SendAsync(http, NominatimGeocoder.Provider, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", settings.Osm.UserAgent);
                return request;
            }, settings.Timeout, cancellationToken);
        }
        catch (MapsUnavailableException ex) when (ex.Reason == "HTTP " + ((int)HttpStatusCode.BadRequest).ToString(CultureInfo.InvariantCulture))
        {
            // O OSRM responde 400 com `NoRoute`/`NoSegment` quando o ponto não liga a nenhuma rua.
            return null;
        }

        var code = MapsHttp.Text(json, "code");
        if (code is "NoRoute" or "NoSegment")
        {
            return null;
        }

        if (code != "Ok" || !json.TryGetProperty("routes", out var routes) || routes.GetArrayLength() == 0)
        {
            throw new MapsUnavailableException(NominatimGeocoder.Provider, "código " + (code ?? "ausente"));
        }

        return (int)Math.Round(routes[0].GetProperty("distance").GetDecimal(), 0, MidpointRounding.AwayFromZero);
    }
}
