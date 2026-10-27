using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AlmoSeis.IntegrationTests.Ordering;
using Npgsql;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Identity;

/// Login de administrador de plataforma e rotas /admin/* (06 §6.15, ADR-0025, ADR-0043) contra a API real.
[Collection(nameof(PostgresCollection))]
public sealed class AdminFlowTests(PostgresFixture db) : IDisposable
{
    private const string Password = "S3nha-forte!";
    private readonly ApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Admin_login_is_only_for_platform_admins_and_issues_a_token_without_tenant()
    {
        var (email, _) = await AdminAsync();
        var anonymous = _api.CreateClient();

        var session = await (await AdminLogin(anonymous, email, Password)).ExpectAsync(HttpStatusCode.OK, _api);
        session.GetProperty("user").GetProperty("email").GetString().ShouldBe(email);
        session.TryGetProperty("refreshToken", out _).ShouldBeFalse();
        var claims = Claims(session.GetProperty("accessToken").GetString()!);
        claims.GetProperty("platform_admin").GetString().ShouldBe("true");
        claims.TryGetProperty("tenant_id", out _).ShouldBeFalse();
        claims.TryGetProperty("employee_id", out _).ShouldBeFalse();

        // Senha errada e usuário comum recebem a mesma resposta.
        (await (await AdminLogin(anonymous, email, "Errada-123")).ExpectAsync(HttpStatusCode.Unauthorized, _api))
            .GetProperty("errorCode").GetString().ShouldBe("InvalidCredentials");
        var commonEmail = $"comum.{Guid.NewGuid():N}@example.com";
        await _api.SignUpAsync(ApiScenarios.SignUpBody(email: commonEmail));
        (await (await AdminLogin(anonymous, commonEmail, Password)).ExpectAsync(HttpStatusCode.Unauthorized, _api))
            .GetProperty("errorCode").GetString().ShouldBe("InvalidCredentials");
    }

    [Fact]
    public async Task Admin_and_tenant_tokens_do_not_cross()
    {
        var (email, tenantSession) = await AdminAsync();
        var admin = _api.Authenticated(await AdminTokenAsync(email));

        // Token de admin fora de /admin → 401; token de estabelecimento (mesmo de um admin) em /admin → 403.
        await (await admin.GetAsync("/api/v1/auth/me", Ct)).ExpectAsync(HttpStatusCode.Unauthorized, _api);
        await (await admin.GetAsync("/api/v1/orders", Ct)).ExpectAsync(HttpStatusCode.Unauthorized, _api);
        var tenantClient = _api.Authenticated(tenantSession.AccessToken());
        await (await tenantClient.GetAsync("/api/v1/admin/tenants", Ct)).ExpectAsync(HttpStatusCode.Forbidden, _api);
        Claims(tenantSession.AccessToken()).TryGetProperty("platform_admin", out _).ShouldBeFalse();

        await (await _api.CreateClient().GetAsync("/api/v1/admin/tenants", Ct)).ExpectAsync(HttpStatusCode.Unauthorized, _api);
        await (await admin.GetAsync("/api/v1/admin/tenants", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
    }

    [Fact]
    public async Task Admin_token_is_revoked_when_the_flag_or_the_password_changes()
    {
        var (email, _) = await AdminAsync();
        var admin = _api.Authenticated(await AdminTokenAsync(email));
        await (await admin.GetAsync("/api/v1/admin/tenants?limit=1", Ct)).ExpectAsync(HttpStatusCode.OK, _api);

        await SqlAsync("UPDATE users SET is_platform_admin = false WHERE normalized_email = upper(@e)", email);
        await (await admin.GetAsync("/api/v1/admin/tenants?limit=1", Ct)).ExpectAsync(HttpStatusCode.Unauthorized, _api);

        await SqlAsync("UPDATE users SET is_platform_admin = true WHERE normalized_email = upper(@e)", email);
        var renewed = _api.Authenticated(await AdminTokenAsync(email));
        await (await renewed.GetAsync("/api/v1/admin/tenants?limit=1", Ct)).ExpectAsync(HttpStatusCode.OK, _api);

        // Novo security stamp (troca de senha) derruba o token emitido antes.
        await SqlAsync("UPDATE users SET security_stamp = md5(random()::text) WHERE normalized_email = upper(@e)", email);
        await (await renewed.GetAsync("/api/v1/admin/tenants?limit=1", Ct)).ExpectAsync(HttpStatusCode.Unauthorized, _api);
    }

    [Fact]
    public async Task Tenant_list_and_detail_show_only_registration_subscription_and_counts()
    {
        var (email, _) = await AdminAsync();
        var admin = _api.Authenticated(await AdminTokenAsync(email));
        var s = await OrderingScenario.CreateAsync(_api, db);
        await s.CreateOrderAsync(s.Pf());
        var document = (await (await s.Client.GetAsync("/api/v1/tenant", Ct)).ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("documentNumber").GetString();

        var found = await (await admin.GetAsync($"/api/v1/admin/tenants?search={document}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        var item = found.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("id").GetString().ShouldBe(s.TenantId.ToString());
        item.GetProperty("subscription").GetProperty("status").GetString().ShouldBe("TRIALING");
        item.GetProperty("activeEmployeeCount").GetInt32().ShouldBe(1);
        item.GetProperty("lastOrderAt").ValueKind.ShouldBe(JsonValueKind.String);

        var page = await (await admin.GetAsync("/api/v1/admin/tenants?status=TRIALING&limit=1", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        page.GetProperty("items").GetArrayLength().ShouldBe(1);
        page.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.String);

        var detail = await (await admin.GetAsync($"/api/v1/admin/tenants/{s.TenantId}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        detail.GetProperty("ordersLast30Days").GetInt32().ShouldBe(1);
        detail.GetProperty("openBusinessDay").GetProperty("businessDate").GetString().ShouldBe(OrderingScenario.Today.ToString("yyyy-MM-dd"));
        detail.TryGetProperty("orders", out _).ShouldBeFalse();
        await (await admin.GetAsync($"/api/v1/admin/tenants/{Guid.NewGuid()}", Ct)).ExpectAsync(HttpStatusCode.NotFound, _api);
    }

    [Fact]
    public async Task Extend_suspend_and_reactivate_change_the_subscription_with_audit()
    {
        var (email, _) = await AdminAsync();
        var admin = _api.Authenticated(await AdminTokenAsync(email));
        var s = await OrderingScenario.CreateAsync(_api, db);
        var url = $"/api/v1/admin/tenants/{s.TenantId}";
        var trialEnd = (await (await s.Client.GetAsync("/api/v1/subscription", Ct)).ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("trialEndsAt").GetDateTimeOffset();

        var extended = await (await admin.PostAsJsonAsync($"{url}/extend-trial", new { days = 7, reason = "Instalação atrasada no piloto" }, Ct))
            .ExpectAsync(HttpStatusCode.OK, _api);
        extended.GetProperty("status").GetString().ShouldBe("TRIALING");
        (extended.GetProperty("trialEndsAt").GetDateTimeOffset() - trialEnd).ShouldBe(TimeSpan.FromDays(7));
        (await (await admin.PostAsJsonAsync($"{url}/extend-trial", new { days = 90, reason = "Muito tempo" }, Ct)).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api))
            .GetProperty("errors").TryGetProperty("days", out _).ShouldBeTrue();
        (await (await admin.PostAsJsonAsync($"{url}/suspend", new { reason = "x" }, Ct)).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api))
            .GetProperty("errors").TryGetProperty("reason", out _).ShouldBeTrue();

        // Suspensa: o estabelecimento fica só para consulta na hora (cache invalidado).
        var suspended = await (await admin.PostAsJsonAsync($"{url}/suspend", new { reason = "Fraude confirmada" }, Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        suspended.GetProperty("canWrite").GetBoolean().ShouldBeFalse();
        await (await s.Client.PostIdempotentAsync("/api/v1/orders", s.Pf(), Guid.NewGuid())).ExpectAsync(HttpStatusCode.PaymentRequired, _api);

        // Suspensa sem nenhum período pago: estender volta ao teste.
        var backToTrial = await (await admin.PostAsJsonAsync($"{url}/extend-trial", new { days = 3, reason = "Nova chance no piloto" }, Ct))
            .ExpectAsync(HttpStatusCode.OK, _api);
        backToTrial.GetProperty("status").GetString().ShouldBe("TRIALING");

        var reactivated = await (await admin.PostAsJsonAsync($"{url}/reactivate", new { reason = "Pagamento confirmado manualmente" }, Ct))
            .ExpectAsync(HttpStatusCode.OK, _api);
        reactivated.GetProperty("status").GetString().ShouldBe("ACTIVE");
        reactivated.GetProperty("currentPeriodEndsAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(27));
        await (await s.Client.PostIdempotentAsync("/api/v1/orders", s.Pf(), Guid.NewGuid())).ExpectAsync(HttpStatusCode.Created, _api);

        // Ativa já teve período pago: não volta ao teste.
        await (await admin.PostAsJsonAsync($"{url}/extend-trial", new { days = 3, reason = "Tentativa inválida" }, Ct)).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);

        (await CountAsync(
            "SELECT count(*) FROM audit_logs a JOIN users u ON u.id = a.actor_user_id WHERE a.tenant_id = @t AND a.origin = 'PLATFORM_ADMIN' AND u.normalized_email = upper(@e) AND a.operation_type IN ('TrialExtended', 'SubscriptionSuspendedByAdmin', 'SubscriptionReactivatedByAdmin')",
            s.TenantId, email)).ShouldBe(4);
        (await CountAsync("SELECT count(*) FROM outbox_messages WHERE tenant_id = @t AND event_type = 'SubscriptionStatusChanged'", s.TenantId, email)).ShouldBe(3);
    }

    [Fact]
    public async Task Reprocess_puts_the_webhook_back_in_the_queue()
    {
        var (email, _) = await AdminAsync();
        var admin = _api.Authenticated(await AdminTokenAsync(email));
        var eventId = $"evt_{Guid.NewGuid():N}";
        var webhook = new HttpRequestMessage(HttpMethod.Post, "/webhooks/asaas")
        {
            Content = new StringContent($$"""{ "id": "{{eventId}}", "event": "PAYMENT_CREATED", "payment": { "id": "pay_1" } }""", Encoding.UTF8, "application/json"),
        };
        webhook.Headers.Add("asaas-access-token", ApiFactory.WebhookToken);
        (await _api.CreateClient().SendAsync(webhook, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var id = await ScalarAsync<Guid>("SELECT id FROM billing_webhook_events WHERE gateway_event_id = @e", eventId);
        await SqlAsync("UPDATE billing_webhook_events SET processed_at = now() WHERE gateway_event_id = @e", eventId);

        (await admin.PostAsync($"/api/v1/admin/billing-webhooks/{id}/reprocess", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // O processador em segundo plano pode reprocessar logo em seguida; o que importa é ter voltado à fila e ser auditado.
        (await CountAsync("SELECT count(*) FROM audit_logs WHERE operation_type = 'BillingWebhookReprocessed' AND entity_id = @t AND origin = 'PLATFORM_ADMIN'", id, email))
            .ShouldBe(1);
        await (await admin.PostAsync($"/api/v1/admin/billing-webhooks/{Guid.NewGuid()}/reprocess", null, Ct)).ExpectAsync(HttpStatusCode.NotFound, _api);
    }

    /// Usuário com estabelecimento próprio marcado como admin de plataforma (a marca só é dada por SQL).
    private async Task<(string Email, JsonElement TenantSession)> AdminAsync()
    {
        var email = $"ops.{Guid.NewGuid():N}@almoseis.app";
        var session = await _api.SignUpAsync(ApiScenarios.SignUpBody(email: email));
        await SqlAsync("UPDATE users SET is_platform_admin = true WHERE normalized_email = upper(@e)", email);
        return (email, session);
    }

    private async Task<string> AdminTokenAsync(string email) =>
        (await (await AdminLogin(_api.CreateClient(), email, Password)).ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("accessToken").GetString()!;

    private static Task<HttpResponseMessage> AdminLogin(HttpClient client, string login, string password) =>
        client.PostAsJsonAsync("/api/v1/auth/admin/login", new { login, password }, Ct);

    private static JsonElement Claims(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
        return JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement.Clone();
    }

    private async Task SqlAsync(string sql, string email)
    {
        await using var connection = new NpgsqlConnection(db.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("e", email);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<T> ScalarAsync<T>(string sql, string email)
    {
        await using var connection = new NpgsqlConnection(db.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("e", email);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<long> CountAsync(string sql, Guid tenantId, string email)
    {
        await using var connection = new NpgsqlConnection(db.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("e", email);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
