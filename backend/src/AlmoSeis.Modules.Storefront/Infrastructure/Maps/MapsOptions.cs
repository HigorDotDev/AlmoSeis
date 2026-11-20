using AlmoSeis.Modules.Storefront.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Modules.Storefront.Infrastructure.Maps;

/// `Maps:Provider` (ADR-0048 itens 15 a 17).
public enum MapsProvider
{
    /// Sem provedor: a página não oferece entrega por distância (`deliveryAvailable: false`, `MAPS_PROVIDER` em
    /// `pageDelivery.missing`). É o valor do `appsettings.json` versionado até o usuário escolher o provedor.
    None,

    /// Determinístico, sem rede: endereço → ponto por hash perto de `Maps:Fake:Center*`; distância = Haversine × 1,3.
    /// Padrão em Development e nos testes, e quando `Maps:Provider` não aparece na configuração. Proibido em Production.
    Fake,

    /// Nominatim (geocodificação, 1 req/s) + OSRM (rota de carro). Desenvolvimento, ou um OSRM/Nominatim próprio.
    Osm,

    /// Google Geocoding API + Routes API (Compute Route Matrix, Essentials, `TRAFFIC_UNAWARE`). Chave em `Maps:Google:ApiKey`.
    Google,
}

/// Seção `Maps`. Nenhuma chave no repositório: `Maps:Google:ApiKey` vem do Key Vault (ADR-0037) ou do `user-secrets`.
public sealed class MapsOptions
{
    public const string Section = "Maps";

    public MapsProvider Provider { get; set; } = MapsProvider.Fake;

    /// Tempo máximo de cada chamada ao provedor (ADR-0048 item 13).
    public int TimeoutSeconds { get; set; } = 3;

    /// Coordenadas do endereço do cliente no cache por tenant. 30 dias: o limite que os termos do Google permitem para
    /// latitude/longitude da Geocoding API (ADR-0048 item 11, *a confirmar no contrato*). 0 desliga o cache.
    public int GeocodeCacheDays { get; set; } = 30;

    /// Metros da rota guardados no cache, enquanto o ponto do restaurante não muda. 0 desliga (cada cotação nova chama a
    /// Routes API), o que é o seguro se os termos do Google não permitirem guardar o resultado da rota.
    public int RouteCacheDays { get; set; } = 30;

    /// Limpeza das cotações (24 h depois de vencer) e do cache vencido (ADR-0052 item 5).
    public bool RetentionJobEnabled { get; set; } = true;

    public int RetentionIntervalMinutes { get; set; } = 60;

    public GoogleMapsOptions Google { get; set; } = new();

    public OsmMapsOptions Osm { get; set; } = new();

    public FakeMapsOptions Fake { get; set; } = new();

    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);
}

public sealed class GoogleMapsOptions
{
    public string ApiKey { get; set; } = string.Empty;

    public string GeocodingUrl { get; set; } = "https://maps.googleapis.com/maps/api/geocode/json";

    public string RouteMatrixUrl { get; set; } = "https://routes.googleapis.com/distanceMatrix/v2:computeRouteMatrix";
}

public sealed class OsmMapsOptions
{
    /// Instância pública: política de uso de no máximo 1 req/s e User-Agent que identifique a aplicação.
    public string NominatimUrl { get; set; } = "https://nominatim.openstreetmap.org";

    /// Servidor de demonstração do OSRM: só para desenvolvimento. Em produção, um OSRM próprio.
    public string OsrmUrl { get; set; } = "https://router.project-osrm.org";

    /// Genérico do produto, sem e-mail pessoal.
    public string UserAgent { get; set; } = "AlmoSeis-DeliveryQuotes/1.0 (+https://almoseis.app)";

    /// Intervalo mínimo entre chamadas ao Nominatim nesta réplica (política: 1 req/s).
    public int NominatimMinIntervalMilliseconds { get; set; } = 1000;

    /// O Nominatim muitas vezes não conhece o número da casa no Brasil. `true` aceita o resultado no nível da rua como
    /// exato (só desenvolvimento; em produção o ADR-0048 item 9 manda pedir revisão).
    public bool AcceptStreetLevel { get; set; }
}

public sealed class FakeMapsOptions
{
    /// Centro de Uberlândia.
    public decimal CenterLatitude { get; set; } = -18.918600m;

    public decimal CenterLongitude { get; set; } = -48.277200m;

    public decimal RadiusKm { get; set; } = 8m;

    /// Rota ≈ linha reta × 1,3.
    public decimal RouteFactor { get; set; } = 1.3m;
}

internal sealed class MapsOptionsValidator(IHostEnvironment environment) : IValidateOptions<MapsOptions>
{
    public ValidateOptionsResult Validate(string? name, MapsOptions options)
    {
        var failures = new List<string>();
        if (options.TimeoutSeconds is < 1 or > 30)
        {
            failures.Add("Maps:TimeoutSeconds entre 1 e 30.");
        }

        if (options.GeocodeCacheDays < 0 || options.RouteCacheDays < 0 || options.RetentionIntervalMinutes < 1)
        {
            failures.Add("Maps:GeocodeCacheDays e Maps:RouteCacheDays ≥ 0; Maps:RetentionIntervalMinutes ≥ 1.");
        }

        switch (options.Provider)
        {
            case MapsProvider.Google when string.IsNullOrWhiteSpace(options.Google.ApiKey):
                failures.Add("Maps:Google:ApiKey é obrigatório com Maps:Provider = Google (Key Vault).");
                break;
            case MapsProvider.Osm:
                if (!Uri.IsWellFormedUriString(options.Osm.NominatimUrl, UriKind.Absolute) || !Uri.IsWellFormedUriString(options.Osm.OsrmUrl, UriKind.Absolute))
                {
                    failures.Add("Maps:Osm:NominatimUrl e Maps:Osm:OsrmUrl precisam ser URLs absolutas.");
                }

                if (string.IsNullOrWhiteSpace(options.Osm.UserAgent) || options.Osm.UserAgent.Contains('@', StringComparison.Ordinal))
                {
                    failures.Add("Maps:Osm:UserAgent precisa identificar o produto, sem e-mail.");
                }

                break;
            case MapsProvider.Fake when environment.IsProduction():
                failures.Add("Maps:Provider = Fake não é permitido em produção: a taxa seria inventada.");
                break;
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

internal sealed class MapsAvailability(IOptions<MapsOptions> options) : IMapsAvailability
{
    public bool IsConfigured => options.Value.Provider != MapsProvider.None;

    public bool IsExternal => options.Value.Provider is MapsProvider.Osm or MapsProvider.Google;
}
