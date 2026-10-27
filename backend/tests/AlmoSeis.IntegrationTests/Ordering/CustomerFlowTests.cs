using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Ordering;

/// Clientes e fiado (06 §6.8, ADR-0021, ADR-0024) contra a API real.
[Collection(nameof(PostgresCollection))]
public sealed class CustomerFlowTests(PostgresFixture db) : IDisposable
{
    private readonly ApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Phone_is_unique_and_the_conflict_brings_the_existing_customer()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var created = await CreateAsync(s, "+5534999990001");

        created.GetProperty("version").GetInt32().ShouldBe(1);
        created.GetProperty("addresses")[0].GetProperty("isDefault").GetBoolean().ShouldBeTrue();
        created.GetProperty("pendingAmount").GetString().ShouldBe("0.00");

        var problem = await (await s.Client.PostAsJsonAsync("/api/v1/customers", Body("+5534999990001"), Ct)).ExpectAsync(HttpStatusCode.Conflict, _api);
        problem.GetProperty("errorCode").GetString().ShouldBe("PhoneAlreadyRegistered");
        problem.GetProperty("current").Id().ShouldBe(created.Id());

        (await (await s.Client.PostAsJsonAsync("/api/v1/customers", Body("34 99999"), Ct)).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api))
            .GetProperty("errors").TryGetProperty("phoneNumber", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Switching_the_default_address_keeps_a_single_default()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var customer = await CreateAsync(s, "+5534999990002");

        var withWork = await (await s.Client.SendJsonAsync(HttpMethod.Post, $"/api/v1/customers/{customer.Id()}/addresses",
                new { label = "Trabalho", street = "Av. Rondon Pacheco", number = "500", isDefault = true }, 1))
            .ExpectAsync(HttpStatusCode.Created, _api);

        var addresses = withWork.GetProperty("addresses");
        addresses.GetArrayLength().ShouldBe(2);
        addresses[0].GetProperty("label").GetString().ShouldBe("Trabalho"); // o padrão vem primeiro
        addresses.EnumerateArray().Count(a => a.GetProperty("isDefault").GetBoolean()).ShouldBe(1);

        var home = addresses[1].Id();
        var back = await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/customers/{customer.Id()}/addresses/{home}", new { isDefault = true }, 2))
            .ExpectAsync(HttpStatusCode.OK, _api);
        back.GetProperty("addresses")[0].Id().ShouldBe(home);
        back.GetProperty("addresses").EnumerateArray().Count(a => a.GetProperty("isDefault").GetBoolean()).ShouldBe(1);

        var removed = await (await s.Client.SendJsonAsync(HttpMethod.Delete, $"/api/v1/customers/{customer.Id()}/addresses/{home}", null, 3))
            .ExpectAsync(HttpStatusCode.OK, _api);
        removed.GetProperty("addresses").GetArrayLength().ShouldBe(1);
        removed.GetProperty("addresses")[0].GetProperty("isDefault").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Order_with_saved_customer_and_address_becomes_pending_credit()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var customer = await CreateAsync(s, "+5534999990003");
        var addressId = customer.GetProperty("addresses")[0].Id();

        var order = await s.CreateOrderAsync(new
        {
            orderType = "MARMITA",
            fulfillmentType = "DELIVERY",
            customerId = customer.Id(),
            customerAddressId = addressId,
            items = new object[] { new { itemKind = "MARMITA", marmitaSizeId = s.SizeM, quantity = 1, mainDishProductIds = new[] { s.Frango } } },
        });

        order.GetProperty("customer").GetProperty("name").GetString().ShouldBe("Maria Souza");
        order.GetProperty("customer").GetProperty("phoneNumber").GetString().ShouldBe("+5534999990003");
        order.GetProperty("deliveryAddress").GetProperty("street").GetString().ShouldBe("Rua das Flores");

        var pending = await (await s.Client.GetAsync($"/api/v1/customers/{customer.Id()}/pending-orders", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        pending.GetProperty("totalPending").GetString().ShouldBe("25.00");
        pending.GetProperty("orders")[0].GetProperty("balance").GetString().ShouldBe("25.00");

        var reloaded = await (await s.Client.GetAsync($"/api/v1/customers/{customer.Id()}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        reloaded.GetProperty("pendingAmount").GetString().ShouldBe("25.00");
        reloaded.GetProperty("pendingOrderCount").GetInt32().ShouldBe(1);

        // Pedido pago sai do fiado.
        await s.MarkPaidAsync(order.Id());
        (await (await s.Client.GetAsync($"/api/v1/customers/{customer.Id()}/pending-orders", Ct)).ExpectAsync(HttpStatusCode.OK, _api))
            .GetProperty("orders").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Save_customer_links_by_phone_instead_of_duplicating()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);

        var first = await s.CreateOrderAsync(s.MarmitaDelivery(saveCustomer: true, phone: "+5534999990004"));
        var second = await s.CreateOrderAsync(s.MarmitaDelivery(saveCustomer: true, phone: "+5534999990004"));

        var customerId = first.GetProperty("customer").GetProperty("id").GetString();
        customerId.ShouldNotBeNull();
        second.GetProperty("customer").GetProperty("id").GetString().ShouldBe(customerId);

        var found = await (await s.Client.GetAsync("/api/v1/customers?search=99990004", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        found.GetProperty("items").GetArrayLength().ShouldBe(1);
        found.GetProperty("items")[0].GetProperty("addresses")[0].GetProperty("street").GetString().ShouldBe("Rua das Acácias");
        (await (await s.Client.GetAsync("/api/v1/customers?search=jo%C3%A3o", Ct)).ExpectAsync(HttpStatusCode.OK, _api))
            .GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Anonymizing_scrubs_the_customer_and_the_order_snapshots()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var order = await s.CreateOrderAsync(s.MarmitaDelivery(saveCustomer: true, phone: "+5534999990005"));
        var customerId = Guid.Parse(order.GetProperty("customer").GetProperty("id").GetString()!);

        var attendant = _api.Authenticated(await _api.EmployeeTokenAsync(db, s.Owner, "ATTENDANT"));
        (await (await attendant.SendJsonAsync(HttpMethod.Post, $"/api/v1/customers/{customerId}/anonymize", new { reason = "Pedido do titular" }, 1))
            .ExpectAsync(HttpStatusCode.Forbidden, _api)).GetProperty("permission").GetString().ShouldBe("settings.manage");

        var anonymized = await (await s.Client.SendJsonAsync(HttpMethod.Post, $"/api/v1/customers/{customerId}/anonymize", new { reason = "Pedido do titular via WhatsApp" }, 1))
            .ExpectAsync(HttpStatusCode.OK, _api);
        anonymized.GetProperty("name").GetString().ShouldBe("Cliente anonimizado");
        anonymized.GetProperty("phoneNumber").ValueKind.ShouldBe(JsonValueKind.Null);
        anonymized.GetProperty("addresses").GetArrayLength().ShouldBe(0);
        anonymized.GetProperty("anonymizedAt").ValueKind.ShouldNotBe(JsonValueKind.Null);

        var reloaded = await (await s.Client.GetAsync($"/api/v1/orders/{order.Id()}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        reloaded.GetProperty("customer").GetProperty("name").GetString().ShouldBe("Cliente anonimizado");
        reloaded.GetProperty("customer").GetProperty("phoneNumber").ValueKind.ShouldBe(JsonValueKind.Null);
        reloaded.GetProperty("deliveryAddress").GetProperty("street").GetString().ShouldBe("Endereço anonimizado");
        reloaded.GetProperty("totalAmount").GetString().ShouldBe("55.00"); // valores financeiros ficam

        (await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/customers/{customerId}", new { name = "Volta" }, 2))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api)).GetProperty("errorCode").GetString().ShouldBe("ValidationFailed");
    }

    [Fact]
    public async Task Customers_of_another_tenant_are_invisible()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var customer = await CreateAsync(s, "+5534999990006");
        var other = _api.Authenticated((await _api.SignUpAsync()).AccessToken());

        await (await other.GetAsync($"/api/v1/customers/{customer.Id()}", Ct)).ExpectAsync(HttpStatusCode.NotFound, _api);
        (await (await other.GetAsync("/api/v1/customers?search=Maria", Ct)).ExpectAsync(HttpStatusCode.OK, _api))
            .GetProperty("items").GetArrayLength().ShouldBe(0);
        // O mesmo telefone pode existir em outro estabelecimento.
        await (await other.PostAsJsonAsync("/api/v1/customers", Body("+5534999990006"), Ct)).ExpectAsync(HttpStatusCode.Created, _api);
    }

    private static object Body(string phone) => new
    {
        name = "Maria Souza",
        phoneNumber = phone,
        notes = (string?)null,
        addresses = new[] { new { label = "Casa", street = "Rua das Flores", number = "10", complement = (string?)null, neighborhood = "Centro", reference = (string?)null, isDefault = true } },
    };

    private async Task<JsonElement> CreateAsync(OrderingScenario s, string phone) =>
        await (await s.Client.PostAsJsonAsync("/api/v1/customers", Body(phone), Ct)).ExpectAsync(HttpStatusCode.Created, _api);
}
