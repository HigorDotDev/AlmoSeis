using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlmoSeis.IntegrationTests.Ordering;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Payments;

/// Pagamentos e estornos (06 §6.10, ADR-0021, ADR-0022) contra a API real.
[Collection(nameof(PostgresCollection))]
public sealed class PaymentFlowTests(PostgresFixture db) : IDisposable
{
    private readonly ApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Cash_with_change_then_pix_settles_the_order()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var order = await s.CreateOrderAsync(s.MarmitaDelivery()); // 55,00

        var response = await Pay(s, order.Id(), 1, new { amount = "35.00", paymentMethod = "CASH", cashReceived = "50.00", label = (string?)null });
        var first = await response.ExpectAsync(HttpStatusCode.Created, _api);
        var payment = first.GetProperty("payment");
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/payments/{payment.Id()}");
        payment.GetProperty("changeGiven").GetString().ShouldBe("15.00");
        payment.GetProperty("status").GetString().ShouldBe("CONFIRMED");
        payment.GetProperty("reversal").ValueKind.ShouldBe(JsonValueKind.Null);
        first.GetProperty("order").GetProperty("version").GetInt32().ShouldBe(2);
        first.GetProperty("order").GetProperty("balance").GetString().ShouldBe("20.00");
        first.GetProperty("order").GetProperty("isPaid").GetBoolean().ShouldBeFalse();

        var second = await (await Pay(s, order.Id(), 2, new { amount = "20.00", paymentMethod = "PIX", cashReceived = (string?)null, label = "Pessoa 2" }))
            .ExpectAsync(HttpStatusCode.Created, _api);
        second.GetProperty("order").GetProperty("isPaid").GetBoolean().ShouldBeTrue();
        second.GetProperty("order").GetProperty("balance").GetString().ShouldBe("0.00");

        var reloaded = await (await s.Client.GetAsync($"/api/v1/orders/{order.Id()}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        reloaded.GetProperty("paidAmount").GetString().ShouldBe("55.00");
        reloaded.GetProperty("payments").GetArrayLength().ShouldBe(2);
        reloaded.GetProperty("payments")[1].GetProperty("paymentMethod").GetString().ShouldBe("PIX");
        reloaded.GetProperty("payments")[1].GetProperty("label").GetString().ShouldBe("Pessoa 2");

        (await s.ScalarAsync<long>("SELECT count(*) FROM outbox_messages WHERE tenant_id = @t AND event_type = 'OrderPaymentRegistered'")).ShouldBe(2);
        (await s.ScalarAsync<long>("SELECT count(*) FROM outbox_messages WHERE tenant_id = @t AND event_type = 'OrderPaymentConfirmed'")).ShouldBe(1);
        (await s.ScalarAsync<long>("SELECT count(*) FROM change_feed_entries WHERE tenant_id = @t AND entity_type = 'PAYMENT'")).ShouldBe(2);
    }

    [Fact]
    public async Task Payment_rules_are_enforced()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var order = await s.CreateOrderAsync(s.Pf()); // 48,00

        var exceeds = await (await Pay(s, order.Id(), 1, new { amount = "50.00", paymentMethod = "PIX" })).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);
        exceeds.GetProperty("errorCode").GetString().ShouldBe("PaymentExceedsBalance");
        exceeds.GetProperty("balance").GetString().ShouldBe("48.00");

        (await (await Pay(s, order.Id(), 1, new { amount = "10.00", paymentMethod = "PIX", cashReceived = "20.00" })).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api))
            .GetProperty("errors").TryGetProperty("cashReceived", out _).ShouldBeTrue();
        (await (await Pay(s, order.Id(), 1, new { amount = "10.00", paymentMethod = "CASH", cashReceived = "5.00" })).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api))
            .GetProperty("errors").TryGetProperty("cashReceived", out _).ShouldBeTrue();
        (await (await Pay(s, order.Id(), 1, new { amount = "0.00", paymentMethod = "CASH" })).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api))
            .GetProperty("errors").TryGetProperty("amount", out _).ShouldBeTrue();

        await s.SqlAsync("UPDATE tenant_settings SET enabled_payment_methods = ARRAY['CASH', 'PIX'] WHERE tenant_id = @t");
        (await (await Pay(s, order.Id(), 1, new { amount = "10.00", paymentMethod = "CARD" })).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api))
            .GetProperty("errorCode").GetString().ShouldBe("PaymentMethodDisabled");

        // Total mudou depois que o app leu o pedido: ninguém quita um valor desatualizado.
        var stale = await (await Pay(s, order.Id(), 7, new { amount = "10.00", paymentMethod = "PIX" })).ExpectAsync(HttpStatusCode.Conflict, _api);
        stale.GetProperty("errorCode").GetString().ShouldBe("VersionConflict");
        stale.GetProperty("current").GetProperty("totalAmount").GetString().ShouldBe("48.00");

        (await s.ScalarAsync<long>("SELECT count(*) FROM payments WHERE tenant_id = @t")).ShouldBe(0);
    }

    [Fact]
    public async Task Replaying_a_payment_does_not_charge_twice()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var order = await s.CreateOrderAsync(s.Pf());
        var key = Guid.NewGuid();

        var first = await (await PayWithKey(s, order.Id(), 1, new { amount = "10.00", paymentMethod = "PIX" }, key)).ExpectAsync(HttpStatusCode.Created, _api);
        var replay = await PayWithKey(s, order.Id(), 1, new { amount = "10.00", paymentMethod = "PIX" }, key);

        (await replay.ExpectAsync(HttpStatusCode.Created, _api)).GetProperty("payment").Id().ShouldBe(first.GetProperty("payment").Id());
        replay.Headers.Contains("Idempotent-Replayed").ShouldBeTrue();
        (await s.ScalarAsync<decimal>("SELECT paid_amount FROM orders WHERE tenant_id = @t AND id = @o", ("o", order.Id()))).ShouldBe(10.00m);
    }

    [Fact]
    public async Task Reversal_unlocks_a_paid_order_and_allows_cancellation()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var order = await s.CreateOrderAsync(s.Pf()); // 48,00
        var paid = await (await Pay(s, order.Id(), 1, new { amount = "48.00", paymentMethod = "CARD" })).ExpectAsync(HttpStatusCode.Created, _api);
        var paymentId = paid.GetProperty("payment").Id();

        var reversed = await (await s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/payments/{paymentId}/reverse", new { reason = "Cobrado em duplicidade" }, 2))
            .ExpectAsync(HttpStatusCode.Created, _api);
        reversed.GetProperty("reversal").GetProperty("amount").GetString().ShouldBe("48.00");
        reversed.GetProperty("reversal").GetProperty("paymentMethod").GetString().ShouldBe("CARD");
        reversed.GetProperty("order").GetProperty("isPaid").GetBoolean().ShouldBeFalse();
        reversed.GetProperty("order").GetProperty("balance").GetString().ShouldBe("48.00");

        var payment = await (await s.Client.GetAsync($"/api/v1/payments/{paymentId}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        payment.GetProperty("status").GetString().ShouldBe("REVERSED");
        payment.GetProperty("reversal").GetProperty("reason").GetString().ShouldBe("Cobrado em duplicidade");

        (await (await s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/payments/{paymentId}/reverse", new { reason = "De novo, por engano" }, 3))
            .ExpectAsync(HttpStatusCode.Conflict, _api)).GetProperty("errorCode").GetString().ShouldBe("PaymentAlreadyReversed");

        // Destravado: edita e, sem pagamento válido, pode cancelar (ADR-0022).
        await (await s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/items",
            new { items = new[] { new { itemKind = "BEVERAGE", productId = s.Coca, quantity = 1 } } }, 3)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await s.Client.SendJsonAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/cancel", new { reason = "Cliente desistiu" }, 4))
            .ExpectAsync(HttpStatusCode.OK, _api);

        var payments = (await (await s.Client.GetAsync($"/api/v1/orders/{order.Id()}/payments", Ct)).ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("items");
        payments.GetArrayLength().ShouldBe(1);
        var history = (await (await s.Client.GetAsync($"/api/v1/orders/{order.Id()}/history", Ct)).ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("items");
        history.EnumerateArray().Select(h => h.GetProperty("operationType").GetString())
            .ShouldBe(["OrderCanceled", "OrderItemAdded", "PaymentReversed", "PaymentRegistered", "OrderCreated"]);
    }

    [Fact]
    public async Task Pending_order_from_a_closed_day_is_paid_in_the_open_day()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var order = await s.CreateOrderAsync(s.MarmitaDelivery(saveCustomer: true, phone: "+5534999990101"));
        var firstDay = order.GetProperty("businessDayId").GetString();

        await s.SqlAsync("UPDATE business_days SET status = 'CLOSED', closed_at = now(), closed_by_employee_id = opened_by_employee_id WHERE tenant_id = @t AND status = 'OPEN'");
        (await (await Pay(s, order.Id(), 1, new { amount = "55.00", paymentMethod = "PIX" })).ExpectAsync(HttpStatusCode.Conflict, _api))
            .GetProperty("errorCode").GetString().ShouldBe("BusinessDayRequired");

        var opened = await (await s.Client.PostIdempotentAsync("/api/v1/business-days", new { openingBalance = "0.00", confirmSameDate = true }, Guid.NewGuid()))
            .ExpectAsync(HttpStatusCode.Created, _api);
        var paid = await (await Pay(s, order.Id(), 1, new { amount = "55.00", paymentMethod = "PIX" })).ExpectAsync(HttpStatusCode.Created, _api);

        paid.GetProperty("payment").GetProperty("businessDayId").GetString().ShouldBe(opened.GetProperty("businessDay").GetProperty("id").GetString());
        paid.GetProperty("payment").GetProperty("businessDayId").GetString().ShouldNotBe(firstDay);
        var customerId = order.GetProperty("customer").GetProperty("id").GetString();
        (await (await s.Client.GetAsync($"/api/v1/customers/{customerId}/pending-orders", Ct)).ExpectAsync(HttpStatusCode.OK, _api))
            .GetProperty("totalPending").GetString().ShouldBe("0.00");
    }

    [Fact]
    public async Task Roles_limit_who_registers_and_who_reverses()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var order = await s.CreateOrderAsync(s.Pf());
        var kitchen = _api.Authenticated(await _api.EmployeeTokenAsync(db, s.Owner, "KITCHEN"));
        var cashier = _api.Authenticated(await _api.EmployeeTokenAsync(db, s.Owner, "CASHIER"));

        await (await kitchen.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/payments", new { amount = "10.00", paymentMethod = "PIX" }, 1))
            .ExpectAsync(HttpStatusCode.Forbidden, _api);
        var paid = await (await cashier.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/payments", new { amount = "10.00", paymentMethod = "PIX" }, 1))
            .ExpectAsync(HttpStatusCode.Created, _api);

        (await (await cashier.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/payments/{paid.GetProperty("payment").Id()}/reverse", new { reason = "Teste de permissão" }, 2))
            .ExpectAsync(HttpStatusCode.Forbidden, _api)).GetProperty("permission").GetString().ShouldBe("payments.reverse");
    }

    private Task<HttpResponseMessage> Pay(OrderingScenario s, Guid orderId, int orderVersion, object body) =>
        s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{orderId}/payments", body, orderVersion);

    private static Task<HttpResponseMessage> PayWithKey(OrderingScenario s, Guid orderId, int orderVersion, object body, Guid key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{orderId}/payments") { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString());
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{orderVersion}\"");
        return s.Client.SendAsync(request, Ct);
    }
}
