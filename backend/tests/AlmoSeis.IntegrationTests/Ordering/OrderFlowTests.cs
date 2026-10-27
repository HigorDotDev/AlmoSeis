using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Ordering;

/// Pedidos (06 §6.9, ADR-0005/0007/0020–0023) contra a API real.
[Collection(nameof(PostgresCollection))]
public sealed class OrderFlowTests(PostgresFixture db) : IDisposable
{
    private readonly ApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Marmita_delivery_prices_everything_on_the_server()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);

        var response = await s.Client.PostIdempotentAsync("/api/v1/orders", s.MarmitaDelivery(), Guid.NewGuid());
        var order = await response.ExpectAsync(HttpStatusCode.Created, _api);

        response.Headers.Location!.ToString().ShouldBe($"/api/v1/orders/{order.Id()}");
        response.Headers.ETag!.Tag.ShouldBe("\"1\"");
        order.GetProperty("orderNumber").GetInt32().ShouldBe(1);
        order.GetProperty("businessDate").GetString().ShouldBe(OrderingScenario.Today.ToString("yyyy-MM-dd"));
        order.GetProperty("productionStatus").GetString().ShouldBe("OPEN");
        order.GetProperty("cancellationStatus").GetString().ShouldBe("ACTIVE");

        // 2 × 20,00 + ovo 2,00 × 1 × 2 + Coca 6,00 = 50,00; + 5,00 de entrega padrão = 55,00 (06 §6.9).
        order.GetProperty("itemsTotal").GetString().ShouldBe("50.00");
        order.GetProperty("deliveryFee").GetString().ShouldBe("5.00");
        order.GetProperty("totalAmount").GetString().ShouldBe("55.00");
        order.GetProperty("paidAmount").GetString().ShouldBe("0.00");
        order.GetProperty("balance").GetString().ShouldBe("55.00");
        order.GetProperty("isPaid").GetBoolean().ShouldBeFalse();

        var marmita = order.GetProperty("items")[0];
        marmita.GetProperty("name").GetString().ShouldBe("Marmita M");
        marmita.GetProperty("composition").GetString().ShouldBe("Frango grelhado · Arroz, Feijão");
        marmita.GetProperty("components").GetArrayLength().ShouldBe(3);
        marmita.GetProperty("lineTotal").GetString().ShouldBe("40.00");
        marmita.GetProperty("requiresPreparation").GetBoolean().ShouldBeTrue();
        var addon = marmita.GetProperty("addons")[0];
        addon.GetProperty("name").GetString().ShouldBe("Ovo frito");
        addon.GetProperty("lineTotal").GetString().ShouldBe("4.00");
        order.GetProperty("items")[1].GetProperty("requiresPreparation").GetBoolean().ShouldBeFalse();

        order.GetProperty("customer").GetProperty("name").GetString().ShouldBe("João Silva");
        order.GetProperty("deliveryAddress").GetProperty("street").GetString().ShouldBe("Rua das Acácias");
        order.GetProperty("payments").GetArrayLength().ShouldBe(0);
        order.GetProperty("createdBy").GetProperty("displayName").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Numbers_follow_the_business_date_and_a_replay_does_not_consume_one()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var key = Guid.NewGuid();

        var first = await (await s.Client.PostIdempotentAsync("/api/v1/orders", s.Pf(), key)).ExpectAsync(HttpStatusCode.Created, _api);
        var replay = await s.Client.PostIdempotentAsync("/api/v1/orders", s.Pf(), key);
        (await replay.ExpectAsync(HttpStatusCode.Created, _api)).Id().ShouldBe(first.Id());
        replay.Headers.Contains("Idempotent-Replayed").ShouldBeTrue();

        var second = await s.CreateOrderAsync(s.Pf("6"));
        first.GetProperty("orderNumber").GetInt32().ShouldBe(1);
        second.GetProperty("orderNumber").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Order_needs_an_open_business_day_and_an_idempotency_key()
    {
        var s = await OrderingScenario.CreateAsync(_api, db, openDay: false);

        (await (await s.Client.PostAsJsonAsync("/api/v1/orders", s.Pf(), Ct)).ExpectAsync(HttpStatusCode.PreconditionRequired, _api))
            .GetProperty("errorCode").GetString().ShouldBe("IdempotencyKeyRequired");
        (await (await s.Client.PostIdempotentAsync("/api/v1/orders", s.Pf(), Guid.NewGuid())).ExpectAsync(HttpStatusCode.Conflict, _api))
            .GetProperty("errorCode").GetString().ShouldBe("BusinessDayRequired");
    }

    [Fact]
    public async Task Pf_price_comes_from_the_menu()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);

        var order = await s.CreateOrderAsync(s.Pf());

        var item = order.GetProperty("items")[0];
        item.GetProperty("name").GetString().ShouldBe("PF Frango grelhado");
        item.GetProperty("unitPrice").GetString().ShouldBe("24.00");
        order.GetProperty("totalAmount").GetString().ShouldBe("48.00");
        order.GetProperty("customer").ValueKind.ShouldBe(JsonValueKind.Null);
        order.GetProperty("deliveryFee").GetString().ShouldBe("0.00");
    }

    [Fact]
    public async Task Menu_and_kind_rules_reject_the_order()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);

        // Costela está no cardápio, mas marcada como "acabou".
        var soldOut = new
        {
            orderType = "MARMITA", fulfillmentType = "PICKUP", customerName = "Ana",
            items = new object[] { new { itemKind = "MARMITA", marmitaSizeId = s.SizeM, quantity = 1, mainDishProductIds = new[] { s.Costela } } },
        };
        var problem = await (await s.Client.PostIdempotentAsync("/api/v1/orders", soldOut, Guid.NewGuid())).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);
        problem.GetProperty("errorCode").GetString().ShouldBe("ProductUnavailable");

        var wrongKind = new { orderType = "PF", fulfillmentType = "DINE_IN", items = new object[] { new { itemKind = "MARMITA", marmitaSizeId = s.SizeM, quantity = 1, mainDishProductIds = new[] { s.Frango } } } };
        (await (await s.Client.PostIdempotentAsync("/api/v1/orders", wrongKind, Guid.NewGuid())).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api))
            .GetProperty("errors").TryGetProperty("items[0].itemKind", out _).ShouldBeTrue();

        var noAddress = new { orderType = "MARMITA", fulfillmentType = "DELIVERY", customerName = "Ana", items = new object[] { new { itemKind = "BEVERAGE", productId = s.Coca, quantity = 1 } } };
        (await (await s.Client.PostIdempotentAsync("/api/v1/orders", noAddress, Guid.NewGuid())).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api))
            .GetProperty("errors").TryGetProperty("deliveryAddress", out _).ShouldBeTrue();

        var empty = new { orderType = "PF", fulfillmentType = "DINE_IN", items = Array.Empty<object>() };
        (await (await s.Client.PostIdempotentAsync("/api/v1/orders", empty, Guid.NewGuid())).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api))
            .GetProperty("errors").TryGetProperty("items", out _).ShouldBeTrue();

        // Nada foi gravado, nem o número do pedido.
        (await s.ScalarAsync<long>("SELECT count(*) FROM orders WHERE tenant_id = @t")).ShouldBe(0);
    }

    [Fact]
    public async Task Kilo_order_snapshots_the_price_per_kg_and_rounds_each_weighing()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);

        (await (await s.Client.PostIdempotentAsync("/api/v1/orders", s.Kilo(452), Guid.NewGuid())).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api))
            .GetProperty("errorCode").GetString().ShouldBe("KiloPriceNotConfigured");

        await s.SetKiloPriceAsync("59.90");
        var order = await s.CreateOrderAsync(s.Kilo(452, 318));

        order.GetProperty("productionStatus").ValueKind.ShouldBe(JsonValueKind.Null);
        order.GetProperty("weighings")[0].GetProperty("amount").GetString().ShouldBe("27.07"); // 452 × 59,90 / 1000 = 27,0748
        order.GetProperty("weighings")[1].GetProperty("amount").GetString().ShouldBe("19.05"); // 19,0482
        order.GetProperty("weighingsTotal").GetString().ShouldBe("46.12");
        order.GetProperty("customer").GetProperty("id").ValueKind.ShouldBe(JsonValueKind.Null);
        order.GetProperty("customer").GetProperty("name").GetString().ShouldBe("Mesa 3");

        // Preço novo não muda pesagem já feita; a próxima usa o vigente.
        await s.SetKiloPriceAsync("60.00");
        var updated = await (await s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/weighings", new { weighings = new[] { new { weightGrams = 1000 } } }, order.Version()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        updated.GetProperty("weighings")[2].GetProperty("amount").GetString().ShouldBe("60.00");
        updated.GetProperty("totalAmount").GetString().ShouldBe("106.12");

        (await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{order.Id()}/status", new { productionStatus = "COMPLETED" }, updated.Version()))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api)).GetProperty("errors").TryGetProperty("productionStatus", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Delivery_fee_different_from_the_default_needs_permission()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var attendant = _api.Authenticated(await _api.EmployeeTokenAsync(db, s.Owner, "ATTENDANT"));

        var problem = await (await attendant.PostIdempotentAsync("/api/v1/orders", s.MarmitaDelivery(deliveryFee: "8.00"), Guid.NewGuid()))
            .ExpectAsync(HttpStatusCode.Forbidden, _api);
        problem.GetProperty("permission").GetString().ShouldBe("orders.delivery-fee.edit");

        (await s.CreateOrderAsync(s.MarmitaDelivery(deliveryFee: "5.00"), attendant)).GetProperty("deliveryFee").GetString().ShouldBe("5.00");
        (await s.CreateOrderAsync(s.MarmitaDelivery(deliveryFee: "8.00"))).GetProperty("totalAmount").GetString().ShouldBe("58.00");
    }

    [Fact]
    public async Task Item_edits_recalculate_totals_and_bump_the_version_once()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var order = await s.CreateOrderAsync(s.MarmitaDelivery(quantity: 1));
        var marmitaId = order.GetProperty("items")[0].Id();

        // 3 marmitas: o ovo (filho) passa a contar 3 vezes.
        var changed = await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{order.Id()}/items/{marmitaId}", new { quantity = 3 }, 1))
            .ExpectAsync(HttpStatusCode.OK, _api);
        changed.Version().ShouldBe(2);
        changed.GetProperty("items")[0].GetProperty("addons")[0].GetProperty("lineTotal").GetString().ShouldBe("6.00");
        changed.GetProperty("itemsTotal").GetString().ShouldBe("72.00"); // 60 + 6 + 6

        var stale = await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{order.Id()}/items/{marmitaId}", new { quantity = 2 }, 1))
            .ExpectAsync(HttpStatusCode.Conflict, _api);
        stale.GetProperty("current").GetProperty("version").GetInt32().ShouldBe(2);

        var cocaId = changed.GetProperty("items")[1].Id();
        var removed = await (await s.Client.SendJsonAsync(HttpMethod.Delete, $"/api/v1/orders/{order.Id()}/items/{cocaId}", null, 2))
            .ExpectAsync(HttpStatusCode.OK, _api);
        removed.GetProperty("items").GetArrayLength().ShouldBe(1);
        removed.GetProperty("totalAmount").GetString().ShouldBe("71.00");

        (await (await s.Client.SendJsonAsync(HttpMethod.Delete, $"/api/v1/orders/{order.Id()}/items/{marmitaId}", null, 3))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api)).GetProperty("errors").TryGetProperty("itemId", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Adding_an_item_with_preparation_reopens_a_completed_order()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var order = await s.CreateOrderAsync(s.Pf());
        var pfId = order.GetProperty("items")[0].Id();
        await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{order.Id()}/status", new { productionStatus = "COMPLETED" }, 1))
            .ExpectAsync(HttpStatusCode.OK, _api);

        var withCoca = await (await s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/items",
                new { items = new[] { new { itemKind = "BEVERAGE", productId = s.Coca, quantity = 1 } } }, 2))
            .ExpectAsync(HttpStatusCode.OK, _api);
        withCoca.GetProperty("productionStatus").GetString().ShouldBe("COMPLETED"); // bebida não tem preparo

        var withEgg = await (await s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/items",
                new { items = new[] { new { itemKind = "ADDON", parentOrderItemId = pfId, productId = s.Ovo, quantity = 1 } } }, 3))
            .ExpectAsync(HttpStatusCode.OK, _api);
        withEgg.GetProperty("productionStatus").GetString().ShouldBe("IN_PROGRESS");
        withEgg.GetProperty("items")[0].GetProperty("addons")[0].GetProperty("lineTotal").GetString().ShouldBe("4.00"); // 2,00 × 1 × 2 PFs
    }

    [Fact]
    public async Task Paid_order_only_accepts_notes_table_and_tab()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var order = await s.CreateOrderAsync(s.Pf());
        await s.MarkPaidAsync(order.Id());

        var notes = await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{order.Id()}", new { notes = "Levar talheres", tableLabel = "7" }, 2))
            .ExpectAsync(HttpStatusCode.OK, _api);
        notes.GetProperty("tableLabel").GetString().ShouldBe("7");

        (await (await s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/items",
                new { items = new[] { new { itemKind = "BEVERAGE", productId = s.Coca, quantity = 1 } } }, 3))
            .ExpectAsync(HttpStatusCode.Conflict, _api)).GetProperty("errorCode").GetString().ShouldBe("OrderAlreadyPaid");
        (await (await s.Client.SendJsonAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/cancel", new { reason = "Cliente desistiu" }, 3))
            .ExpectAsync(HttpStatusCode.Conflict, _api)).GetProperty("errorCode").GetString().ShouldBe("OrderHasPayments");

        // Status de produção muda mesmo pago (RN-007.11).
        await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{order.Id()}/status", new { productionStatus = "IN_PROGRESS" }, 3))
            .ExpectAsync(HttpStatusCode.OK, _api);
    }

    [Fact]
    public async Task Fulfillment_change_drops_address_and_fee()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var order = await s.CreateOrderAsync(s.MarmitaDelivery());

        var pickup = await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{order.Id()}", new { fulfillmentType = "PICKUP" }, 1))
            .ExpectAsync(HttpStatusCode.OK, _api);

        pickup.GetProperty("deliveryAddress").ValueKind.ShouldBe(JsonValueKind.Null);
        pickup.GetProperty("deliveryFee").GetString().ShouldBe("0.00");
        pickup.GetProperty("totalAmount").GetString().ShouldBe("50.00");

        (await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{order.Id()}", new { fulfillmentType = "DINE_IN" }, 2))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api)).GetProperty("errors").TryGetProperty("fulfillmentType", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Canceled_order_is_read_only()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var order = await s.CreateOrderAsync(s.Pf());

        (await (await s.Client.SendJsonAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/cancel", new { reason = "Ops" }, 1))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api)).GetProperty("errors").TryGetProperty("reason", out _).ShouldBeTrue();

        var canceled = await (await s.Client.SendJsonAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/cancel", new { reason = "Cliente desistiu do pedido" }, 1))
            .ExpectAsync(HttpStatusCode.OK, _api);
        canceled.GetProperty("cancellationStatus").GetString().ShouldBe("CANCELED");
        canceled.GetProperty("cancellation").GetProperty("reason").GetString().ShouldBe("Cliente desistiu do pedido");

        (await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{order.Id()}", new { notes = "x" }, 2))
            .ExpectAsync(HttpStatusCode.Conflict, _api)).GetProperty("errorCode").GetString().ShouldBe("OrderCanceled");
    }

    [Fact]
    public async Task List_filters_searches_and_pages()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        await s.CreateOrderAsync(s.MarmitaDelivery());
        await s.CreateOrderAsync(s.Pf("5"));
        var third = await s.CreateOrderAsync(s.Pf("12"));

        var page = await (await s.Client.GetAsync("/api/v1/orders?limit=2", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        page.GetProperty("items").GetArrayLength().ShouldBe(2);
        page.GetProperty("items")[0].Id().ShouldBe(third.Id()); // mais recente primeiro
        var next = await (await s.Client.GetAsync($"/api/v1/orders?limit=2&cursor={page.GetProperty("nextCursor").GetString()}", Ct))
            .ExpectAsync(HttpStatusCode.OK, _api);
        next.GetProperty("items")[0].GetProperty("orderType").GetString().ShouldBe("MARMITA");
        next.GetProperty("items")[0].GetProperty("itemCount").GetInt32().ShouldBe(3);
        next.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);

        (await (await s.Client.GetAsync("/api/v1/orders?orderType=PF", Ct)).ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("items").GetArrayLength().ShouldBe(2);
        (await (await s.Client.GetAsync("/api/v1/orders?search=jo%C3%A3o", Ct)).ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("items").GetArrayLength().ShouldBe(1);
        (await (await s.Client.GetAsync("/api/v1/orders?search=12", Ct)).ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Kitchen_gets_alerts_from_others_and_acknowledges_them()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var kitchen = _api.Authenticated(await _api.EmployeeTokenAsync(db, s.Owner, "KITCHEN"));
        var order = await s.CreateOrderAsync(s.Pf());
        var pfId = order.GetProperty("items")[0].Id();

        var edited = await (await s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/items",
                new { items = new[] { new { itemKind = "BEVERAGE", productId = s.Coca, quantity = 1 } } }, 1))
            .ExpectAsync(HttpStatusCode.OK, _api);
        await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{order.Id()}/items/{pfId}", new { notes = "Sem salada" }, edited.Version()))
            .ExpectAsync(HttpStatusCode.OK, _api);

        // Quem alterou não vê o próprio alerta (RN-008.6).
        (await (await s.Client.GetAsync($"/api/v1/orders/{order.Id()}", Ct)).ExpectAsync(HttpStatusCode.OK, _api))
            .GetProperty("pendingAlertCount").GetInt32().ShouldBe(0);

        var board = await (await kitchen.GetAsync("/api/v1/production/orders", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        var open = board.GetProperty("groups").GetProperty("OPEN");
        open[0].GetProperty("label").GetString().ShouldBe("Mesa 5");
        open[0].GetProperty("pendingAlertCount").GetInt32().ShouldBe(2);
        board.GetProperty("groups").GetProperty("COMPLETED").GetArrayLength().ShouldBe(0);

        var alerts = (await (await kitchen.GetAsync($"/api/v1/orders/{order.Id()}/alerts", Ct)).ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("items");
        alerts.GetArrayLength().ShouldBe(2);
        alerts[0].GetProperty("changeKind").GetString().ShouldBe("ITEM_ADDED");
        alerts[0].GetProperty("summary").GetString().ShouldBe("+1 Coca-Cola 350ml");
        alerts[0].GetProperty("affectsProduction").GetBoolean().ShouldBeFalse();
        alerts[0].GetProperty("details").GetProperty("name").GetString().ShouldBe("Coca-Cola 350ml");
        alerts[1].GetProperty("changeKind").GetString().ShouldBe("ITEM_NOTES_CHANGED");
        alerts[1].GetProperty("affectsProduction").GetBoolean().ShouldBeTrue();

        var ids = alerts.EnumerateArray().Select(a => a.GetProperty("eventId").GetString()).ToArray();
        (await kitchen.PostAsJsonAsync($"/api/v1/orders/{order.Id()}/alerts/acknowledge", new { eventIds = ids }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await kitchen.PostAsJsonAsync($"/api/v1/orders/{order.Id()}/alerts/acknowledge", new { eventIds = ids }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await kitchen.GetAsync($"/api/v1/orders/{order.Id()}/alerts", Ct)).ExpectAsync(HttpStatusCode.OK, _api))
            .GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task History_lists_the_audit_of_the_order_newest_first()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var order = await s.CreateOrderAsync(s.Pf());
        await (await s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/items",
                new { items = new[] { new { itemKind = "BEVERAGE", productId = s.Coca, quantity = 1 } } }, 1))
            .ExpectAsync(HttpStatusCode.OK, _api);

        var history = (await (await s.Client.GetAsync($"/api/v1/orders/{order.Id()}/history", Ct)).ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("items");

        history.GetArrayLength().ShouldBe(2);
        history[0].GetProperty("operationType").GetString().ShouldBe("OrderItemAdded");
        history[0].GetProperty("after").GetProperty("name").GetString().ShouldBe("Coca-Cola 350ml");
        history[0].GetProperty("after").GetProperty("orderTotal").GetString().ShouldBe("54.00");
        history[0].GetProperty("actor").GetProperty("displayName").GetString().ShouldNotBeNullOrEmpty();
        history[1].GetProperty("operationType").GetString().ShouldBe("OrderCreated");
    }

    [Fact]
    public async Task Sold_product_cannot_change_kind()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        await s.CreateOrderAsync(s.MarmitaDelivery());

        var product = await (await s.Client.GetAsync($"/api/v1/products/{s.Coca}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        (await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/products/{s.Coca}", new { kind = "ADDON" }, product.Version()))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api)).GetProperty("errors").TryGetProperty("kind", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Creation_writes_feed_outbox_and_audit_together()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var order = await s.CreateOrderAsync(s.MarmitaDelivery(saveCustomer: true));

        (await s.ScalarAsync<long>("SELECT count(*) FROM change_feed_entries WHERE tenant_id = @t AND entity_type = 'ORDER' AND entity_id = @o", ("o", order.Id()))).ShouldBe(1);
        (await s.ScalarAsync<long>("SELECT count(*) FROM change_feed_entries WHERE tenant_id = @t AND entity_type = 'CUSTOMER'")).ShouldBe(1);
        (await s.ScalarAsync<long>("SELECT count(*) FROM outbox_messages WHERE tenant_id = @t AND event_type = 'OrderCreated' AND payload->'data'->>'label' = 'João Silva'")).ShouldBe(1);
        // Auditoria sem telefone nem endereço (ADR-0024).
        (await s.ScalarAsync<long>("SELECT count(*) FROM audit_logs WHERE tenant_id = @t AND (after::text LIKE '%99999%' OR after::text LIKE '%Acácias%')")).ShouldBe(0);
    }
}
