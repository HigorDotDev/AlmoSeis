using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlmoSeis.Modules.Storefront.Application;
using AlmoSeis.Modules.Storefront.Domain;
using AlmoSeis.Modules.Storefront.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Ordering;

/// Taxa de entrega por distância, fatia 4 do EP-002 (ADR-0048, RF-204) contra a API real. O provedor de mapas é um
/// roteiro em memória (`ScriptedMaps`): cada rua tem um ponto e uma distância de rota conhecidos, e nada sai da máquina.
[Collection(nameof(PostgresCollection))]
public sealed class DeliveryQuoteFlowTests : IDisposable
{
    private readonly PostgresFixture _db;
    private readonly ScriptedMaps _maps = new();
    private readonly ApiFactory _api;

    public DeliveryQuoteFlowTests(PostgresFixture db)
    {
        _db = db;
        _api = new ApiFactory(db, services =>
        {
            services.AddSingleton<IGeocoder>(_maps);
            services.AddSingleton<IRouteDistanceProvider>(_maps);
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _api.Dispose();

    // ---------- Configuração e localização do restaurante ----------

    [Fact]
    public async Task Store_location_is_geocoded_confirmed_kept_without_location_and_cleared_by_a_new_address()
    {
        var s = await OrderingScenario.CreateAsync(_api, _db);
        var config = await GetStorefront(s);
        var saved = await Put(s, Body(config.GetProperty("slug").GetString()!), config.Version());

        // Ao salvar o endereço, o ponto geocodificado vem sem confirmação: a entrega ainda não aparece.
        saved.GetProperty("location").GetProperty("latitude").GetDecimal().ShouldBe(ScriptedMaps.Store.Latitude);
        saved.GetProperty("location").GetProperty("isConfirmed").GetBoolean().ShouldBeFalse();
        saved.GetProperty("pageDelivery").GetProperty("missing").EnumerateArray().Select(m => m.GetString()).ShouldBe(["CONFIRMED_LOCATION"]);

        // O proprietário confirma o ponto (arrastado um pouco).
        var confirmed = await Put(s, Body(Slug(saved), location: new { latitude = -18.900100m, longitude = -48.200100m, isConfirmed = true }), saved.Version());
        confirmed.GetProperty("location").GetProperty("isConfirmed").GetBoolean().ShouldBeTrue();
        confirmed.GetProperty("location").GetProperty("latitude").GetDecimal().ShouldBe(-18.900100m);
        confirmed.GetProperty("pageDelivery").GetProperty("available").GetBoolean().ShouldBeTrue();

        // Compatibilidade com o app da fatia 2: `location` ausente (ou nulo) mantém o ponto confirmado.
        var paused = await Put(s, Body(Slug(saved), accepting: false), confirmed.Version());
        paused.GetProperty("location").GetProperty("isConfirmed").GetBoolean().ShouldBeTrue();
        paused.GetProperty("location").GetProperty("latitude").GetDecimal().ShouldBe(-18.900100m);
        var withNull = await Put(s, Body(Slug(saved), location: (object?)null, sendLocation: true), paused.Version());
        withNull.GetProperty("location").GetProperty("isConfirmed").GetBoolean().ShouldBeTrue();

        // Endereço novo: o ponto (inclusive o enviado junto) é descartado e geocodificado de novo, sem confirmação.
        var moved = await Put(s, Body(Slug(saved), number: "200", location: new { latitude = -18.900100m, longitude = -48.200100m, isConfirmed = true }), withNull.Version());
        moved.GetProperty("location").GetProperty("isConfirmed").GetBoolean().ShouldBeFalse();
        moved.GetProperty("pageDelivery").GetProperty("available").GetBoolean().ShouldBeFalse();

        var audit = await s.ScalarAsync<string>(
            "SELECT after::text FROM audit_logs WHERE tenant_id = @t AND operation_type = 'StorefrontUpdated' ORDER BY occurred_at DESC LIMIT 1");
        audit.ShouldNotContain("-18.9");
    }

    // ---------- Cotação e envio ----------

    [Fact]
    public async Task Quote_follows_the_formula_and_the_delivery_order_goes_through_acceptance_with_the_quoted_fee()
    {
        var (s, slug) = await ReadyAsync();
        var store = await (await Public().GetAsync($"/api/v1/public/stores/{slug}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        store.GetProperty("deliveryAvailable").GetBoolean().ShouldBeTrue();
        var rule = store.GetProperty("deliveryRule");
        rule.GetProperty("feeMode").GetString().ShouldBe("DISTANCE");
        rule.GetProperty("pricePerKm").GetString().ShouldBe("0.80");
        rule.GetProperty("minimumFee").GetString().ShouldBe("3.00");
        rule.GetProperty("maxDistanceKm").GetString().ShouldBe("6.0");

        // 4.480 m → 4,5 km × R$ 0,80 = R$ 3,60 (exemplo do usuário).
        var before = DateTimeOffset.UtcNow;
        var quote = await Quote(slug, "Rua das Palmeiras");
        quote.GetProperty("distanceKm").GetString().ShouldBe("4.5");
        quote.GetProperty("deliveryFee").GetString().ShouldBe("3.60");
        quote.GetProperty("formattedAddress").GetString().ShouldBe("Rua das Palmeiras, 45 - Centro, Uberlândia - MG (geocodificado)");
        var expiresAt = quote.GetProperty("expiresAt").GetDateTimeOffset();
        expiresAt.ShouldBeInRange(before.AddMinutes(14.9), DateTimeOffset.UtcNow.AddMinutes(15.1));

        // Mesma rua de novo: cache por tenant, sem chamar o provedor.
        var (geocodes, routes) = (_maps.GeocodeCalls, _maps.RouteCalls);
        (await Quote(slug, "R. das Palmeiras")).GetProperty("deliveryFee").GetString().ShouldBe("3.60");
        _maps.GeocodeCalls.ShouldBe(geocodes);
        _maps.RouteCalls.ShouldBe(routes);
        (await s.ScalarAsync<string>("SELECT string_agg(column_name, ',') FROM information_schema.columns WHERE table_name = 'geocode_cache_entries' AND data_type IN ('character', 'character varying', 'text')"))
            .ShouldBe("address_hash", "o cache guarda o hash, nunca o texto do endereço");

        // Envio: 1 marmita M com ovo (22,00) + 3,60 = 25,60.
        var placed = await (await PlaceOrder(slug, Order(s, quote.GetProperty("quoteId").GetGuid(), "25.60"))).ExpectAsync(HttpStatusCode.Created, _api);
        placed.GetProperty("deliveryFee").GetString().ShouldBe("3.60");
        placed.GetProperty("totalAmount").GetString().ShouldBe("25.60");

        var orderId = await s.ScalarAsync<Guid>("SELECT id FROM orders WHERE tenant_id = @t");
        var order = await s.GetOrderAsync(orderId);
        order.GetProperty("deliveryFeeSource").GetString().ShouldBe("DISTANCE");
        order.GetProperty("deliveryDistanceKm").GetString().ShouldBe("4.5");
        order.GetProperty("deliveryAddress").GetProperty("street").GetString().ShouldBe("Rua das Palmeiras");
        (await s.ScalarAsync<int>("SELECT delivery_distance_meters FROM orders WHERE tenant_id = @t")).ShouldBe(4480);

        var accepted = await (await s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{orderId}/accept", new { estimatedReadyMinutes = 30 }, order.Version()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        accepted.GetProperty("acceptanceStatus").GetString().ShouldBe("ACCEPTED");
        accepted.GetProperty("deliveryFee").GetString().ShouldBe("3.60");
        accepted.GetProperty("totalAmount").GetString().ShouldBe("25.60");

        var tracking = await (await Public().GetAsync($"/api/v1/public/tracking/{placed.GetProperty("trackingToken").GetString()}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        tracking.GetProperty("deliveryDistanceKm").GetString().ShouldBe("4.5");
        tracking.GetProperty("deliveryFee").GetString().ShouldBe("3.60");
    }

    [Fact]
    public async Task Minimum_radius_and_imprecise_addresses()
    {
        var (_, slug) = await ReadyAsync();

        (await Quote(slug, "Rua Perto")).GetProperty("deliveryFee").GetString().ShouldBe("3.00", "2,1 km × 0,80 = 1,68 → mínimo");
        var edge = await Quote(slug, "Rua Seis");
        edge.GetProperty("distanceKm").GetString().ShouldBe("6.0");
        edge.GetProperty("deliveryFee").GetString().ShouldBe("4.80");

        var far = await (await QuoteRaw(slug, "Rua Longe")).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);
        far.GetProperty("errorCode").GetString().ShouldBe("OutOfDeliveryArea");
        far.GetProperty("distanceKm").GetString().ShouldBe("6.1");
        far.GetProperty("maxDistanceKm").GetString().ShouldBe("6.0");

        var noRoute = await (await QuoteRaw(slug, "Rua Ilhada")).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);
        noRoute.GetProperty("errorCode").GetString().ShouldBe("OutOfDeliveryArea");

        var approximate = await (await QuoteRaw(slug, "Rua Aproximada")).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);
        approximate.GetProperty("errorCode").GetString().ShouldBe("ValidationFailed");
        approximate.GetProperty("errors").GetProperty("address.number").GetArrayLength().ShouldBe(1);

        var unknown = await (await QuoteRaw(slug, "Rua Inexistente")).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);
        unknown.GetProperty("errors").GetProperty("address.street").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Changed_price_gives_409_with_a_new_quote_and_an_expired_quote_with_the_same_fee_goes_through()
    {
        var (s, slug) = await ReadyAsync();
        var quote = await Quote(slug, "Rua das Palmeiras");
        var quoteId = quote.GetProperty("quoteId").GetGuid();

        // Restaurante sobe o preço por km depois da cotação: o cliente nunca paga a taxa que não viu.
        var config = await GetStorefront(s);
        await Put(s, Body(Slug(config), pricePerKm: "1.00"), config.Version());
        var changed = await (await PlaceOrder(slug, Order(s, quoteId, "25.60"))).ExpectAsync(HttpStatusCode.Conflict, _api);
        changed.GetProperty("errorCode").GetString().ShouldBe("DeliveryQuoteChanged");
        var fresh = changed.GetProperty("quote");
        fresh.GetProperty("deliveryFee").GetString().ShouldBe("4.50"); // 4,5 × 1,00
        fresh.GetProperty("distanceKm").GetString().ShouldBe("4.5");
        var freshId = fresh.GetProperty("quoteId").GetGuid();
        freshId.ShouldNotBe(quoteId);
        (await s.ScalarAsync<long>("SELECT count(*) FROM orders WHERE tenant_id = @t")).ShouldBe(0);

        // A cotação nova foi gravada apesar do 409: o reenvio com ela passa.
        (await (await PlaceOrder(slug, Order(s, freshId, "26.50", phone: "34977776666"))).ExpectAsync(HttpStatusCode.Created, _api))
            .GetProperty("deliveryFee").GetString().ShouldBe("4.50");

        // Cotação vencida com a mesma taxa: recalcula e segue.
        var other = await Quote(slug, "Rua Perto");
        await s.SqlAsync("UPDATE delivery_quotes SET created_at = now() - interval '1 hour', expires_at = now() - interval '30 minutes' WHERE id = @q",
            ("q", other.GetProperty("quoteId").GetGuid()));
        (await (await PlaceOrder(slug, Order(s, other.GetProperty("quoteId").GetGuid(), "25.00", street: "Rua Perto", phone: "34966665555")))
            .ExpectAsync(HttpStatusCode.Created, _api)).GetProperty("deliveryFee").GetString().ShouldBe("3.00");
    }

    [Fact]
    public async Task Swapped_address_is_recalculated_and_a_different_fee_needs_confirmation()
    {
        var (s, slug) = await ReadyAsync();
        var quoteId = (await Quote(slug, "Rua das Palmeiras")).GetProperty("quoteId").GetGuid();

        // Cotou a Rua das Palmeiras (3,60) e enviou para a Rua Perto (3,00): 409 com a cotação do endereço enviado.
        var swapped = await (await PlaceOrder(slug, Order(s, quoteId, "25.60", street: "Rua Perto"))).ExpectAsync(HttpStatusCode.Conflict, _api);
        swapped.GetProperty("errorCode").GetString().ShouldBe("DeliveryQuoteChanged");
        swapped.GetProperty("quote").GetProperty("deliveryFee").GetString().ShouldBe("3.00");

        // Só o complemento diferente: mesmo endereço, a cotação vale.
        (await (await PlaceOrder(slug, Order(s, quoteId, "25.60", complement: "Apto 101"))).ExpectAsync(HttpStatusCode.Created, _api))
            .GetProperty("deliveryFee").GetString().ShouldBe("3.60");

        // Total de quem não viu a taxa: CartChanged com a taxa no carrinho.
        var otherQuote = (await Quote(slug, "Rua das Palmeiras")).GetProperty("quoteId").GetGuid();
        var cart = await (await PlaceOrder(slug, Order(s, otherQuote, "22.00", phone: "34955554444"))).ExpectAsync(HttpStatusCode.Conflict, _api);
        cart.GetProperty("errorCode").GetString().ShouldBe("CartChanged");
        cart.GetProperty("cart").GetProperty("deliveryFee").GetString().ShouldBe("3.60");
    }

    [Fact]
    public async Task Provider_down_offers_only_pickup()
    {
        var (s, slug) = await ReadyAsync();
        _maps.Down = true;
        try
        {
            var down = await (await QuoteRaw(slug, "Rua das Palmeiras")).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);
            down.GetProperty("errorCode").GetString().ShouldBe("DeliveryUnavailable");
            down.GetProperty("detail").GetString().ShouldBe("Não conseguimos calcular a entrega agora. Você pode retirar no local.");

            // A falha é momentânea: o perfil continua oferecendo entrega, e a retirada funciona.
            var store = await (await Public().GetAsync($"/api/v1/public/stores/{slug}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
            store.GetProperty("deliveryAvailable").GetBoolean().ShouldBeTrue();
            store.GetProperty("pickupAvailable").GetBoolean().ShouldBeTrue();
            await (await PlaceOrder(slug, Order(s, null, "22.00", pickup: true))).ExpectAsync(HttpStatusCode.Created, _api);
        }
        finally
        {
            _maps.Down = false;
        }
    }

    [Fact]
    public async Task Quotes_and_cache_are_isolated_between_tenants()
    {
        var (a, slugA) = await ReadyAsync();
        var (b, slugB) = await ReadyAsync();
        var quoteA = (await Quote(slugA, "Rua das Palmeiras")).GetProperty("quoteId").GetGuid();

        // A cotação de A não existe na página de B.
        var cross = await (await PlaceOrder(slugB, Order(b, quoteA, "25.60"))).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);
        cross.GetProperty("errors").GetProperty("deliveryQuoteId").GetArrayLength().ShouldBe(1);
        (await b.ScalarAsync<long>("SELECT count(*) FROM orders WHERE tenant_id = @t")).ShouldBe(0);

        // O cache de A não serve para B: o mesmo endereço chama o provedor de novo.
        var geocodes = _maps.GeocodeCalls;
        await Quote(slugB, "Rua das Palmeiras");
        _maps.GeocodeCalls.ShouldBe(geocodes + 1);
        (await a.ScalarAsync<long>("SELECT count(*) FROM delivery_quotes WHERE tenant_id = @t")).ShouldBe(1);
        (await b.ScalarAsync<long>("SELECT count(*) FROM geocode_cache_entries WHERE tenant_id = @t")).ShouldBe(1);
    }

    [Fact]
    public async Task Retention_deletes_old_quotes_and_expired_cache()
    {
        var (s, slug) = await ReadyAsync();
        await Quote(slug, "Rua das Palmeiras");
        await Quote(slug, "Rua Perto");
        await s.SqlAsync(
            "UPDATE delivery_quotes SET created_at = now() - interval '2 days', expires_at = now() - interval '25 hours' WHERE tenant_id = @t AND distance_meters = 4480");
        await s.SqlAsync(
            "UPDATE geocode_cache_entries SET geocoded_at = now() - interval '31 days', expires_at = now() - interval '1 day' WHERE tenant_id = @t AND distance_meters = 2130");

        var result = await _api.Services.GetRequiredService<DeliveryQuoteRetentionJob>().RunOnceAsync(Ct);
        result.ShouldNotBeNull();
        (await s.ScalarAsync<long>("SELECT count(*) FROM delivery_quotes WHERE tenant_id = @t")).ShouldBe(1);
        (await s.ScalarAsync<long>("SELECT count(*) FROM geocode_cache_entries WHERE tenant_id = @t")).ShouldBe(1);
    }

    // ---------- Apoio ----------

    private async Task<(OrderingScenario Scenario, string Slug)> ReadyAsync()
    {
        var s = await OrderingScenario.CreateAsync(_api, _db);
        var config = await GetStorefront(s);
        var saved = await Put(s, Body(Slug(config)), config.Version());
        var location = saved.GetProperty("location");
        await Put(s, Body(Slug(saved), location: new
        {
            latitude = location.GetProperty("latitude").GetDecimal(),
            longitude = location.GetProperty("longitude").GetDecimal(),
            isConfirmed = true,
        }), saved.Version());
        return (s, Slug(saved));
    }

    private static string Slug(JsonElement storefront) => storefront.GetProperty("slug").GetString()!;

    private HttpClient Public() => _api.CreateClient();

    private async Task<JsonElement> GetStorefront(OrderingScenario s) =>
        await (await s.Client.GetAsync("/api/v1/storefront", Ct)).ExpectAsync(HttpStatusCode.OK, _api);

    private async Task<JsonElement> Put(OrderingScenario s, Dictionary<string, object?> body, int version) =>
        await (await s.Client.SendJsonAsync(HttpMethod.Put, "/api/v1/storefront", body, version)).ExpectAsync(HttpStatusCode.OK, _api);

    /// Corpo do PUT. Sem `location` (como o app da fatia 2), a menos que `location`/`sendLocation` digam o contrário.
    private static Dictionary<string, object?> Body(
        string slug, bool accepting = true, string number = "123", string pricePerKm = "0.80", object? location = null, bool sendLocation = false)
    {
        var body = new Dictionary<string, object?>
        {
            ["slug"] = slug,
            ["publicName"] = "Marmitaria da Ana",
            ["isPageActive"] = true,
            ["isAcceptingOrders"] = accepting,
            ["isDeliveryEnabled"] = true,
            ["offersDelivery"] = true,
            ["offersPickup"] = true,
            ["contactPhone"] = "+5534999990000",
            ["hoursText"] = "Seg a sáb, 10h30 às 14h",
            ["address"] = new { street = "Rua das Flores", number, complement = (string?)null, neighborhood = "Centro", city = "Uberlândia", state = "MG", zipCode = "38400-000" },
            ["delivery"] = new { feeMode = "DISTANCE", pricePerKm, minimumFee = "3.00", maxDistanceKm = "6.0" },
        };
        if (location is not null || sendLocation)
        {
            body["location"] = location;
        }

        return body;
    }

    private Task<HttpResponseMessage> QuoteRaw(string slug, string street) =>
        Public().PostAsJsonAsync($"/api/v1/public/stores/{slug}/delivery-quotes",
            new { address = Address(street) }, Ct);

    private async Task<JsonElement> Quote(string slug, string street) =>
        await (await QuoteRaw(slug, street)).ExpectAsync(HttpStatusCode.Created, _api);

    private static object Address(string street, string? complement = "Casa 2") =>
        new { street, number = "45", complement, neighborhood = "Centro", city = "Uberlândia", zipCode = (string?)null, reference = (string?)null };

    private Task<HttpResponseMessage> PlaceOrder(string slug, object body) =>
        Public().PostIdempotentAsync($"/api/v1/public/stores/{slug}/orders", body, Guid.NewGuid());

    /// 1 marmita M (Frango, Arroz, Feijão) com ovo: 22,00 + taxa. Pix, sem troco.
    private static object Order(
        OrderingScenario s, Guid? quoteId, string expectedTotal, string street = "Rua das Palmeiras", string? complement = "Casa 2",
        string phone = "(34) 98888-7777", bool pickup = false) => new
        {
            fulfillmentType = pickup ? "PICKUP" : "DELIVERY",
            customerName = "Carla Souza",
            customerPhone = phone,
            deliveryAddress = pickup ? null : Address(street, complement),
            deliveryQuoteId = quoteId,
            expectedPaymentMethod = "PIX",
            changeFor = (string?)null,
            notes = (string?)null,
            items = new object[]
            {
                new
                {
                    itemKind = "MARMITA", marmitaSizeId = s.SizeM, quantity = 1, mainDishProductIds = new[] { s.Frango },
                    sideDishProductIds = new[] { s.Arroz, s.Feijao }, addons = new[] { new { productId = s.Ovo, quantity = 1 } },
                },
            },
            expectedTotal,
            website = (string?)null,
        };

    /// Roteiro do provedor: o restaurante na Rua das Flores e um destino por rua, com a distância de rota conhecida.
    private sealed class ScriptedMaps : IGeocoder, IRouteDistanceProvider
    {
        public static readonly GeoPoint Store = new(-18.900000m, -48.200000m);

        private static readonly Dictionary<string, (GeoPoint Point, int? Meters)> Streets = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Rua das Palmeiras"] = (new GeoPoint(-18.910000m, -48.210000m), 4480), // 4,5 km → 3,60
            ["R. das Palmeiras"] = (new GeoPoint(-18.910000m, -48.210000m), 4480),
            ["Rua Perto"] = (new GeoPoint(-18.905000m, -48.205000m), 2130),         // 2,1 km → mínimo 3,00
            ["Rua Seis"] = (new GeoPoint(-18.920000m, -48.220000m), 6040),          // 6,0 km → 4,80, dentro do raio
            ["Rua Longe"] = (new GeoPoint(-18.930000m, -48.230000m), 6050),         // 6,1 km → fora do raio de 6,0
            ["Rua Ilhada"] = (new GeoPoint(-18.940000m, -48.240000m), null),        // sem rota
        };

        private int _geocodes;
        private int _routes;

        public int GeocodeCalls => _geocodes;

        public int RouteCalls => _routes;

        public volatile bool Down;

        public Task<GeocodeResult?> GeocodeAsync(GeocodeQuery query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _geocodes);
            if (Down)
            {
                throw new MapsUnavailableException("Scripted", "fora do ar");
            }

            if (query.Street.Equals("Rua das Flores", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<GeocodeResult?>(new GeocodeResult(Store, GeocodePrecision.Exact, "Rua das Flores, 123"));
            }

            if (query.Street.Equals("Rua Aproximada", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<GeocodeResult?>(new GeocodeResult(new GeoPoint(-18.95m, -48.25m), GeocodePrecision.Approximate, "Rua Aproximada"));
            }

            return Task.FromResult(Streets.TryGetValue(query.Street, out var street)
                ? new GeocodeResult(street.Point, GeocodePrecision.Exact, $"{query.Street}, {query.Number} - {query.Neighborhood}, {query.City} - {query.State} (geocodificado)")
                : null);
        }

        public Task<int?> DrivingDistanceMetersAsync(GeoPoint origin, GeoPoint destination, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _routes);
            if (Down)
            {
                throw new MapsUnavailableException("Scripted", "fora do ar");
            }

            return Task.FromResult(Streets.Values.First(s => s.Point == destination).Meters);
        }
    }
}
