using System.Net;
using System.Text.Json;
using AlmoSeis.IntegrationTests.Ordering;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Closing;

/// Caixa e fechamento (06 §6.11, ADR-0013/0014/0019/0039) contra a API real.
[Collection(nameof(PostgresCollection))]
public sealed class ClosingFlowTests(PostgresFixture db) : IDisposable
{
    private readonly ApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Day_summary_finalize_and_next_day_effects()
    {
        var s = await OrderingScenario.CreateAsync(_api, db); // saldo inicial 100,00
        var marmita = await s.CreateOrderAsync(s.MarmitaDelivery()); // 55,00 (5,00 de entrega)
        var pf = await s.CreateOrderAsync(s.Pf()); // 48,00, fica pendente
        var canceled = await s.CreateOrderAsync(s.Pf("9")); // 48,00, cancelado
        await (await s.Client.SendJsonAsync(HttpMethod.Post, $"/api/v1/orders/{canceled.Id()}/cancel", new { reason = "Cliente desistiu" }, 1))
            .ExpectAsync(HttpStatusCode.OK, _api);

        var cash = await (await Pay(s, marmita.Id(), 1, new { amount = "35.00", paymentMethod = "CASH", cashReceived = "50.00" })).ExpectAsync(HttpStatusCode.Created, _api);
        await (await Pay(s, marmita.Id(), 2, new { amount = "20.00", paymentMethod = "PIX" })).ExpectAsync(HttpStatusCode.Created, _api);
        await Movement(s, "SUPPLY", "50.00", "Troco trazido do cofre");
        await Movement(s, "WITHDRAWAL", "20.00", "Sangria para depósito");

        var summary = await Summary(s);
        summary.GetProperty("isFinal").GetBoolean().ShouldBeFalse();
        summary.GetProperty("orderCount").GetInt32().ShouldBe(2);
        summary.GetProperty("totalSold").GetString().ShouldBe("103.00");
        summary.GetProperty("deliveryFeesTotal").GetString().ShouldBe("5.00");
        summary.GetProperty("totalReceived").GetString().ShouldBe("55.00");
        summary.GetProperty("totalPending").GetString().ShouldBe("48.00");
        summary.GetProperty("canceled").GetProperty("count").GetInt32().ShouldBe(1);
        summary.GetProperty("canceled").GetProperty("total").GetString().ShouldBe("48.00");
        var byType = summary.GetProperty("byOrderType").EnumerateArray().ToDictionary(l => l.GetProperty("orderType").GetString()!);
        byType["MARMITA"].GetProperty("received").GetString().ShouldBe("55.00");
        byType["PF"].GetProperty("pending").GetString().ShouldBe("48.00");
        byType["KILO"].GetProperty("count").GetInt32().ShouldBe(0);
        var byMethod = summary.GetProperty("byPaymentMethod").EnumerateArray().ToDictionary(l => l.GetProperty("paymentMethod").GetString()!);
        byMethod["CASH"].GetProperty("received").GetString().ShouldBe("35.00");
        byMethod["PIX"].GetProperty("count").GetInt32().ShouldBe(1);
        var cashBlock = summary.GetProperty("cash");
        cashBlock.GetProperty("expectedCash").GetString().ShouldBe("165.00"); // 100 + 35 + 50 − 20
        cashBlock.GetProperty("countedCash").ValueKind.ShouldBe(JsonValueKind.Null);

        // Movimentação depois de abrir a tela: a confirmação com a versão antiga recebe 409.
        var dayId = summary.GetProperty("businessDay").Id();
        var version = summary.GetProperty("businessDay").Version();
        await Movement(s, "SUPPLY", "5.00", "Moedas");
        (await (await Finalize(s, dayId, version, "170.00", null)).ExpectAsync(HttpStatusCode.Conflict, _api))
            .GetProperty("errorCode").GetString().ShouldBe("VersionConflict");

        summary = await Summary(s);
        version = summary.GetProperty("businessDay").Version();
        summary.GetProperty("cash").GetProperty("expectedCash").GetString().ShouldBe("170.00");

        var noJustification = await (await Finalize(s, dayId, version, "165.00", null)).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);
        noJustification.GetProperty("errorCode").GetString().ShouldBe("JustificationRequired");

        var finalizeResponse = await Finalize(s, dayId, version, "165.00", "Troco dado a mais em uma entrega");
        var closing = await finalizeResponse.ExpectAsync(HttpStatusCode.Created, _api);
        var closingId = closing.GetProperty("dailyClosingId").GetString()!;
        finalizeResponse.Headers.Location!.ToString().ShouldBe($"/api/v1/closing/{closingId}");
        closing.GetProperty("isFinal").GetBoolean().ShouldBeTrue();
        closing.GetProperty("cash").GetProperty("cashDifference").GetString().ShouldBe("-5.00");
        closing.GetProperty("businessDay").GetProperty("status").GetString().ShouldBe("CLOSED");
        closing.GetProperty("closedBy").GetProperty("displayName").GetString().ShouldNotBeNullOrEmpty();

        (await (await Finalize(s, dayId, version + 1, "165.00", "De novo")).ExpectAsync(HttpStatusCode.Conflict, _api))
            .GetProperty("errorCode").GetString().ShouldBe("ClosingAlreadyFinalized");
        (await (await Pay(s, pf.Id(), 1, new { amount = "10.00", paymentMethod = "PIX" })).ExpectAsync(HttpStatusCode.Conflict, _api))
            .GetProperty("errorCode").GetString().ShouldBe("BusinessDayRequired");

        // O snapshot não muda com o que acontece depois (ADR-0014).
        var snapshot = await (await s.Client.GetAsync($"/api/v1/closing/summary?businessDayId={dayId}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        snapshot.GetProperty("dailyClosingId").GetString().ShouldBe(closingId);
        snapshot.GetProperty("totalReceived").GetString().ShouldBe("55.00");
        (await (await s.Client.GetAsync($"/api/v1/business-days?limit=1", Ct)).ExpectAsync(HttpStatusCode.OK, _api))
            .GetProperty("items")[0].GetProperty("dailyClosingId").GetString().ShouldBe(closingId);

        // Dia seguinte (mesma data, ADR-0039): fiado recebido, estorno do dinheiro de ontem e ajuste.
        await (await s.Client.PostIdempotentAsync("/api/v1/business-days", new { openingBalance = "80.00", confirmSameDate = true }, Guid.NewGuid()))
            .ExpectAsync(HttpStatusCode.Created, _api);
        await (await Pay(s, pf.Id(), 1, new { amount = "48.00", paymentMethod = "CARD" })).ExpectAsync(HttpStatusCode.Created, _api);
        await (await s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/payments/{cash.GetProperty("payment").Id()}/reverse", new { reason = "Cobrado em duplicidade" }, 3))
            .ExpectAsync(HttpStatusCode.Created, _api);
        var adjustment = await (await s.Client.PostIdempotentAsync($"/api/v1/closing/{closingId}/adjustments",
                new { adjustmentType = "CASH_COUNT_CORRECTION", amount = "5.00", reason = "Nota de R$ 5 encontrada na gaveta" }, Guid.NewGuid()))
            .ExpectAsync(HttpStatusCode.Created, _api);
        adjustment.GetProperty("relatedClosingId").GetString().ShouldBe(closingId);

        var today = await Summary(s);
        today.GetProperty("orderCount").GetInt32().ShouldBe(0);
        today.GetProperty("totalReceived").GetString().ShouldBe("48.00");
        today.GetProperty("receivedFromPreviousDays").GetString().ShouldBe("48.00");
        today.GetProperty("reversed").GetProperty("total").GetString().ShouldBe("35.00");
        today.GetProperty("cash").GetProperty("cashReversed").GetString().ShouldBe("35.00");
        today.GetProperty("cash").GetProperty("expectedCash").GetString().ShouldBe("45.00"); // 80 − 35
        today.GetProperty("adjustments").GetArrayLength().ShouldBe(1);

        var original = await (await s.Client.GetAsync($"/api/v1/closing/{closingId}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        original.GetProperty("cash").GetProperty("cashDifference").GetString().ShouldBe("-5.00");
        original.GetProperty("adjustments")[0].GetProperty("amount").GetString().ShouldBe("5.00");

        var history = await (await s.Client.GetAsync("/api/v1/closing/history", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        history.GetProperty("items")[0].GetProperty("adjustmentCount").GetInt32().ShouldBe(1);
        var month = OrderingScenario.Today.ToString("yyyy-MM");
        var monthly = await (await s.Client.GetAsync($"/api/v1/closing/monthly?month={month}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        monthly.GetProperty("closingCount").GetInt32().ShouldBe(1);
        monthly.GetProperty("cashDifferenceTotal").GetString().ShouldBe("-5.00");
        monthly.GetProperty("byPaymentMethod").EnumerateArray().Single(m => m.GetProperty("paymentMethod").GetString() == "CASH")
            .GetProperty("received").GetString().ShouldBe("35.00");
    }

    [Fact]
    public async Task Only_closing_roles_see_and_finalize()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var attendant = _api.Authenticated(await _api.EmployeeTokenAsync(db, s.Owner, "ATTENDANT"));
        var cashier = _api.Authenticated(await _api.EmployeeTokenAsync(db, s.Owner, "CASHIER"));

        await (await attendant.GetAsync("/api/v1/closing/summary", Ct)).ExpectAsync(HttpStatusCode.Forbidden, _api);
        var summary = await (await cashier.GetAsync("/api/v1/closing/summary", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        (await (await cashier.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/closing/{Guid.NewGuid()}/adjustments",
                new { adjustmentType = "OTHER", amount = "1.00", reason = "Sem permissão" }, 1))
            .ExpectAsync(HttpStatusCode.Forbidden, _api)).GetProperty("permission").GetString().ShouldBe("closing.adjust");

        await (await Finalize(s, summary.GetProperty("businessDay").Id(), summary.GetProperty("businessDay").Version(), "100.00", null, cashier))
            .ExpectAsync(HttpStatusCode.Created, _api);
        (await (await s.Client.GetAsync("/api/v1/closing/summary", Ct)).ExpectAsync(HttpStatusCode.NotFound, _api))
            .GetProperty("errorCode").GetString().ShouldBe("NoOpenBusinessDay");
    }

    private Task<HttpResponseMessage> Pay(OrderingScenario s, Guid orderId, int version, object body) =>
        s.Client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{orderId}/payments", body, version);

    private async Task Movement(OrderingScenario s, string type, string amount, string description) =>
        await (await s.Client.PostIdempotentAsync("/api/v1/closing/cash-movements", new { movementType = type, amount, description }, Guid.NewGuid()))
            .ExpectAsync(HttpStatusCode.Created, _api);

    private async Task<JsonElement> Summary(OrderingScenario s) =>
        await (await s.Client.GetAsync("/api/v1/closing/summary", Ct)).ExpectAsync(HttpStatusCode.OK, _api);

    private static Task<HttpResponseMessage> Finalize(OrderingScenario s, Guid dayId, int version, string counted, string? justification, HttpClient? client = null) =>
        (client ?? s.Client).SendWithKeyAsync(HttpMethod.Post, "/api/v1/closing/finalize",
            new { businessDayId = dayId, countedCash = counted, justification }, version);
}
