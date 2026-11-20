using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AlmoSeis.Infrastructure.Security;
using AlmoSeis.Modules.Billing.Contracts;
using AlmoSeis.Modules.Ordering.Jobs;
using AlmoSeis.Api.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Ordering;

/// Página de pedidos pela web, fatia 2 do EP-002 (ADR-0044, ADR-0045, ADR-0047, ADR-0052) contra a API real: perfil e
/// estado, resolução pelo slug com RLS, envio com preço do servidor, idempotência anônima, anti-abuso, acompanhamento,
/// desistência, CORS da página e retenção.
[Collection(nameof(PostgresCollection))]
public sealed class PublicStorefrontFlowTests(PostgresFixture db) : IDisposable
{
    private readonly ApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _api.Dispose();

    // ---------- Perfil, configuração e estado ----------

    [Fact]
    public async Task Page_is_born_inactive_with_a_slug_and_activation_requires_the_address()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var config = await GetStorefront(s);
        config.GetProperty("isPageActive").GetBoolean().ShouldBeFalse();
        config.GetProperty("status").GetString().ShouldBe("UNAVAILABLE");
        var slug = config.GetProperty("slug").GetString()!;
        slug.ShouldStartWith("marmitaria-");
        config.GetProperty("previousSlugs").GetArrayLength().ShouldBe(0);

        var store = await PublicGet(slug);
        store.GetProperty("status").GetString().ShouldBe("UNAVAILABLE");
        store.GetProperty("menu").ValueKind.ShouldBe(JsonValueKind.Null);
        store.GetProperty("address").ValueKind.ShouldBe(JsonValueKind.Null);

        var noAddress = await PutStorefront(s, StorefrontBody(slug, street: null), config.Version());
        (await noAddress.ExpectAsync(HttpStatusCode.UnprocessableEntity, _api)).GetProperty("errors").GetProperty("isPageActive").GetArrayLength().ShouldBeGreaterThan(0);

        var active = await (await PutStorefront(s, StorefrontBody(slug), config.Version())).ExpectAsync(HttpStatusCode.OK, _api);
        active.GetProperty("isPageActive").GetBoolean().ShouldBeTrue();
        active.GetProperty("status").GetString().ShouldBe("OPEN");
        active.Version().ShouldBe(config.Version() + 1);
        active.GetProperty("delivery").GetProperty("pricePerKm").GetString().ShouldBe("0.80");
        active.GetProperty("delivery").GetProperty("maxDistanceKm").GetString().ShouldBe("6.0");
        active.GetProperty("location").ValueKind.ShouldBe(JsonValueKind.Null);

        // If-Match antigo → 409 com o estado atual.
        var stale = await PutStorefront(s, StorefrontBody(slug, accepting: false), config.Version());
        var conflict = await stale.ExpectAsync(HttpStatusCode.Conflict, _api);
        conflict.GetProperty("errorCode").GetString().ShouldBe("VersionConflict");
        conflict.GetProperty("current").GetProperty("isPageActive").GetBoolean().ShouldBeTrue();

        // Auditoria sem telefone nem endereço.
        var audit = await s.ScalarAsync<string>(
            "SELECT after::text FROM audit_logs WHERE tenant_id = @t AND operation_type = 'StorefrontUpdated' ORDER BY occurred_at DESC LIMIT 1");
        audit.ShouldNotContain("99990000");
        audit.ShouldNotContain("Rua das Flores");
    }

    [Fact]
    public async Task Public_profile_shows_the_daily_menu_with_unavailable_items_disabled()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var slug = await ActivateAsync(s);
        var hidden = await HiddenBeverageAsync(s);

        var store = await PublicGet(slug);
        store.GetProperty("status").GetString().ShouldBe("OPEN");
        store.GetProperty("name").GetString().ShouldBe("Marmitaria da Ana");
        store.GetProperty("pickupAvailable").GetBoolean().ShouldBeTrue();
        store.GetProperty("deliveryAvailable").GetBoolean().ShouldBeFalse("a taxa por distância é da fatia 4");
        store.GetProperty("deliveryRule").ValueKind.ShouldBe(JsonValueKind.Null);
        store.GetProperty("address").GetProperty("street").GetString().ShouldBe("Rua das Flores");
        store.GetProperty("businessDate").GetString().ShouldBe(OrderingScenario.Today.ToString("yyyy-MM-dd"));
        store.GetProperty("privacy").GetProperty("controllerName").GetString().ShouldBe("Marmitaria da Ana");
        store.GetProperty("privacy").GetProperty("operatorName").GetString().ShouldBe("AlmoSeis");
        store.GetProperty("limits").GetProperty("maxMarmitas").GetInt32().ShouldBe(20);

        var menu = store.GetProperty("menu");
        menu.GetProperty("marmitaSizes")[0].GetProperty("price").GetString().ShouldBe("20.00");
        var mains = menu.GetProperty("mainDishes").EnumerateArray().ToDictionary(d => d.GetProperty("productId").GetGuid(), d => d.GetProperty("isAvailable").GetBoolean());
        mains[s.Frango].ShouldBeTrue();
        mains[s.Costela].ShouldBeFalse("indisponível aparece desabilitado");
        menu.GetProperty("sideDishes").GetArrayLength().ShouldBe(2);
        menu.GetProperty("addons")[0].GetProperty("price").GetString().ShouldBe("2.00");
        var beverages = menu.GetProperty("beverages").EnumerateArray().Select(b => b.GetProperty("productId").GetGuid()).ToList();
        beverages.ShouldContain(s.Coca);
        beverages.ShouldNotContain(hidden, "isVisibleOnPage = false");
    }

    [Fact]
    public async Task Closed_paused_and_suspended_pages_refuse_orders()
    {
        var closed = await OrderingScenario.CreateAsync(_api, db, openDay: false);
        var closedSlug = await ActivateAsync(closed);
        var closedStore = await PublicGet(closedSlug);
        closedStore.GetProperty("status").GetString().ShouldBe("CLOSED");
        closedStore.GetProperty("menu").ValueKind.ShouldBe(JsonValueKind.Null);
        await ExpectStoreUnavailable(await PlaceOrder(closedSlug, Order(closed)), "CLOSED");

        var s = await OrderingScenario.CreateAsync(_api, db);
        var slug = await ActivateAsync(s);
        var config = await GetStorefront(s);
        await (await PutStorefront(s, StorefrontBody(slug, accepting: false), config.Version())).ExpectAsync(HttpStatusCode.OK, _api);
        (await PublicGet(slug)).GetProperty("status").GetString().ShouldBe("PAUSED");
        await ExpectStoreUnavailable(await PlaceOrder(slug, Order(s)), "PAUSED");

        config = await GetStorefront(s);
        await (await PutStorefront(s, StorefrontBody(slug), config.Version())).ExpectAsync(HttpStatusCode.OK, _api);
        await s.SqlAsync("UPDATE subscriptions SET status = 'SUSPENDED', suspended_at = now() WHERE tenant_id = @t");
        _api.Services.GetRequiredService<ISubscriptionAccess>().Invalidate(s.TenantId);
        var suspended = await PublicGet(slug);
        suspended.GetProperty("status").GetString().ShouldBe("UNAVAILABLE");
        suspended.GetProperty("privacy").ValueKind.ShouldBe(JsonValueKind.Null);
        await ExpectStoreUnavailable(await PlaceOrder(slug, Order(s)), "UNAVAILABLE");
    }

    [Fact]
    public async Task Configuration_requires_storefront_manage()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var attendant = _api.Authenticated(await _api.EmployeeTokenAsync(db, s.Owner, "ATTENDANT"));
        var config = await (await attendant.GetAsync("/api/v1/storefront", Ct)).ExpectAsync(HttpStatusCode.OK, _api);

        var denied = await attendant.SendJsonAsync(HttpMethod.Put, "/api/v1/storefront", StorefrontBody(config.GetProperty("slug").GetString()!), config.Version());
        (await denied.ExpectAsync(HttpStatusCode.Forbidden, _api)).GetProperty("permission").GetString().ShouldBe("storefront.manage");
    }

    // ---------- Slug ----------

    [Fact]
    public async Task Old_slug_redirects_and_slugs_are_unique_and_not_reserved()
    {
        var a = await OrderingScenario.CreateAsync(_api, db);
        var b = await OrderingScenario.CreateAsync(_api, db);
        var oldSlug = await ActivateAsync(a);
        var newSlug = "zelia-" + Guid.NewGuid().ToString("N")[..8];

        var config = await GetStorefront(a);
        var renamed = await (await PutStorefront(a, StorefrontBody(newSlug), config.Version())).ExpectAsync(HttpStatusCode.OK, _api);
        renamed.GetProperty("slug").GetString().ShouldBe(newSlug);
        renamed.GetProperty("previousSlugs")[0].GetString().ShouldBe(oldSlug);

        var client = Public(followRedirects: false);
        var redirect = await client.GetAsync($"/api/v1/public/stores/{oldSlug}?x=1", Ct);
        redirect.StatusCode.ShouldBe(HttpStatusCode.MovedPermanently);
        redirect.Headers.Location!.ToString().ShouldBe($"/api/v1/public/stores/{newSlug}?x=1");
        var post = await client.PostIdempotentAsync($"/api/v1/public/stores/{oldSlug}/orders", Order(a), Guid.NewGuid());
        post.StatusCode.ShouldBe(HttpStatusCode.PermanentRedirect);
        post.Headers.Location!.ToString().ShouldBe($"/api/v1/public/stores/{newSlug}/orders");
        (await client.GetAsync($"/api/v1/public/stores/{oldSlug.ToUpperInvariant()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.MovedPermanently);

        var unknown = await client.GetAsync("/api/v1/public/stores/nao-existe-" + Guid.NewGuid().ToString("N")[..8], Ct);
        (await unknown.ExpectAsync(HttpStatusCode.NotFound, _api)).GetProperty("errorCode").GetString().ShouldBe("StoreNotFound");

        // Slug atual ou antigo de outro tenant, reservado ou ofensivo → 422 em `slug`.
        var bConfig = await GetStorefront(b);
        foreach (var taken in new[] { newSlug, oldSlug, "admin", "pedidos", "Com Espaço", "12345" })
        {
            var refused = await PutStorefront(b, StorefrontBody(taken, active: false), bConfig.Version());
            (await refused.ExpectAsync(HttpStatusCode.UnprocessableEntity, _api)).GetProperty("errors").GetProperty("slug").GetArrayLength().ShouldBeGreaterThan(0, taken);
        }

        // Voltar ao slug antigo do próprio tenant o reativa.
        config = await GetStorefront(a);
        var back = await (await PutStorefront(a, StorefrontBody(oldSlug), config.Version())).ExpectAsync(HttpStatusCode.OK, _api);
        back.GetProperty("slug").GetString().ShouldBe(oldSlug);
        back.GetProperty("previousSlugs")[0].GetString().ShouldBe(newSlug);
        (await PublicGet(oldSlug)).GetProperty("slug").GetString().ShouldBe(oldSlug);
    }

    // ---------- Isolamento entre tenants ----------

    [Fact]
    public async Task Slug_of_A_never_reads_or_writes_data_of_B()
    {
        var a = await OrderingScenario.CreateAsync(_api, db);
        var b = await OrderingScenario.CreateAsync(_api, db);
        var slugA = await ActivateAsync(a);
        await ActivateAsync(b);

        var store = await PublicGet(slugA);
        var ids = store.GetProperty("menu").EnumerateObject().SelectMany(p => p.Value.EnumerateArray())
            .Select(i => i.TryGetProperty("productId", out var id) ? id.GetGuid() : i.GetProperty("id").GetGuid()).ToList();
        ids.ShouldContain(a.Frango);
        ids.ShouldNotContain(b.Frango);
        ids.ShouldNotContain(b.SizeM);

        // Produtos de B no carrinho de A: não estão no cardápio de A → carrinho mudou, nada é gravado.
        var foreign = await PlaceOrder(slugA, Order(b));
        (await foreign.ExpectAsync(HttpStatusCode.Conflict, _api)).GetProperty("errorCode").GetString().ShouldBe("CartChanged");

        // Authorization de B numa rota pública de A é ignorado: o tenant vem só do slug.
        var withBToken = Public();
        withBToken.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", b.Owner.AccessToken());
        var placed = await (await withBToken.PostIdempotentAsync($"/api/v1/public/stores/{slugA}/orders", Order(a), Guid.NewGuid()))
            .ExpectAsync(HttpStatusCode.Created, _api);
        var orderId = await a.ScalarAsync<Guid>("SELECT id FROM orders WHERE tenant_id = @t AND origin = 'WEB'");

        (await b.Client.GetAsync($"/api/v1/orders/{orderId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.ScalarAsync<long>("SELECT count(*) FROM orders WHERE tenant_id = @t AND origin = 'WEB'")).ShouldBe(0);
        (await a.GetOrderAsync(orderId)).GetProperty("origin").GetString().ShouldBe("WEB");

        var tracking = await TrackingGet(placed.GetProperty("trackingToken").GetString()!);
        tracking.GetProperty("store").GetProperty("slug").GetString().ShouldBe(slugA);
    }

    // ---------- Envio ----------

    [Fact]
    public async Task Price_is_always_computed_on_the_server()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var slug = await ActivateAsync(s);

        // 2 × (20,00 + 2,00 ovo) + 6,00 Coca = 50,00. O cliente diz 30,00.
        var wrong = await PlaceOrder(slug, Order(s, expectedTotal: "30.00"));
        var problem = await wrong.ExpectAsync(HttpStatusCode.Conflict, _api);
        problem.GetProperty("errorCode").GetString().ShouldBe("CartChanged");
        var cart = problem.GetProperty("cart");
        cart.GetProperty("totalAmount").GetString().ShouldBe("50.00");
        cart.GetProperty("items")[0].GetProperty("lineTotal").GetString().ShouldBe("44.00");
        cart.GetProperty("items")[1].GetProperty("unitPrice").GetString().ShouldBe("6.00");

        // Prato indisponível (Costela) → carrinho mudou, com o item marcado.
        var unavailable = await PlaceOrder(slug, Order(s, main: s.Costela, expectedTotal: "50.00"));
        var cart2 = (await unavailable.ExpectAsync(HttpStatusCode.Conflict, _api)).GetProperty("cart");
        cart2.GetProperty("items")[0].GetProperty("available").GetBoolean().ShouldBeFalse();
        cart2.GetProperty("unavailableProductIds")[0].GetGuid().ShouldBe(s.Costela);

        // Preço no corpo é ignorado (campo desconhecido): o pedido fica com o preço do tamanho.
        var placed = await (await PlaceOrder(slug, Order(s, extraPrice: true))).ExpectAsync(HttpStatusCode.Created, _api);
        placed.GetProperty("totalAmount").GetString().ShouldBe("50.00");
        placed.GetProperty("acceptanceStatus").GetString().ShouldBe("AWAITING_ACCEPTANCE");
        placed.TryGetProperty("customerPhone", out _).ShouldBeFalse();
        var token = placed.GetProperty("trackingToken").GetString()!;
        token.Length.ShouldBe(22);
        (await s.ScalarAsync<long>("SELECT count(*) FROM order_tracking_tokens WHERE tenant_id = @t AND token_hash = @h",
            ("h", _api.Services.GetRequiredService<ISecretHasher>().Hash(token)))).ShouldBe(1, "só o hash é guardado");
    }

    [Fact]
    public async Task Web_order_shows_up_awaiting_acceptance_and_goes_through_the_acceptance()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var slug = await ActivateAsync(s);
        var placed = await (await PlaceOrder(slug, Order(s))).ExpectAsync(HttpStatusCode.Created, _api);
        var token = placed.GetProperty("trackingToken").GetString()!;

        var awaiting = await (await s.Client.GetAsync("/api/v1/orders?acceptanceStatus=AWAITING_ACCEPTANCE", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        var summary = awaiting.GetProperty("items").EnumerateArray().Single();
        var order = await s.GetOrderAsync(summary.Id());
        order.GetProperty("origin").GetString().ShouldBe("WEB");
        order.GetProperty("acceptanceStatus").GetString().ShouldBe("AWAITING_ACCEPTANCE");
        order.GetProperty("orderNumber").ValueKind.ShouldBe(JsonValueKind.Null);
        order.GetProperty("customer").GetProperty("phoneNumber").GetString().ShouldBe("+5534988887777");
        order.GetProperty("expectedPaymentMethod").GetString().ShouldBe("CASH");
        order.GetProperty("changeFor").GetString().ShouldBe("50.00");
        order.GetProperty("riskFlag").ValueKind.ShouldBe(JsonValueKind.Null);
        order.GetProperty("totalAmount").GetString().ShouldBe("50.00");
        order.GetProperty("createdBy").ValueKind.ShouldBe(JsonValueKind.Null);

        // Auditoria do envio: origem WEB, sem ator e sem dado pessoal.
        var audit = await s.ScalarAsync<string>(
            "SELECT origin || ' ' || coalesce(actor_employee_id::text, '-') || ' ' || after::text FROM audit_logs WHERE tenant_id = @t AND operation_type = 'WebOrderSubmitted'");
        audit.ShouldStartWith("WEB - ");
        audit.ShouldNotContain("8888");
        audit.ShouldNotContain("Carla");

        var before = await TrackingGet(token);
        before.GetProperty("status").GetString().ShouldBe("AWAITING_ACCEPTANCE");
        before.GetProperty("canWithdraw").GetBoolean().ShouldBeTrue();

        var accepted = await (await s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/accept", new { estimatedReadyMinutes = 30 }, order.Version()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        accepted.GetProperty("orderNumber").GetInt32().ShouldBe(1);

        var after = await TrackingGet(token);
        after.GetProperty("status").GetString().ShouldBe("CONFIRMED");
        after.GetProperty("orderNumber").GetInt32().ShouldBe(1);
        after.GetProperty("estimatedReadyMinutes").GetInt32().ShouldBe(30);
        after.GetProperty("canWithdraw").GetBoolean().ShouldBeFalse();
        after.GetProperty("timeline").EnumerateArray().Select(t => t.GetProperty("step").GetString()).ShouldBe(["SUBMITTED", "ACCEPTED"]);

        await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{order.Id()}/status", new { productionStatus = "IN_PROGRESS" }, accepted.Version()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        (await TrackingGet(token)).GetProperty("status").GetString().ShouldBe("IN_PREPARATION");
    }

    [Fact]
    public async Task Anonymous_idempotency_replays_the_same_order()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var slug = await ActivateAsync(s);
        var key = Guid.NewGuid();
        var client = Public();

        var first = await client.PostIdempotentAsync($"/api/v1/public/stores/{slug}/orders", Order(s), key);
        var firstBody = await first.ExpectAsync(HttpStatusCode.Created, _api);
        var replay = await client.PostIdempotentAsync($"/api/v1/public/stores/{slug}/orders", Order(s), key);
        var replayBody = await replay.ExpectAsync(HttpStatusCode.Created, _api);

        replay.Headers.GetValues("Idempotent-Replayed").Single().ShouldBe("true");
        replayBody.GetProperty("trackingToken").GetString().ShouldBe(firstBody.GetProperty("trackingToken").GetString());
        (await s.ScalarAsync<long>("SELECT count(*) FROM orders WHERE tenant_id = @t AND origin = 'WEB'")).ShouldBe(1);
        (await s.ScalarAsync<long>("SELECT count(*) FROM idempotency_records WHERE tenant_id = @t AND user_id IS NULL")).ShouldBe(1);

        var reused = await client.PostIdempotentAsync($"/api/v1/public/stores/{slug}/orders", Order(s, quantity: 1, expectedTotal: "28.00"), key);
        (await reused.ExpectAsync(HttpStatusCode.UnprocessableEntity, _api)).GetProperty("errorCode").GetString().ShouldBe("IdempotencyKeyReused");

        var noKey = await client.PostAsJsonAsync($"/api/v1/public/stores/{slug}/orders", Order(s), Ct);
        (await noKey.ExpectAsync((HttpStatusCode)428, _api)).GetProperty("errorCode").GetString().ShouldBe("IdempotencyKeyRequired");
    }

    [Fact]
    public async Task Order_limits_and_honeypot()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var slug = await ActivateAsync(s);

        async Task<JsonElement> Refused(object body, HttpStatusCode status) => await (await PlaceOrder(slug, body)).ExpectAsync(status, _api);

        // 21 marmitas (11 + 10): acima do limite.
        var tooMany = await Refused(Order(s, quantity: 11, secondMarmita: 10), HttpStatusCode.UnprocessableEntity);
        tooMany.GetProperty("errors").GetProperty("items").GetArrayLength().ShouldBeGreaterThan(0);

        // Só bebidas.
        var drinks = await Refused(new
        {
            fulfillmentType = "PICKUP", customerName = "Carla", customerPhone = "34988887777", expectedPaymentMethod = "PIX", expectedTotal = "6.00",
            items = new object[] { new { itemKind = "BEVERAGE", productId = s.Coca, quantity = 1 } },
        }, HttpStatusCode.UnprocessableEntity);
        drinks.GetProperty("errors").GetProperty("items")[0].GetString()!.ShouldContain("marmita");

        // PF não é vendido pela página; telefone sem DDD; troco em Pix.
        var pf = await Refused(new
        {
            fulfillmentType = "PICKUP", customerName = "Carla", customerPhone = "98888-7777", expectedPaymentMethod = "PIX", changeFor = "50.00", expectedTotal = "24.00",
            items = new object[] { new { itemKind = "PF", productId = s.Frango, quantity = 1 } },
        }, HttpStatusCode.UnprocessableEntity);
        var errors = pf.GetProperty("errors");
        errors.GetProperty("items[0].itemKind").GetArrayLength().ShouldBeGreaterThan(0);
        errors.GetProperty("customerPhone").GetArrayLength().ShouldBeGreaterThan(0);
        errors.GetProperty("changeFor").GetArrayLength().ShouldBeGreaterThan(0);

        // Entrega ainda não existe na página (fatia 4).
        var delivery = await Refused(Order(s, delivery: true), HttpStatusCode.UnprocessableEntity);
        delivery.GetProperty("errorCode").GetString().ShouldBe("DeliveryUnavailable");
        var quote = await Public().PostAsJsonAsync($"/api/v1/public/stores/{slug}/delivery-quotes",
            new { address = new { street = "Rua A", number = "1", complement = (string?)null, neighborhood = "Centro", city = "Uberlândia", zipCode = (string?)null, reference = (string?)null } }, Ct);
        (await quote.ExpectAsync(HttpStatusCode.UnprocessableEntity, _api)).GetProperty("errorCode").GetString().ShouldBe("DeliveryUnavailable");

        // Campo-isca preenchido: 400 sem detalhes.
        var bot = await Refused(Order(s, website: "http://spam"), HttpStatusCode.BadRequest);
        bot.GetProperty("errorCode").GetString().ShouldBe("MalformedRequest");
        bot.TryGetProperty("errors", out _).ShouldBeFalse();

        (await s.ScalarAsync<long>("SELECT count(*) FROM orders WHERE tenant_id = @t")).ShouldBe(0);
    }

    [Fact]
    public async Task At_most_two_pending_orders_per_phone_and_three_per_ip()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var slug = await ActivateAsync(s);

        await (await PlaceOrder(slug, Order(s, phone: "(34) 91111-0001"))).ExpectAsync(HttpStatusCode.Created, _api);
        await (await PlaceOrder(slug, Order(s, phone: "34911110001"))).ExpectAsync(HttpStatusCode.Created, _api);
        var third = await PlaceOrder(slug, Order(s, phone: "+55 34 91111-0001"));
        (await third.ExpectAsync(HttpStatusCode.TooManyRequests, _api)).GetProperty("errorCode").GetString().ShouldBe("TooManyPendingOrders");

        // Mesmo IP (o TestServer não tem IP de cliente: todos caem na mesma partição): 3 aguardando é o máximo.
        await (await PlaceOrder(slug, Order(s, phone: "34922220002"))).ExpectAsync(HttpStatusCode.Created, _api);
        var fourth = await PlaceOrder(slug, Order(s, phone: "34933330003"));
        (await fourth.ExpectAsync(HttpStatusCode.TooManyRequests, _api)).GetProperty("errorCode").GetString().ShouldBe("TooManyPendingOrders");

        // Um pedido que deixa de aguardar (recusado) libera a vaga.
        var first = await s.ScalarAsync<Guid>("SELECT id FROM orders WHERE tenant_id = @t ORDER BY created_at LIMIT 1");
        var order = await s.GetOrderAsync(first);
        await (await s.Client.SendJsonAsync(HttpMethod.Post, $"/api/v1/orders/{first}/reject", new { reasonCode = "TOO_BUSY", message = (string?)null }, order.Version()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        await (await PlaceOrder(slug, Order(s, phone: "34933330003"))).ExpectAsync(HttpStatusCode.Created, _api);
    }

    // ---------- Acompanhamento e desistência ----------

    [Fact]
    public async Task Tracking_shows_only_what_the_adr_allows_and_wrong_tokens_reveal_nothing()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var slug = await ActivateAsync(s);
        var token = (await (await PlaceOrder(slug, Order(s))).ExpectAsync(HttpStatusCode.Created, _api)).GetProperty("trackingToken").GetString()!;

        var response = await Public().GetAsync($"/api/v1/public/tracking/{token}", Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        var tracking = await response.ExpectAsync(HttpStatusCode.OK, _api);
        tracking.GetProperty("customerFirstName").GetString().ShouldBe("Carla");
        tracking.GetProperty("store").GetProperty("name").GetString().ShouldBe("Marmitaria da Ana");
        tracking.GetProperty("items")[0].GetProperty("addons")[0].GetProperty("name").GetString().ShouldBe("Ovo frito");
        tracking.GetProperty("totalAmount").GetString().ShouldBe("50.00");
        tracking.GetProperty("deliveryAddress").ValueKind.ShouldBe(JsonValueKind.Null);
        raw.ShouldNotContain("Souza");
        raw.ShouldNotContain("8888");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Headers.GetValues("Referrer-Policy").Single().ShouldBe("no-referrer");
        response.Headers.GetValues("X-Robots-Tag").Single().ShouldBe("noindex");

        var etag = response.Headers.ETag!.Tag;
        var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/public/tracking/{token}");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);
        (await Public().SendAsync(conditional, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotModified);

        var invented = await Public().GetAsync($"/api/v1/public/tracking/{TrackingTokens.New()}", Ct);
        var inventedBody = await invented.ExpectAsync(HttpStatusCode.NotFound, _api);
        inventedBody.GetProperty("errorCode").GetString().ShouldBe("NotFound");
        inventedBody.ToString().ShouldNotContain("Marmitaria");
        invented.Headers.CacheControl!.NoStore.ShouldBeTrue();
        (await Public().GetAsync("/api/v1/public/tracking/curto", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await s.SqlAsync("UPDATE order_tracking_tokens SET created_at = now() - interval '8 days', expires_at = now() - interval '1 day' WHERE tenant_id = @t");
        var expired = await Public().GetAsync($"/api/v1/public/tracking/{token}", Ct);
        (await expired.ExpectAsync(HttpStatusCode.Gone, _api)).GetProperty("errorCode").GetString().ShouldBe("TrackingExpired");
    }

    [Fact]
    public async Task Customer_can_withdraw_while_awaiting_acceptance()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var slug = await ActivateAsync(s);
        var token = (await (await PlaceOrder(slug, Order(s))).ExpectAsync(HttpStatusCode.Created, _api)).GetProperty("trackingToken").GetString()!;

        var withdrawn = await (await Public().PostAsync($"/api/v1/public/tracking/{token}/withdraw", null, Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        withdrawn.GetProperty("status").GetString().ShouldBe("WITHDRAWN");
        withdrawn.GetProperty("canWithdraw").GetBoolean().ShouldBeFalse();
        withdrawn.GetProperty("timeline").EnumerateArray().Last().GetProperty("step").GetString().ShouldBe("WITHDRAWN");

        var again = await Public().PostAsync($"/api/v1/public/tracking/{token}/withdraw", null, Ct);
        (await again.ExpectAsync(HttpStatusCode.Conflict, _api)).GetProperty("errorCode").GetString().ShouldBe("OrderNotAwaitingAcceptance");

        var audit = await s.ScalarAsync<string>("SELECT origin FROM audit_logs WHERE tenant_id = @t AND operation_type = 'WebOrderWithdrawn'");
        audit.ShouldBe("WEB");
    }

    // ---------- Anti-abuso: Turnstile, rate limit, CORS ----------

    [Fact]
    public async Task Turnstile_when_enabled_blocks_robots_and_marks_orders_when_cloudflare_is_down()
    {
        using var api = new ApiFactory(db, services =>
        {
            services.Configure<TurnstileOptions>(o => (o.Enabled, o.SecretKey) = (true, "test-secret"));
            services.AddHttpClient<ITurnstileVerifier, TurnstileVerifier>().ConfigurePrimaryHttpMessageHandler(() => new FakeSiteVerify());
        });
        var s = await OrderingScenario.CreateAsync(api, db);
        var slug = await ActivateAsync(s, api);
        var client = api.CreateClient();

        Task<HttpResponseMessage> Send(string? captcha, string phone)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/public/stores/{slug}/orders") { Content = JsonContent.Create(Order(s, phone: phone)) };
            request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
            if (captcha is not null)
            {
                request.Headers.TryAddWithoutValidation("X-Turnstile-Token", captcha);
            }

            return client.SendAsync(request, Ct);
        }

        (await (await Send(null, "34911110001")).ExpectAsync(HttpStatusCode.Forbidden, api)).GetProperty("errorCode").GetString().ShouldBe("CaptchaFailed");
        (await (await Send("fail", "34911110001")).ExpectAsync(HttpStatusCode.Forbidden, api)).GetProperty("errorCode").GetString().ShouldBe("CaptchaFailed");
        await (await Send("pass", "34911110001")).ExpectAsync(HttpStatusCode.Created, api);
        await (await Send("down", "34922220002")).ExpectAsync(HttpStatusCode.Created, api);

        var flags = await s.ScalarAsync<string>(
            "SELECT string_agg(coalesce(risk_flag, '-'), ',' ORDER BY created_at) FROM orders WHERE tenant_id = @t");
        flags.ShouldBe("-,CAPTCHA_UNAVAILABLE");
        var store = await (await client.GetAsync($"/api/v1/public/stores/{slug}", Ct)).ExpectAsync(HttpStatusCode.OK, api);
        store.GetProperty("privacy").GetProperty("thirdParties")[0].GetString().ShouldBe("ANTI_BOT");
    }

    [Fact]
    public async Task Public_routes_are_rate_limited_per_ip()
    {
        using var api = new ApiFactory(db, services => services.Configure<RateLimitSettings>(o => o.Enabled = true));
        var s = await OrderingScenario.CreateAsync(api, db);
        var slug = await ActivateAsync(s, api);
        var client = api.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            statuses.Add((await client.PostIdempotentAsync($"/api/v1/public/stores/{slug}/orders", Order(s, website: "bot"), Guid.NewGuid())).StatusCode);
        }

        statuses.Take(5).ShouldAllBe(c => c == HttpStatusCode.BadRequest);
        statuses.Last().ShouldBe(HttpStatusCode.TooManyRequests);

        // Qualquer envio seguinte do mesmo IP continua barrado, com Retry-After.
        var limited = await client.PostIdempotentAsync($"/api/v1/public/stores/{slug}/orders", Order(s), Guid.NewGuid());
        (await limited.ExpectAsync(HttpStatusCode.TooManyRequests, api)).GetProperty("errorCode").GetString().ShouldBe("TooManyRequests");
        limited.Headers.RetryAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task Public_page_cors_is_separate_from_the_restaurant_app_and_has_no_credentials()
    {
        var client = _api.CreateClient();

        var preflight = await Preflight(client, "/api/v1/public/stores/qualquer/orders", ApiFactory.PublicPageOrigin, "content-type,idempotency-key,x-turnstile-token");
        preflight.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(ApiFactory.PublicPageOrigin);
        preflight.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();

        // Origem do app do restaurante nas rotas públicas, e origem da página nas rotas do restaurante: nada.
        var appOnPublic = await Preflight(client, "/api/v1/public/stores/qualquer/orders", ApiFactory.WebOrigin, "content-type");
        appOnPublic.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
        var pageOnApp = await Preflight(client, "/api/v1/orders", ApiFactory.PublicPageOrigin, "content-type,authorization");
        pageOnApp.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();

        // Resposta real (404 StoreNotFound) também leva o CORS da página, sem credenciais.
        var get = new HttpRequestMessage(HttpMethod.Get, "/api/v1/public/stores/nao-existe-xyz");
        get.Headers.TryAddWithoutValidation("Origin", ApiFactory.PublicPageOrigin);
        var response = await client.SendAsync(get, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(ApiFactory.PublicPageOrigin);
        response.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();
    }

    // ---------- LGPD ----------

    [Fact]
    public async Task Retention_anonymizes_not_accepted_orders_after_seven_days_and_purges_tokens_and_ip_hashes()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var slug = await ActivateAsync(s);
        var oldToken = (await (await PlaceOrder(slug, Order(s, phone: "34911110001"))).ExpectAsync(HttpStatusCode.Created, _api)).GetProperty("trackingToken").GetString()!;
        await (await PlaceOrder(slug, Order(s, phone: "34922220002"))).ExpectAsync(HttpStatusCode.Created, _api);
        await (await Public().PostAsync($"/api/v1/public/tracking/{oldToken}/withdraw", null, Ct)).ExpectAsync(HttpStatusCode.OK, _api);

        var withdrawnId = await s.ScalarAsync<Guid>("SELECT id FROM orders WHERE tenant_id = @t AND acceptance_status = 'WITHDRAWN'");
        await s.SqlAsync(
            """
            UPDATE orders SET withdrawn_at = now() - interval '8 days', created_at = now() - interval '8 days' WHERE tenant_id = @t AND id = @o;
            UPDATE order_tracking_tokens SET created_at = now() - interval '8 days', expires_at = now() - interval '1 day' WHERE tenant_id = @t AND order_id = @o;
            UPDATE web_order_intakes SET created_at = now() - interval '8 days' WHERE tenant_id = @t AND order_id = @o;
            """, ("o", withdrawnId));

        var result = await _api.Services.GetRequiredService<WebOrderRetentionJob>().RunOnceAsync(Ct);
        result.ShouldNotBeNull();
        result.Anonymized.ShouldBeGreaterThanOrEqualTo(1);

        var anonymized = await s.GetOrderAsync(withdrawnId);
        anonymized.GetProperty("customer").GetProperty("name").GetString().ShouldBe("Cliente anonimizado");
        anonymized.GetProperty("customer").GetProperty("phoneNumber").ValueKind.ShouldBe(JsonValueKind.Null);
        anonymized.GetProperty("notes").ValueKind.ShouldBe(JsonValueKind.Null);
        anonymized.GetProperty("totalAmount").GetString().ShouldBe("50.00", "itens e valores ficam");
        (await s.ScalarAsync<long>("SELECT count(*) FROM order_tracking_tokens WHERE tenant_id = @t")).ShouldBe(1, "só o token vencido sai");
        (await s.ScalarAsync<long>("SELECT count(*) FROM web_order_intakes WHERE tenant_id = @t")).ShouldBe(1);
        (await s.ScalarAsync<string>("SELECT origin FROM audit_logs WHERE tenant_id = @t AND operation_type = 'WebOrderAnonymized'")).ShouldBe("SYSTEM");

        // O pedido ainda aguardando não é tocado.
        (await s.ScalarAsync<long>("SELECT count(*) FROM orders WHERE tenant_id = @t AND customer_phone = '+5534922220002'")).ShouldBe(1);
        (await Public().GetAsync($"/api/v1/public/tracking/{oldToken}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---------- Apoio ----------

    private HttpClient Public(bool followRedirects = true) =>
        _api.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = followRedirects });

    private async Task<JsonElement> PublicGet(string slug) =>
        await (await Public().GetAsync($"/api/v1/public/stores/{slug}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);

    private async Task<JsonElement> TrackingGet(string token) =>
        await (await Public().GetAsync($"/api/v1/public/tracking/{token}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);

    private Task<HttpResponseMessage> PlaceOrder(string slug, object body) =>
        Public().PostIdempotentAsync($"/api/v1/public/stores/{slug}/orders", body, Guid.NewGuid());

    private async Task ExpectStoreUnavailable(HttpResponseMessage response, string status)
    {
        var problem = await response.ExpectAsync(HttpStatusCode.Conflict, _api);
        problem.GetProperty("errorCode").GetString().ShouldBe("StoreUnavailable");
        problem.GetProperty("storeStatus").GetString().ShouldBe(status);
    }

    private async Task<JsonElement> GetStorefront(OrderingScenario s) =>
        await (await s.Client.GetAsync("/api/v1/storefront", Ct)).ExpectAsync(HttpStatusCode.OK, s.Api);

    private static Task<HttpResponseMessage> PutStorefront(OrderingScenario s, object body, int version) =>
        s.Client.SendJsonAsync(HttpMethod.Put, "/api/v1/storefront", body, version);

    private static async Task<string> ActivateAsync(OrderingScenario s, ApiFactory? api = null)
    {
        var config = await (await s.Client.GetAsync("/api/v1/storefront", Ct)).ExpectAsync(HttpStatusCode.OK, api ?? s.Api);
        var slug = config.GetProperty("slug").GetString()!;
        await (await PutStorefront(s, StorefrontBody(slug), config.Version())).ExpectAsync(HttpStatusCode.OK, api ?? s.Api);
        return slug;
    }

    private static object StorefrontBody(string slug, bool active = true, bool accepting = true, string? street = "Rua das Flores") => new
    {
        slug,
        publicName = "Marmitaria da Ana",
        isPageActive = active,
        isAcceptingOrders = accepting,
        isDeliveryEnabled = true,
        offersDelivery = true,
        offersPickup = true,
        contactPhone = "+5534999990000",
        hoursText = "Seg a sáb, 10h30 às 14h",
        address = new { street, number = "123", complement = (string?)null, neighborhood = "Centro", city = "Uberlândia", state = "MG", zipCode = "38400-000" },
        delivery = new { feeMode = "DISTANCE", pricePerKm = "0.80", minimumFee = "3.00", maxDistanceKm = "6.0" },
    };

    /// 2 marmitas M (Frango, Arroz, Feijão) com ovo + 1 Coca: 2 × 22,00 + 6,00 = 50,00.
    private static object Order(
        OrderingScenario s, string expectedTotal = "50.00", string phone = "(34) 98888-7777", int quantity = 2, Guid? main = null,
        int secondMarmita = 0, bool delivery = false, string? website = null, bool extraPrice = false)
    {
        var items = new List<object>
        {
            new
            {
                itemKind = "MARMITA", marmitaSizeId = s.SizeM, quantity, mainDishProductIds = new[] { main ?? s.Frango },
                sideDishProductIds = new[] { s.Arroz, s.Feijao }, addons = new[] { new { productId = s.Ovo, quantity = 1 } }, notes = "Sem cebola",
            },
        };
        if (secondMarmita > 0)
        {
            items.Add(new { itemKind = "MARMITA", marmitaSizeId = s.SizeM, quantity = secondMarmita, mainDishProductIds = new[] { s.Frango } });
        }

        items.Add(extraPrice
            ? new { itemKind = "BEVERAGE", productId = s.Coca, quantity = 1, unitPrice = "0.01" }
            : new { itemKind = "BEVERAGE", productId = s.Coca, quantity = 1 });

        return new
        {
            fulfillmentType = delivery ? "DELIVERY" : "PICKUP",
            customerName = "Carla Souza",
            customerPhone = phone,
            deliveryAddress = delivery
                ? new { street = "Rua das Palmeiras", number = "45", complement = "Casa 2", neighborhood = "Centro", city = "Uberlândia", zipCode = (string?)null, reference = (string?)null }
                : null,
            deliveryQuoteId = (Guid?)null,
            expectedPaymentMethod = "CASH",
            changeFor = quantity == 2 && secondMarmita == 0 ? "50.00" : null,
            notes = "Sem talher",
            items,
            expectedTotal,
            website,
        };
    }

    /// Bebida ativa, mas escondida da página (ADR-0044 item 5).
    private async Task<Guid> HiddenBeverageAsync(OrderingScenario s)
    {
        var created = await (await s.Client.PostAsJsonAsync("/api/v1/products",
            new { kind = "BEVERAGE", name = "Cerveja", description = (string?)null, category = (string?)null, defaultPrice = "9.00", requiresPreparation = (bool?)null }, Ct))
            .ExpectAsync(HttpStatusCode.Created, _api);
        var updated = await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/products/{created.Id()}", new { isVisibleOnPage = false }, created.Version()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        updated.GetProperty("isVisibleOnPage").GetBoolean().ShouldBeFalse();
        return created.Id();
    }

    private static async Task<HttpResponseMessage> Preflight(HttpClient client, string url, string origin, string headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, url);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", headers);
        return await client.SendAsync(request, Ct);
    }

    /// `siteverify` do Cloudflare: "pass" passa, "fail" falha, "down" simula o Cloudflare fora do ar.
    private sealed class FakeSiteVerify : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            form.ShouldContain("secret=test-secret");
            if (form.Contains("response=down", StringComparison.Ordinal))
            {
                throw new HttpRequestException("Cloudflare fora do ar");
            }

            var success = form.Contains("response=pass", StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"success":{{(success ? "true" : "false")}},"error-codes":[]}""", Encoding.UTF8, "application/json"),
            };
        }
    }
}
