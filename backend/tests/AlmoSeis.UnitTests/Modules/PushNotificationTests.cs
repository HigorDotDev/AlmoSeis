using System.Net;
using System.Text;
using System.Text.Json;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Notifications.Application;
using AlmoSeis.Modules.Notifications.Contracts;
using AlmoSeis.Modules.Notifications.Domain;
using AlmoSeis.Modules.Notifications.Infrastructure;
using AlmoSeis.Modules.Ordering.Application;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Ordering.Domain;
using AlmoSeis.Modules.Ordering.Infrastructure;
using AlmoSeis.SharedKernel.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;

namespace AlmoSeis.UnitTests.Modules;

/// Push dos pedidos da página (ADR-0049): conteúdo, token, mensagens do outbox e o cliente da Expo.
public sealed class PushNotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);

    // ---------- conteúdo (itens 2 e 3) ----------

    [Theory]
    [InlineData(2, "DELIVERY", 10, "2 marmitas · Entrega · aceite em até 10 min")]
    [InlineData(1, "PICKUP", 9, "1 marmita · Retirada · aceite em até 9 min")]
    public void New_web_order_body_has_only_quantity_mode_and_deadline(int marmitas, string mode, int minutes, string expected)
    {
        var (title, body) = WebOrderPushContent.For(PushKind.WebOrderAwaitingAcceptance, marmitas, mode, minutes, null);
        title.ShouldBe("Novo pedido do site");
        body.ShouldBe(expected);
    }

    [Fact]
    public void Reminder_says_how_long_until_it_expires_and_names_the_store_when_asked()
    {
        var (title, body) = WebOrderPushContent.For(PushKind.WebOrderReminder, 2, "DELIVERY", 7, "Marmitaria da Ana");
        title.ShouldBe("Pedido do site esperando · Marmitaria da Ana");
        body.ShouldBe("Faltam 7 min para expirar");
    }

    [Theory]
    [InlineData(600, 10)]
    [InlineData(599.6, 10)]
    [InlineData(420, 7)]
    [InlineData(420.4, 7)]
    [InlineData(421, 8)]
    [InlineData(30, 1)]
    [InlineData(0, 0)]
    public void Minutes_left_round_up_to_the_whole_minute(double seconds, int expected) =>
        WebOrderPushContent.MinutesLeft(Now.AddSeconds(seconds), Now).ShouldBe(expected);

    // ---------- token ----------

    [Theory]
    [InlineData("ExponentPushToken[xxxxxxxxxxxxxxxxxxxxxx]", true)]
    [InlineData("ExpoPushToken[AbC_12-34xyZ]", true)]
    [InlineData("ExponentPushToken[curto]", false)]
    [InlineData("ExponentPushToken[com espaço aqui]", false)]
    [InlineData("fcm:abcdefghijkl", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_expo_tokens_are_accepted(string? token, bool valid) => PushRegistration.IsExpoToken(token).ShouldBe(valid);

    [Fact]
    public void Token_is_masked_for_logs()
    {
        PushRegistration.Mask("ExponentPushToken[abcdefghijklmnopqrstuv]").ShouldBe("ExponentPushToken[abcd…]");
        PushRegistration.Mask("lixo").ShouldBe("***");
    }

    [Fact]
    public void Registration_refresh_keeps_the_option_when_not_sent()
    {
        var registration = PushRegistration.Create(Guid.NewGuid(), Guid.NewGuid(), "inst_1", Guid.NewGuid(), null, PushPlatform.Android,
            "ExponentPushToken[aaaaaaaaaaaa]", webOrdersEnabled: false, showTenantName: false, Now);
        var employee = Guid.NewGuid();
        registration.Refresh(employee, Guid.NewGuid(), PushPlatform.Ios, "ExponentPushToken[bbbbbbbbbbbb]", null, true, Now.AddDays(1));

        registration.WebOrdersEnabled.ShouldBeFalse();
        registration.TenantEmployeeId.ShouldBe(employee, "no aparelho compartilhado vale o último funcionário");
        registration.ShowTenantName.ShouldBeTrue();
        registration.CreatedAt.ShouldBe(Now);
        registration.UpdatedAt.ShouldBe(Now.AddDays(1));
    }

    [Fact]
    public void Delivery_keeps_either_the_ticket_or_a_truncated_error()
    {
        PushDelivery.Record(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), PushKind.WebOrderReminder, "t1", "ignorado", Now).Error.ShouldBeNull();
        var failed = PushDelivery.Record(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), PushKind.WebOrderReminder, null, new string('x', 60), Now);
        failed.Error!.Length.ShouldBe(PushDelivery.ErrorMax);
    }

    // ---------- mensagens do outbox (item 8; ADR-0046 item 15) ----------

    [Fact]
    public void Web_order_produces_the_push_now_and_the_reminder_three_minutes_later_without_the_total()
    {
        var envelopes = Map(new OrderAwaitingAcceptance(Guid.NewGuid(), FulfillmentType.Delivery, 2, Money.Of(45m), Now.AddMinutes(10), Now), reminderMinutes: 3);

        envelopes.Count.ShouldBe(2);
        envelopes.ShouldAllBe(e => e.EventType == WebOrderPushMessage.EventType && e.EntityType == "ORDER");
        envelopes[0].AvailableAt.ShouldBeNull();
        envelopes[1].AvailableAt.ShouldBe(Now.AddMinutes(3));
        var data = JsonSerializer.Serialize(envelopes.Select(e => e.Data), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        data.ShouldContain("\"kind\":\"WEB_ORDER_AWAITING_ACCEPTANCE\"");
        data.ShouldContain("\"kind\":\"WEB_ORDER_REMINDER\"");
        data.ShouldContain("\"marmitaCount\":2");
        data.ShouldContain("\"fulfillmentType\":\"DELIVERY\"");
        data.ShouldNotContain("45");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(12)]
    public void No_reminder_when_disabled_or_after_the_deadline(int reminderMinutes) =>
        Map(new OrderAwaitingAcceptance(Guid.NewGuid(), FulfillmentType.Pickup, 1, Money.Of(20m), Now.AddMinutes(10), Now), reminderMinutes)
            .Count.ShouldBe(1);

    private static List<AlmoSeis.Infrastructure.Persistence.IntegrationEnvelope> Map(OrderAwaitingAcceptance e, int reminderMinutes)
    {
        var tenant = new TenantContext();
        tenant.InitializeForSystem(Guid.NewGuid());
        var mapper = new WebOrderPushMapper(tenant, Options.Create(new WebOrderOptions { ReminderMinutes = reminderMinutes }));
        return mapper.Map(e).ToList();
    }

    // ---------- Expo ----------

    [Fact]
    public async Task Expo_sender_posts_the_batch_and_maps_tickets_in_order()
    {
        string? body = null;
        AuthenticationHeaderValueHolder auth = new();
        var server = new FakeServer((request, content) =>
        {
            body = content;
            auth.Value = request.Headers.Authorization?.ToString();
            request.RequestUri!.ToString().ShouldBe("https://exp.host/--/api/v2/push/send");
            return """
                {"data":[{"status":"ok","id":"tk-1"},{"status":"error","message":"\"ExponentPushToken[x]\" is not a registered push notification recipient","details":{"error":"DeviceNotRegistered"}}]}
                """;
        });
        var sender = new ExpoPushSender(new HttpClient(server), Options.Create(new PushOptions { Provider = PushProvider.Expo }));

        var tickets = await sender.SendAsync([Message("ExponentPushToken[aaaaaaaaaaaa]"), Message("ExponentPushToken[bbbbbbbbbbbb]")], CancellationToken.None);

        tickets.ShouldBe([new PushTicket("tk-1", null), new PushTicket(null, "DeviceNotRegistered")]);
        auth.Value.ShouldBeNull("sem Push:Expo:AccessToken, sem Authorization");
        using var json = JsonDocument.Parse(body!);
        var first = json.RootElement[0];
        first.GetProperty("to").GetString().ShouldBe("ExponentPushToken[aaaaaaaaaaaa]");
        first.GetProperty("channelId").GetString().ShouldBe("web-orders");
        first.GetProperty("sound").GetString().ShouldBe("web_order.wav");
        first.GetProperty("priority").GetString().ShouldBe("high");
        first.GetProperty("ttl").GetInt32().ShouldBe(600);
        first.GetProperty("data").GetProperty("kind").GetString().ShouldBe("WEB_ORDER_AWAITING_ACCEPTANCE");
    }

    [Fact]
    public async Task Expo_sender_uses_the_access_token_when_configured_and_reads_receipts()
    {
        AuthenticationHeaderValueHolder auth = new();
        var server = new FakeServer((request, _) =>
        {
            auth.Value = request.Headers.Authorization?.ToString();
            return """{"data":{"tk-1":{"status":"ok"},"tk-2":{"status":"error","message":"x","details":{"error":"DeviceNotRegistered"}}}}""";
        });
        var options = new PushOptions { Provider = PushProvider.Expo };
        options.Expo.AccessToken = "segredo-expo";
        var sender = new ExpoPushSender(new HttpClient(server), Options.Create(options));

        var receipts = await sender.GetReceiptsAsync(["tk-1", "tk-2", "tk-3"], CancellationToken.None);

        auth.Value.ShouldBe("Bearer segredo-expo");
        receipts["tk-1"].Error.ShouldBeNull();
        receipts["tk-2"].Error.ShouldBe("DeviceNotRegistered");
        receipts.ContainsKey("tk-3").ShouldBeFalse("recibo ainda não disponível");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "{}")]
    [InlineData(HttpStatusCode.BadGateway, "{}")]
    [InlineData(HttpStatusCode.BadRequest, """{"errors":[{"code":"PUSH_TOO_MANY_EXPERIENCE_IDS","message":"ExponentPushToken[aaaaaaaaaaaa] ..."}]}""")]
    [InlineData(HttpStatusCode.OK, "não é json")]
    public async Task Expo_failures_become_unavailable_without_the_token_in_the_message(HttpStatusCode status, string response)
    {
        var sender = new ExpoPushSender(new HttpClient(new FakeServer((_, _) => response, status)), Options.Create(new PushOptions { Provider = PushProvider.Expo }));

        var error = await Should.ThrowAsync<PushUnavailableException>(() => sender.SendAsync([Message("ExponentPushToken[aaaaaaaaaaaa]")], CancellationToken.None));
        error.Message.ShouldNotContain("ExponentPushToken");
    }

    // ---------- configuração ----------

    [Fact]
    public void Fake_is_refused_in_production_and_urls_must_be_https()
    {
        Validate(new PushOptions { Provider = PushProvider.Fake }, Environments.Production).Failed.ShouldBeTrue();
        Validate(new PushOptions { Provider = PushProvider.Fake }, Environments.Development).Succeeded.ShouldBeTrue();
        Validate(new PushOptions { Provider = PushProvider.Expo }, Environments.Production).Succeeded.ShouldBeTrue();
        var insecure = new PushOptions { Provider = PushProvider.Expo };
        insecure.Expo.SendUrl = "http://exp.host/--/api/v2/push/send";
        Validate(insecure, Environments.Production).Failed.ShouldBeTrue();
        Validate(new PushOptions { Provider = PushProvider.Expo, MaxAttempts = 0 }, Environments.Production).Failed.ShouldBeTrue();
    }

    private static ValidateOptionsResult Validate(PushOptions options, string environment) =>
        new PushOptionsValidator(new FakeEnvironment(environment)).Validate(null, options);

    private static PushMessage Message(string to) =>
        new(to, "Novo pedido do site", "2 marmitas · Entrega · aceite em até 10 min",
            new Dictionary<string, string> { ["tenantId"] = Guid.Empty.ToString(), ["orderId"] = Guid.Empty.ToString(), ["kind"] = "WEB_ORDER_AWAITING_ACCEPTANCE" },
            "web_order.wav", "web-orders", 600);

    private sealed class AuthenticationHeaderValueHolder
    {
        public string? Value { get; set; }
    }

    private sealed class FakeServer(Func<HttpRequestMessage, string?, string> respond, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(respond(request, content), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "AlmoSeis.Api";

        public string ContentRootPath { get; set; } = ".";

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
