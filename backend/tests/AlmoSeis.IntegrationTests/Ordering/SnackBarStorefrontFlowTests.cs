using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Ordering;

/// Página de pedidos da lanchonete, EP-003 fatia 5 (RF-316; ADR-0044 complementado, ADR-0057, ADR-0058 item 14; Q-309,
/// Q-317, Q-318) contra a API real: seções da página, cardápio fixo só com o que vende no site e não está pausado, pelo
/// preço de site; pedido `A_LA_CARTE` com combo; um tipo por pedido; limites; aceite, cozinha, acompanhamento e item
/// acrescentado no app; ativação; restaurante com os dois comportamentos; isolamento entre tenants.
[Collection(nameof(PostgresCollection))]
public sealed class SnackBarStorefrontFlowTests(PostgresFixture db) : IDisposable
{
    private const string FixedMenu = "/api/v1/fixed-menu";

    private readonly ApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _api.Dispose();

    /// Lanchonete com o expediente aberto e a página ativa. Balcão → site:
    /// - Lanches: X-Burguer 22,00 → 23,90 (grupo "Adicionais de lanche": Bacon 4,00 e Cheddar 3,00);
    /// - Porções: Batata frita P 12,00 → 13,00 e G 18,00 (G não vende no site);
    /// - Sobremesas: Pudim 8,00 (não vende no site: a aba some);
    /// - Refrigerante Lata 350 ml 6,00 → 6,50 (Coca-Cola e Coca-Cola Zero +0,50) e 2 L 14,00 (não vende no site);
    /// - combo "X-Burguer + batata + refri" 32,00 → 34,90 (X-Burguer + Batata P + Lata) e "Batata + refri" 16,00 (não vende).
    private sealed record Shop(
        JsonElement Owner, HttpClient Client, Guid TenantId, string Slug, Guid Lanches, Guid Sobremesas, Guid XBurguer, Guid Batata, Guid BatataP,
        Guid BatataG, Guid Pudim, Guid Refri, Guid Lata, Guid DoisLitros, Guid Coca, Guid CocaZero, Guid Grupo, Guid Bacon, Guid Cheddar,
        Guid Combo, Guid BurgerPart, Guid BatataPart, Guid LataPart, Guid ComboOffSite);

    private async Task<Shop> ShopAsync(bool activate = true, bool openDay = true)
    {
        var owner = await _api.SignUpAsync(ApiScenarios.SignUpBody(behavior: "SNACK_BAR"));
        var client = _api.Authenticated(owner.AccessToken());
        var m = await SeedFixedMenuAsync(client);
        if (openDay)
        {
            await (await client.PostIdempotentAsync("/api/v1/business-days", new { openingBalance = "100.00", confirmSameDate = false }, Guid.NewGuid()))
                .ExpectAsync(HttpStatusCode.Created, _api);
        }

        var slug = activate ? await ActivateAsync(client) : (await GetStorefront(client)).GetProperty("slug").GetString()!;
        return m with { Owner = owner, Client = client, TenantId = Guid.Parse(owner.TenantId()), Slug = slug };
    }

    private async Task<Shop> SeedFixedMenuAsync(HttpClient client)
    {
        var lanches = (await Created(client, "dish-categories", new { name = "Lanches", sortOrder = 1 })).Id();
        var porcoes = (await Created(client, "dish-categories", new { name = "Porções", sortOrder = 2 })).Id();
        var sobremesas = (await Created(client, "dish-categories", new { name = "Sobremesas", sortOrder = 3 })).Id();
        var xburguer = (await Created(client, "dishes", new
        {
            dishCategoryId = lanches, name = "X-Burguer", description = "Pão, hambúrguer 150 g, queijo", requiresPreparation = (bool?)null, hasSizes = false,
            price = "22.00", sellsOnSite = true, sitePrice = "23.90", sortOrder = 1, sizes = (object[]?)null,
        })).Id();
        var batata = await Created(client, "dishes", new
        {
            dishCategoryId = porcoes, name = "Batata frita", description = (string?)null, requiresPreparation = (bool?)null, hasSizes = true,
            price = (string?)null, sellsOnSite = false, sitePrice = (string?)null, sortOrder = 1,
            sizes = new[]
            {
                new { id = (Guid?)null, name = "P", price = "12.00", sellsOnSite = true, sitePrice = (string?)"13.00", sortOrder = 1 },
                new { id = (Guid?)null, name = "G", price = "18.00", sellsOnSite = false, sitePrice = (string?)null, sortOrder = 2 },
            },
        });
        var pudim = (await Created(client, "dishes", new
        {
            dishCategoryId = sobremesas, name = "Pudim", description = (string?)null, requiresPreparation = false, hasSizes = false,
            price = "8.00", sellsOnSite = false, sitePrice = (string?)null, sortOrder = 1, sizes = (object[]?)null,
        })).Id();
        var refri = await Created(client, "beverage-types", new
        {
            name = "Refrigerante", sortOrder = 1,
            sizes = new object[]
            {
                new
                {
                    id = (Guid?)null, name = "Lata 350 ml", price = "6.00", sellsOnSite = true, sitePrice = "6.50", sortOrder = 1,
                    flavors = new[]
                    {
                        new { id = (Guid?)null, name = "Coca-Cola", extraPrice = "0.00", sortOrder = 1 },
                        new { id = (Guid?)null, name = "Coca-Cola Zero", extraPrice = "0.50", sortOrder = 2 },
                    },
                },
                new { id = (Guid?)null, name = "2 L", price = "14.00", sellsOnSite = false, sitePrice = (string?)null, sortOrder = 2, flavors = Array.Empty<object>() },
            },
        });
        var lata = refri.GetProperty("sizes").EnumerateArray().Single(z => z.GetProperty("name").GetString() == "Lata 350 ml");
        var doisLitros = refri.GetProperty("sizes").EnumerateArray().Single(z => z.GetProperty("name").GetString() == "2 L");
        var grupo = await Created(client, "addon-groups", new
        {
            name = "Adicionais de lanche", sortOrder = 1,
            options = new[]
            {
                new { id = (Guid?)null, name = "Bacon", price = "4.00", sortOrder = 1 },
                new { id = (Guid?)null, name = "Cheddar", price = "3.00", sortOrder = 2 },
            },
            dishCategoryIds = new[] { lanches }, dishIds = Array.Empty<Guid>(),
        });
        var batataP = SizeId(batata, "P");
        var combo = await Created(client, "combos", new
        {
            name = "X-Burguer + batata + refri", description = (string?)null, price = "32.00", sellsOnSite = true, sitePrice = "34.90", sortOrder = 1,
            components = new object[]
            {
                new { kind = "DISH", dishId = xburguer, dishSizeId = (Guid?)null, beverageSizeId = (Guid?)null, quantity = 1 },
                new { kind = "DISH", dishId = batata.Id(), dishSizeId = (Guid?)batataP, beverageSizeId = (Guid?)null, quantity = 1 },
                new { kind = "BEVERAGE", dishId = (Guid?)null, dishSizeId = (Guid?)null, beverageSizeId = (Guid?)lata.Id(), quantity = 1 },
            },
        });
        var offSite = await Created(client, "combos", new
        {
            name = "Batata + refri", description = (string?)null, price = "16.00", sellsOnSite = false, sitePrice = (string?)null, sortOrder = 2,
            components = new object[]
            {
                new { kind = "DISH", dishId = batata.Id(), dishSizeId = (Guid?)batataP, beverageSizeId = (Guid?)null, quantity = 1 },
                new { kind = "BEVERAGE", dishId = (Guid?)null, dishSizeId = (Guid?)null, beverageSizeId = (Guid?)lata.Id(), quantity = 1 },
            },
        });
        var components = combo.GetProperty("components");
        var options = grupo.GetProperty("options");
        return new Shop(
            default, client, Guid.Empty, string.Empty, lanches, sobremesas, xburguer, batata.Id(), batataP, SizeId(batata, "G"), pudim, refri.Id(),
            lata.Id(), doisLitros.Id(), lata.GetProperty("flavors")[0].Id(), lata.GetProperty("flavors")[1].Id(), grupo.Id(),
            options[0].Id(), options[1].Id(), combo.Id(), components[0].Id(), components[1].Id(), components[2].Id(), offSite.Id());
    }

    // ---------- Perfil e cardápio da página ----------

    [Fact]
    public async Task Snack_bar_page_shows_only_what_sells_on_site_and_is_not_paused_at_site_prices()
    {
        var s = await ShopAsync();
        var store = await PublicGet(s.Slug);
        store.GetProperty("status").GetString().ShouldBe("OPEN");
        Strings(store.GetProperty("sections")).ShouldBe(["A_LA_CARTE"]);
        store.GetProperty("menu").ValueKind.ShouldBe(JsonValueKind.Null, "a lanchonete não tem a seção de marmitas");
        store.GetProperty("limits").GetProperty("maxDishesAndCombos").GetInt32().ShouldBe(20);
        store.GetProperty("limits").GetProperty("maxLines").GetInt32().ShouldBe(30);
        store.GetProperty("limits").GetProperty("maxAddonQuantity").GetInt32().ShouldBe(10);

        var menu = store.GetProperty("fixedMenu");
        var categories = menu.GetProperty("categories").EnumerateArray().ToList();
        categories.Select(c => c.GetProperty("name").GetString()).ShouldBe(["Lanches", "Porções"], "Sobremesas só tem o pudim, que não vende no site");
        var burger = categories[0].GetProperty("dishes")[0];
        burger.Id().ShouldBe(s.XBurguer);
        burger.GetProperty("price").GetString().ShouldBe("23.90");
        burger.GetProperty("hasSizes").GetBoolean().ShouldBeFalse();
        burger.GetProperty("description").GetString().ShouldBe("Pão, hambúrguer 150 g, queijo");
        Guids(burger.GetProperty("addonGroupIds")).ShouldBe([s.Grupo]);
        var batata = categories[1].GetProperty("dishes")[0];
        batata.GetProperty("price").ValueKind.ShouldBe(JsonValueKind.Null);
        batata.GetProperty("sizes").EnumerateArray().Select(z => (z.Id(), z.GetProperty("price").GetString())).ShouldBe([(s.BatataP, "13.00")], "a G não vende no site");

        var refri = menu.GetProperty("beverages").EnumerateArray().Single();
        refri.GetProperty("name").GetString().ShouldBe("Refrigerante");
        var lata = refri.GetProperty("sizes").EnumerateArray().Single();
        lata.Id().ShouldBe(s.Lata);
        lata.GetProperty("price").GetString().ShouldBe("6.50");
        lata.GetProperty("flavors").EnumerateArray().Select(f => f.GetProperty("extraPrice").GetString()).ShouldBe(["0.00", "0.50"]);

        var combo = menu.GetProperty("combos").EnumerateArray().Single();
        combo.Id().ShouldBe(s.Combo, "o combo que não vende no site fica de fora");
        combo.GetProperty("price").GetString().ShouldBe("34.90");
        var parts = combo.GetProperty("components").EnumerateArray().ToList();
        parts.Select(p => p.GetProperty("name").GetString()).ShouldBe(["X-Burguer", "Batata frita P", "Refrigerante Lata 350 ml"]);
        Guids(parts[0].GetProperty("addonGroupIds")).ShouldBe([s.Grupo]);
        parts[2].GetProperty("kind").GetString().ShouldBe("BEVERAGE");
        parts[2].GetProperty("flavors").GetArrayLength().ShouldBe(2);

        var group = menu.GetProperty("addonGroups").EnumerateArray().Single();
        group.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("price").GetString()).ShouldBe(["4.00", "3.00"]);

        // Pausar no app tira da página na próxima leitura: a Batata P some (e a Batata com ela), e o combo que a usa
        // também; o Cheddar e a Coca-Cola Zero pausados somem das escolhas.
        var dish = await (await s.Client.GetAsync($"{FixedMenu}/dishes/{s.Batata}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"{FixedMenu}/dishes/{s.Batata}/availability",
            new { isPaused = (bool?)null, sizes = new[] { new { id = s.BatataP, isPaused = true } } }, dish.Version())).ExpectAsync(HttpStatusCode.OK, _api);
        await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"{FixedMenu}/addon-groups/{s.Grupo}/availability",
            new { options = new[] { new { id = s.Cheddar, isPaused = true } } }, 1)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"{FixedMenu}/beverage-types/{s.Refri}/availability",
            new { flavors = new[] { new { id = s.CocaZero, isPaused = true } } }, 1)).ExpectAsync(HttpStatusCode.OK, _api);

        menu = (await PublicGet(s.Slug)).GetProperty("fixedMenu");
        menu.GetProperty("categories").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ShouldBe(["Lanches"]);
        menu.GetProperty("combos").GetArrayLength().ShouldBe(0, "combo com a batata pausada não aparece");
        menu.GetProperty("addonGroups")[0].GetProperty("options").EnumerateArray().Select(o => o.Id()).ShouldBe([s.Bacon]);
        menu.GetProperty("beverages")[0].GetProperty("sizes")[0].GetProperty("flavors").EnumerateArray().Select(f => f.Id()).ShouldBe([s.Coca]);
    }

    [Fact]
    public async Task Closed_and_unavailable_snack_bar_pages_do_not_show_the_menu()
    {
        var s = await ShopAsync(activate: false, openDay: false);
        var inactive = await PublicGet(s.Slug);
        inactive.GetProperty("status").GetString().ShouldBe("UNAVAILABLE");
        inactive.GetProperty("sections").GetArrayLength().ShouldBe(0);
        inactive.GetProperty("fixedMenu").ValueKind.ShouldBe(JsonValueKind.Null);

        await ActivateAsync(s.Client);
        var closed = await PublicGet(s.Slug);
        closed.GetProperty("status").GetString().ShouldBe("CLOSED");
        Strings(closed.GetProperty("sections")).ShouldBe(["A_LA_CARTE"]);
        closed.GetProperty("fixedMenu").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ---------- Envio, aceite, cozinha e acompanhamento ----------

    /// 1 combo com bacon no X-Burguer e Coca-Cola Zero (34,90 + 4,00 + 0,50 = 39,40) + 1 X-Burguer (23,90) + 1 lata de
    /// Coca-Cola (6,50) = 69,80, tudo pelo preço de site.
    [Fact]
    public async Task Web_order_with_combo_goes_through_acceptance_kitchen_and_tracking()
    {
        var s = await ShopAsync();
        var placed = await (await PlaceOrder(s.Slug, Order(s, "69.80", ComboItem(s, 1, s.CocaZero), Dish(s.XBurguer), Beverage(s.Lata, s.Coca))))
            .ExpectAsync(HttpStatusCode.Created, _api);
        placed.GetProperty("acceptanceStatus").GetString().ShouldBe("AWAITING_ACCEPTANCE");
        placed.GetProperty("itemsTotal").GetString().ShouldBe("69.80");
        placed.GetProperty("totalAmount").GetString().ShouldBe("69.80");
        var token = placed.GetProperty("trackingToken").GetString()!;

        // No app: a faixa "Novos do site" da aba Delivery.
        var pending = await (await s.Client.GetAsync(
                "/api/v1/orders?orderType=A_LA_CARTE&fulfillmentType=DELIVERY&fulfillmentType=PICKUP&acceptanceStatus=AWAITING_ACCEPTANCE", Ct))
            .ExpectAsync(HttpStatusCode.OK, _api);
        var row = pending.GetProperty("items").EnumerateArray().Single();
        var orderId = row.Id();
        var order = await (await s.Client.GetAsync($"/api/v1/orders/{orderId}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        order.GetProperty("orderType").GetString().ShouldBe("A_LA_CARTE");
        order.GetProperty("origin").GetString().ShouldBe("WEB");
        order.GetProperty("priceList").GetString().ShouldBe("SITE");
        order.GetProperty("fulfillmentType").GetString().ShouldBe("PICKUP");
        order.GetProperty("orderNumber").ValueKind.ShouldBe(JsonValueKind.Null);
        var combo = order.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("itemKind").GetString() == "COMBO");
        combo.GetProperty("unitPrice").GetString().ShouldBe("34.90");
        combo.GetProperty("parts").GetArrayLength().ShouldBe(3);
        combo.GetProperty("parts")[0].GetProperty("addons")[0].GetProperty("name").GetString().ShouldBe("Bacon");

        // Evento e push do pedido do site: itemCount 3 (combo, prato e bebida), marmitaCount 0 (ADR-0058 item 14).
        (await Scalar<string>(s.TenantId,
                $"SELECT (payload->'data'->>'marmitaCount') || '/' || (payload->'data'->>'itemCount') FROM outbox_messages WHERE tenant_id = @t AND event_type = 'OrderAwaitingAcceptance' AND payload->>'entityId' = '{orderId}'"))
            .ShouldBe("0/3");
        (await Scalar<long>(s.TenantId,
                $"SELECT count(*) FROM outbox_messages WHERE tenant_id = @t AND event_type = 'WebOrderPushRequested' AND payload->>'entityId' = '{orderId}' AND payload->'data'->>'itemCount' = '3'"))
            .ShouldBeGreaterThan(0);

        var tracking = await TrackingGet(token);
        tracking.GetProperty("status").GetString().ShouldBe("AWAITING_ACCEPTANCE");

        // Aceite: número, cozinha com as partes do combo e o prato (sem a linha COMBO nem bebidas).
        var accepted = await Accept(s.Client, orderId, order.Version());
        accepted.GetProperty("orderNumber").GetInt32().ShouldBe(1);
        accepted.GetProperty("productionStatus").GetString().ShouldBe("OPEN");
        var production = await (await s.Client.GetAsync("/api/v1/production/orders", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        var card = production.GetProperty("groups").GetProperty("OPEN").EnumerateArray().Single(o => o.Id() == orderId);
        card.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).ShouldBe(["X-Burguer", "Batata frita P", "X-Burguer"], ignoreOrder: true);

        tracking = await TrackingGet(token);
        tracking.GetProperty("status").GetString().ShouldBe("CONFIRMED");
        tracking.GetProperty("orderNumber").GetInt32().ShouldBe(1);
        var trackedCombo = tracking.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("itemKind").GetString() == "COMBO");
        trackedCombo.GetProperty("lineTotal").GetString().ShouldBe("34.90");
        trackedCombo.GetProperty("addons").GetArrayLength().ShouldBe(0);
        var trackedParts = trackedCombo.GetProperty("parts").EnumerateArray().ToList();
        trackedParts.Select(p => p.GetProperty("name").GetString()).ShouldBe(["X-Burguer", "Batata frita P", "Refrigerante Lata 350 ml · Coca-Cola Zero"]);
        trackedParts[0].GetProperty("addons")[0].GetProperty("lineTotal").GetString().ShouldBe("4.00");
        trackedParts[2].GetProperty("lineTotal").GetString().ShouldBe("0.50");
        tracking.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("itemKind").GetString() == "FIXED_BEVERAGE")
            .GetProperty("name").GetString().ShouldBe("Refrigerante Lata 350 ml · Coca-Cola");
        tracking.GetProperty("totalAmount").GetString().ShouldBe("69.80");

        // Q-309: item acrescentado no app ao pedido do site usa o preço de site de quem vende no site (lata 6,50) e o
        // de balcão de quem não vende (Batata G 18,00).
        var added = await (await s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{orderId}/items",
                new { items = new object[] { Beverage(s.Lata, s.Coca), Dish(s.Batata, s.BatataG) } }, accepted.Version()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        var prices = added.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("name").GetString(), i.GetProperty("unitPrice").GetString())).ToList();
        prices.Count(p => p == ("Refrigerante Lata 350 ml · Coca-Cola", "6.50")).ShouldBe(2);
        prices.ShouldContain(("Batata frita G", "18.00"));
        added.GetProperty("totalAmount").GetString().ShouldBe("94.30");
        (await TrackingGet(token)).GetProperty("changedByStore").GetBoolean().ShouldBeTrue();
    }

    /// Entrega com a taxa fixa da página (modo `FIXED`, sem mapa): 1 X-Burguer com 2 bacons (23,90 + 8,00) + 5,00 = 36,90.
    [Fact]
    public async Task Web_order_for_delivery_uses_the_quote_and_can_be_rejected()
    {
        var s = await ShopAsync();
        await Sql(s.TenantId, "UPDATE tenant_settings SET default_delivery_fee = 5.00 WHERE tenant_id = @t");
        var config = await GetStorefront(s.Client);
        await (await s.Client.SendJsonAsync(HttpMethod.Put, "/api/v1/storefront", StorefrontBody(config.GetProperty("slug").GetString()!, fixedFee: true), config.Version()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        (await PublicGet(s.Slug)).GetProperty("deliveryAvailable").GetBoolean().ShouldBeTrue();

        var quote = await (await Public().PostAsJsonAsync($"/api/v1/public/stores/{s.Slug}/delivery-quotes", new { address = Address() }, Ct))
            .ExpectAsync(HttpStatusCode.Created, _api);
        quote.GetProperty("deliveryFee").GetString().ShouldBe("5.00");

        var body = Order(s, "36.90", [Dish(s.XBurguer, addons: (s.Bacon, 2))], delivery: true, quoteId: quote.GetProperty("quoteId").GetGuid());
        var placed = await (await PlaceOrder(s.Slug, body)).ExpectAsync(HttpStatusCode.Created, _api);
        placed.GetProperty("deliveryFee").GetString().ShouldBe("5.00");
        placed.GetProperty("totalAmount").GetString().ShouldBe("36.90");

        var orderId = await Scalar<Guid>(s.TenantId, "SELECT id FROM orders WHERE tenant_id = @t");
        var order = await (await s.Client.GetAsync($"/api/v1/orders/{orderId}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        order.GetProperty("fulfillmentType").GetString().ShouldBe("DELIVERY");
        order.GetProperty("deliveryAddress").GetProperty("street").GetString().ShouldBe("Rua das Palmeiras");
        order.GetProperty("expectedPaymentMethod").GetString().ShouldBe("CASH");

        var rejected = await (await s.Client.SendJsonAsync(HttpMethod.Post, $"/api/v1/orders/{orderId}/reject",
            new { reasonCode = "ITEM_UNAVAILABLE", message = "O bacon acabou." }, order.Version())).ExpectAsync(HttpStatusCode.OK, _api);
        rejected.GetProperty("acceptanceStatus").GetString().ShouldBe("REJECTED");
        var tracking = await TrackingGet(placed.GetProperty("trackingToken").GetString()!);
        tracking.GetProperty("status").GetString().ShouldBe("REJECTED");
        tracking.GetProperty("deliveryAddress").GetProperty("street").GetString().ShouldBe("Rua das Palmeiras");
    }

    [Fact]
    public async Task Cart_changes_when_the_price_or_the_site_availability_changes()
    {
        var s = await ShopAsync();

        // Total visto pela página diferente do recalculado (o balcão do X-Burguer é 22,00; o site, 23,90).
        var wrong = await (await PlaceOrder(s.Slug, Order(s, "22.00", Dish(s.XBurguer)))).ExpectAsync(HttpStatusCode.Conflict, _api);
        wrong.GetProperty("errorCode").GetString().ShouldBe("CartChanged");
        var cart = wrong.GetProperty("cart");
        cart.GetProperty("items")[0].GetProperty("name").GetString().ShouldBe("X-Burguer");
        cart.GetProperty("items")[0].GetProperty("unitPrice").GetString().ShouldBe("23.90");
        cart.GetProperty("totalAmount").GetString().ShouldBe("23.90");

        // O que não vende no site (Batata G, combo "Batata + refri", 2 L, pudim) é indisponível para a página.
        var offSite = await (await PlaceOrder(s.Slug, Order(s, "23.90", Dish(s.XBurguer), Dish(s.Batata, s.BatataG),
                new { itemKind = "COMBO", comboId = s.ComboOffSite, quantity = 1, parts = new[] { new { comboComponentId = Guid.NewGuid(), beverageFlavorId = (Guid?)null, notes = (string?)null, groupAddons = (object[]?)null } } },
                Beverage(s.DoisLitros, null), Dish(s.Pudim))))
            .ExpectAsync(HttpStatusCode.Conflict, _api);
        offSite.GetProperty("errorCode").GetString().ShouldBe("CartChanged");
        offSite.GetProperty("cart").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("available").GetBoolean()).ShouldBe([true, false, false, false, false]);
        Guids(offSite.GetProperty("cart").GetProperty("unavailableProductIds")).ShouldBe([s.BatataG, s.ComboOffSite, s.DoisLitros, s.Pudim], ignoreOrder: true);
        offSite.GetProperty("cart").GetProperty("totalAmount").GetString().ShouldBe("23.90");

        // Componente pausado tira o combo; sabor pausado também.
        var dish = await (await s.Client.GetAsync($"{FixedMenu}/dishes/{s.Batata}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"{FixedMenu}/dishes/{s.Batata}/availability",
            new { isPaused = (bool?)null, sizes = new[] { new { id = s.BatataP, isPaused = true } } }, dish.Version())).ExpectAsync(HttpStatusCode.OK, _api);
        await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"{FixedMenu}/beverage-types/{s.Refri}/availability",
            new { flavors = new[] { new { id = s.CocaZero, isPaused = true } } }, 1)).ExpectAsync(HttpStatusCode.OK, _api);
        var paused = await (await PlaceOrder(s.Slug, Order(s, "45.90", ComboItem(s, 1, s.Coca, bacon: false), Beverage(s.Lata, s.CocaZero))))
            .ExpectAsync(HttpStatusCode.Conflict, _api);
        paused.GetProperty("cart").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("available").GetBoolean()).ShouldBe([false, false]);
        Guids(paused.GetProperty("cart").GetProperty("unavailableProductIds")).ShouldBe([s.Combo, s.BatataP, s.CocaZero], ignoreOrder: true);

        (await Scalar<long>(s.TenantId, "SELECT count(*) FROM orders WHERE tenant_id = @t")).ShouldBe(0);
    }

    [Fact]
    public async Task One_type_per_order_and_the_snack_bar_limits()
    {
        var s = await ShopAsync();

        async Task<JsonElement> Refused(object body) => await (await PlaceOrder(s.Slug, body)).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);

        // Marmita num pedido A_LA_CARTE (Q-317): um tipo por pedido.
        var mixed = await Refused(Order(s, "23.90", Dish(s.XBurguer),
            new { itemKind = "MARMITA", marmitaSizeId = Guid.NewGuid(), quantity = 1, mainDishProductIds = new[] { Guid.NewGuid() } }));
        mixed.GetProperty("errors").GetProperty("items[1].itemKind")[0].GetString()!.ShouldContain("um tipo de pedido por vez");

        // Só bebidas não é pedido (Q-318).
        var drinks = await Refused(Order(s, "6.50", Beverage(s.Lata, s.Coca)));
        drinks.GetProperty("errors").GetProperty("items")[0].GetString()!.ShouldContain("prato ou combo");

        // 21 pratos e combos (11 + 10): acima do limite; bebidas não contam.
        var tooMany = await Refused(Order(s, "1.00", Dish(s.XBurguer, quantity: 11), ComboItem(s, 10, s.Coca), Beverage(s.Lata, s.Coca, quantity: 20)));
        tooMany.GetProperty("errors").GetProperty("items")[0].GetString()!.ShouldContain("20 pratos e combos");

        // 21 de uma linha; adicional acima de 10; observação no combo; sabor faltando (conferido contra o cardápio).
        var line = await Refused(Order(s, "1.00", Dish(s.XBurguer, quantity: 21)));
        line.GetProperty("errors").GetProperty("items[0].quantity").GetArrayLength().ShouldBeGreaterThan(0);
        var addon = await Refused(Order(s, "1.00", Dish(s.XBurguer, addons: (s.Bacon, 11))));
        addon.GetProperty("errors").GetProperty("items[0].groupAddons[0].quantity").GetArrayLength().ShouldBeGreaterThan(0);
        var flavor = await Refused(Order(s, "23.90", Dish(s.XBurguer), Beverage(s.Lata, null)));
        flavor.GetProperty("errors").GetProperty("items[1].beverageFlavorId").GetArrayLength().ShouldBeGreaterThan(0);

        // Pedido de marmita numa lanchonete: a página não tem a seção.
        var marmita = await Refused(new
        {
            orderType = "MARMITA", fulfillmentType = "PICKUP", customerName = "Carla", customerPhone = "34988887777", expectedPaymentMethod = "PIX",
            expectedTotal = "20.00", items = new object[] { new { itemKind = "MARMITA", marmitaSizeId = Guid.NewGuid(), quantity = 1, mainDishProductIds = new[] { Guid.NewGuid() } } },
        });
        marmita.GetProperty("errors").GetProperty("orderType").GetArrayLength().ShouldBeGreaterThan(0);

        (await Scalar<long>(s.TenantId, "SELECT count(*) FROM orders WHERE tenant_id = @t")).ShouldBe(0);
    }

    // ---------- Ativação ----------

    [Fact]
    public async Task Snack_bar_activation_requires_a_dish_or_combo_sold_on_site()
    {
        var owner = await _api.SignUpAsync(ApiScenarios.SignUpBody(behavior: "SNACK_BAR"));
        var client = _api.Authenticated(owner.AccessToken());
        var config = await GetStorefront(client);
        var slug = config.GetProperty("slug").GetString()!;

        // Só bebida no site não basta (o pedido precisa de prato ou combo, Q-318).
        var category = (await Created(client, "dish-categories", new { name = "Lanches", sortOrder = 1 })).Id();
        await Created(client, "beverage-types", new
        {
            name = "Suco", sortOrder = 1,
            sizes = new[] { new { id = (Guid?)null, name = "500 ml", price = "9.00", sellsOnSite = true, sitePrice = "9.50", sortOrder = 1, flavors = Array.Empty<object>() } },
        });
        var dish = await Created(client, "dishes", new
        {
            dishCategoryId = category, name = "X-Salada", description = (string?)null, requiresPreparation = (bool?)null, hasSizes = false,
            price = "20.00", sellsOnSite = false, sitePrice = (string?)null, sortOrder = 1, sizes = (object[]?)null,
        });
        var refused = await (await client.SendJsonAsync(HttpMethod.Put, "/api/v1/storefront", StorefrontBody(slug), config.Version()))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);
        refused.GetProperty("errors").GetProperty("isPageActive")[0].GetString()!.ShouldContain("prato ou combo");

        // Ligar o "vende no site" do prato libera a ativação, mesmo com o prato pausado (a pausa é passageira).
        await (await client.SendJsonAsync(HttpMethod.Patch, $"{FixedMenu}/dishes/{dish.Id()}", new { sellsOnSite = true }, dish.Version()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        var updated = await (await client.GetAsync($"{FixedMenu}/dishes/{dish.Id()}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await client.SendJsonAsync(HttpMethod.Patch, $"{FixedMenu}/dishes/{dish.Id()}/availability", new { isPaused = true }, updated.Version()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        var active = await (await client.SendJsonAsync(HttpMethod.Put, "/api/v1/storefront", StorefrontBody(slug), config.Version()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        active.GetProperty("isPageActive").GetBoolean().ShouldBeTrue();
    }

    // ---------- Os dois comportamentos ----------

    /// Restaurante que acrescentou a lanchonete (Q-317): a página mostra as duas seções, e cada pedido é de um tipo.
    [Fact]
    public async Task Restaurant_with_both_behaviors_shows_both_sections_one_type_per_order()
    {
        var r = await OrderingScenario.CreateAsync(_api, db);
        await r.SqlAsync("INSERT INTO tenant_behaviors (tenant_id, behavior, origin, enabled_at) VALUES (@t, 'SNACK_BAR', 'SYSTEM', now())");
        var m = await SeedFixedMenuAsync(r.Client);
        var slug = await ActivateAsync(r.Client);

        var store = await PublicGet(slug);
        Strings(store.GetProperty("sections")).ShouldBe(["MARMITA", "A_LA_CARTE"]);
        store.GetProperty("menu").GetProperty("marmitaSizes")[0].GetProperty("price").GetString().ShouldBe("20.00");
        store.GetProperty("fixedMenu").GetProperty("combos")[0].Id().ShouldBe(m.Combo);

        // Marmita (sem orderType, como a página do EP-002) e à la carte, em pedidos separados.
        await (await PlaceOrder(slug, new
        {
            fulfillmentType = "PICKUP", customerName = "Carla Souza", customerPhone = "34988887777", expectedPaymentMethod = "PIX", expectedTotal = "20.00",
            items = new object[] { new { itemKind = "MARMITA", marmitaSizeId = r.SizeM, quantity = 1, mainDishProductIds = new[] { r.Frango } } },
        })).ExpectAsync(HttpStatusCode.Created, _api);
        await (await PlaceOrder(slug, Order(m, "23.90", Dish(m.XBurguer)))).ExpectAsync(HttpStatusCode.Created, _api);

        var mixed = await (await PlaceOrder(slug, new
        {
            orderType = "MARMITA", fulfillmentType = "PICKUP", customerName = "Carla Souza", customerPhone = "34977776666", expectedPaymentMethod = "PIX",
            expectedTotal = "43.90",
            items = new object[] { new { itemKind = "MARMITA", marmitaSizeId = r.SizeM, quantity = 1, mainDishProductIds = new[] { r.Frango } }, Dish(m.XBurguer) },
        })).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);
        mixed.GetProperty("errors").GetProperty("items[1].itemKind")[0].GetString()!.ShouldContain("um tipo de pedido por vez");

        (await r.ScalarAsync<string>("SELECT string_agg(order_type, ',' ORDER BY order_type) FROM orders WHERE tenant_id = @t")).ShouldBe("A_LA_CARTE,MARMITA");
    }

    // ---------- Isolamento entre tenants ----------

    [Fact]
    public async Task Slug_of_A_never_shows_or_sells_the_fixed_menu_of_B()
    {
        var a = await ShopAsync();
        var b = await ShopAsync();

        var json = (await PublicGet(a.Slug)).GetProperty("fixedMenu").GetRawText();
        json.ShouldContain(a.Combo.ToString());
        foreach (var id in new[] { b.Combo, b.XBurguer, b.Lata, b.Bacon, b.BatataP })
        {
            json.ShouldNotContain(id.ToString());
        }

        // Itens de B pelo slug de A: não existem para A.
        var changed = await (await PlaceOrder(a.Slug, Order(b, "23.90", Dish(b.XBurguer)))).ExpectAsync(HttpStatusCode.Conflict, _api);
        changed.GetProperty("errorCode").GetString().ShouldBe("CartChanged");
        Guids(changed.GetProperty("cart").GetProperty("unavailableProductIds")).ShouldBe([b.XBurguer]);
        (await Scalar<long>(a.TenantId, "SELECT count(*) FROM orders WHERE tenant_id = @t")).ShouldBe(0);
        (await Scalar<long>(b.TenantId, "SELECT count(*) FROM orders WHERE tenant_id = @t")).ShouldBe(0);

        // O pedido de A e o acompanhamento dele não aparecem em B.
        var placed = await (await PlaceOrder(a.Slug, Order(a, "23.90", Dish(a.XBurguer)))).ExpectAsync(HttpStatusCode.Created, _api);
        var tracking = await TrackingGet(placed.GetProperty("trackingToken").GetString()!);
        tracking.GetProperty("store").GetProperty("slug").GetString().ShouldBe(a.Slug);
        var listB = await (await b.Client.GetAsync("/api/v1/orders?acceptanceStatus=AWAITING_ACCEPTANCE", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        listB.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    // ---------- Apoio ----------

    private static object Order(Shop s, string expectedTotal, params object[] items) => Order(s, expectedTotal, items, delivery: false);

    private static object Order(Shop s, string expectedTotal, object[] items, bool delivery, Guid? quoteId = null) => new
    {
        orderType = "A_LA_CARTE",
        fulfillmentType = delivery ? "DELIVERY" : "PICKUP",
        customerName = "Carla Souza",
        customerPhone = "(34) 98888-7777",
        deliveryAddress = delivery ? Address() : null,
        deliveryQuoteId = quoteId,
        expectedPaymentMethod = "CASH",
        changeFor = (string?)null,
        notes = "Sem talher",
        items,
        expectedTotal,
        website = (string?)null,
    };

    private static object Address() =>
        new { street = "Rua das Palmeiras", number = "45", complement = "Casa 2", neighborhood = "Centro", city = "Uberlândia", zipCode = (string?)null, reference = (string?)null };

    private static object Dish(Guid dish, Guid? size = null, int quantity = 1, params (Guid Option, int Quantity)[] addons) => new
    {
        itemKind = "FIXED_DISH", dishId = dish, dishSizeId = size, quantity, notes = (string?)null,
        groupAddons = addons.Select(a => new { addonOptionId = a.Option, quantity = a.Quantity }).ToArray(),
    };

    private static object Beverage(Guid size, Guid? flavor, int quantity = 1) =>
        new { itemKind = "FIXED_BEVERAGE", beverageSizeId = size, beverageFlavorId = flavor, quantity, notes = (string?)null };

    private static object ComboItem(Shop s, int quantity, Guid flavor, bool bacon = true) => new
    {
        itemKind = "COMBO", comboId = s.Combo, quantity,
        parts = new object[]
        {
            new
            {
                comboComponentId = s.BurgerPart, beverageFlavorId = (Guid?)null, notes = "Sem cebola",
                groupAddons = bacon ? new[] { new { addonOptionId = s.Bacon, quantity = 1 } } : [],
            },
            new { comboComponentId = s.LataPart, beverageFlavorId = (Guid?)flavor, notes = (string?)null, groupAddons = (object[]?)null },
        },
    };

    private static object StorefrontBody(string slug, bool fixedFee = false) => new
    {
        slug,
        publicName = "Lanchonete do Zé",
        isPageActive = true,
        isAcceptingOrders = true,
        isDeliveryEnabled = true,
        offersDelivery = true,
        offersPickup = true,
        contactPhone = "+5534999990000",
        hoursText = "Todos os dias, 18h às 23h",
        address = new { street = "Rua das Flores", number = "123", complement = (string?)null, neighborhood = "Centro", city = "Uberlândia", state = "MG", zipCode = "38400-000" },
        delivery = fixedFee
            ? new { feeMode = "FIXED", pricePerKm = (string?)null, minimumFee = "0.00", maxDistanceKm = (string?)null }
            : new { feeMode = "DISTANCE", pricePerKm = (string?)"0.80", minimumFee = "3.00", maxDistanceKm = (string?)"6.0" },
    };

    private async Task<string> ActivateAsync(HttpClient client)
    {
        var config = await GetStorefront(client);
        var slug = config.GetProperty("slug").GetString()!;
        await (await client.SendJsonAsync(HttpMethod.Put, "/api/v1/storefront", StorefrontBody(slug), config.Version())).ExpectAsync(HttpStatusCode.OK, _api);
        return slug;
    }

    private async Task<JsonElement> Accept(HttpClient client, Guid orderId, int version)
    {
        var accept = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{orderId}/accept") { Content = JsonContent.Create(new { estimatedReadyMinutes = (int?)30 }) };
        accept.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        accept.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return await (await client.SendAsync(accept, Ct)).ExpectAsync(HttpStatusCode.OK, _api);
    }

    private async Task<JsonElement> GetStorefront(HttpClient client) =>
        await (await client.GetAsync("/api/v1/storefront", Ct)).ExpectAsync(HttpStatusCode.OK, _api);

    private HttpClient Public() => _api.CreateClient();

    private async Task<JsonElement> PublicGet(string slug) =>
        await (await Public().GetAsync($"/api/v1/public/stores/{slug}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);

    private async Task<JsonElement> TrackingGet(string token) =>
        await (await Public().GetAsync($"/api/v1/public/tracking/{token}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);

    private Task<HttpResponseMessage> PlaceOrder(string slug, object body) =>
        Public().PostIdempotentAsync($"/api/v1/public/stores/{slug}/orders", body, Guid.NewGuid());

    private async Task<JsonElement> Created(HttpClient client, string path, object body) =>
        await (await client.PostAsJsonAsync($"{FixedMenu}/{path}", body, Ct)).ExpectAsync(HttpStatusCode.Created, _api);

    private static Guid SizeId(JsonElement dish, string name) =>
        dish.GetProperty("sizes").EnumerateArray().Single(z => z.GetProperty("name").GetString() == name).Id();

    private static List<string?> Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()).ToList();

    private static List<Guid> Guids(JsonElement array) => array.EnumerateArray().Select(e => e.GetGuid()).ToList();

    private async Task Sql(Guid tenant, string sql)
    {
        await using var connection = new NpgsqlConnection(db.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("t", tenant);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<T> Scalar<T>(Guid tenant, string sql)
    {
        await using var connection = new NpgsqlConnection(db.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("t", tenant);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
