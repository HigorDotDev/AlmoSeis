using System.Net;
using System.Text.Json;
using Npgsql;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Ordering;

/// Aceite do pedido da página e status de atendimento/entrega (ADR-0046, EP-002 F-206/F-207) contra a API real.
[Collection(nameof(PostgresCollection))]
public sealed class WebOrderFlowTests(PostgresFixture db) : IDisposable
{
    private readonly ApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task App_orders_are_still_born_accepted_with_a_number()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);

        var marmita = await s.CreateOrderAsync(s.MarmitaDelivery());
        var pf = await s.CreateOrderAsync(s.Pf());

        marmita.GetProperty("orderNumber").GetInt32().ShouldBe(1);
        marmita.GetProperty("origin").GetString().ShouldBe("APP");
        marmita.GetProperty("acceptanceStatus").GetString().ShouldBe("ACCEPTED");
        marmita.GetProperty("acceptedAt").GetString().ShouldBe(marmita.GetProperty("createdAt").GetString());
        marmita.GetProperty("acceptedBy").GetProperty("id").GetString().ShouldBe(marmita.GetProperty("createdBy").GetProperty("id").GetString());
        marmita.GetProperty("acceptanceDeadline").ValueKind.ShouldBe(JsonValueKind.Null);
        marmita.GetProperty("rejection").ValueKind.ShouldBe(JsonValueKind.Null);
        marmita.GetProperty("fulfillmentStatus").GetString().ShouldBe("PENDING");
        marmita.GetProperty("isReadyForPickup").GetBoolean().ShouldBeFalse();
        pf.GetProperty("orderNumber").GetInt32().ShouldBe(2);
        pf.GetProperty("fulfillmentStatus").ValueKind.ShouldBe(JsonValueKind.Null);

        var list = await List(s, "");
        list.Select(o => o.GetProperty("acceptanceStatus").GetString()).ShouldAllBe(a => a == "ACCEPTED");
        list.Count.ShouldBe(2);
    }

    /// Expand/contract (09 §9.4, 05 §5.17 0003): a API anterior grava pedido sem `accepted_at` e sem
    /// `fulfillment_status`. O banco aceita, e esta versão trata o nulo como "aceito na criação" e `PENDING`.
    [Fact]
    public async Task Orders_written_by_the_previous_api_version_still_work()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var pickup = await s.CreateOrderAsync(new
        {
            orderType = "MARMITA", fulfillmentType = "PICKUP", customerName = "Joana",
            items = new[] { new { itemKind = "MARMITA", marmitaSizeId = s.SizeM, quantity = 1, mainDishProductIds = new[] { s.Frango } } },
        });
        var delivery = await s.CreateOrderAsync(s.MarmitaDelivery());
        await s.SqlAsync(
            """
            UPDATE orders SET accepted_at = NULL, accepted_by_employee_id = NULL, fulfillment_status = NULL, fulfillment_status_changed_at = NULL
             WHERE tenant_id = @t
            """);

        var board = await (await s.Client.GetAsync("/api/v1/production/orders", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        board.GetProperty("groups").GetProperty("OPEN").GetArrayLength().ShouldBe(2);
        await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{pickup.Id()}/status", new { productionStatus = "COMPLETED" }, 1))
            .ExpectAsync(HttpStatusCode.OK, _api);
        (await s.GetOrderAsync(pickup.Id())).GetProperty("isReadyForPickup").GetBoolean().ShouldBeTrue();

        var dispatched = await (await Fulfillment(s.Client, delivery.Id(), 1, "OUT_FOR_DELIVERY", completeProduction: true)).ExpectAsync(HttpStatusCode.OK, _api);
        dispatched.GetProperty("fulfillmentStatus").GetString().ShouldBe("OUT_FOR_DELIVERY");
        var summary = await (await s.Client.GetAsync("/api/v1/closing/summary", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        summary.GetProperty("orderCount").GetInt32().ShouldBe(2);
        summary.GetProperty("unmarkedDeliveryCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Web_order_awaits_acceptance_outside_lists_kitchen_closing_and_writes()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var app = await s.CreateOrderAsync(s.Pf());
        var id = await s.SubmitWebOrderAsync();

        var order = await s.GetOrderAsync(id);
        order.GetProperty("origin").GetString().ShouldBe("WEB");
        order.GetProperty("acceptanceStatus").GetString().ShouldBe("AWAITING_ACCEPTANCE");
        order.GetProperty("orderNumber").ValueKind.ShouldBe(JsonValueKind.Null);
        order.GetProperty("productionStatus").ValueKind.ShouldBe(JsonValueKind.Null);
        order.GetProperty("fulfillmentStatus").ValueKind.ShouldBe(JsonValueKind.Null);
        order.GetProperty("createdBy").ValueKind.ShouldBe(JsonValueKind.Null);
        order.GetProperty("items")[0].GetProperty("createdBy").ValueKind.ShouldBe(JsonValueKind.Null);
        order.GetProperty("customer").GetProperty("id").ValueKind.ShouldBe(JsonValueKind.Null);
        order.GetProperty("totalAmount").GetString().ShouldBe("45.00");
        var deadline = DateTimeOffset.Parse(order.GetProperty("acceptanceDeadline").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        (deadline - DateTimeOffset.Parse(order.GetProperty("createdAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture))
            .ShouldBe(TimeSpan.FromMinutes(10));

        // Listas: a padrão só tem aceitos; a faixa "Novos do site" usa o filtro.
        (await List(s, "")).Select(o => o.Id()).ShouldBe([app.Id()]);
        var awaiting = (await List(s, "?acceptanceStatus=AWAITING_ACCEPTANCE")).ShouldHaveSingleItem();
        awaiting.Id().ShouldBe(id);
        awaiting.GetProperty("origin").GetString().ShouldBe("WEB");
        awaiting.GetProperty("orderNumber").ValueKind.ShouldBe(JsonValueKind.Null);
        awaiting.GetProperty("acceptanceDeadline").GetString().ShouldNotBeNull();

        // Cozinha e fechamento não veem.
        var board = await (await s.Client.GetAsync("/api/v1/production/orders", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        board.GetProperty("groups").GetProperty("OPEN").EnumerateArray().Select(o => o.Id()).ShouldBe([app.Id()]);
        var summary = await (await s.Client.GetAsync("/api/v1/closing/summary", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        summary.GetProperty("orderCount").GetInt32().ShouldBe(1);
        summary.GetProperty("totalSold").GetString().ShouldBe("48.00");
        summary.GetProperty("webOrders").GetProperty("awaitingAcceptanceCount").GetInt32().ShouldBe(1);

        // Sem edição, produção, pagamento, cancelamento nem atendimento (ADR-0046 item 4).
        await ExpectError(s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{id}", new { notes = "x" }, 1), HttpStatusCode.Conflict, "OrderNotAccepted");
        await ExpectError(s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{id}/items",
            new { items = new[] { new { itemKind = "BEVERAGE", productId = s.Coca, quantity = 1 } } }, 1), HttpStatusCode.Conflict, "OrderNotAccepted");
        await ExpectError(s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{id}/status", new { productionStatus = "IN_PROGRESS" }, 1), HttpStatusCode.Conflict, "OrderNotAccepted");
        await ExpectError(s.Client.SendJsonAsync(HttpMethod.Post, $"/api/v1/orders/{id}/cancel", new { reason = "Cliente desistiu" }, 1), HttpStatusCode.Conflict, "OrderNotAccepted");
        await ExpectError(s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{id}/payments", new { amount = "10.00", paymentMethod = "PIX" }, 1),
            HttpStatusCode.Conflict, "OrderNotAccepted");
        await ExpectError(s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{id}/fulfillment", new { fulfillmentStatus = "OUT_FOR_DELIVERY", completeProduction = true }, 1),
            HttpStatusCode.Conflict, "OrderNotAccepted");
        (await s.GetOrderAsync(id)).Version().ShouldBe(1);

        // Feed, outbox e auditoria sem dado pessoal (ADR-0024, ADR-0046 itens 23 e 24).
        (await s.ScalarAsync<long>("SELECT count(*) FROM change_feed_entries WHERE tenant_id = @t AND entity_type = 'ORDER' AND entity_id = @o", ("o", id))).ShouldBe(1);
        var payload = await s.ScalarAsync<string>("SELECT payload::text FROM outbox_messages WHERE tenant_id = @t AND event_type = 'OrderAwaitingAcceptance'");
        payload.ShouldContain("\"marmitaCount\": 2");
        payload.ShouldContain("\"totalAmount\": \"45.00\"");
        payload.ShouldNotContain("Carla");
        payload.ShouldNotContain("Palmeiras");
        payload.ShouldNotContain("98888");
        (await s.ScalarAsync<long>(
            "SELECT count(*) FROM audit_logs WHERE tenant_id = @t AND operation_type = 'WebOrderSubmitted' AND origin = 'WEB' AND actor_employee_id IS NULL AND order_id = @o",
            ("o", id))).ShouldBe(1);
        (await s.ScalarAsync<long>("SELECT count(*) FROM audit_logs WHERE tenant_id = @t AND (after::text LIKE '%Carla%' OR after::text LIKE '%98888%')")).ShouldBe(0);
    }

    [Fact]
    public async Task Accept_takes_the_next_number_links_the_customer_and_sends_to_the_kitchen()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        await s.CreateOrderAsync(s.Pf()); // #1
        var id = await s.SubmitWebOrderAsync();

        await ExpectError(Accept(s.Client, id, 1, minutes: 20), HttpStatusCode.UnprocessableEntity, "ValidationFailed");
        (await s.ScalarAsync<int>("SELECT last_order_number FROM business_date_counters WHERE tenant_id = @t")).ShouldBe(1);

        var key = Guid.NewGuid();
        var response = await Accept(s.Client, id, 1, minutes: 30, key: key);
        var accepted = await response.ExpectAsync(HttpStatusCode.OK, _api);
        response.Headers.ETag!.Tag.ShouldBe("\"2\"");
        accepted.GetProperty("acceptanceStatus").GetString().ShouldBe("ACCEPTED");
        accepted.GetProperty("orderNumber").GetInt32().ShouldBe(2);
        accepted.GetProperty("estimatedReadyMinutes").GetInt32().ShouldBe(30);
        accepted.GetProperty("productionStatus").GetString().ShouldBe("OPEN");
        accepted.GetProperty("fulfillmentStatus").GetString().ShouldBe("PENDING");
        accepted.GetProperty("acceptedBy").GetProperty("displayName").GetString().ShouldNotBeNullOrEmpty();
        var customerId = Guid.Parse(accepted.GetProperty("customer").GetProperty("id").GetString()!);

        // Reenvio idempotente: mesma resposta, sem consumir outro número.
        var replay = await Accept(s.Client, id, 1, minutes: 30, key: key);
        (await replay.ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("orderNumber").GetInt32().ShouldBe(2);
        replay.Headers.Contains("Idempotent-Replayed").ShouldBeTrue();
        (await s.ScalarAsync<int>("SELECT last_order_number FROM business_date_counters WHERE tenant_id = @t")).ShouldBe(2);

        // O segundo aceite com a versão antiga recebe o estado atual ("Já aceito por Ana"); com a nova, o motivo.
        var conflict = await ExpectError(Accept(s.Client, id, 1), HttpStatusCode.Conflict, "VersionConflict");
        conflict.GetProperty("current").GetProperty("acceptanceStatus").GetString().ShouldBe("ACCEPTED");
        (await ExpectError(Accept(s.Client, id, 2), HttpStatusCode.Conflict, "OrderNotAwaitingAcceptance"))
            .GetProperty("acceptanceStatus").GetString().ShouldBe("ACCEPTED");

        // Cliente criado pelo telefone; o segundo pedido do mesmo telefone reaproveita, sem alterar o cadastro.
        (await s.ScalarAsync<long>("SELECT count(*) FROM customers WHERE tenant_id = @t AND phone_number = '+5534988887777' AND name = 'Carla Souza'")).ShouldBe(1);
        var second = await s.SubmitWebOrderAsync(delivery: false, quantity: 1, name: "Outro Nome");
        var secondAccepted = await (await Accept(s.Client, second, 1)).ExpectAsync(HttpStatusCode.OK, _api);
        Guid.Parse(secondAccepted.GetProperty("customer").GetProperty("id").GetString()!).ShouldBe(customerId);
        secondAccepted.GetProperty("estimatedReadyMinutes").ValueKind.ShouldBe(JsonValueKind.Null);
        (await s.ScalarAsync<string>("SELECT name FROM customers WHERE tenant_id = @t AND id = @c", ("c", customerId))).ShouldBe("Carla Souza");

        // Para a cozinha, nasce no aceite: OrderCreated sem rótulo pessoal, e o pedido entra no painel.
        (await s.ScalarAsync<long>(
            "SELECT count(*) FROM outbox_messages WHERE tenant_id = @t AND event_type = 'OrderCreated' AND payload->>'entityId' = @o AND payload->'data'->>'label' IS NULL",
            ("o", id.ToString()))).ShouldBe(1);
        (await s.ScalarAsync<string>(
            "SELECT payload->'data'->'acceptedBy'->>'displayName' FROM outbox_messages WHERE tenant_id = @t AND event_type = 'OrderAccepted' AND payload->>'entityId' = @o",
            ("o", id.ToString()))).ShouldNotBeNullOrEmpty();
        var board = await (await s.Client.GetAsync("/api/v1/production/orders", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        var onBoard = board.GetProperty("groups").GetProperty("OPEN").EnumerateArray().Single(o => o.Id() == id);
        onBoard.GetProperty("origin").GetString().ShouldBe("WEB");
        onBoard.GetProperty("orderNumber").GetInt32().ShouldBe(2);

        // Depois do aceite é um pedido comum: entra na lista padrão, edita e recebe pagamento.
        (await List(s, "")).Select(o => o.Id()).ShouldContain(id);
        await (await s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{id}/items",
            new { items = new[] { new { itemKind = "BEVERAGE", productId = s.Coca, quantity = 1 } } }, 2)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{id}/payments", new { amount = "10.00", paymentMethod = "PIX" }, 3))
            .ExpectAsync(HttpStatusCode.Created, _api);
        (await s.ScalarAsync<long>("SELECT count(*) FROM audit_logs WHERE tenant_id = @t AND operation_type = 'OrderAccepted' AND order_id = @o", ("o", id))).ShouldBe(1);
    }

    [Fact]
    public async Task Two_employees_accepting_at_once_result_in_a_single_acceptance()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var cashier = _api.Authenticated(await _api.EmployeeTokenAsync(db, s.Owner, "CASHIER"));
        var id = await s.SubmitWebOrderAsync();

        var responses = await Task.WhenAll(Accept(s.Client, id, 1), Accept(cashier, id, 1));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1, string.Join("\n", _api.ServerErrors));
        var loser = responses.Single(r => r.StatusCode != HttpStatusCode.OK);
        loser.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await loser.ExpectAsync(HttpStatusCode.Conflict, _api)).GetProperty("errorCode").GetString().ShouldBe("VersionConflict");

        // Um número só: a sequência do dia não tem buracos.
        (await s.GetOrderAsync(id)).GetProperty("orderNumber").GetInt32().ShouldBe(1);
        (await s.ScalarAsync<int>("SELECT last_order_number FROM business_date_counters WHERE tenant_id = @t")).ShouldBe(1);
        (await s.CreateOrderAsync(s.Pf())).GetProperty("orderNumber").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Reject_needs_permission_and_a_listed_reason_and_never_gets_a_number()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var kitchen = _api.Authenticated(await _api.EmployeeTokenAsync(db, s.Owner, "KITCHEN"));
        var attendant = _api.Authenticated(await _api.EmployeeTokenAsync(db, s.Owner, "ATTENDANT"));
        var id = await s.SubmitWebOrderAsync();

        (await ExpectError(Reject(kitchen, id, 1, "TOO_BUSY", null), HttpStatusCode.Forbidden, "PermissionDenied"))
            .GetProperty("permission").GetString().ShouldBe("orders.accept");
        (await ExpectError(Accept(kitchen, id, 1), HttpStatusCode.Forbidden, "PermissionDenied")).GetProperty("permission").GetString().ShouldBe("orders.accept");
        (await ExpectError(Reject(attendant, id, 1, "OTHER", null), HttpStatusCode.UnprocessableEntity, "ValidationFailed"))
            .GetProperty("errors").GetProperty("message").GetArrayLength().ShouldBe(1);
        await ExpectError(Reject(attendant, id, 1, "OTHER", "abc"), HttpStatusCode.UnprocessableEntity, "ValidationFailed");
        await ExpectError(Reject(attendant, id, 1, "BUSINESS_DAY_CLOSED", null), HttpStatusCode.UnprocessableEntity, "ValidationFailed");
        await ExpectError(Reject(attendant, id, 1, "SOLD_OUT", null), HttpStatusCode.BadRequest, "MalformedRequest");
        var noIfMatch = await attendant.SendJsonAsync(HttpMethod.Post, $"/api/v1/orders/{id}/reject", new { reasonCode = "TOO_BUSY", message = (string?)null });
        (await noIfMatch.ExpectAsync(HttpStatusCode.PreconditionRequired, _api)).GetProperty("errorCode").GetString().ShouldBe("IfMatchRequired");

        var rejected = await (await Reject(attendant, id, 1, "ITEM_UNAVAILABLE", "A costela acabou hoje.")).ExpectAsync(HttpStatusCode.OK, _api);
        rejected.GetProperty("acceptanceStatus").GetString().ShouldBe("REJECTED");
        rejected.GetProperty("orderNumber").ValueKind.ShouldBe(JsonValueKind.Null);
        var rejection = rejected.GetProperty("rejection");
        rejection.GetProperty("reasonCode").GetString().ShouldBe("ITEM_UNAVAILABLE");
        rejection.GetProperty("message").GetString().ShouldBe("A costela acabou hoje.");
        rejection.GetProperty("rejectedBy").GetProperty("displayName").GetString().ShouldBe("attendant");

        await ExpectError(Accept(s.Client, id, 2), HttpStatusCode.Conflict, "OrderNotAwaitingAcceptance");
        (await List(s, "?acceptanceStatus=REJECTED")).ShouldHaveSingleItem().Id().ShouldBe(id);
        (await List(s, "?acceptanceStatus=AWAITING_ACCEPTANCE")).ShouldBeEmpty();
        (await s.ScalarAsync<int?>("SELECT last_order_number FROM business_date_counters WHERE tenant_id = @t")).ShouldBeNull();
        (await s.ScalarAsync<string>(
            "SELECT payload->'data'->>'reasonCode' FROM outbox_messages WHERE tenant_id = @t AND event_type = 'OrderRejected' AND payload->'data'->>'acceptanceStatus' = 'REJECTED'"))
            .ShouldBe("ITEM_UNAVAILABLE");
    }

    [Fact]
    public async Task Expiry_job_expires_late_orders_and_a_late_accept_gets_WebOrderExpired()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var late = await s.SubmitWebOrderAsync();
        var onTime = await s.SubmitWebOrderAsync(delivery: false);
        await s.SetAcceptanceDeadlineAsync(late, DateTimeOffset.UtcNow.AddSeconds(-1));

        // Vencido antes de o job passar: o aceite já responde WebOrderExpired.
        await ExpectError(Accept(s.Client, late, 1), HttpStatusCode.Conflict, "WebOrderExpired");

        // Outros testes podem ter deixado pedidos vencidos no mesmo banco: o job expira todos.
        (await s.RunExpiryJobAsync()).ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(1);

        var expired = await s.GetOrderAsync(late);
        expired.GetProperty("acceptanceStatus").GetString().ShouldBe("EXPIRED");
        expired.GetProperty("expiredAt").GetString().ShouldNotBeNull();
        expired.GetProperty("orderNumber").ValueKind.ShouldBe(JsonValueKind.Null);
        (await s.GetOrderAsync(onTime)).GetProperty("acceptanceStatus").GetString().ShouldBe("AWAITING_ACCEPTANCE");

        // If-Match antigo (o job subiu a versão): o motivo continua sendo o prazo.
        await ExpectError(Accept(s.Client, late, 1), HttpStatusCode.Conflict, "WebOrderExpired");
        await ExpectError(Reject(s.Client, late, 2, "TOO_BUSY", null), HttpStatusCode.Conflict, "WebOrderExpired");
        (await s.ScalarAsync<long>(
            "SELECT count(*) FROM audit_logs WHERE tenant_id = @t AND operation_type = 'WebOrderExpired' AND origin = 'SYSTEM' AND actor_employee_id IS NULL AND order_id = @o",
            ("o", late))).ShouldBe(1);
        (await s.ScalarAsync<long>(
            "SELECT count(*) FROM outbox_messages WHERE tenant_id = @t AND event_type = 'OrderRejected' AND payload->'data'->>'acceptanceStatus' = 'EXPIRED'")).ShouldBe(1);
        (await s.ScalarAsync<long>("SELECT count(*) FROM change_feed_entries WHERE tenant_id = @t AND entity_id = @o AND entity_version = 2", ("o", late))).ShouldBe(1);
        await s.RunExpiryJobAsync();
        (await s.GetOrderAsync(late)).Version().ShouldBe(2);
    }

    /// ADR-0046 item 16: aceite e expiração disputam pela versão; só um vence. O pedido fica travado por uma conexão
    /// externa enquanto o aceite (antes do prazo) e o job (depois do prazo) chegam ao UPDATE; ao soltar, o primeiro da
    /// fila grava e o outro cai no token de concorrência.
    [Fact]
    public async Task Accept_and_expiry_race_on_the_version_and_only_one_wins()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var id = await s.SubmitWebOrderAsync();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        await s.SetAcceptanceDeadlineAsync(id, deadline);

        await using var holder = new NpgsqlConnection(db.SuperuserConnectionString);
        await holder.OpenAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM orders WHERE id = @o FOR UPDATE", holder, transaction))
        {
            lockRow.Parameters.AddWithValue("o", id);
            await lockRow.ExecuteScalarAsync(Ct);
        }

        var accept = Accept(s.Client, id, 1);
        await WaitForBlockedUpdatesAsync(1);

        while (DateTimeOffset.UtcNow <= deadline.AddMilliseconds(200))
        {
            await Task.Delay(100, Ct);
        }

        var job = s.RunExpiryJobAsync();
        await WaitForBlockedUpdatesAsync(2);
        await transaction.RollbackAsync(Ct);

        var response = await accept;
        (await response.ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("orderNumber").GetInt32().ShouldBe(1);
        await job;

        var order = await s.GetOrderAsync(id);
        order.GetProperty("acceptanceStatus").GetString().ShouldBe("ACCEPTED");
        order.Version().ShouldBe(2);
        (await s.ScalarAsync<long>("SELECT count(*) FROM audit_logs WHERE tenant_id = @t AND operation_type = 'WebOrderExpired'")).ShouldBe(0);
    }

    /// Teste obrigatório (ADR-0046 itens 4 e 13, RF-206/RF-210): pedido não aceito nunca entra no fechamento, e a
    /// finalização recusa os que ainda aguardam, na mesma transação, sem ficar bloqueada.
    [Fact]
    public async Task Closing_never_counts_unaccepted_orders_and_rejects_the_awaiting_ones()
    {
        var s = await OrderingScenario.CreateAsync(_api, db, defaultFee: "5.00");
        var app = await s.CreateOrderAsync(s.MarmitaDelivery()); // 55,00, #1
        var awaiting = await s.SubmitWebOrderAsync(); // fica aguardando: recusada pelo fechamento
        var rejected = await s.SubmitWebOrderAsync();
        var expired = await s.SubmitWebOrderAsync();
        var withdrawn = await s.SubmitWebOrderAsync();
        var accepted = await s.SubmitWebOrderAsync(); // 45,00, #2

        await (await Reject(s.Client, rejected, 1, "TOO_BUSY", null)).ExpectAsync(HttpStatusCode.OK, _api);
        await s.SetAcceptanceDeadlineAsync(expired, DateTimeOffset.UtcNow.AddSeconds(-1));
        await s.RunExpiryJobAsync();
        (await s.GetOrderAsync(expired)).GetProperty("acceptanceStatus").GetString().ShouldBe("EXPIRED");
        await s.WithdrawWebOrderAsync(withdrawn);
        await (await Accept(s.Client, accepted, 1)).ExpectAsync(HttpStatusCode.OK, _api);

        // A entrega do app foi marcada como entregue; a do site não (indicador informativo, item 20).
        await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{app.Id()}/fulfillment",
            new { fulfillmentStatus = "OUT_FOR_DELIVERY", completeProduction = true }, 1)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{app.Id()}/fulfillment",
            new { fulfillmentStatus = "DELIVERED", completeProduction = false }, 2)).ExpectAsync(HttpStatusCode.OK, _api);

        var summary = await (await s.Client.GetAsync("/api/v1/closing/summary", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        summary.GetProperty("orderCount").GetInt32().ShouldBe(2);
        summary.GetProperty("totalSold").GetString().ShouldBe("100.00");
        summary.GetProperty("deliveryFeesTotal").GetString().ShouldBe("10.00");
        summary.GetProperty("totalPending").GetString().ShouldBe("100.00");
        summary.GetProperty("canceled").GetProperty("count").GetInt32().ShouldBe(0);
        var marmita = summary.GetProperty("byOrderType").EnumerateArray().Single(l => l.GetProperty("orderType").GetString() == "MARMITA");
        marmita.GetProperty("count").GetInt32().ShouldBe(2);
        marmita.GetProperty("sold").GetString().ShouldBe("100.00");
        var web = summary.GetProperty("webOrders");
        web.GetProperty("awaitingAcceptanceCount").GetInt32().ShouldBe(1);
        web.GetProperty("autoRejectedCount").GetInt32().ShouldBe(0);
        web.GetProperty("notAcceptedCount").GetInt32().ShouldBe(3);
        summary.GetProperty("unmarkedDeliveryCount").GetInt32().ShouldBe(1);

        var day = summary.GetProperty("businessDay");
        var closing = await (await s.Client.SendWithKeyAsync(HttpMethod.Post, "/api/v1/closing/finalize",
            new { businessDayId = day.Id(), countedCash = "100.00", justification = (string?)null }, day.Version())).ExpectAsync(HttpStatusCode.Created, _api);
        closing.GetProperty("orderCount").GetInt32().ShouldBe(2);
        closing.GetProperty("totalSold").GetString().ShouldBe("100.00");
        closing.GetProperty("totalPending").GetString().ShouldBe("100.00");
        closing.GetProperty("webOrders").GetProperty("awaitingAcceptanceCount").GetInt32().ShouldBe(0);
        closing.GetProperty("webOrders").GetProperty("autoRejectedCount").GetInt32().ShouldBe(1);
        closing.GetProperty("webOrders").GetProperty("notAcceptedCount").GetInt32().ShouldBe(4);
        closing.GetProperty("unmarkedDeliveryCount").GetInt32().ShouldBe(1);

        var snapshot = await (await s.Client.GetAsync($"/api/v1/closing/{closing.GetProperty("dailyClosingId").GetString()}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        snapshot.GetProperty("totalSold").GetString().ShouldBe("100.00");
        snapshot.GetProperty("webOrders").GetProperty("notAcceptedCount").GetInt32().ShouldBe(4);

        var autoRejected = await s.GetOrderAsync(awaiting);
        autoRejected.GetProperty("acceptanceStatus").GetString().ShouldBe("REJECTED");
        autoRejected.GetProperty("rejection").GetProperty("reasonCode").GetString().ShouldBe("BUSINESS_DAY_CLOSED");
        autoRejected.GetProperty("rejection").GetProperty("rejectedBy").ValueKind.ShouldBe(JsonValueKind.Null);
        autoRejected.GetProperty("orderNumber").ValueKind.ShouldBe(JsonValueKind.Null);
        (await s.ScalarAsync<long>("SELECT count(*) FROM audit_logs WHERE tenant_id = @t AND operation_type = 'WebOrderAutoRejected' AND origin = 'SYSTEM'")).ShouldBe(1);
        (await s.ScalarAsync<long>(
            "SELECT count(*) FROM outbox_messages WHERE tenant_id = @t AND event_type = 'OrderRejected' AND payload->'data'->>'reasonCode' = 'BUSINESS_DAY_CLOSED'")).ShouldBe(1);

        // Fiado: só o pedido aceito; os não aceitos nunca são vinculados ao cliente.
        var customerId = (await s.GetOrderAsync(accepted)).GetProperty("customer").GetProperty("id").GetString()!;
        var pending = await (await s.Client.GetAsync($"/api/v1/customers/{customerId}/pending-orders", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        pending.GetProperty("orders").EnumerateArray().Select(o => o.Id()).ShouldBe([accepted]);
        (await s.ScalarAsync<long>("SELECT count(*) FROM orders WHERE tenant_id = @t AND acceptance_status <> 'ACCEPTED' AND customer_id IS NOT NULL")).ShouldBe(0);
        (await s.ScalarAsync<long>("SELECT count(*) FROM orders WHERE tenant_id = @t AND acceptance_status <> 'ACCEPTED' AND order_number IS NOT NULL")).ShouldBe(0);
    }

    [Fact]
    public async Task Customer_can_withdraw_while_awaiting()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var id = await s.SubmitWebOrderAsync();

        await s.WithdrawWebOrderAsync(id);

        var order = await s.GetOrderAsync(id);
        order.GetProperty("acceptanceStatus").GetString().ShouldBe("WITHDRAWN");
        order.GetProperty("withdrawnAt").GetString().ShouldNotBeNull();
        await ExpectError(Accept(s.Client, id, 2), HttpStatusCode.Conflict, "OrderNotAwaitingAcceptance");
        (await s.ScalarAsync<long>(
            "SELECT count(*) FROM audit_logs WHERE tenant_id = @t AND operation_type = 'WebOrderWithdrawn' AND origin = 'WEB' AND actor_employee_id IS NULL")).ShouldBe(1);
    }

    [Fact]
    public async Task Fulfillment_follows_the_transitions_and_blocks_new_items_after_dispatch()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var attendant = _api.Authenticated(await _api.EmployeeTokenAsync(db, s.Owner, "ATTENDANT"));
        var kitchen = _api.Authenticated(await _api.EmployeeTokenAsync(db, s.Owner, "KITCHEN"));
        var order = await s.CreateOrderAsync(s.MarmitaDelivery());
        var id = order.Id();

        var noIfMatch = await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{id}/fulfillment", new { fulfillmentStatus = "OUT_FOR_DELIVERY", completeProduction = false });
        (await noIfMatch.ExpectAsync(HttpStatusCode.PreconditionRequired, _api)).GetProperty("errorCode").GetString().ShouldBe("IfMatchRequired");
        await ExpectError(Fulfillment(s.Client, id, 1, "OUT_FOR_DELIVERY"), HttpStatusCode.Conflict, "ProductionNotCompleted");
        await ExpectError(Fulfillment(s.Client, id, 1, "DELIVERED"), HttpStatusCode.UnprocessableEntity, "ValidationFailed");
        await ExpectError(Fulfillment(s.Client, id, 1, "PICKED_UP"), HttpStatusCode.UnprocessableEntity, "ValidationFailed");

        // "Concluir e marcar" exige também production.update: o atendente não tem.
        (await ExpectError(Fulfillment(attendant, id, 1, "OUT_FOR_DELIVERY", completeProduction: true), HttpStatusCode.Forbidden, "PermissionDenied"))
            .GetProperty("permission").GetString().ShouldBe("production.update");

        var outResponse = await Fulfillment(kitchen, id, 1, "OUT_FOR_DELIVERY", completeProduction: true);
        var dispatched = await outResponse.ExpectAsync(HttpStatusCode.OK, _api);
        outResponse.Headers.ETag!.Tag.ShouldBe("\"2\"");
        dispatched.GetProperty("productionStatus").GetString().ShouldBe("COMPLETED");
        dispatched.GetProperty("fulfillmentStatus").GetString().ShouldBe("OUT_FOR_DELIVERY");
        dispatched.GetProperty("outForDeliveryAt").GetString().ShouldNotBeNull();
        (await s.ScalarAsync<long>(
            "SELECT count(*) FROM outbox_messages WHERE tenant_id = @t AND payload->>'entityId' = @o AND event_type IN ('OrderStatusChanged', 'OrderFulfillmentChanged')",
            ("o", id.ToString()))).ShouldBe(2);

        // Depois de sair, não entra item novo (ADR-0046 item 19); o financeiro continua livre.
        await ExpectError(s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{id}/items",
            new { items = new[] { new { itemKind = "BEVERAGE", productId = s.Coca, quantity = 1 } } }, 2), HttpStatusCode.Conflict, "OrderAlreadyDispatched");
        await ExpectError(s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{id}", new { fulfillmentType = "PICKUP", deliveryAddress = (object?)null }, 2),
            HttpStatusCode.Conflict, "OrderAlreadyDispatched");

        var delivered = await (await Fulfillment(attendant, id, 2, "DELIVERED")).ExpectAsync(HttpStatusCode.OK, _api);
        delivered.GetProperty("fulfillmentStatus").GetString().ShouldBe("DELIVERED");
        delivered.GetProperty("isPaid").GetBoolean().ShouldBeFalse();
        delivered.GetProperty("balance").GetString().ShouldBe("55.00");

        // Correção e "voltou sem entregar": libera acrescentar item de novo.
        await (await Fulfillment(s.Client, id, 3, "OUT_FOR_DELIVERY")).ExpectAsync(HttpStatusCode.OK, _api);
        var back = await (await Fulfillment(s.Client, id, 4, "PENDING")).ExpectAsync(HttpStatusCode.OK, _api);
        back.GetProperty("outForDeliveryAt").ValueKind.ShouldBe(JsonValueKind.Null);
        await (await s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{id}/items",
            new { items = new[] { new { itemKind = "BEVERAGE", productId = s.Coca, quantity = 1 } } }, 5)).ExpectAsync(HttpStatusCode.OK, _api);
        (await s.ScalarAsync<long>("SELECT count(*) FROM audit_logs WHERE tenant_id = @t AND operation_type = 'OrderFulfillmentChanged' AND order_id = @o", ("o", id)))
            .ShouldBe(4);

        // Retirada: "Pronto para retirar" é derivado da produção concluída.
        var pickup = await s.CreateOrderAsync(new
        {
            orderType = "MARMITA", fulfillmentType = "PICKUP", customerName = "Joana",
            items = new[] { new { itemKind = "MARMITA", marmitaSizeId = s.SizeM, quantity = 1, mainDishProductIds = new[] { s.Frango } } },
        });
        await (await s.Client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{pickup.Id()}/status", new { productionStatus = "COMPLETED" }, 1)).ExpectAsync(HttpStatusCode.OK, _api);
        (await s.GetOrderAsync(pickup.Id())).GetProperty("isReadyForPickup").GetBoolean().ShouldBeTrue();
        (await List(s, "")).Single(o => o.Id() == pickup.Id()).GetProperty("isReadyForPickup").GetBoolean().ShouldBeTrue();
        await ExpectError(Fulfillment(s.Client, pickup.Id(), 2, "OUT_FOR_DELIVERY"), HttpStatusCode.UnprocessableEntity, "ValidationFailed");
        var pickedUp = await (await Fulfillment(kitchen, pickup.Id(), 2, "PICKED_UP")).ExpectAsync(HttpStatusCode.OK, _api);
        pickedUp.GetProperty("isReadyForPickup").GetBoolean().ShouldBeFalse();
        pickedUp.GetProperty("pickedUpAt").GetString().ShouldNotBeNull();

        // Salão não tem atendimento.
        var pf = await s.CreateOrderAsync(s.Pf());
        await ExpectError(Fulfillment(s.Client, pf.Id(), 1, "PICKED_UP"), HttpStatusCode.UnprocessableEntity, "ValidationFailed");
    }

    [Fact]
    public async Task Another_tenant_cannot_see_accept_reject_or_dispatch()
    {
        var a = await OrderingScenario.CreateAsync(_api, db);
        var b = await OrderingScenario.CreateAsync(_api, db);
        var web = await a.SubmitWebOrderAsync();
        var app = await a.CreateOrderAsync(a.MarmitaDelivery());

        (await b.Client.GetAsync($"/api/v1/orders/{web}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await List(b, "?acceptanceStatus=AWAITING_ACCEPTANCE")).ShouldBeEmpty();
        await ExpectError(Accept(b.Client, web, 1), HttpStatusCode.NotFound, "NotFound");
        await ExpectError(Reject(b.Client, web, 1, "TOO_BUSY", null), HttpStatusCode.NotFound, "NotFound");
        await ExpectError(Fulfillment(b.Client, app.Id(), 1, "OUT_FOR_DELIVERY", completeProduction: true), HttpStatusCode.NotFound, "NotFound");

        (await a.GetOrderAsync(web)).GetProperty("acceptanceStatus").GetString().ShouldBe("AWAITING_ACCEPTANCE");
        (await a.GetOrderAsync(app.Id())).Version().ShouldBe(1);
    }

    private static Task<HttpResponseMessage> Accept(HttpClient client, Guid id, int version, int? minutes = null, Guid? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{id}/accept")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { estimatedReadyMinutes = minutes }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> Reject(HttpClient client, Guid id, int version, string reasonCode, string? message) =>
        client.SendJsonAsync(HttpMethod.Post, $"/api/v1/orders/{id}/reject", new { reasonCode, message }, version);

    private static Task<HttpResponseMessage> Fulfillment(HttpClient client, Guid id, int version, string status, bool completeProduction = false) =>
        client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/orders/{id}/fulfillment", new { fulfillmentStatus = status, completeProduction }, version);

    private async Task<List<JsonElement>> List(OrderingScenario s, string query) =>
        (await (await s.Client.GetAsync($"/api/v1/orders{query}", Ct)).ExpectAsync(HttpStatusCode.OK, _api))
            .GetProperty("items").EnumerateArray().ToList();

    private async Task<JsonElement> ExpectError(Task<HttpResponseMessage> send, HttpStatusCode status, string errorCode)
    {
        var body = await (await send).ExpectAsync(status, _api);
        body.GetProperty("errorCode").GetString().ShouldBe(errorCode);
        return body;
    }

    /// UPDATEs de `orders` parados na trava de linha (o teste de corrida espera cada um entrar na fila).
    private async Task WaitForBlockedUpdatesAsync(int expected)
    {
        await using var connection = new NpgsqlConnection(db.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        var until = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query ILIKE '%UPDATE orders%'", connection);
            if ((long)(await command.ExecuteScalarAsync(Ct))! >= expected)
            {
                return;
            }

            if (DateTimeOffset.UtcNow > until)
            {
                throw new TimeoutException($"Esperava {expected} UPDATE(s) de orders travados. Erros do servidor: {string.Join("\n", _api.ServerErrors)}");
            }

            await Task.Delay(50, Ct);
        }
    }
}
