using System.Net;
using System.Text.Json;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Ordering;

/// Delivery por telefone e cliente pelo telefone (EP-003 fatia 4, ADR-0059; 06 §6.8 e §6.9) contra a API real, na
/// marmita do restaurante: nome e telefone (Q-312), forma prevista e "troco para" (Q-316), taxa digitada na criação
/// (Q-315), cliente criado uma vez pelo telefone sem mudar o nome (Q-314), busca por telefone com o endereço do último
/// pedido (Q-313), filtros da aba Delivery, idempotência e isolamento entre tenants.
[Collection(nameof(PostgresCollection))]
public sealed class PhoneDeliveryFlowTests(PostgresFixture db) : IDisposable
{
    private readonly ApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _api.Dispose();

    /// Critérios 2, 4 e 5: 2 marmitas M com Ovo + Coca (50,00) + taxa 5,00 = 55,00. A forma prevista Pix não paga nada; o
    /// telefone novo cria o cliente só com nome e telefone; o segundo pedido do mesmo telefone vem com o nome do
    /// cadastro pela busca e, com outro nome digitado, não muda o cadastro.
    [Fact]
    public async Task Phone_creates_the_customer_once_without_address_and_another_name_does_not_change_it()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        const string phone = "+5534991110001";

        var none = await LookupAsync(s.Client, phone);
        none.GetProperty("customer").ValueKind.ShouldBe(JsonValueKind.Null);
        none.GetProperty("lastDeliveryAddress").ValueKind.ShouldBe(JsonValueKind.Null);

        var first = await s.CreateOrderAsync(s.MarmitaDelivery(phone: phone, expectedPaymentMethod: "PIX"));
        first.GetProperty("orderNumber").GetInt32().ShouldBe(1);
        first.GetProperty("acceptanceStatus").GetString().ShouldBe("ACCEPTED");
        first.GetProperty("productionStatus").GetString().ShouldBe("OPEN");
        first.GetProperty("expectedPaymentMethod").GetString().ShouldBe("PIX");
        first.GetProperty("totalAmount").GetString().ShouldBe("55.00");
        first.GetProperty("paidAmount").GetString().ShouldBe("0.00");
        first.GetProperty("balance").GetString().ShouldBe("55.00");
        first.GetProperty("isPaid").GetBoolean().ShouldBeFalse();
        first.GetProperty("payments").GetArrayLength().ShouldBe(0);
        var customerId = first.GetProperty("customer").Id();

        (await s.ScalarAsync<long>("SELECT count(*) FROM payments WHERE tenant_id = @t")).ShouldBe(0);
        (await s.ScalarAsync<long>("SELECT count(*) FROM customer_addresses WHERE tenant_id = @t")).ShouldBe(0);

        // Feed e auditoria do cliente criado por SQL: versão 1, sem telefone na auditoria (ADR-0024).
        (await s.ScalarAsync<int>(
            "SELECT entity_version FROM change_feed_entries WHERE tenant_id = @t AND entity_type = 'CUSTOMER' AND entity_id = @c", ("c", customerId))).ShouldBe(1);
        var audit = await s.ScalarAsync<string>(
            "SELECT after::text FROM audit_logs WHERE tenant_id = @t AND operation_type = 'CustomerCreated' AND entity_id = @c", ("c", customerId));
        audit.ShouldContain("João Silva");
        audit.ShouldNotContain("99111");

        var found = await LookupAsync(s.Client, phone);
        found.GetProperty("customer").GetProperty("name").GetString().ShouldBe("João Silva");
        found.GetProperty("customer").GetProperty("addresses").GetArrayLength().ShouldBe(0);
        found.GetProperty("customer").GetProperty("pendingAmount").GetString().ShouldBe("55.00");
        found.GetProperty("lastDeliveryAddress").GetProperty("street").GetString().ShouldBe("Rua das Acácias");

        // Q-314: outro nome no mesmo telefone fica só no pedido.
        var other = new Dictionary<string, object?>(Body(s.MarmitaDelivery(phone: phone))) { ["customerName"] = "Joana" };
        var second = await s.CreateOrderAsync(other);
        second.GetProperty("customer").Id().ShouldBe(customerId);
        second.GetProperty("customer").GetProperty("name").GetString().ShouldBe("Joana");
        (await s.ScalarAsync<string>("SELECT name FROM customers WHERE tenant_id = @t AND id = @c", ("c", customerId))).ShouldBe("João Silva");
        (await s.ScalarAsync<long>("SELECT count(*) FROM customers WHERE tenant_id = @t")).ShouldBe(1);
    }

    /// Critério 7 e RF-314: dois atendentes registrando o mesmo telefone novo ao mesmo tempo criam um cliente só; o
    /// reenvio com a mesma chave não duplica pedido nem cliente.
    [Fact]
    public async Task Concurrent_orders_with_a_new_phone_create_one_customer_and_replay_duplicates_nothing()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        const string phone = "+5534991110002";

        var responses = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => s.Client.PostIdempotentAsync("/api/v1/orders", s.MarmitaDelivery(phone: phone), Guid.NewGuid())));
        var orders = new List<JsonElement>();
        foreach (var response in responses)
        {
            orders.Add(await response.ExpectAsync(HttpStatusCode.Created, _api));
        }

        orders.Select(o => o.GetProperty("customer").Id()).Distinct().Count().ShouldBe(1);
        (await s.ScalarAsync<long>("SELECT count(*) FROM customers WHERE tenant_id = @t")).ShouldBe(1);

        var key = Guid.NewGuid();
        var body = s.MarmitaDelivery(phone: "+5534991110003");
        var created = await (await s.Client.PostIdempotentAsync("/api/v1/orders", body, key)).ExpectAsync(HttpStatusCode.Created, _api);
        var replay = await (await s.Client.PostIdempotentAsync("/api/v1/orders", body, key)).ExpectAsync(HttpStatusCode.Created, _api);
        replay.Id().ShouldBe(created.Id());
        (await s.ScalarAsync<long>("SELECT count(*) FROM customers WHERE tenant_id = @t")).ShouldBe(2);
        (await s.ScalarAsync<long>("SELECT count(*) FROM orders WHERE tenant_id = @t")).ShouldBe(5);
    }

    /// Q-312 e Q-316 na marmita: entrega sem telefone ou sem forma prevista → 422; retirada no balcão sem telefone segue
    /// como hoje (sem cliente); "troco para" só em dinheiro e de pelo menos o total; forma desabilitada → 422; salão
    /// com forma prevista → 422.
    [Fact]
    public async Task Marmita_rules_for_phone_expected_payment_and_change()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);

        (await Errors(s, s.MarmitaDelivery(phone: null))).ShouldContain("customerPhone");
        (await Errors(s, s.MarmitaDelivery(expectedPaymentMethod: null))).ShouldContain("expectedPaymentMethod");
        (await Errors(s, s.MarmitaDelivery(expectedPaymentMethod: "CASH", changeFor: "50.00"))).ShouldContain("changeFor");
        (await Errors(s, s.MarmitaDelivery(expectedPaymentMethod: "PIX", changeFor: "100.00"))).ShouldContain("changeFor");

        var cash = await s.CreateOrderAsync(s.MarmitaDelivery(expectedPaymentMethod: "CASH", changeFor: "100.00"));
        cash.GetProperty("changeFor").GetString().ShouldBe("100.00");
        cash.GetProperty("totalAmount").GetString().ShouldBe("55.00");
        var exact = await s.CreateOrderAsync(s.MarmitaDelivery(expectedPaymentMethod: "CASH", changeFor: "55.00"));
        exact.GetProperty("changeFor").GetString().ShouldBe("55.00");

        var counter = new Dictionary<string, object?>(Body(s.MarmitaDelivery(phone: null, expectedPaymentMethod: null)))
        {
            ["fulfillmentType"] = "PICKUP", ["deliveryAddress"] = null,
        };
        var pickup = await s.CreateOrderAsync(counter);
        pickup.GetProperty("customer").GetProperty("id").ValueKind.ShouldBe(JsonValueKind.Null);
        pickup.GetProperty("customer").GetProperty("phoneNumber").ValueKind.ShouldBe(JsonValueKind.Null);
        pickup.GetProperty("expectedPaymentMethod").ValueKind.ShouldBe(JsonValueKind.Null);

        var pf = new Dictionary<string, object?>(Body(s.Pf())) { ["expectedPaymentMethod"] = "PIX" };
        (await Errors(s, pf)).ShouldContain("expectedPaymentMethod");

        await s.SqlAsync("UPDATE tenant_settings SET enabled_payment_methods = ARRAY['CASH', 'PIX'] WHERE tenant_id = @t");
        var disabled = await (await s.Client.PostIdempotentAsync("/api/v1/orders", s.MarmitaDelivery(expectedPaymentMethod: "CARD"), Guid.NewGuid()))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);
        disabled.GetProperty("errorCode").GetString().ShouldBe("PaymentMethodDisabled");
    }

    /// Q-315: o atendente digita a taxa na criação (MANUAL, valor e padrão na auditoria) e recebe 403 ao alterá-la
    /// depois; o gerente altera.
    [Fact]
    public async Task Attendant_types_the_fee_at_creation_but_needs_permission_to_change_it_later()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var attendant = _api.Authenticated(await _api.EmployeeTokenAsync(db, s.Owner, "ATTENDANT"));

        var order = await s.CreateOrderAsync(s.MarmitaDelivery(deliveryFee: "7.00"), attendant);
        order.GetProperty("deliveryFee").GetString().ShouldBe("7.00");
        order.GetProperty("deliveryFeeSource").GetString().ShouldBe("MANUAL");
        order.GetProperty("totalAmount").GetString().ShouldBe("57.00");

        var audit = await s.ScalarAsync<string>(
            "SELECT after::text FROM audit_logs WHERE tenant_id = @t AND operation_type = 'OrderCreated' AND entity_id = @o", ("o", order.Id()));
        using (var after = JsonDocument.Parse(audit))
        {
            after.RootElement.GetProperty("deliveryFee").GetString().ShouldBe("7.00");
            after.RootElement.GetProperty("deliveryFeeSource").GetString().ShouldBe("MANUAL");
            after.RootElement.GetProperty("defaultDeliveryFee").GetString().ShouldBe("5.00");
            after.RootElement.GetProperty("expectedPaymentMethod").GetString().ShouldBe("PIX");
        }

        audit.ShouldNotContain("99999");
        audit.ShouldNotContain("Acácias");

        var standard = await s.CreateOrderAsync(s.MarmitaDelivery(), attendant);
        standard.GetProperty("deliveryFeeSource").GetString().ShouldBe("FIXED_DEFAULT");

        var denied = await (await attendant.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{order.Id()}", new { deliveryFee = "8.00" }, order.Version()))
            .ExpectAsync(HttpStatusCode.Forbidden, _api);
        denied.GetProperty("errorCode").GetString().ShouldBe("PermissionDenied");
        denied.GetProperty("permission").GetString().ShouldBe("orders.delivery-fee.edit");

        var changed = await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{order.Id()}", new { deliveryFee = "8.00" }, order.Version()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        changed.GetProperty("deliveryFee").GetString().ShouldBe("8.00");
    }

    /// Aba Delivery (ADR-0059 itens 10 a 12): entrega e retirada do telefone e do site aceito na mesma lista, com a forma
    /// prevista e o "troco para" no resumo; filtro por status de atendimento.
    [Fact]
    public async Task Delivery_list_mixes_phone_and_site_orders_with_fulfillment_filters()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var phone = await s.CreateOrderAsync(s.MarmitaDelivery(expectedPaymentMethod: "CASH", changeFor: "60.00"));
        var webId = await s.SubmitWebOrderAsync(delivery: false);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{webId}/accept") { Content = System.Net.Http.Json.JsonContent.Create(new { estimatedReadyMinutes = (int?)null }) };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        await (await s.Client.SendAsync(request, Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        await s.CreateOrderAsync(s.Pf());

        var list = await ListAsync(s.Client, "?fulfillmentType=DELIVERY&fulfillmentType=PICKUP");
        list.Select(o => o.Id()).ShouldBe([webId, phone.Id()]);
        list[0].GetProperty("origin").GetString().ShouldBe("WEB");
        list[1].GetProperty("origin").GetString().ShouldBe("APP");
        list[1].GetProperty("expectedPaymentMethod").GetString().ShouldBe("CASH");
        list[1].GetProperty("changeFor").GetString().ShouldBe("60.00");
        list[1].GetProperty("balance").GetString().ShouldBe("55.00");

        (await ListAsync(s.Client, "?fulfillmentType=PICKUP")).Select(o => o.Id()).ShouldBe([webId]);
        (await ListAsync(s.Client, "?fulfillmentType=DINE_IN")).Count.ShouldBe(1);

        var version = phone.Version();
        await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{phone.Id()}/fulfillment",
            new { fulfillmentStatus = "OUT_FOR_DELIVERY", completeProduction = true }, version)).ExpectAsync(HttpStatusCode.OK, _api);
        (await ListAsync(s.Client, "?fulfillmentType=DELIVERY&fulfillmentType=PICKUP&fulfillmentStatus=PENDING")).Select(o => o.Id()).ShouldBe([webId]);
        (await ListAsync(s.Client, "?fulfillmentStatus=OUT_FOR_DELIVERY")).Select(o => o.Id()).ShouldBe([phone.Id()]);

        (await (await s.Client.GetAsync("/api/v1/orders?fulfillmentType=SOMEWHERE", Ct)).ExpectAsync(HttpStatusCode.BadRequest, _api))
            .GetProperty("errorCode").GetString().ShouldBe("MalformedRequest");
    }

    [Fact]
    public async Task Lookup_validates_the_phone_format()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var problem = await (await s.Client.GetAsync("/api/v1/customers/by-phone?phoneNumber=34999", Ct)).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);
        problem.GetProperty("errors").TryGetProperty("phoneNumber", out _).ShouldBeTrue();
        (await (await s.Client.GetAsync("/api/v1/customers/by-phone", Ct)).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api))
            .GetProperty("errorCode").GetString().ShouldBe("ValidationFailed");
    }

    /// ADR-0026: o mesmo telefone em dois tenants são dois clientes; a busca de B não vê o cliente de A, nem o endereço
    /// dos pedidos de A; a lista de B não traz os pedidos de A.
    [Fact]
    public async Task Phone_customers_and_lookups_are_isolated_between_tenants()
    {
        var a = await OrderingScenario.CreateAsync(_api, db);
        var b = await OrderingScenario.CreateAsync(_api, db);
        const string phone = "+5534991110009";

        var fromA = await a.CreateOrderAsync(a.MarmitaDelivery(phone: phone));
        (await LookupAsync(b.Client, phone)).GetProperty("customer").ValueKind.ShouldBe(JsonValueKind.Null);
        (await ListAsync(b.Client, "?fulfillmentType=DELIVERY")).Count.ShouldBe(0);

        var fromB = await b.CreateOrderAsync(b.MarmitaDelivery(phone: phone));
        fromB.GetProperty("customer").Id().ShouldNotBe(fromA.GetProperty("customer").Id());
        (await LookupAsync(b.Client, phone)).GetProperty("customer").Id().ShouldBe(fromB.GetProperty("customer").Id());
        (await LookupAsync(a.Client, phone)).GetProperty("customer").Id().ShouldBe(fromA.GetProperty("customer").Id());
        (await a.ScalarAsync<long>("SELECT count(*) FROM customers WHERE tenant_id = @t")).ShouldBe(1);
        (await b.ScalarAsync<long>("SELECT count(*) FROM customers WHERE tenant_id = @t")).ShouldBe(1);
    }

    private async Task<JsonElement> LookupAsync(HttpClient client, string phone) =>
        await (await client.GetAsync($"/api/v1/customers/by-phone?phoneNumber={Uri.EscapeDataString(phone)}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);

    private async Task<List<JsonElement>> ListAsync(HttpClient client, string query) =>
        (await (await client.GetAsync($"/api/v1/orders{query}", Ct)).ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("items").EnumerateArray().ToList();

    private async Task<List<string>> Errors(OrderingScenario s, object body)
    {
        var problem = await (await s.Client.PostIdempotentAsync("/api/v1/orders", body, Guid.NewGuid())).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);
        problem.GetProperty("errorCode").GetString().ShouldBe("ValidationFailed");
        return problem.GetProperty("errors").EnumerateObject().Select(p => p.Name).ToList();
    }

    /// Objeto anônimo → dicionário, para trocar um campo do corpo padrão.
    private static Dictionary<string, object?> Body(object body) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(body, ApiFactory.Json))!
            .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
}
