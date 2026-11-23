using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Ordering;

/// Pedido à la carte (EP-003 fatia 3, ADR-0058; 06 §6.9) contra a API real: comanda com prato, tamanho, adicionais,
/// bebida com sabor e combo; pausa; um tipo por pedido; inclusão depois com If-Match; cozinha; pagamento, cancelamento e
/// fechamento; módulo; isolamento entre tenants.
[Collection(nameof(PostgresCollection))]
public sealed class ALaCarteOrderFlowTests(PostgresFixture db) : IDisposable
{
    private const string FixedMenu = "/api/v1/fixed-menu";

    private readonly ApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _api.Dispose();

    /// Lanchonete com expediente aberto. X-Burguer 22,00 (Lanches, com Bacon 4,00); Batata frita P 12,00 / G 18,00;
    /// Açaí 300 ml 14,00 / 500 ml 18,00 (Açaí, com Granola 2,00); Refrigerante Lata 350 ml 6,00 com Coca-Cola e
    /// Coca-Cola Zero (+0,50); combo "X-Burguer + batata + refri" 32,00 = X-Burguer + Batata P + Lata.
    private sealed record Shop(
        JsonElement Owner, HttpClient Client, Guid TenantId, Guid XBurguer, Guid Batata, Guid BatataP, Guid Acai, Guid Acai500,
        Guid Refri, Guid Lata, Guid Coca, Guid CocaZero, Guid Bacon, Guid Granola, Guid Combo, Guid BurgerPart, Guid BatataPart, Guid LataPart);

    private async Task<Shop> ShopAsync()
    {
        var owner = await _api.SignUpAsync(ApiScenarios.SignUpBody(behavior: "SNACK_BAR"));
        var client = _api.Authenticated(owner.AccessToken());

        var lanches = (await Created(client, "dish-categories", new { name = "Lanches", sortOrder = 1 })).Id();
        var porcoes = (await Created(client, "dish-categories", new { name = "Porções", sortOrder = 2 })).Id();
        var acaiCategory = (await Created(client, "dish-categories", new { name = "Açaí", sortOrder = 3 })).Id();
        var xburguer = (await Created(client, "dishes", new
        {
            dishCategoryId = lanches, name = "X-Burguer", description = (string?)null, requiresPreparation = (bool?)null, hasSizes = false,
            price = "22.00", sellsOnSite = true, sitePrice = "23.90", sortOrder = 0, sizes = (object[]?)null,
        })).Id();
        var batata = await Created(client, "dishes", Sized(porcoes, "Batata frita", ("P", "12.00"), ("G", "18.00")));
        var acai = await Created(client, "dishes", Sized(acaiCategory, "Açaí", ("300 ml", "14.00"), ("500 ml", "18.00")));
        var refri = await Created(client, "beverage-types", new
        {
            name = "Refrigerante", sortOrder = 1,
            sizes = new[]
            {
                new
                {
                    id = (Guid?)null, name = "Lata 350 ml", price = "6.00", sellsOnSite = false, sitePrice = (string?)null, sortOrder = 1,
                    flavors = new[]
                    {
                        new { id = (Guid?)null, name = "Coca-Cola", extraPrice = "0.00", sortOrder = 1 },
                        new { id = (Guid?)null, name = "Coca-Cola Zero", extraPrice = "0.50", sortOrder = 2 },
                    },
                },
            },
        });
        var lata = refri.GetProperty("sizes")[0];
        var lanche = await Created(client, "addon-groups", new
        {
            name = "Adicionais de lanche", sortOrder = 1,
            options = new[] { new { id = (Guid?)null, name = "Bacon", price = "4.00", sortOrder = 1 } },
            dishCategoryIds = new[] { lanches }, dishIds = Array.Empty<Guid>(),
        });
        var complementos = await Created(client, "addon-groups", new
        {
            name = "Complementos de açaí", sortOrder = 2,
            options = new[] { new { id = (Guid?)null, name = "Granola", price = "2.00", sortOrder = 1 } },
            dishCategoryIds = new[] { acaiCategory }, dishIds = Array.Empty<Guid>(),
        });
        var batataP = SizeId(batata, "P");
        var combo = await Created(client, "combos", new
        {
            name = "X-Burguer + batata + refri", description = (string?)null, price = "32.00", sellsOnSite = false, sitePrice = (string?)null, sortOrder = 0,
            components = new object[]
            {
                new { kind = "DISH", dishId = xburguer, dishSizeId = (Guid?)null, beverageSizeId = (Guid?)null, quantity = 1 },
                new { kind = "DISH", dishId = batata.Id(), dishSizeId = (Guid?)batataP, beverageSizeId = (Guid?)null, quantity = 1 },
                new { kind = "BEVERAGE", dishId = (Guid?)null, dishSizeId = (Guid?)null, beverageSizeId = (Guid?)lata.Id(), quantity = 1 },
            },
        });
        var components = combo.GetProperty("components");

        await (await client.PostIdempotentAsync("/api/v1/business-days", new { openingBalance = "100.00", confirmSameDate = false }, Guid.NewGuid()))
            .ExpectAsync(HttpStatusCode.Created, _api);

        return new Shop(
            owner, client, Guid.Parse(owner.TenantId()), xburguer, batata.Id(), batataP, acai.Id(), SizeId(acai, "500 ml"),
            refri.Id(), lata.Id(), lata.GetProperty("flavors")[0].Id(), lata.GetProperty("flavors")[1].Id(),
            lanche.GetProperty("options")[0].Id(), complementos.GetProperty("options")[0].Id(),
            combo.Id(), components[0].Id(), components[1].Id(), components[2].Id());
    }

    private static object Comanda(params object[] items) => Order("DINE_IN", items, table: "5");

    private static object Order(
        string fulfillment, object[] items, string? table = null, string? tab = null, string? name = null, object? address = null,
        string? phone = "+5534999990000", string? payment = "PIX") => new
    {
        orderType = "A_LA_CARTE",
        fulfillmentType = fulfillment,
        customerId = (Guid?)null,
        customerName = name,
        customerPhone = name is null ? null : phone,
        saveCustomer = false,
        customerAddressId = (Guid?)null,
        deliveryAddress = address,
        deliveryFee = (string?)null,
        tableLabel = table,
        tabNumber = tab,
        notes = (string?)null,
        expectedPaymentMethod = fulfillment == "DELIVERY" ? payment : null,
        items,
        weighings = Array.Empty<object>(),
    };

    private static object Dish(Guid dish, Guid? size, int quantity = 1, string? notes = null, params (Guid Option, int Quantity)[] addons) => new
    {
        itemKind = "FIXED_DISH", dishId = dish, dishSizeId = size, quantity, notes,
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

    /// Critérios 1 a 4 da fatia 3: comanda de mesa com açaí 500 ml + granola, lata de Coca e 2 combos com bacon e Coca
    /// Zero (73,00, ADR-0058 item 9) = 99,00; árvore e snapshots na resposta e no banco; reenvio idempotente; a cozinha vê
    /// 3 itens (açaí, X-Burguer ×2 com bacon, Batata P ×2), sem a linha do combo e sem bebidas.
    [Fact]
    public async Task Comanda_with_sizes_addons_flavors_and_combos_is_priced_on_the_server_and_reaches_the_kitchen()
    {
        var s = await ShopAsync();
        var key = Guid.NewGuid();
        var body = Comanda(Dish(s.Acai, s.Acai500, 1, "Pouco leite", (s.Granola, 1)), Beverage(s.Lata, s.Coca), ComboItem(s, 2, s.CocaZero));

        var response = await s.Client.PostIdempotentAsync("/api/v1/orders", body, key);
        var order = await response.ExpectAsync(HttpStatusCode.Created, _api);

        response.Headers.ETag!.Tag.ShouldBe("\"1\"");
        order.GetProperty("orderType").GetString().ShouldBe("A_LA_CARTE");
        order.GetProperty("fulfillmentType").GetString().ShouldBe("DINE_IN");
        order.GetProperty("priceList").GetString().ShouldBe("COUNTER");
        order.GetProperty("tableLabel").GetString().ShouldBe("5");
        order.GetProperty("productionStatus").GetString().ShouldBe("OPEN");
        order.GetProperty("fulfillmentStatus").ValueKind.ShouldBe(JsonValueKind.Null);
        order.GetProperty("itemsTotal").GetString().ShouldBe("99.00");
        order.GetProperty("totalAmount").GetString().ShouldBe("99.00");

        var items = order.GetProperty("items");
        items.GetArrayLength().ShouldBe(3);
        var acai = items[0];
        acai.GetProperty("itemKind").GetString().ShouldBe("FIXED_DISH");
        acai.GetProperty("dishId").GetGuid().ShouldBe(s.Acai);
        acai.GetProperty("dishSizeId").GetGuid().ShouldBe(s.Acai500);
        acai.GetProperty("name").GetString().ShouldBe("Açaí 500 ml");
        acai.GetProperty("sizeName").GetString().ShouldBe("500 ml");
        acai.GetProperty("unitPrice").GetString().ShouldBe("18.00");
        acai.GetProperty("requiresPreparation").GetBoolean().ShouldBeTrue();
        var granola = acai.GetProperty("addons").EnumerateArray().ShouldHaveSingleItem();
        granola.GetProperty("itemKind").GetString().ShouldBe("GROUP_ADDON");
        granola.GetProperty("addonOptionId").GetGuid().ShouldBe(s.Granola);
        granola.GetProperty("productId").ValueKind.ShouldBe(JsonValueKind.Null);
        granola.GetProperty("lineTotal").GetString().ShouldBe("2.00");
        acai.GetProperty("parts").GetArrayLength().ShouldBe(0);

        var coca = items[1];
        coca.GetProperty("itemKind").GetString().ShouldBe("FIXED_BEVERAGE");
        coca.GetProperty("name").GetString().ShouldBe("Refrigerante Lata 350 ml · Coca-Cola");
        coca.GetProperty("flavorName").GetString().ShouldBe("Coca-Cola");
        coca.GetProperty("requiresPreparation").GetBoolean().ShouldBeFalse();

        var combo = items[2];
        combo.GetProperty("itemKind").GetString().ShouldBe("COMBO");
        combo.GetProperty("comboId").GetGuid().ShouldBe(s.Combo);
        combo.GetProperty("quantity").GetInt32().ShouldBe(2);
        combo.GetProperty("lineTotal").GetString().ShouldBe("64.00");
        combo.GetProperty("requiresPreparation").GetBoolean().ShouldBeFalse();
        var parts = combo.GetProperty("parts");
        parts.EnumerateArray().Select(p => p.GetProperty("name").GetString()).ShouldBe(["X-Burguer", "Batata frita P", "Refrigerante Lata 350 ml · Coca-Cola Zero"]);
        parts.EnumerateArray().Select(p => p.GetProperty("itemKind").GetString()).ShouldAllBe(k => k == "COMBO_PART");
        parts[0].GetProperty("notes").GetString().ShouldBe("Sem cebola");
        parts[0].GetProperty("lineTotal").GetString().ShouldBe("0.00");
        var bacon = parts[0].GetProperty("addons").EnumerateArray().ShouldHaveSingleItem();
        bacon.GetProperty("name").GetString().ShouldBe("Bacon");
        bacon.GetProperty("lineTotal").GetString().ShouldBe("8.00");
        parts[1].GetProperty("dishSizeId").GetGuid().ShouldBe(s.BatataP);
        parts[2].GetProperty("unitPrice").GetString().ShouldBe("0.50");
        parts[2].GetProperty("lineTotal").GetString().ShouldBe("1.00");
        parts[2].GetProperty("beverageFlavorId").GetGuid().ShouldBe(s.CocaZero);

        // O banco grava o tipo e o prato do pai (05 §5.17 0010).
        (await Scalar<long>(s.TenantId, "SELECT count(*) FROM order_items WHERE tenant_id = @t AND parent_item_kind IS NOT NULL")).ShouldBe(5);
        (await Scalar<long>(s.TenantId, "SELECT count(*) FROM order_items WHERE tenant_id = @t AND item_kind = 'GROUP_ADDON' AND parent_dish_id IS NOT NULL")).ShouldBe(2);

        // Reenvio com a mesma chave: o mesmo pedido, sem outro número.
        var replay = await (await s.Client.PostIdempotentAsync("/api/v1/orders", body, key)).ExpectAsync(HttpStatusCode.Created, _api);
        replay.Id().ShouldBe(order.Id());
        (await Scalar<long>(s.TenantId, "SELECT count(*) FROM orders WHERE tenant_id = @t")).ShouldBe(1);

        var kitchen = _api.Authenticated(await _api.EmployeeTokenAsync(db, s.Owner, "KITCHEN"));
        var board = await (await kitchen.GetAsync("/api/v1/production/orders", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        var card = board.GetProperty("groups").GetProperty("OPEN").EnumerateArray().ShouldHaveSingleItem();
        card.GetProperty("orderType").GetString().ShouldBe("A_LA_CARTE");
        card.GetProperty("label").GetString().ShouldBe("Mesa 5");
        var lines = card.GetProperty("items").EnumerateArray().ToList();
        lines.Select(l => l.GetProperty("name").GetString()).ShouldBe(["Açaí 500 ml", "X-Burguer", "Batata frita P"]);
        lines.Select(l => l.GetProperty("quantity").GetInt32()).ShouldBe([1, 2, 2]);
        lines[0].GetProperty("notes").GetString().ShouldBe("Pouco leite");
        lines[0].GetProperty("addons").EnumerateArray().Single().GetProperty("name").GetString().ShouldBe("Granola");
        lines[1].GetProperty("notes").GetString().ShouldBe("Sem cebola");
        lines[1].GetProperty("addons").EnumerateArray().Single().GetProperty("name").GetString().ShouldBe("Bacon");
        lines.ShouldAllBe(l => l.GetProperty("requiresPreparation").GetBoolean());
    }

    /// Critério 2: tamanho e sabor obrigatórios; item pausado → `422 ProductUnavailable` (o status do 06; ADR-0056
    /// item 24 complementado). Pausar a Batata P tira o combo; pausar a Coca Zero tira o sabor.
    [Fact]
    public async Task Missing_choices_are_422_and_paused_items_are_unavailable()
    {
        var s = await ShopAsync();

        var noSize = await Problem(s.Client.PostIdempotentAsync("/api/v1/orders", Comanda(Dish(s.Acai, null)), Guid.NewGuid()), HttpStatusCode.UnprocessableEntity);
        noSize.GetProperty("errorCode").GetString().ShouldBe("ValidationFailed");
        noSize.GetProperty("errors").TryGetProperty("items[0].dishSizeId", out _).ShouldBeTrue();
        var noFlavor = await Problem(s.Client.PostIdempotentAsync("/api/v1/orders", Comanda(Beverage(s.Lata, null)), Guid.NewGuid()), HttpStatusCode.UnprocessableEntity);
        noFlavor.GetProperty("errors").TryGetProperty("items[0].beverageFlavorId", out _).ShouldBeTrue();
        var notLinked = await Problem(s.Client.PostIdempotentAsync("/api/v1/orders", Comanda(Dish(s.XBurguer, null, 1, null, (s.Granola, 1))), Guid.NewGuid()),
            HttpStatusCode.UnprocessableEntity);
        notLinked.GetProperty("errors").TryGetProperty("items[0].groupAddons[0].addonOptionId", out _).ShouldBeTrue();

        var batata = await (await s.Client.GetAsync($"{FixedMenu}/dishes/{s.Batata}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"{FixedMenu}/dishes/{s.Batata}/availability",
            new { sizes = new[] { new { id = s.BatataP, isPaused = true } } }, batata.Version())).ExpectAsync(HttpStatusCode.OK, _api);
        var combo = await Problem(s.Client.PostIdempotentAsync("/api/v1/orders", Comanda(ComboItem(s, 1, s.Coca)), Guid.NewGuid()), HttpStatusCode.UnprocessableEntity);
        combo.GetProperty("errorCode").GetString().ShouldBe("ProductUnavailable");
        combo.GetProperty("detail").GetString()!.ShouldContain("Batata frita P");

        await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"{FixedMenu}/beverage-types/{s.Refri}/availability",
            new { flavors = new[] { new { id = s.CocaZero, isPaused = true } } }, 1)).ExpectAsync(HttpStatusCode.OK, _api);
        (await Problem(s.Client.PostIdempotentAsync("/api/v1/orders", Comanda(Beverage(s.Lata, s.CocaZero)), Guid.NewGuid()), HttpStatusCode.UnprocessableEntity))
            .GetProperty("errorCode").GetString().ShouldBe("ProductUnavailable");

        // O que não está pausado continua vendendo.
        (await CreateAsync(s.Client, Comanda(Beverage(s.Lata, s.Coca)))).GetProperty("totalAmount").GetString().ShouldBe("6.00");
        (await Scalar<long>(s.TenantId, "SELECT count(*) FROM orders WHERE tenant_id = @t")).ShouldBe(1);
    }

    /// Critério 5 e Q-317: um tipo por pedido. Mesmo num tenant com os dois comportamentos, o cardápio fixo não entra no
    /// PF e o PF não entra na comanda, na criação nem na inclusão.
    [Fact]
    public async Task One_order_type_per_order()
    {
        var s = await ShopAsync();
        await Sql(s.TenantId, "INSERT INTO tenant_behaviors (tenant_id, behavior, origin, enabled_at) VALUES (@t, 'RESTAURANT', 'SYSTEM', now())");

        var beverageProduct = await Problem(s.Client.PostIdempotentAsync("/api/v1/orders",
            Comanda(new { itemKind = "BEVERAGE", productId = Guid.NewGuid(), quantity = 1 }), Guid.NewGuid()), HttpStatusCode.UnprocessableEntity);
        beverageProduct.GetProperty("errors").TryGetProperty("items[0].itemKind", out _).ShouldBeTrue();
        var pfInComanda = await Problem(s.Client.PostIdempotentAsync("/api/v1/orders",
            Comanda(Dish(s.XBurguer, null), new { itemKind = "PF", productId = Guid.NewGuid(), quantity = 1 }), Guid.NewGuid()), HttpStatusCode.UnprocessableEntity);
        pfInComanda.GetProperty("errors").TryGetProperty("items[1].itemKind", out _).ShouldBeTrue();
        var dishInPf = await Problem(s.Client.PostIdempotentAsync("/api/v1/orders", new
        {
            orderType = "PF", fulfillmentType = "DINE_IN", tableLabel = "2", items = new[] { Dish(s.XBurguer, null) }, weighings = Array.Empty<object>(),
        }, Guid.NewGuid()), HttpStatusCode.UnprocessableEntity);
        dishInPf.GetProperty("errors").TryGetProperty("items[0].itemKind", out _).ShouldBeTrue();

        var order = await CreateAsync(s.Client, Comanda(Dish(s.XBurguer, null)));
        (await Problem(s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/items",
                new { items = new[] { new { itemKind = "PF", productId = Guid.NewGuid(), quantity = 1 } } }, 1), HttpStatusCode.UnprocessableEntity))
            .GetProperty("errors").TryGetProperty("items[0].itemKind", out _).ShouldBeTrue();
        (await Problem(s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/items",
                new { items = new[] { new { itemKind = "ADDON", parentOrderItemId = order.GetProperty("items")[0].Id(), productId = Guid.NewGuid(), quantity = 1 } } }, 1),
            HttpStatusCode.UnprocessableEntity)).GetProperty("errorCode").GetString().ShouldBe("ValidationFailed");
    }

    /// RF-310: dois funcionários na mesma comanda (o segundo recebe `409` com a comanda atual); adicional incluído
    /// depois numa parte de prato e item com preparo em pedido pronto reabrem a produção (RN-007.10); a quantidade do
    /// combo reprecifica a árvore; a parte não sai sozinha; remover o combo leva a árvore.
    [Fact]
    public async Task Adding_items_later_with_if_match_reopens_production_and_edits_follow_the_combo()
    {
        var s = await ShopAsync();
        var order = await CreateAsync(s.Client, Comanda(ComboItem(s, 1, s.Coca, bacon: false)));
        var burger = order.GetProperty("items")[0].GetProperty("parts")[0].Id();
        var lataPart = order.GetProperty("items")[0].GetProperty("parts")[2].Id();
        await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{order.Id()}/status", new { productionStatus = "COMPLETED" }, 1))
            .ExpectAsync(HttpStatusCode.OK, _api);

        var attendant = _api.Authenticated(await _api.EmployeeTokenAsync(db, s.Owner, "ATTENDANT"));
        var bacon = new { items = new[] { new { itemKind = "GROUP_ADDON", parentOrderItemId = burger, addonOptionId = s.Bacon, quantity = 1 } } };
        var updated = await (await attendant.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/items", bacon, 2)).ExpectAsync(HttpStatusCode.OK, _api);
        updated.GetProperty("productionStatus").GetString().ShouldBe("IN_PROGRESS");
        updated.GetProperty("totalAmount").GetString().ShouldBe("36.00");
        updated.GetProperty("items")[0].GetProperty("parts")[0].GetProperty("addons")[0].GetProperty("name").GetString().ShouldBe("Bacon");

        var conflict = await Problem(s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/items",
            new { items = new[] { Beverage(s.Lata, s.Coca) } }, 2), HttpStatusCode.Conflict);
        conflict.GetProperty("errorCode").GetString().ShouldBe("VersionConflict");
        conflict.GetProperty("current").GetProperty("totalAmount").GetString().ShouldBe("36.00");

        // Adicional na bebida do combo → 422.
        (await Problem(s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/items",
                new { items = new[] { new { itemKind = "GROUP_ADDON", parentOrderItemId = lataPart, addonOptionId = s.Bacon, quantity = 1 } } }, updated.Version()),
            HttpStatusCode.UnprocessableEntity)).GetProperty("errors").TryGetProperty("items[0].parentOrderItemId", out _).ShouldBeTrue();

        var combo = updated.GetProperty("items")[0].Id();
        var doubled = await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{order.Id()}/items/{combo}", new { quantity = 2 }, updated.Version()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        doubled.GetProperty("totalAmount").GetString().ShouldBe("72.00");
        (await Problem(s.Client.SendJsonAsync(HttpMethod.Delete, $"/api/v1/orders/{order.Id()}/items/{burger}", null, doubled.Version()), HttpStatusCode.UnprocessableEntity))
            .GetProperty("errorCode").GetString().ShouldBe("ValidationFailed");
        (await Problem(s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{order.Id()}/items/{burger}", new { quantity = 3 }, doubled.Version()), HttpStatusCode.UnprocessableEntity))
            .GetProperty("errors").TryGetProperty("quantity", out _).ShouldBeTrue();

        var withAcai = await (await s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/items",
            new { items = new[] { Dish(s.Acai, s.Acai500) } }, doubled.Version())).ExpectAsync(HttpStatusCode.OK, _api);
        var removed = await (await s.Client.SendJsonAsync(HttpMethod.Delete, $"/api/v1/orders/{order.Id()}/items/{combo}", null, withAcai.Version()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        removed.GetProperty("items").EnumerateArray().ShouldHaveSingleItem().GetProperty("name").GetString().ShouldBe("Açaí 500 ml");
        removed.GetProperty("totalAmount").GetString().ShouldBe("18.00");
        (await Scalar<long>(s.TenantId, "SELECT count(*) FROM order_items WHERE tenant_id = @t AND status = 'REMOVED'")).ShouldBe(5); // combo, 3 partes, bacon
    }

    /// Critérios 6 e 7: pagamento parcial e troco, quitado bloqueia, cancelar exige estornar; o fechamento mostra a linha
    /// "À la carte" (uma linha, Q-311) e o "Vendido pelo site", no resumo e no snapshot.
    [Fact]
    public async Task Payments_cancellation_and_closing_work_for_a_la_carte()
    {
        var s = await ShopAsync();
        var comanda = await CreateAsync(s.Client, Comanda(ComboItem(s, 2, s.CocaZero), Dish(s.Acai, s.Acai500)));
        comanda.GetProperty("totalAmount").GetString().ShouldBe("91.00"); // 73,00 + 18,00

        var cash = await (await Pay(s.Client, comanda.Id(), 1, new { amount = "41.00", paymentMethod = "CASH", cashReceived = "50.00", label = "Pessoa 1" }))
            .ExpectAsync(HttpStatusCode.Created, _api);
        cash.GetProperty("order").GetProperty("balance").GetString().ShouldBe("50.00");
        var paid = await (await Pay(s.Client, comanda.Id(), cash.GetProperty("order").Version(), new { amount = "50.00", paymentMethod = "PIX", cashReceived = (string?)null, label = "Pessoa 2" }))
            .ExpectAsync(HttpStatusCode.Created, _api);
        paid.GetProperty("order").GetProperty("isPaid").GetBoolean().ShouldBeTrue();
        var version = paid.GetProperty("order").Version();
        (await Problem(s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{comanda.Id()}/items", new { items = new[] { Beverage(s.Lata, s.Coca) } }, version),
            HttpStatusCode.Conflict)).GetProperty("errorCode").GetString().ShouldBe("OrderAlreadyPaid");

        var avulso = await CreateAsync(s.Client, Order("PICKUP", [Dish(s.XBurguer, null)], name: "Carla"));
        avulso.GetProperty("fulfillmentStatus").GetString().ShouldBe("PENDING");
        var payment = await (await Pay(s.Client, avulso.Id(), 1, new { amount = "22.00", paymentMethod = "CARD", cashReceived = (string?)null, label = (string?)null }))
            .ExpectAsync(HttpStatusCode.Created, _api);
        (await Problem(s.Client.SendJsonAsync(HttpMethod.Post, $"/api/v1/orders/{avulso.Id()}/cancel", new { reason = "Cliente desistiu" }, 2), HttpStatusCode.Conflict))
            .GetProperty("errorCode").GetString().ShouldBe("OrderHasPayments");
        var reversed = await (await s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/payments/{payment.GetProperty("payment").Id()}/reverse",
            new { reason = "Cobrado no cartão errado" }, 2)).ExpectAsync(HttpStatusCode.Created, _api);
        await (await s.Client.SendJsonAsync(HttpMethod.Post, $"/api/v1/orders/{avulso.Id()}/cancel", new { reason = "Cliente desistiu" }, reversed.GetProperty("order").Version()))
            .ExpectAsync(HttpStatusCode.OK, _api);

        var summary = await (await s.Client.GetAsync("/api/v1/closing/summary", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        summary.GetProperty("siteSoldTotal").GetString().ShouldBe("0.00");
        summary.GetProperty("totalSold").GetString().ShouldBe("91.00");
        summary.GetProperty("canceled").GetProperty("total").GetString().ShouldBe("22.00");
        summary.GetProperty("byOrderType").EnumerateArray().Select(l => l.GetProperty("orderType").GetString()).ShouldBe(["MARMITA", "PF", "KILO", "A_LA_CARTE"]);
        var line = summary.GetProperty("byOrderType").EnumerateArray().Single(l => l.GetProperty("orderType").GetString() == "A_LA_CARTE");
        line.GetProperty("count").GetInt32().ShouldBe(1);
        line.GetProperty("sold").GetString().ShouldBe("91.00");
        line.GetProperty("received").GetString().ShouldBe("113.00"); // 41 + 50 + 22 (o estorno conta à parte)
        line.GetProperty("reversed").GetString().ShouldBe("22.00");
        line.GetProperty("pending").GetString().ShouldBe("0.00");

        var day = summary.GetProperty("businessDay");
        var closing = await (await s.Client.SendWithKeyAsync(HttpMethod.Post, "/api/v1/closing/finalize",
                new { businessDayId = day.Id(), countedCash = summary.GetProperty("cash").GetProperty("expectedCash").GetString(), justification = (string?)null }, day.Version()))
            .ExpectAsync(HttpStatusCode.Created, _api);
        closing.GetProperty("isFinal").GetBoolean().ShouldBeTrue();
        closing.GetProperty("siteSoldTotal").GetString().ShouldBe("0.00");
        closing.GetProperty("byOrderType").EnumerateArray().Single(l => l.GetProperty("orderType").GetString() == "A_LA_CARTE")
            .GetProperty("sold").GetString().ShouldBe("91.00");
        (await Scalar<long>(s.TenantId, "SELECT count(*) FROM daily_closing_lines WHERE tenant_id = @t AND key = 'A_LA_CARTE'")).ShouldBe(1);
    }

    /// "Vendido pelo site" (ADR-0057 item 12): só o pedido `SITE` aceito; o do app fica fora. Restaurante: a linha
    /// `A_LA_CARTE` sai zerada.
    [Fact]
    public async Task Site_sold_total_counts_only_accepted_site_orders()
    {
        var r = await OrderingScenario.CreateAsync(_api, db);
        var web = await r.SubmitWebOrderAsync(delivery: false);
        await r.SubmitWebOrderAsync(delivery: false, phone: "+5534977776666");
        var accept = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{web}/accept") { Content = JsonContent.Create(new { estimatedReadyMinutes = (int?)null }) };
        accept.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        accept.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        await (await r.Client.SendAsync(accept, Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        await r.CreateOrderAsync(r.Pf());

        var summary = await (await r.Client.GetAsync("/api/v1/closing/summary", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        summary.GetProperty("siteSoldTotal").GetString().ShouldBe("40.00");
        summary.GetProperty("totalSold").GetString().ShouldBe("88.00"); // 40,00 do site + PF 2 × 24,00
        var line = summary.GetProperty("byOrderType").EnumerateArray().Single(l => l.GetProperty("orderType").GetString() == "A_LA_CARTE");
        line.GetProperty("count").GetInt32().ShouldBe(0);
        line.GetProperty("sold").GetString().ShouldBe("0.00");
    }

    /// ADR-0058 item 2: entrega e retirada à la carte (os campos de entrega de hoje; as exigências do telefone são da fatia 4).
    [Fact]
    public async Task Delivery_and_pickup_a_la_carte_use_the_current_delivery_fields()
    {
        var s = await ShopAsync();
        await Sql(s.TenantId, "UPDATE tenant_settings SET default_delivery_fee = 6.00 WHERE tenant_id = @t");

        var delivery = await CreateAsync(s.Client, Order("DELIVERY", [Dish(s.XBurguer, null)], name: "João",
            address: new { street = "Rua das Acácias", number = "120", complement = (string?)null, neighborhood = (string?)null, reference = (string?)null }));
        delivery.GetProperty("deliveryFee").GetString().ShouldBe("6.00");
        delivery.GetProperty("deliveryFeeSource").GetString().ShouldBe("FIXED_DEFAULT");
        delivery.GetProperty("totalAmount").GetString().ShouldBe("28.00");
        delivery.GetProperty("fulfillmentStatus").GetString().ShouldBe("PENDING");

        (await Problem(s.Client.PostIdempotentAsync("/api/v1/orders", Order("DELIVERY", [Dish(s.XBurguer, null)], name: "João"), Guid.NewGuid()),
            HttpStatusCode.UnprocessableEntity)).GetProperty("errors").TryGetProperty("deliveryAddress", out _).ShouldBeTrue();

        // Balcão "para viagem" (Q-320): comanda do salão com o número da comanda.
        var tab = await CreateAsync(s.Client, Order("DINE_IN", [Dish(s.XBurguer, null)], tab: "47"));
        tab.GetProperty("tabNumber").GetString().ShouldBe("47");
    }

    /// ADR-0058 item 1 e ADR-0054 item 14: comanda exige `A_LA_CARTE`; entrega e retirada, `DELIVERY`; antes do expediente.
    [Fact]
    public async Task Restaurant_without_the_modules_gets_403()
    {
        var r = await OrderingScenario.CreateAsync(_api, db, openDay: false);

        foreach (var (fulfillment, module) in new[] { ("DINE_IN", "A_LA_CARTE"), ("PICKUP", "DELIVERY"), ("DELIVERY", "DELIVERY") })
        {
            var problem = await Problem(r.Client.PostIdempotentAsync("/api/v1/orders", Order(fulfillment, [Dish(Guid.NewGuid(), null)], name: "Ana"), Guid.NewGuid()),
                HttpStatusCode.Forbidden);
            problem.GetProperty("errorCode").GetString().ShouldBe("ModuleNotEnabled");
            problem.GetProperty("module").GetString().ShouldBe(module);
        }
    }

    /// Isolamento (ADR-0026): o prato, o combo e o pedido de A não existem para B.
    [Fact]
    public async Task Tenants_are_isolated()
    {
        var a = await ShopAsync();
        var b = await ShopAsync();
        var order = await CreateAsync(a.Client, Comanda(ComboItem(a, 1, a.Coca)));

        (await Problem(b.Client.PostIdempotentAsync("/api/v1/orders", Comanda(Dish(a.XBurguer, null)), Guid.NewGuid()), HttpStatusCode.UnprocessableEntity))
            .GetProperty("errorCode").GetString().ShouldBe("ProductUnavailable");
        (await Problem(b.Client.PostIdempotentAsync("/api/v1/orders", Comanda(ComboItem(a, 1, a.Coca)), Guid.NewGuid()), HttpStatusCode.UnprocessableEntity))
            .GetProperty("errorCode").GetString().ShouldBe("ProductUnavailable");
        // Prato de B com o adicional de A.
        (await Problem(b.Client.PostIdempotentAsync("/api/v1/orders", Comanda(Dish(b.XBurguer, null, 1, null, (a.Bacon, 1))), Guid.NewGuid()), HttpStatusCode.UnprocessableEntity))
            .GetProperty("errorCode").GetString().ShouldBe("ProductUnavailable");
        (await (await b.Client.GetAsync($"/api/v1/orders/{order.Id()}", Ct)).ExpectAsync(HttpStatusCode.NotFound, _api)).GetProperty("errorCode").GetString().ShouldBe("NotFound");
        await Problem(b.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/items", new { items = new[] { Dish(b.XBurguer, null) } }, 1), HttpStatusCode.NotFound);

        var board = await (await b.Client.GetAsync("/api/v1/production/orders", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        board.GetProperty("groups").GetProperty("OPEN").GetArrayLength().ShouldBe(0);
        (await Scalar<long>(b.TenantId, "SELECT count(*) FROM orders WHERE tenant_id = @t")).ShouldBe(0);
    }

    /// EP-003 fatia 4 (ADR-0059, RF-312): delivery por telefone da lanchonete. 1 combo (32,00) + açaí 500 ml com granola
    /// (20,00) + taxa digitada 5,00 (padrão 4,00 → MANUAL) = 57,00; troco para 100,00. Nasce aceito, com número, na
    /// cozinha e na lista da aba Delivery; retirada e entrega exigem nome e telefone; troco abaixo do total, forma no salão
    /// e entrega sem forma → 422.
    [Fact]
    public async Task Phone_delivery_is_born_accepted_in_the_kitchen_with_change_and_contact_rules()
    {
        var s = await ShopAsync();
        await Sql(s.TenantId, "UPDATE tenant_settings SET default_delivery_fee = 4.00 WHERE tenant_id = @t");
        var address = new { street = "Rua das Flores", number = "123", complement = (string?)null, neighborhood = "Centro", reference = (string?)null };
        object[] items = [ComboItem(s, 1, s.Coca, bacon: false), Dish(s.Acai, s.Acai500, 1, null, (s.Granola, 1))];

        Dictionary<string, object?> Phone(string fulfillment, string? name = "Carla", string? phone = "+5534988887777", string? payment = "CASH",
            string? changeFor = null, string? fee = null)
        {
            var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(
                Order(fulfillment, items, name: name, address: fulfillment == "DELIVERY" ? address : null, phone: phone, payment: payment), ApiFactory.Json))!
                .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            body["changeFor"] = changeFor;
            body["deliveryFee"] = fee;
            body["customerPhone"] = phone;
            body["expectedPaymentMethod"] = payment;
            return body;
        }

        var order = await CreateAsync(s.Client, Phone("DELIVERY", changeFor: "100.00", fee: "5.00"));
        order.GetProperty("orderNumber").GetInt32().ShouldBe(1);
        order.GetProperty("acceptanceStatus").GetString().ShouldBe("ACCEPTED");
        order.GetProperty("origin").GetString().ShouldBe("APP");
        order.GetProperty("deliveryFee").GetString().ShouldBe("5.00");
        order.GetProperty("deliveryFeeSource").GetString().ShouldBe("MANUAL");
        order.GetProperty("totalAmount").GetString().ShouldBe("57.00");
        order.GetProperty("expectedPaymentMethod").GetString().ShouldBe("CASH");
        order.GetProperty("changeFor").GetString().ShouldBe("100.00");
        order.GetProperty("balance").GetString().ShouldBe("57.00");
        order.GetProperty("customer").GetProperty("id").ValueKind.ShouldBe(JsonValueKind.String);
        (await Scalar<long>(s.TenantId, "SELECT count(*) FROM customer_addresses WHERE tenant_id = @t")).ShouldBe(0);

        var board = await (await s.Client.GetAsync("/api/v1/production/orders", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        board.GetProperty("groups").GetProperty("OPEN").EnumerateArray().ShouldHaveSingleItem().Id().ShouldBe(order.Id());

        var tab = await (await s.Client.GetAsync("/api/v1/orders?orderType=A_LA_CARTE&fulfillmentType=DELIVERY&fulfillmentType=PICKUP", Ct))
            .ExpectAsync(HttpStatusCode.OK, _api);
        var card = tab.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        card.GetProperty("changeFor").GetString().ShouldBe("100.00");
        card.GetProperty("fulfillmentStatus").GetString().ShouldBe("PENDING");

        async Task<List<string>> Refused(object body)
        {
            var problem = await Problem(s.Client.PostIdempotentAsync("/api/v1/orders", body, Guid.NewGuid()), HttpStatusCode.UnprocessableEntity);
            problem.GetProperty("errorCode").GetString().ShouldBe("ValidationFailed");
            return problem.GetProperty("errors").EnumerateObject().Select(p => p.Name).ToList();
        }

        (await Refused(Phone("DELIVERY", changeFor: "50.00"))).ShouldContain("changeFor");
        (await Refused(Phone("DELIVERY", payment: null))).ShouldContain("expectedPaymentMethod");
        (await Refused(Phone("PICKUP", phone: null, payment: null))).ShouldContain("customerPhone");
        (await Refused(Phone("PICKUP", name: null, payment: null))).ShouldContain("customerName");
        (await Refused(Phone("DINE_IN", name: null, phone: null, payment: "PIX"))).ShouldContain("expectedPaymentMethod");

        var pickup = await CreateAsync(s.Client, Phone("PICKUP", payment: null));
        pickup.GetProperty("orderNumber").GetInt32().ShouldBe(2);
        pickup.GetProperty("expectedPaymentMethod").ValueKind.ShouldBe(JsonValueKind.Null);
        pickup.GetProperty("customer").Id().ShouldBe(order.GetProperty("customer").Id());
    }

    private static object Sized(Guid category, string name, params (string Name, string Price)[] sizes) => new
    {
        dishCategoryId = category, name, description = (string?)null, requiresPreparation = (bool?)null, hasSizes = true, price = (string?)null,
        sellsOnSite = false, sitePrice = (string?)null, sortOrder = 0,
        sizes = sizes.Select((s, i) => new { id = (Guid?)null, name = s.Name, price = s.Price, sellsOnSite = false, sitePrice = (string?)null, sortOrder = i + 1 }).ToArray(),
    };

    private static Guid SizeId(JsonElement dish, string name) =>
        dish.GetProperty("sizes").EnumerateArray().Single(s => s.GetProperty("name").GetString() == name).Id();

    private async Task<JsonElement> Created(HttpClient client, string path, object body) =>
        await (await client.PostAsJsonAsync($"{FixedMenu}/{path}", body, Ct)).ExpectAsync(HttpStatusCode.Created, _api);

    private async Task<JsonElement> CreateAsync(HttpClient client, object body) =>
        await (await client.PostIdempotentAsync("/api/v1/orders", body, Guid.NewGuid())).ExpectAsync(HttpStatusCode.Created, _api);

    private static Task<HttpResponseMessage> Pay(HttpClient client, Guid orderId, int version, object body) =>
        client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{orderId}/payments", body, version);

    private async Task<JsonElement> Problem(Task<HttpResponseMessage> call, HttpStatusCode status) => await (await call).ExpectAsync(status, _api);

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
