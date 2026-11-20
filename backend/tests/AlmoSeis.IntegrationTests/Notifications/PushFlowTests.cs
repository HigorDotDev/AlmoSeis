using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AlmoSeis.Infrastructure.Realtime;
using AlmoSeis.IntegrationTests.Ordering;
using AlmoSeis.Modules.Notifications.Application;
using AlmoSeis.Modules.Notifications.Infrastructure;
using AlmoSeis.Modules.Notifications.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Notifications;

/// Push dos pedidos da página (ADR-0049, EP-002 F-209) contra a API real, com o `FakePushSender` no lugar da Expo e o
/// dispatcher do outbox chamado pelo teste (`Outbox:Enabled = false`).
[Collection(nameof(PostgresCollection))]
public sealed class PushFlowTests(PostgresFixture db) : IDisposable
{
    private const string Password = "S3nha-forte!";

    private readonly ApiFactory _api = new(db, s => s.Configure<OutboxOptions>(o => o.Enabled = false));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakePushSender Push => _api.Services.GetRequiredService<FakePushSender>();

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Registers_updates_and_removes_the_token_of_this_installation()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var owner = s.Owner.AccessToken();
        var token = NewToken();

        await ExpectField(Register(owner, token, installation: "inst_de_outro"), "installationId");
        await ExpectField(Register(owner, token, platform: "WEB"), "platform");
        await ExpectField(Register(owner, "ExponentPushToken[curto]"), "expoPushToken");
        await ExpectField(Register(owner, "token-qualquer"), "expoPushToken");

        var registered = await (await Register(owner, token)).ExpectAsync(HttpStatusCode.OK, _api);
        registered.GetProperty("installationId").GetString().ShouldBe(InstallationOf(owner));
        registered.GetProperty("platform").GetString().ShouldBe("ANDROID");
        registered.GetProperty("webOrdersEnabled").GetBoolean().ShouldBeTrue();
        registered.GetProperty("receivesWebOrders").GetBoolean().ShouldBeTrue();
        registered.TryGetProperty("expoPushToken", out _).ShouldBeFalse("o token não volta na resposta");

        // Mesmo aparelho: atualiza o mesmo registro (token novo, opção desligada); sem a opção, mantém a atual.
        var renewed = NewToken();
        var off = await (await Register(owner, renewed, enabled: false, platform: "IOS")).ExpectAsync(HttpStatusCode.OK, _api);
        off.GetProperty("webOrdersEnabled").GetBoolean().ShouldBeFalse();
        off.GetProperty("receivesWebOrders").GetBoolean().ShouldBeFalse();
        off.GetProperty("registeredAt").GetString().ShouldBe(registered.GetProperty("registeredAt").GetString());
        (await (await Register(owner, renewed, enabled: null)).ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("webOrdersEnabled").GetBoolean().ShouldBeFalse();
        (await Count(s, "SELECT count(*) FROM push_registrations WHERE tenant_id = @t")).ShouldBe(1);
        (await s.ScalarAsync<string>("SELECT expo_push_token FROM push_registrations WHERE tenant_id = @t")).ShouldBe(renewed);

        // Quem não tem orders.accept pode registrar, mas não recebe pedidos do site.
        var kitchen = await _api.EmployeeTokenAsync(db, s.Owner, "KITCHEN", "inst_kitchen");
        (await (await Register(kitchen, NewToken())).ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("receivesWebOrders").GetBoolean().ShouldBeFalse();

        (await _api.Authenticated(owner).DeleteAsync("/api/v1/devices/push-token", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _api.Authenticated(owner).DeleteAsync("/api/v1/devices/push-token", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Count(s, "SELECT count(*) FROM push_registrations WHERE tenant_id = @t AND installation_id = @i", ("i", InstallationOf(owner)))).ShouldBe(0);
        (await Count(s, "SELECT count(*) FROM push_registrations WHERE tenant_id = @t")).ShouldBe(1);
        (await _api.CreateClient().DeleteAsync("/api/v1/devices/push-token", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Web_order_pushes_devices_with_orders_accept_without_personal_data()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var ownerToken = NewToken();
        var cashierToken = NewToken();
        var kitchenToken = NewToken();
        var attendantToken = NewToken();
        await (await Register(s.Owner.AccessToken(), ownerToken)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await Register(await _api.EmployeeTokenAsync(db, s.Owner, "CASHIER", "inst_cashier"), cashierToken)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await Register(await _api.EmployeeTokenAsync(db, s.Owner, "KITCHEN", "inst_kitchen"), kitchenToken)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await Register(await _api.EmployeeTokenAsync(db, s.Owner, "ATTENDANT", "inst_attendant"), attendantToken, enabled: false))
            .ExpectAsync(HttpStatusCode.OK, _api);

        var orderId = await s.SubmitWebOrderAsync(delivery: true, quantity: 2);
        await DispatchAsync();

        var sent = SentFor(orderId);
        sent.Select(m => m.To).Order().ShouldBe(new[] { ownerToken, cashierToken }.Order(), "só quem tem orders.accept e a opção ligada");
        var toOwner = sent.Single(m => m.To == ownerToken);
        toOwner.Title.ShouldBe("Novo pedido do site");
        toOwner.Body.ShouldBe("2 marmitas · Entrega · aceite em até 10 min");
        toOwner.Sound.ShouldBe("web_order.wav");
        toOwner.ChannelId.ShouldBe("web-orders");
        toOwner.TtlSeconds.ShouldBeInRange(590, 600);
        toOwner.Data.Keys.Order().ShouldBe(["kind", "orderId", "tenantId"]);
        toOwner.Data["kind"].ShouldBe("WEB_ORDER_AWAITING_ACCEPTANCE");
        toOwner.Data["orderId"].ShouldBe(orderId.ToString());
        toOwner.Data["tenantId"].ShouldBe(s.TenantId.ToString());

        // O caixa tem vínculo em dois estabelecimentos: o título leva o nome deste (item 3).
        var tenantName = s.Owner.GetProperty("me").GetProperty("tenant").GetProperty("name").GetString();
        sent.Single(m => m.To == cashierToken).Title.ShouldBe($"Novo pedido do site · {tenantName}");

        // Sem dado pessoal nem valor (ADR-0049 item 3, ADR-0052 item 7).
        foreach (var message in sent)
        {
            var text = message.Title + message.Body + string.Join(",", message.Data.Values);
            foreach (var personal in new[] { "Carla", "Souza", "988887777", "Palmeiras", "Casa 2", "40.00", "45.00", "R$" })
            {
                text.ShouldNotContain(personal);
            }
        }

        (await Count(s, "SELECT count(*) FROM push_deliveries WHERE tenant_id = @t AND order_id = @o AND ticket_id IS NOT NULL", ("o", orderId))).ShouldBe(2);
        (await Count(s, "SELECT count(*) FROM outbox_messages WHERE tenant_id = @t AND event_type = 'WebOrderPushRequested' AND payload::text LIKE '%Carla%'")).ShouldBe(0);

        // Retirada de 1 marmita.
        var pickup = await s.SubmitWebOrderAsync(delivery: false, quantity: 1, phone: "+5534977776666");
        await DispatchAsync();
        SentFor(pickup).Single(m => m.To == ownerToken).Body.ShouldBe("1 marmita · Retirada · aceite em até 10 min");
    }

    [Fact]
    public async Task App_orders_do_not_generate_push()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var token = NewToken();
        await (await Register(s.Owner.AccessToken(), token)).ExpectAsync(HttpStatusCode.OK, _api);

        await s.CreateOrderAsync(s.MarmitaDelivery());
        await s.CreateOrderAsync(s.Pf());
        await DispatchAsync();

        Push.Sent.ShouldNotContain(m => m.To == token);
        (await Count(s, "SELECT count(*) FROM outbox_messages WHERE tenant_id = @t AND event_type = 'WebOrderPushRequested'")).ShouldBe(0);
    }

    [Fact]
    public async Task Redelivered_outbox_message_does_not_push_twice_and_gives_up_after_three_attempts()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var token = NewToken();
        await (await Register(s.Owner.AccessToken(), token)).ExpectAsync(HttpStatusCode.OK, _api);

        var orderId = await s.SubmitWebOrderAsync();
        await DispatchAsync();
        SentFor(orderId).Count.ShouldBe(1);

        // O outbox entrega pelo menos uma vez: a mesma mensagem de novo não toca o celular de novo.
        await s.SqlAsync(
            "UPDATE outbox_messages SET processed_at = NULL WHERE tenant_id = @t AND event_type = 'WebOrderPushRequested' AND payload->'data'->>'kind' = 'WEB_ORDER_AWAITING_ACCEPTANCE'");
        await DispatchAsync();
        SentFor(orderId).Count.ShouldBe(1);
        (await Count(s, "SELECT count(*) FROM push_deliveries WHERE tenant_id = @t AND order_id = @o", ("o", orderId))).ShouldBe(1);

        // Expo fora do ar: nova tentativa em 30 s, e desistência na terceira (item 8).
        var second = await s.SubmitWebOrderAsync(phone: "+5534977776666");
        Push.FailFor(token, 3);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await DispatchAsync();
            var row = await s.ScalarAsync<string>(
                """
                SELECT attempts || '|' || (processed_at IS NOT NULL) || '|' || coalesce(round(extract(epoch FROM available_at - now()))::text, '-')
                  FROM outbox_messages
                 WHERE tenant_id = @t AND event_type = 'WebOrderPushRequested' AND payload->>'entityId' = @o::text
                   AND payload->'data'->>'kind' = 'WEB_ORDER_AWAITING_ACCEPTANCE'
                """, ("o", second));
            var parts = row.Split('|');
            parts[0].ShouldBe(attempt.ToString());
            parts[1].ShouldBe(attempt == 3 ? "true" : "false");
            if (attempt < 3)
            {
                int.Parse(parts[2]).ShouldBeInRange(25, 31);
                await s.SqlAsync("UPDATE outbox_messages SET available_at = now() WHERE tenant_id = @t AND payload->>'entityId' = @o::text AND available_at > now() + interval '10 seconds' AND payload->'data'->>'kind' = 'WEB_ORDER_AWAITING_ACCEPTANCE'", ("o", second));
            }
        }

        SentFor(second).ShouldBeEmpty();
        (await s.ScalarAsync<string>(
            "SELECT last_error FROM outbox_messages WHERE tenant_id = @t AND payload->>'entityId' = @o::text AND payload->'data'->>'kind' = 'WEB_ORDER_AWAITING_ACCEPTANCE'",
            ("o", second))).ShouldContain("fora do ar");
    }

    [Fact]
    public async Task Reminder_is_scheduled_at_three_minutes_and_sent_once_while_awaiting()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var token = NewToken();
        await (await Register(s.Owner.AccessToken(), token)).ExpectAsync(HttpStatusCode.OK, _api);

        var orderId = await s.SubmitWebOrderAsync();
        (await Count(s,
            """
            SELECT count(*) FROM outbox_messages
             WHERE tenant_id = @t AND event_type = 'WebOrderPushRequested' AND payload->>'entityId' = @o::text
               AND payload->'data'->>'kind' = 'WEB_ORDER_REMINDER' AND available_at = occurred_at + interval '3 minutes'
            """, ("o", orderId))).ShouldBe(1);

        await DispatchAsync();
        SentFor(orderId).Select(m => m.Data["kind"]).ShouldBe(["WEB_ORDER_AWAITING_ACCEPTANCE"], "o lembrete espera os 3 min");

        // Aos 3 min (o relógio é simulado pelo prazo e pelo available_at): faltam 7 min.
        await s.SetAcceptanceDeadlineAsync(orderId, DateTimeOffset.UtcNow.AddMinutes(6).AddSeconds(50));
        await MakeReminderDueAsync(s, orderId);
        await DispatchAsync();
        var reminder = SentFor(orderId).Single(m => m.Data["kind"] == "WEB_ORDER_REMINDER");
        reminder.Title.ShouldBe("Pedido do site esperando");
        reminder.Body.ShouldBe("Faltam 7 min para expirar");
        reminder.To.ShouldBe(token);

        await DispatchAsync();
        await s.SqlAsync("UPDATE outbox_messages SET processed_at = NULL WHERE tenant_id = @t AND payload->'data'->>'kind' = 'WEB_ORDER_REMINDER'");
        await DispatchAsync();
        SentFor(orderId).Count(m => m.Data["kind"] == "WEB_ORDER_REMINDER").ShouldBe(1, "um lembrete só");

        // Pedido aceito antes dos 3 min: sem lembrete.
        var accepted = await s.SubmitWebOrderAsync(phone: "+5534977776666");
        await DispatchAsync();
        await AcceptAsync(s, accepted);
        await MakeReminderDueAsync(s, accepted);
        await DispatchAsync();
        SentFor(accepted).Select(m => m.Data["kind"]).ShouldBe(["WEB_ORDER_AWAITING_ACCEPTANCE"]);
    }

    [Fact]
    public async Task Device_not_registered_removes_the_registration_from_ticket_and_receipt()
    {
        var s = await OrderingScenario.CreateAsync(_api, db);
        var gone = $"ExponentPushToken[{FakePushSender.UnregisteredMarker}{Guid.NewGuid():N}]"[..60] + "]";
        var good = NewToken();
        await (await Register(s.Owner.AccessToken(), gone)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await Register(await _api.EmployeeTokenAsync(db, s.Owner, "MANAGER", "inst_manager"), good)).ExpectAsync(HttpStatusCode.OK, _api);

        var orderId = await s.SubmitWebOrderAsync();
        await DispatchAsync();

        SentFor(orderId).Select(m => m.To).ShouldBe([good]);
        (await Count(s, "SELECT count(*) FROM push_registrations WHERE tenant_id = @t AND expo_push_token = @k", ("k", gone))).ShouldBe(0);
        (await s.ScalarAsync<string>("SELECT error FROM push_deliveries WHERE tenant_id = @t AND order_id = @o AND ticket_id IS NULL", ("o", orderId)))
            .ShouldBe("DeviceNotRegistered");

        // Recibo conferido 15 min depois: DeviceNotRegistered também apaga.
        var ticket = await s.ScalarAsync<string>("SELECT ticket_id FROM push_deliveries WHERE tenant_id = @t AND order_id = @o AND ticket_id IS NOT NULL", ("o", orderId));
        Push.ReceiptErrors[ticket] = "DeviceNotRegistered";
        var job = _api.Services.GetRequiredService<PushMaintenanceJob>();
        (await job.RunOnceAsync(Ct)).ShouldNotBeNull();
        (await Count(s, "SELECT count(*) FROM push_registrations WHERE tenant_id = @t")).ShouldBe(1, "antes de 15 min o recibo não é conferido");

        await s.SqlAsync("UPDATE push_deliveries SET sent_at = sent_at - interval '16 minutes' WHERE tenant_id = @t");
        (await job.RunOnceAsync(Ct)).ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(1);
        (await Count(s, "SELECT count(*) FROM push_registrations WHERE tenant_id = @t")).ShouldBe(0);
        (await s.ScalarAsync<string>("SELECT receipt_error FROM push_deliveries WHERE tenant_id = @t AND ticket_id = @k", ("k", ticket))).ShouldBe("DeviceNotRegistered");

        // Limpeza: registro sem uso há 30 dias e entregas com mais de 7 dias.
        var again = NewToken();
        await (await Register(s.Owner.AccessToken(), again)).ExpectAsync(HttpStatusCode.OK, _api);
        await s.SqlAsync("UPDATE push_registrations SET updated_at = now() - interval '31 days' WHERE tenant_id = @t");
        await s.SqlAsync("UPDATE push_deliveries SET sent_at = now() - interval '8 days' WHERE tenant_id = @t");
        await job.RunOnceAsync(Ct);
        (await Count(s, "SELECT count(*) FROM push_registrations WHERE tenant_id = @t")).ShouldBe(0);
        (await Count(s, "SELECT count(*) FROM push_deliveries WHERE tenant_id = @t")).ShouldBe(0);
    }

    [Fact]
    public async Task Push_never_crosses_tenants_and_a_device_that_moved_leaves_the_old_one()
    {
        var a = await OrderingScenario.CreateAsync(_api, db);
        var b = await OrderingScenario.CreateAsync(_api, db);
        var tokenA = NewToken();
        var tokenB = NewToken();
        await (await Register(a.Owner.AccessToken(), tokenA)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await Register(b.Owner.AccessToken(), tokenB)).ExpectAsync(HttpStatusCode.OK, _api);

        var orderA = await a.SubmitWebOrderAsync();
        await DispatchAsync();
        SentFor(orderA).Select(m => m.To).ShouldBe([tokenA]);

        // B não apaga nem enxerga o registro de A.
        (await b.Client.DeleteAsync("/api/v1/devices/push-token", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Count(a, "SELECT count(*) FROM push_registrations WHERE tenant_id = @t")).ShouldBe(1);
        (await Count(b, "SELECT count(*) FROM push_registrations WHERE tenant_id = @t")).ShouldBe(0);
        await (await Register(b.Owner.AccessToken(), tokenB)).ExpectAsync(HttpStatusCode.OK, _api);

        // O mesmo aparelho (mesmo token) agora logado em B, sem logout em A: sai de A.
        var moved = await _api.EmployeeTokenAsync(db, b.Owner, "CASHIER", "inst_moved");
        await (await Register(moved, tokenA)).ExpectAsync(HttpStatusCode.OK, _api);
        (await Count(a, "SELECT count(*) FROM push_registrations WHERE tenant_id = @t")).ShouldBe(0);
        (await Count(b, "SELECT count(*) FROM push_registrations WHERE tenant_id = @t AND expo_push_token = @k", ("k", tokenA))).ShouldBe(1);

        var orderA2 = await a.SubmitWebOrderAsync(phone: "+5534977776666");
        var orderB = await b.SubmitWebOrderAsync();
        await DispatchAsync();
        SentFor(orderA2).ShouldBeEmpty("o aparelho saiu de A");
        SentFor(orderB).Select(m => m.To).Order().ShouldBe(new[] { tokenA, tokenB }.Order());
        SentFor(orderB).ShouldAllBe(m => m.Data["tenantId"] == b.TenantId.ToString());
    }

    [Fact]
    public async Task Logout_switch_tenant_and_revocations_remove_the_registration()
    {
        // Logout: o registro desta instalação sai.
        var email = $"dona.{Guid.NewGuid():N}@example.com";
        var owner = await _api.SignUpAsync(ApiScenarios.SignUpBody(email: email));
        var session = await LoginAsync(email, "inst_owner_phone");
        await (await Register(session.AccessToken(), NewToken())).ExpectAsync(HttpStatusCode.OK, _api);
        (await Registrations(owner)).ShouldBe(1);
        (await _api.CreateClient().PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = RefreshOf(session) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Registrations(owner)).ShouldBe(0);

        // Desativação e troca de perfis (AccessRevoked).
        var ownerClient = _api.Authenticated(owner.AccessToken());
        var cashier = await _api.EmployeeTokenAsync(db, owner, "CASHIER", "inst_cashier");
        var attendant = await _api.EmployeeTokenAsync(db, owner, "ATTENDANT", "inst_attendant");
        await (await Register(cashier, NewToken())).ExpectAsync(HttpStatusCode.OK, _api);
        await (await Register(attendant, NewToken())).ExpectAsync(HttpStatusCode.OK, _api);
        (await Registrations(owner)).ShouldBe(2);

        var cashierId = await EmployeeIdAsync(cashier);
        var cashierDto = await (await ownerClient.GetAsync($"/api/v1/employees/{cashierId}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await ownerClient.SendJsonAsync(HttpMethod.Post, $"/api/v1/employees/{cashierId}/deactivate", null, cashierDto.Version())).ExpectAsync(HttpStatusCode.OK, _api);
        var attendantId = await EmployeeIdAsync(attendant);
        var attendantDto = await (await ownerClient.GetAsync($"/api/v1/employees/{attendantId}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await ownerClient.SendJsonAsync(HttpMethod.Patch, $"/api/v1/employees/{attendantId}", new { roles = new[] { "KITCHEN" } }, attendantDto.Version()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        (await Registrations(owner)).ShouldBe(0);

        // Troca de estabelecimento: o push do anterior para de chegar nesta instalação.
        var manager = await _api.EmployeeTokenAsync(db, owner, "MANAGER", "inst_manager");
        await (await Register(manager, NewToken())).ExpectAsync(HttpStatusCode.OK, _api);
        var me = await (await _api.Authenticated(manager).GetAsync("/api/v1/auth/me", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        var otherTenant = me.GetProperty("memberships").EnumerateArray().Select(m => m.GetProperty("tenantId").GetString()).Single(t => t != owner.TenantId());
        await (await _api.Authenticated(manager).PostAsJsonAsync("/api/v1/auth/switch-tenant", new { tenantId = otherTenant }, Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        (await Registrations(owner)).ShouldBe(0);

        // Aparelho compartilhado: o logout (também o do tempo ocioso) mantém o registro; revogar o aparelho apaga.
        var tablet = await LoginAsync(email, "inst_tablet");
        var device = await (await _api.Authenticated(tablet.AccessToken()).PostAsJsonAsync("/api/v1/devices/shared",
            new { installationId = "inst_tablet", name = "Tablet balcão", platform = "ANDROID" }, Ct)).ExpectAsync(HttpStatusCode.Created, _api);
        await (await _api.Authenticated(tablet.AccessToken()).SendJsonAsync(HttpMethod.Put, "/api/v1/auth/me/pin", new { pin = "4821", currentPassword = Password }))
            .ExpectAsync(HttpStatusCode.NoContent, _api);
        var pinRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/pin-login")
        {
            Content = JsonContent.Create(new { tenantId = owner.TenantId(), installationId = "inst_tablet", employeeId = owner.GetProperty("me").GetProperty("employee").GetProperty("id").GetString(), pin = "4821" }),
        };
        pinRequest.Headers.Add("X-Device-Secret", device.GetProperty("deviceSecret").GetString());
        var pin = await (await _api.CreateClient().SendAsync(pinRequest, Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await Register(pin.AccessToken(), NewToken())).ExpectAsync(HttpStatusCode.OK, _api);
        (await _api.CreateClient().PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = RefreshOf(pin) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Registrations(owner)).ShouldBe(1, "o tablet continua avisando depois do tempo ocioso");
        (await _api.Authenticated(owner.AccessToken()).PostAsync($"/api/v1/devices/shared/{device.GetProperty("id").GetString()}/revoke", null, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Registrations(owner)).ShouldBe(0);
    }

    // ---------- apoio ----------

    private static string NewToken() => $"ExponentPushToken[{Guid.NewGuid():N}]";

    private Task<HttpResponseMessage> Register(string accessToken, string token, bool? enabled = true, string platform = "ANDROID", string? installation = null) =>
        _api.Authenticated(accessToken).SendJsonAsync(HttpMethod.Put, "/api/v1/devices/push-token",
            new { installationId = installation ?? InstallationOf(accessToken), expoPushToken = token, platform, webOrdersEnabled = enabled });

    private async Task ExpectField(Task<HttpResponseMessage> response, string field) =>
        (await (await response).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api)).GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue(field);

    /// `installation_id` do access token (o mesmo que o app mandou no login).
    private static string InstallationOf(string accessToken)
    {
        var payload = accessToken.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        return json.RootElement.GetProperty("installation_id").GetString()!;
    }

    private static string RefreshOf(JsonElement session) => session.GetProperty("tokens").GetProperty("refreshToken").GetString()!;

    private async Task<JsonElement> LoginAsync(string email, string installationId) =>
        await (await _api.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { login = email, password = Password, installationId }, Ct))
            .ExpectAsync(HttpStatusCode.OK, _api);

    private async Task<string> EmployeeIdAsync(string token) =>
        (await (await _api.Authenticated(token).GetAsync("/api/v1/auth/me", Ct)).ExpectAsync(HttpStatusCode.OK, _api))
            .GetProperty("employee").GetProperty("id").GetString()!;

    private async Task<long> Registrations(JsonElement owner)
    {
        await using var connection = new Npgsql.NpgsqlConnection(db.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("SELECT count(*) FROM push_registrations WHERE tenant_id = @t", connection);
        command.Parameters.AddWithValue("t", Guid.Parse(owner.TenantId()));
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static Task<long> Count(OrderingScenario s, string sql, params (string Name, object Value)[] parameters) => s.ScalarAsync<long>(sql, parameters);

    private List<PushMessage> SentFor(Guid orderId) => Push.Sent.Where(m => m.Data["orderId"] == orderId.ToString()).ToList();

    private async Task DispatchAsync()
    {
        var dispatcher = _api.Services.GetRequiredService<OutboxDispatcher>();
        while (await dispatcher.DispatchBatchAsync(Ct) > 0)
        {
            // Esvazia o que está disponível agora (inclusive mensagens antigas de outros testes).
        }
    }

    private static Task MakeReminderDueAsync(OrderingScenario s, Guid orderId) =>
        s.SqlAsync(
            "UPDATE outbox_messages SET available_at = now() - interval '1 second' WHERE tenant_id = @t AND payload->>'entityId' = @o::text AND payload->'data'->>'kind' = 'WEB_ORDER_REMINDER'",
            ("o", orderId));

    private async Task AcceptAsync(OrderingScenario s, Guid orderId)
    {
        var order = await s.GetOrderAsync(orderId);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{orderId}/accept") { Content = JsonContent.Create(new { estimatedReadyMinutes = (int?)null }) };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{order.Version()}\"");
        await (await s.Client.SendAsync(request, Ct)).ExpectAsync(HttpStatusCode.OK, _api);
    }
}
