using System.Net;
using System.Text;
using System.Text.Json;
using AlmoSeis.IntegrationTests.Ordering;
using AlmoSeis.Modules.Billing.Application;
using AlmoSeis.Modules.Billing.Infrastructure;
using AlmoSeis.SharedKernel.Domain;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Billing;

/// Assinatura (ADR-0017): webhook do Asaas, ciclo de vida, bloqueio de escrita e gateway, contra a API real.
[Collection(nameof(PostgresCollection))]
public sealed class BillingFlowTests(PostgresFixture db) : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private ApiFactory? _api;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// Gateway falso no lugar do Asaas (a API real só é chamada com chave configurada).
    private ApiFactory Api => _api ??= new ApiFactory(db, s => s.AddScoped<ISubscriptionBillingGateway>(_ => _gateway));

    public void Dispose() => _api?.Dispose();

    [Fact]
    public async Task Webhook_requires_the_token_and_is_idempotent()
    {
        var client = Api.CreateClient();
        var eventId = $"evt_{Guid.NewGuid():N}";
        var body = $$"""{ "id": "{{eventId}}", "event": "PAYMENT_CREATED", "payment": { "id": "pay_1" } }""";

        (await Webhook(client, body, "errado")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Webhook(client, """{ "nada": 1 }""", ApiFactory.WebhookToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Webhook(client, body, ApiFactory.WebhookToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Webhook(client, body, ApiFactory.WebhookToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var s = await OrderingScenario.CreateAsync(Api, db, openDay: false);
        (await s.ScalarAsync<long>("SELECT count(*) FROM billing_webhook_events WHERE gateway_event_id = @e", ("e", eventId))).ShouldBe(1);
    }

    [Fact]
    public async Task Overdue_grace_suspension_and_payment_drive_write_access()
    {
        var s = await OrderingScenario.CreateAsync(Api, db);
        var gatewayId = $"sub_{Guid.NewGuid():N}";
        await s.SqlAsync("UPDATE subscriptions SET status = 'ACTIVE', gateway_subscription_id = @g WHERE tenant_id = @t", ("g", gatewayId));

        await SendEvent("PAYMENT_OVERDUE", gatewayId);
        var pastDue = await WaitForStatus(s, "PAST_DUE");
        pastDue.GetProperty("canWrite").GetBoolean().ShouldBeTrue();
        pastDue.GetProperty("graceEndsAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
        pastDue.GetProperty("message").GetString()!.ShouldContain("Pagamento pendente");
        pastDue.TryGetProperty("monthlyPrice", out _).ShouldBeFalse(); // sem preço (ADR-0017)
        (await s.ScalarAsync<long>("SELECT count(*) FROM audit_logs WHERE tenant_id = @t AND origin = 'BILLING_WEBHOOK' AND operation_type = 'SubscriptionStatusChanged'"))
            .ShouldBe(1);

        // Carência vencida → suspensa → só leitura (e a finalização do fechamento em aberto).
        await s.SqlAsync("UPDATE subscriptions SET past_due_since = now() - interval '6 days' WHERE tenant_id = @t");
        await Api.Services.GetRequiredService<SubscriptionLifecycleJob>().RunOnceAsync(Ct);
        await WaitForStatus(s, "SUSPENDED");

        var blocked = await (await s.Client.PostIdempotentAsync("/api/v1/orders", s.Pf(), Guid.NewGuid())).ExpectAsync(HttpStatusCode.PaymentRequired, Api);
        blocked.GetProperty("errorCode").GetString().ShouldBe("SubscriptionInactive");
        await (await s.Client.GetAsync("/api/v1/orders", Ct)).ExpectAsync(HttpStatusCode.OK, Api);
        var summary = await (await s.Client.GetAsync("/api/v1/closing/summary", Ct)).ExpectAsync(HttpStatusCode.OK, Api);
        await (await s.Client.SendWithKeyAsync(HttpMethod.Post, "/api/v1/closing/finalize",
                new { businessDayId = summary.GetProperty("businessDay").Id(), countedCash = "100.00", justification = (string?)null },
                summary.GetProperty("businessDay").Version()))
            .ExpectAsync(HttpStatusCode.Created, Api);

        // Pagamento confirmado → ativa e libera a escrita na hora (cache invalidado).
        await SendEvent("PAYMENT_CONFIRMED", gatewayId, dueDate: "2026-11-27");
        var active = await WaitForStatus(s, "ACTIVE");
        active.GetProperty("currentPeriodEndsAt").GetString()!.ShouldStartWith("2026-12-27");
        await (await s.Client.PostIdempotentAsync("/api/v1/business-days", new { openingBalance = "0.00", confirmSameDate = true }, Guid.NewGuid()))
            .ExpectAsync(HttpStatusCode.Created, Api);
    }

    [Fact]
    public async Task Expired_trial_is_suspended_and_long_suspension_cancels()
    {
        var s = await OrderingScenario.CreateAsync(Api, db, openDay: false);
        await s.SqlAsync("UPDATE subscriptions SET trial_ends_at = now() - interval '1 minute' WHERE tenant_id = @t");

        await Api.Services.GetRequiredService<SubscriptionLifecycleJob>().RunOnceAsync(Ct);
        (await WaitForStatus(s, "SUSPENDED")).GetProperty("canWrite").GetBoolean().ShouldBeFalse();

        await s.SqlAsync("UPDATE subscriptions SET suspended_at = now() - interval '31 days' WHERE tenant_id = @t");
        await Api.Services.GetRequiredService<SubscriptionLifecycleJob>().RunOnceAsync(Ct);
        (await WaitForStatus(s, "CANCELED")).GetProperty("message").GetString()!.ShouldContain("cancelada");
    }

    [Fact]
    public async Task Trial_ending_creates_the_subscription_in_the_gateway()
    {
        var s = await OrderingScenario.CreateAsync(Api, db, openDay: false);
        await s.SqlAsync("UPDATE subscriptions SET trial_ends_at = now() + interval '1 day' WHERE tenant_id = @t");

        await Api.Services.GetRequiredService<SubscriptionLifecycleJob>().RunOnceAsync(Ct);

        var call = _gateway.Calls.Where(c => c.TenantId == s.TenantId).ShouldHaveSingleItem();
        call.Name.ShouldStartWith("Marmitaria");
        call.Email.ShouldNotBeNull();
        (await s.ScalarAsync<string>("SELECT gateway_subscription_id FROM subscriptions WHERE tenant_id = @t")).ShouldBe($"sub_{s.TenantId:N}");

        // Já vinculada: a próxima execução não cria de novo.
        await Api.Services.GetRequiredService<SubscriptionLifecycleJob>().RunOnceAsync(Ct);
        _gateway.Calls.Count(c => c.TenantId == s.TenantId).ShouldBe(1);
    }

    private async Task SendEvent(string eventType, string gatewaySubscriptionId, string dueDate = "2026-10-27")
    {
        var body = eventType.StartsWith("SUBSCRIPTION_", StringComparison.Ordinal)
            ? $$"""{ "id": "evt_{{Guid.NewGuid():N}}", "event": "{{eventType}}", "subscription": { "id": "{{gatewaySubscriptionId}}" } }"""
            : $$"""{ "id": "evt_{{Guid.NewGuid():N}}", "event": "{{eventType}}", "payment": { "id": "pay_1", "subscription": "{{gatewaySubscriptionId}}", "dueDate": "{{dueDate}}" } }""";
        (await Webhook(Api.CreateClient(), body, ApiFactory.WebhookToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Api.Services.GetRequiredService<BillingWebhookProcessor>().ProcessPendingAsync(Ct);
    }

    /// O processador em segundo plano pode pegar o evento antes do teste: espera o estado em vez de assumir quem processou.
    private async Task<JsonElement> WaitForStatus(OrderingScenario s, string status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        JsonElement last = default;
        while (DateTime.UtcNow < deadline)
        {
            last = await (await s.Client.GetAsync("/api/v1/subscription", Ct)).ExpectAsync(HttpStatusCode.OK, Api);
            if (last.GetProperty("status").GetString() == status)
            {
                return last;
            }

            await Task.Delay(200, Ct);
        }

        throw new TimeoutException($"Assinatura não chegou a {status}: {last}");
    }

    private static Task<HttpResponseMessage> Webhook(HttpClient client, string body, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/asaas") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("asaas-access-token", token);
        return client.SendAsync(request, Ct);
    }

    private sealed class FakeGateway : ISubscriptionBillingGateway
    {
        public System.Collections.Concurrent.ConcurrentBag<BillingCustomer> Calls { get; } = [];

        public bool IsConfigured => true;

        public Task<GatewayIds> CreateCustomerAndSubscriptionAsync(BillingCustomer customer, Money monthlyPrice, CancellationToken cancellationToken)
        {
            Calls.Add(customer);
            return Task.FromResult(new GatewayIds($"cus_{customer.TenantId:N}", $"sub_{customer.TenantId:N}"));
        }
    }
}
