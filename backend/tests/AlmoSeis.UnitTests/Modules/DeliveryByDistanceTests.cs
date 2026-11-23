using System.Net;
using System.Text;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Ordering.Domain;
using AlmoSeis.Modules.Storefront.Application;
using AlmoSeis.Modules.Storefront.Contracts;
using AlmoSeis.Modules.Storefront.Domain;
using AlmoSeis.Modules.Storefront.Infrastructure.Maps;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.Extensions.Options;
using Shouldly;

namespace AlmoSeis.UnitTests.Modules;

/// Fórmula do ADR-0048 (itens 5 a 7) com os exemplos do usuário e da tabela do ADR.
public sealed class DeliveryFeeRuleTests
{
    private static readonly Money PerKm = Money.Of(0.80m);
    private static readonly Money Minimum = Money.Of(3.00m);

    [Theory]
    [InlineData(4480, "4.5")]
    [InlineData(2130, "2.1")]
    [InlineData(6040, "6.0")]
    [InlineData(6050, "6.1")] // 6,05 → 6,1 (metade para longe do zero)
    [InlineData(4449, "4.4")]
    [InlineData(0, "0.0")]
    public void Km_has_one_decimal_away_from_zero(int meters, string km) =>
        DeliveryFeeRule.FormatKm(DeliveryFeeRule.Km(meters)).ShouldBe(km);

    [Theory]
    [InlineData(4480, "3.60")] // 4,5 × 0,80 = 3,60 (exemplo do usuário)
    [InlineData(2130, "3.00")] // 2,1 × 0,80 = 1,68 → mínimo 3,00
    [InlineData(6040, "4.80")]
    [InlineData(3750, "3.04")] // 3,8 × 0,80 = 3,04: acima do mínimo por pouco
    [InlineData(3740, "3.00")] // 3,7 × 0,80 = 2,96 → mínimo
    public void Fee_is_the_max_of_km_times_price_and_the_minimum(int meters, string fee) =>
        DeliveryFeeRule.Fee(DeliveryFeeRule.Km(meters), PerKm, Minimum).ToString().ShouldBe(fee);

    [Fact]
    public void Price_with_three_decimals_rounds_away_from_zero() =>
        // 4,5 × 0,85 = 3,825 → 3,83
        DeliveryFeeRule.Fee(4.5m, Money.Of(0.85m), Minimum).ShouldBe(Money.Of(3.83m));

    [Fact]
    public void Zero_minimum_charges_the_distance() =>
        DeliveryFeeRule.Fee(2.1m, PerKm, Money.Zero).ShouldBe(Money.Of(1.68m));

    [Theory]
    [InlineData(6040, true)]
    [InlineData(6050, false)] // 6,1 km > 6,0 km: fora da área
    public void Radius_uses_the_rounded_km(int meters, bool inside) =>
        DeliveryFeeRule.WithinRadius(DeliveryFeeRule.Km(meters), 6.0m).ShouldBe(inside);

    [Fact]
    public void No_radius_delivers_anywhere() => DeliveryFeeRule.WithinRadius(120m, null).ShouldBeTrue();
}

/// Endereço normalizado: o hash do cache e da conferência da cotação (ADR-0048 itens 10 e 11).
public sealed class DeliveryAddressKeyTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("segredo-de-teste-com-32-caracteres!!");

    private static DeliveryAddressKey Key(
        string street, string? number = "45", string? neighborhood = "Centro", string? city = null, string? complement = null, Guid? tenant = null, byte[]? secret = null) =>
        DeliveryAddressKey.Create(tenant ?? Tenant, new PublicAddressInput(street, number, complement, neighborhood, city, null, null), "Uberlândia", "MG", secret ?? Secret);

    [Fact]
    public void Case_accents_punctuation_and_street_type_abbreviation_do_not_matter()
    {
        var hash = Key("Rua das Palmeiras").Hash;
        Key("R. das Palmeiras").Hash.ShouldBe(hash);
        Key("  RUA  DAS PALMEIRAS ", neighborhood: "centro").Hash.ShouldBe(hash);
        Key("Rua das Palmeiras", city: "Uberlandia").Hash.ShouldBe(hash, "cidade nula = a do restaurante");
        Key("Rua das Palmeiras", complement: "Casa 2").Hash.ShouldBe(hash, "complemento não muda a rota");
        Key("Rua das Palmeiras").Hash.Length.ShouldBe(64);
    }

    [Fact]
    public void Number_neighborhood_and_tenant_change_the_key()
    {
        var hash = Key("Rua das Palmeiras").Hash;
        Key("Rua das Palmeiras", number: "46").Hash.ShouldNotBe(hash);
        Key("Rua das Palmeiras", neighborhood: "Fundinho").Hash.ShouldNotBe(hash);
        Key("Rua das Palmeiras", tenant: Guid.NewGuid()).Hash.ShouldNotBe(hash, "o cache nunca é compartilhado entre tenants");
    }

    [Fact]
    public void Display_is_what_the_customer_typed() =>
        Key("Rua das Palmeiras").Display.ShouldBe("Rua das Palmeiras, 45 - Centro, Uberlândia - MG");

    /// 05 §5.17 0007: HMAC com `Maps:AddressHashSecret`. Sem o segredo, o hash não é recalculável a partir de uma lista de
    /// endereços (o SHA-256 simples do tenant + endereço era).
    [Fact]
    public void Hash_is_keyed_by_the_secret()
    {
        var key = Key("Rua das Palmeiras");
        Key("Rua das Palmeiras", secret: Encoding.UTF8.GetBytes("outro-segredo-com-trinta-e-dois-chars")).Hash.ShouldNotBe(key.Hash);
        var unkeyed = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Tenant.ToString("N") + ":" + key.Normalized)));
        key.Hash.ShouldNotBe(unkeyed);
        key.Hash.Length.ShouldBe(64);
    }

    /// 4 casas no cache (cerca de 11 m); a cotação corrente usa o ponto exato.
    [Fact]
    public void Cache_keeps_four_decimals()
    {
        var coarse = GeocodeCacheEntry.Coarse(new GeoPoint(-18.912345m, -48.270155m));
        coarse.ShouldBe(new GeoPoint(-18.9123m, -48.2702m));
    }
}

/// Localização do restaurante e o que falta para a entrega pela página (ADR-0048 item 3).
public sealed class StorefrontLocationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly StorefrontAddressDto Address = new("Rua das Flores", "123", null, "Centro", "Uberlândia", "MG", "38400000");

    private static StorefrontChanges Changes(StorefrontAddressDto? address = null, StorefrontLocationDto? location = null, DeliveryFeeMode mode = DeliveryFeeMode.Distance, Money? perKm = null) => new(
        "Marmitaria da Ana", true, true, true, true, true, null, null, address ?? Address,
        new DeliveryRule(mode, perKm ?? Money.Of(0.80m), Money.Of(3m), 6.0m), location);

    private static StorefrontSettings Saved()
    {
        var settings = StorefrontSettings.CreateDefault(Guid.NewGuid(), "Marmitaria da Ana", Now);
        settings.Update(Changes(), false, true, Now);
        return settings;
    }

    [Fact]
    public void Geocoded_point_needs_confirmation_before_delivery()
    {
        var settings = Saved();
        settings.MissingForPageDelivery(true).ShouldBe([PageDeliveryRequirement.ConfirmedLocation]);

        settings.SuggestLocation(new GeoPoint(-18.9186004m, -48.2772m), Now).ShouldBeTrue();
        settings.Latitude.ShouldBe(-18.918600m);
        settings.IsLocationConfirmed.ShouldBeFalse();
        settings.SuggestLocation(new GeoPoint(-1m, -1m), Now).ShouldBeFalse("só preenche um ponto vazio");

        settings.Update(Changes(location: new StorefrontLocationDto(-18.918700m, -48.277300m, true)), false, true, Now.AddMinutes(1)).ShouldBeTrue();
        settings.IsLocationConfirmed.ShouldBeTrue();
        settings.Latitude.ShouldBe(-18.918700m, "o proprietário arrastou o marcador");
        settings.IsDeliveryAvailableOnPage(true).ShouldBeTrue();
        settings.MissingForPageDelivery(false).ShouldBe([PageDeliveryRequirement.MapsProvider]);
    }

    [Fact]
    public void Absent_location_keeps_the_point_and_a_new_address_clears_it()
    {
        var settings = Saved();
        settings.Update(Changes(location: new StorefrontLocationDto(-18.9187m, -48.2773m, true)), false, true, Now);
        settings.ClearDomainEvents();

        // O app da fatia 2 não manda `location`: o ponto confirmado fica.
        settings.Update(Changes(), false, true, Now.AddMinutes(1)).ShouldBeFalse();
        settings.IsLocationConfirmed.ShouldBeTrue();

        // Endereço novo: o ponto (até o enviado junto) sai e precisa de nova confirmação.
        settings.Update(Changes(Address with { Number = "321" }, new StorefrontLocationDto(-18.9187m, -48.2773m, true)), false, true, Now.AddMinutes(2)).ShouldBeTrue();
        settings.Latitude.ShouldBeNull();
        settings.IsLocationConfirmed.ShouldBeFalse();
    }

    [Fact]
    public void Missing_price_per_km_and_paused_delivery_are_reported()
    {
        var settings = StorefrontSettings.CreateDefault(Guid.NewGuid(), "Marmitaria da Ana", Now);
        settings.MissingForPageDelivery(false).ShouldBe(
            [PageDeliveryRequirement.PricePerKm, PageDeliveryRequirement.ConfirmedLocation, PageDeliveryRequirement.MapsProvider]);

        var fixedMode = Saved();
        fixedMode.Update(Changes(mode: DeliveryFeeMode.Fixed), false, true, Now);
        fixedMode.IsDeliveryAvailableOnPage(false).ShouldBeTrue("FIXED não consulta mapa");
    }

    [Fact]
    public void Coordinates_out_of_range_are_refused() =>
        Should.Throw<BusinessException>(() => Saved().Update(Changes(location: new StorefrontLocationDto(91m, 0m, true)), false, true, Now))
            .ErrorCode.ShouldBe(ErrorCodes.ValidationFailed);
}

/// Snapshot da taxa no pedido (ADR-0048 itens 19 e 20).
public sealed class OrderDeliveryPricingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);
    private static readonly Money Default = Money.Of(5m);

    private static NewOrder Data(Money fee, DeliveryPricing? pricing) => new(
        OrderType.Marmita, FulfillmentType.Delivery, null, "Carla", "+5534999990000", new DeliveryAddressDto("Rua A", "1", null, null, null), fee,
        null, null, null, [new NewItem(OrderItemKind.Marmita, null, null, Guid.NewGuid(), 2, Money.Of(20m), "Marmita M", "Frango", true, null, [], [])], [],
        pricing, ExpectedPaymentMethod.Cash);

    [Fact]
    public void Distance_order_keeps_the_snapshot_and_shows_the_km()
    {
        var order = Order.SubmitFromWeb(Guid.NewGuid(), Guid.NewGuid(),
            Data(Money.Of(3.60m), new DeliveryPricing(DeliveryFeeSource.Distance, 4480, Money.Of(0.80m), Money.Of(3m))),
            Guid.NewGuid(), new DateOnly(2026, 11, 20), Now.AddMinutes(10), Guid.NewGuid, Now);

        order.DeliveryFeeSource.ShouldBe(DeliveryFeeSource.Distance);
        order.DeliveryDistanceKm.ShouldBe("4.5");
        order.DeliveryFee.ShouldBe(Money.Of(3.60m));
        order.TotalAmount.ShouldBe(Money.Of(43.60m)); // 2 × 20,00 + 3,60
    }

    [Fact]
    public void Editing_the_fee_in_the_app_makes_it_manual_and_leaving_delivery_clears_it()
    {
        var by = Guid.NewGuid();
        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(), Data(Default, new DeliveryPricing(DeliveryFeeSource.FixedDefault)),
            Guid.NewGuid(), new DateOnly(2026, 11, 20), 1, by, Guid.NewGuid, Now);
        order.DeliveryFeeSource.ShouldBe(DeliveryFeeSource.FixedDefault);

        order.UpdateDetails(new OrderDetailsChanges(default, default, default, default, Money.Of(8m), default, default, default), Default, by, Guid.NewGuid, Now);
        order.DeliveryFeeSource.ShouldBe(DeliveryFeeSource.Manual);
        order.DeliveryDistanceKm.ShouldBeNull();

        order.UpdateDetails(new OrderDetailsChanges(default, default, FulfillmentType.Pickup, default, default, default, default, default), Default, by, Guid.NewGuid, Now);
        order.DeliveryFeeSource.ShouldBeNull();
    }
}

/// Provedores atrás das interfaces (ADR-0048 item 15): o `Fake` determinístico e o formato das chamadas ao Google e ao
/// OSM, contra um servidor HTTP simulado (nenhuma chamada sai da máquina).
public sealed class MapsProviderTests
{
    private static IOptions<MapsOptions> Options(Action<MapsOptions>? configure = null)
    {
        var options = new MapsOptions { Provider = MapsProvider.Google, Google = { ApiKey = "test-key" } };
        options.Osm.NominatimMinIntervalMilliseconds = 0;
        configure?.Invoke(options);
        return Microsoft.Extensions.Options.Options.Create(options);
    }

    private static readonly GeocodeQuery Query = new("Rua das Palmeiras", "45", "Centro", "Uberlândia", "MG", null);

    [Fact]
    public async Task Fake_is_deterministic_and_flags_missing_numbers()
    {
        var fake = new FakeMaps(Options());
        var first = await fake.GeocodeAsync(Query, CancellationToken.None);
        var again = await fake.GeocodeAsync(Query, CancellationToken.None);
        first!.Point.ShouldBe(again!.Point);
        first.Precision.ShouldBe(GeocodePrecision.Exact);
        (await fake.GeocodeAsync(Query with { Number = "S/N" }, CancellationToken.None))!.Precision.ShouldBe(GeocodePrecision.Approximate);
        (await fake.GeocodeAsync(Query with { Street = "Rua Inexistente" }, CancellationToken.None)).ShouldBeNull();

        // Haversine × 1,3: 0,01° de latitude ≈ 1.112 m em linha reta → ≈ 1.446 m de "rota".
        var meters = await fake.DrivingDistanceMetersAsync(new GeoPoint(-18.90m, -48.27m), new GeoPoint(-18.91m, -48.27m), CancellationToken.None);
        meters!.Value.ShouldBeInRange(1440, 1450);
    }

    [Fact]
    public async Task Google_geocoding_sends_the_key_and_reads_precision()
    {
        var server = new FakeServer(request =>
        {
            request.RequestUri!.Query.ShouldContain("key=test-key");
            request.RequestUri.Query.ShouldContain("components=country:BR");
            return """{"status":"OK","results":[{"formatted_address":"R. das Palmeiras, 45 - Centro, Uberlândia - MG","types":["street_address"],"geometry":{"location":{"lat":-18.9123456,"lng":-48.2701},"location_type":"ROOFTOP"}}]}""";
        });
        var result = await new GoogleGeocoder(server.Client, Options()).GeocodeAsync(Query, CancellationToken.None);
        result!.Precision.ShouldBe(GeocodePrecision.Exact);
        result.Point.ShouldBe(new GeoPoint(-18.912346m, -48.270100m));
        result.FormattedAddress.ShouldBe("R. das Palmeiras, 45 - Centro, Uberlândia - MG");
    }

    [Fact]
    public async Task Google_geocoding_street_level_is_approximate_and_errors_are_unavailable()
    {
        var approximate = new FakeServer(_ =>
            """{"status":"OK","results":[{"formatted_address":"R. das Palmeiras - Centro","types":["route"],"geometry":{"location":{"lat":-18.9,"lng":-48.2},"location_type":"GEOMETRIC_CENTER"}}]}""");
        (await new GoogleGeocoder(approximate.Client, Options()).GeocodeAsync(Query, CancellationToken.None))!.Precision.ShouldBe(GeocodePrecision.Approximate);

        var none = new FakeServer(_ => """{"status":"ZERO_RESULTS","results":[]}""");
        (await new GoogleGeocoder(none.Client, Options()).GeocodeAsync(Query, CancellationToken.None)).ShouldBeNull();

        var quota = new FakeServer(_ => """{"status":"OVER_QUERY_LIMIT","results":[]}""");
        await Should.ThrowAsync<MapsUnavailableException>(() => new GoogleGeocoder(quota.Client, Options()).GeocodeAsync(Query, CancellationToken.None));

        var down = new FakeServer(_ => throw new HttpRequestException("fora do ar"));
        await Should.ThrowAsync<MapsUnavailableException>(() => new GoogleGeocoder(down.Client, Options()).GeocodeAsync(Query, CancellationToken.None));
    }

    [Fact]
    public async Task Google_route_matrix_is_drive_without_traffic_with_the_key_in_the_header()
    {
        string? body = null;
        var server = new FakeServer(request =>
        {
            request.Headers.GetValues("X-Goog-Api-Key").Single().ShouldBe("test-key");
            request.Headers.GetValues("X-Goog-FieldMask").Single().ShouldBe(GoogleRouteDistance.FieldMask);
            request.RequestUri!.Query.ShouldNotContain("key=");
            body = request.Content!.ReadAsStringAsync().Result;
            return """[{"originIndex":0,"destinationIndex":0,"status":{},"distanceMeters":4480,"condition":"ROUTE_EXISTS"}]""";
        });
        var meters = await new GoogleRouteDistance(server.Client, Options()).DrivingDistanceMetersAsync(new GeoPoint(-18.9m, -48.2m), new GeoPoint(-18.91m, -48.21m), CancellationToken.None);
        meters.ShouldBe(4480);
        body.ShouldNotBeNull();
        body.ShouldContain("\"travelMode\":\"DRIVE\"");
        body.ShouldContain("\"routingPreference\":\"TRAFFIC_UNAWARE\"");

        var noRoute = new FakeServer(_ => """[{"originIndex":0,"destinationIndex":0,"status":{},"condition":"ROUTE_NOT_FOUND"}]""");
        (await new GoogleRouteDistance(noRoute.Client, Options()).DrivingDistanceMetersAsync(new GeoPoint(0, 0), new GeoPoint(1, 1), CancellationToken.None)).ShouldBeNull();

        var error = new FakeServer(_ => "{}", HttpStatusCode.InternalServerError);
        await Should.ThrowAsync<MapsUnavailableException>(() => new GoogleRouteDistance(error.Client, Options()).DrivingDistanceMetersAsync(new GeoPoint(0, 0), new GeoPoint(1, 1), CancellationToken.None));
    }

    [Fact]
    public async Task Nominatim_and_osrm_use_the_generic_user_agent()
    {
        var options = Options(o => o.Provider = MapsProvider.Osm);
        var nominatim = new FakeServer(request =>
        {
            var agent = request.Headers.UserAgent.ToString();
            agent.ShouldStartWith("AlmoSeis-DeliveryQuotes/1.0");
            agent.ShouldNotContain("@");
            request.RequestUri!.AbsolutePath.ShouldBe("/search");
            request.RequestUri.Query.ShouldContain("countrycodes=br");
            return """[{"lat":"-18.9123","lon":"-48.2701","display_name":"45, Rua das Palmeiras, Centro, Uberlândia","address":{"house_number":"45","road":"Rua das Palmeiras","suburb":"Centro","city":"Uberlândia","ISO3166-2-lvl4":"BR-MG"}}]""";
        });
        var result = await new NominatimGeocoder(nominatim.Client, options, new NominatimThrottle(options, TimeProvider.System)).GeocodeAsync(Query, CancellationToken.None);
        result!.Precision.ShouldBe(GeocodePrecision.Exact);
        result.FormattedAddress.ShouldBe("Rua das Palmeiras, 45 - Centro, Uberlândia - MG");

        var streetOnly = new FakeServer(_ => """[{"lat":"-18.9","lon":"-48.2","address":{"road":"Rua das Palmeiras"}}]""");
        (await new NominatimGeocoder(streetOnly.Client, options, new NominatimThrottle(options, TimeProvider.System)).GeocodeAsync(Query, CancellationToken.None))!
            .Precision.ShouldBe(GeocodePrecision.Approximate);

        var osrm = new FakeServer(request =>
        {
            request.RequestUri!.AbsolutePath.ShouldBe("/route/v1/driving/-48.2,-18.9;-48.21,-18.91");
            return """{"code":"Ok","routes":[{"distance":4480.4,"duration":600}]}""";
        });
        (await new OsrmRouteDistance(osrm.Client, options).DrivingDistanceMetersAsync(new GeoPoint(-18.9m, -48.2m), new GeoPoint(-18.91m, -48.21m), CancellationToken.None)).ShouldBe(4480);

        var noRoute = new FakeServer(_ => """{"code":"NoRoute"}""");
        (await new OsrmRouteDistance(noRoute.Client, options).DrivingDistanceMetersAsync(new GeoPoint(0, 0), new GeoPoint(1, 1), CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Nominatim_throttle_spaces_the_calls()
    {
        var options = Options(o => o.Osm.NominatimMinIntervalMilliseconds = 300);
        var throttle = new NominatimThrottle(options, TimeProvider.System);
        var started = TimeProvider.System.GetTimestamp();
        await throttle.RunAsync(() => Task.FromResult(1), CancellationToken.None);
        await throttle.RunAsync(() => Task.FromResult(2), CancellationToken.None);
        TimeProvider.System.GetElapsedTime(started).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(280));
    }

    [Fact]
    public void Validation_refuses_google_without_key_fake_in_production_and_email_in_user_agent()
    {
        static ValidateOptionsResult Validate(MapsOptions o, string environment) =>
            new MapsOptionsValidator(new FakeEnvironment(environment)).Validate(null, o);

        Validate(new MapsOptions { Provider = MapsProvider.Google }, "Production").Failed.ShouldBeTrue();
        Validate(new MapsOptions { Provider = MapsProvider.Fake }, "Production").Failed.ShouldBeTrue();
        Validate(new MapsOptions { Provider = MapsProvider.Fake }, "Development").Succeeded.ShouldBeTrue();
        Validate(new MapsOptions { Provider = MapsProvider.Osm, Osm = { UserAgent = "app (fulano@example.com)" } }, "Development").Failed.ShouldBeTrue();
        Validate(new MapsOptions { Provider = MapsProvider.None, AddressHashSecret = Secret32 }, "Production").Succeeded.ShouldBeTrue();
    }

    /// 05 §5.17 0007: fora de Development, o hash do endereço exige o segredo (Key Vault).
    [Fact]
    public void Address_hash_secret_is_required_outside_development()
    {
        static ValidateOptionsResult Validate(MapsOptions o, string environment) =>
            new MapsOptionsValidator(new FakeEnvironment(environment)).Validate(null, o);

        Validate(new MapsOptions { Provider = MapsProvider.None }, "Production").Failed.ShouldBeTrue();
        Validate(new MapsOptions { Provider = MapsProvider.None }, "Staging").Failed.ShouldBeTrue();
        Validate(new MapsOptions { Provider = MapsProvider.None, AddressHashSecret = "curto" }, "Production").Failed.ShouldBeTrue();
        Validate(new MapsOptions { Provider = MapsProvider.None }, "Development").Succeeded.ShouldBeTrue();
        new MapsOptions().GeocodeCacheDays.ShouldBe(7);
    }

    private const string Secret32 = "segredo-do-endereco-com-32-caracteres";

    private sealed class FakeServer(Func<HttpRequestMessage, string> respond, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpClient Client => new(this);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(respond(request), Encoding.UTF8, "application/json") });
    }

    private sealed class FakeEnvironment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "AlmoSeis.Api";

        public string ContentRootPath { get; set; } = ".";

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
