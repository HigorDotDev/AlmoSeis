using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Identity;

/// Estabelecimento, configurações, funcionários, convites, senha e PIN (06 §6.3/§6.4) contra a API real.
[Collection(nameof(PostgresCollection))]
public sealed class ManagementFlowTests(PostgresFixture db) : IDisposable
{
    private readonly ApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Tenant_and_settings_are_versioned_and_validated()
    {
        var owner = await _api.SignUpAsync();
        var client = _api.Authenticated(owner.AccessToken());

        var tenant = await (await client.GetAsync("/api/v1/tenant", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        tenant.GetProperty("documentType").GetString().ShouldBe("CNPJ");
        var renamed = await (await client.SendJsonAsync(HttpMethod.Patch, "/api/v1/tenant", new { name = "Marmitaria e Restaurante" }, tenant.GetProperty("version").GetInt32()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        renamed.GetProperty("name").GetString().ShouldBe("Marmitaria e Restaurante");
        (await (await client.SendJsonAsync(HttpMethod.Patch, "/api/v1/tenant", new { timeZone = "Marte/Olympus" }, renamed.GetProperty("version").GetInt32()))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api)).GetProperty("errors").TryGetProperty("timeZone", out _).ShouldBeTrue();

        var settings = await (await client.GetAsync("/api/v1/tenant/settings", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        settings.GetProperty("kiloPricePerKg").ValueKind.ShouldBe(JsonValueKind.Null);
        var updated = await (await client.SendJsonAsync(HttpMethod.Patch, "/api/v1/tenant/settings",
                new { kiloPricePerKg = "62.90", enabledPaymentMethods = new[] { "PIX", "CASH" } }, settings.GetProperty("version").GetInt32()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        updated.GetProperty("kiloPricePerKg").GetString().ShouldBe("62.90");
        updated.GetProperty("enabledPaymentMethods").EnumerateArray().Select(m => m.GetString()).ShouldBe(["CASH", "PIX"]);
        updated.GetProperty("version").GetInt32().ShouldBe(settings.GetProperty("version").GetInt32() + 1);

        (await (await client.SendJsonAsync(HttpMethod.Patch, "/api/v1/tenant/settings", new { enabledPaymentMethods = Array.Empty<string>() }, updated.GetProperty("version").GetInt32()))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api)).GetProperty("errors").TryGetProperty("enabledPaymentMethods", out _).ShouldBeTrue();

        var attendant = _api.Authenticated(await _api.EmployeeTokenAsync(db, owner, "ATTENDANT"));
        await (await attendant.GetAsync("/api/v1/tenant/settings", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await attendant.SendJsonAsync(HttpMethod.Patch, "/api/v1/tenant/settings", new { defaultDeliveryFee = "1.00" }, 2))
            .ExpectAsync(HttpStatusCode.Forbidden, _api);
    }

    [Fact]
    public async Task Invitation_is_accepted_once_with_a_new_account()
    {
        var owner = await _api.SignUpAsync();
        var client = _api.Authenticated(owner.AccessToken());

        var invitation = await (await client.PostAsJsonAsync("/api/v1/employees/invitations", new { roles = new[] { "ATTENDANT" } }, Ct))
            .ExpectAsync(HttpStatusCode.Created, _api);
        var code = invitation.GetProperty("code").GetString()!;
        code.ShouldMatch("^[A-HJ-NP-Z2-9]{6}$");
        invitation.GetProperty("link").GetString().ShouldEndWith(code);
        invitation.GetProperty("status").GetString().ShouldBe("PENDING");

        var anonymous = _api.CreateClient();
        var preview = await (await anonymous.GetAsync($"/api/v1/invitations/{code.ToLowerInvariant()}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        preview.GetProperty("tenantName").GetString().ShouldStartWith("Marmitaria");
        preview.GetProperty("invitedBy").GetString().ShouldNotBeNullOrEmpty();

        var phone = "+55349" + ApiScenarios.RandomDigits(8);
        var accepted = await (await anonymous.PostAsJsonAsync($"/api/v1/invitations/{code}/accept", new
        {
            newAccount = new { fullName = "Bruno Lima", email = (string?)null, phoneNumber = phone, password = "Br-s3nha-123" },
            existingAccount = (object?)null,
            displayName = "Bruno",
            installationId = "inst_bruno",
        }, Ct)).ExpectAsync(HttpStatusCode.Created, _api);
        accepted.GetProperty("me").GetProperty("tenant").GetProperty("id").GetString().ShouldBe(owner.TenantId());
        accepted.GetProperty("me").GetProperty("employee").GetProperty("roles").EnumerateArray().Single().GetString().ShouldBe("ATTENDANT");

        // O novo funcionário já usa o app; o convite não vale mais.
        await (await _api.Authenticated(accepted.AccessToken()).GetAsync("/api/v1/menus", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await anonymous.GetAsync($"/api/v1/invitations/{code}", Ct)).ExpectAsync(HttpStatusCode.NotFound, _api);
        var list = await (await client.GetAsync("/api/v1/employees/invitations?status=ACCEPTED", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        list.GetProperty("items").GetArrayLength().ShouldBe(1);

        var employees = await (await client.GetAsync("/api/v1/employees", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        var bruno = employees.GetProperty("items").EnumerateArray().Single(e => e.GetProperty("displayName").GetString() == "Bruno");
        bruno.GetProperty("contact").GetProperty("phoneNumber").GetString().ShouldBe(phone);

        var other = await (await client.PostAsJsonAsync("/api/v1/employees/invitations", new { roles = new[] { "KITCHEN" } }, Ct)).ExpectAsync(HttpStatusCode.Created, _api);
        var otherId = other.GetProperty("id").GetString();
        (await client.PostAsync($"/api/v1/employees/invitations/{otherId}/revoke", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await client.PostAsync($"/api/v1/employees/invitations/{otherId}/revoke", null, Ct)).ExpectAsync(HttpStatusCode.Conflict, _api))
            .GetProperty("errorCode").GetString().ShouldBe("InvitationNotPending");
    }

    [Fact]
    public async Task Existing_account_joins_another_tenant_by_invitation()
    {
        var owner = await _api.SignUpAsync();
        var email = $"carla.{Guid.NewGuid():N}@example.com";
        await _api.SignUpAsync(ApiScenarios.SignUpBody(email: email)); // Carla já tem o próprio estabelecimento

        var invitation = await (await _api.Authenticated(owner.AccessToken()).PostAsJsonAsync("/api/v1/employees/invitations", new { roles = new[] { "CASHIER" } }, Ct))
            .ExpectAsync(HttpStatusCode.Created, _api);
        var accepted = await (await _api.CreateClient().PostAsJsonAsync($"/api/v1/invitations/{invitation.GetProperty("code").GetString()}/accept", new
        {
            newAccount = (object?)null,
            existingAccount = new { login = email, password = "S3nha-forte!" },
            displayName = "Carla",
            installationId = "inst_carla",
        }, Ct)).ExpectAsync(HttpStatusCode.Created, _api);

        accepted.GetProperty("me").GetProperty("tenant").GetProperty("id").GetString().ShouldBe(owner.TenantId());
        accepted.GetProperty("me").GetProperty("memberships").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Role_change_and_deactivation_revoke_access_and_keep_an_owner()
    {
        var owner = await _api.SignUpAsync();
        var client = _api.Authenticated(owner.AccessToken());
        var cashierToken = await _api.EmployeeTokenAsync(db, owner, "CASHIER");
        var cashier = _api.Authenticated(cashierToken);
        var me = await (await cashier.GetAsync("/api/v1/auth/me", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        var cashierId = me.GetProperty("employee").GetProperty("id").GetString();

        var employee = await (await client.GetAsync($"/api/v1/employees/{cashierId}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        var changed = await (await client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/employees/{cashierId}", new { roles = new[] { "ATTENDANT" } }, employee.GetProperty("version").GetInt32()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        changed.GetProperty("roles").EnumerateArray().Single().GetString().ShouldBe("ATTENDANT");

        // Novo security stamp: o token antigo para na hora (cache invalidado).
        await (await cashier.GetAsync("/api/v1/auth/me", Ct)).ExpectAsync(HttpStatusCode.Unauthorized, _api);
        (await CountAsync("SELECT count(*) FROM outbox_messages WHERE event_type = 'AccessRevoked' AND payload->>'entityId' = @e", cashierId!)).ShouldBe(1);

        var deactivated = await (await client.SendJsonAsync(HttpMethod.Post, $"/api/v1/employees/{cashierId}/deactivate", null, changed.GetProperty("version").GetInt32()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        deactivated.GetProperty("status").GetString().ShouldBe("INACTIVE");
        deactivated.GetProperty("deactivatedBy").GetProperty("displayName").GetString().ShouldNotBeNullOrEmpty();
        var reactivated = await (await client.SendJsonAsync(HttpMethod.Post, $"/api/v1/employees/{cashierId}/reactivate", null, deactivated.GetProperty("version").GetInt32()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        reactivated.GetProperty("status").GetString().ShouldBe("ACTIVE");

        // O único proprietário não perde o perfil nem é desativado.
        var ownerId = owner.GetProperty("me").GetProperty("employee").GetProperty("id").GetString();
        var self = await (await client.GetAsync($"/api/v1/employees/{ownerId}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        (await (await client.SendJsonAsync(HttpMethod.Post, $"/api/v1/employees/{ownerId}/deactivate", null, self.GetProperty("version").GetInt32()))
            .ExpectAsync(HttpStatusCode.Conflict, _api)).GetProperty("errorCode").GetString().ShouldBe("LastOwner");
        (await (await client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/employees/{ownerId}", new { roles = new[] { "MANAGER" } }, self.GetProperty("version").GetInt32()))
            .ExpectAsync(HttpStatusCode.Conflict, _api)).GetProperty("errorCode").GetString().ShouldBe("LastOwner");
    }

    [Fact]
    public async Task Password_reset_code_changes_the_password_and_ends_sessions()
    {
        var owner = await _api.SignUpAsync();
        var client = _api.Authenticated(owner.AccessToken());
        var email = $"diego.{Guid.NewGuid():N}@example.com";
        var diegoToken = await EmployeeWithEmailAsync(owner, email);
        var diegoId = (await (await _api.Authenticated(diegoToken).GetAsync("/api/v1/auth/me", Ct)).ExpectAsync(HttpStatusCode.OK, _api))
            .GetProperty("employee").GetProperty("id").GetString();

        var code = (await (await client.PostAsync($"/api/v1/employees/{diegoId}/password-reset-code", null, Ct)).ExpectAsync(HttpStatusCode.Created, _api))
            .GetProperty("code").GetString();
        var anonymous = _api.CreateClient();

        (await (await anonymous.PostAsJsonAsync("/api/v1/auth/password/reset", new { code, login = "outra@example.com", newPassword = "Outra-s3nha!" }, Ct))
            .ExpectAsync(HttpStatusCode.Conflict, _api)).GetProperty("errorCode").GetString().ShouldBe("InvitationNotPending");
        (await anonymous.PostAsJsonAsync("/api/v1/auth/password/reset", new { code, login = email, newPassword = "Outra-s3nha!" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await anonymous.PostAsJsonAsync("/api/v1/auth/password/reset", new { code, login = email, newPassword = "Mais-uma-s3nha" }, Ct))
            .ExpectAsync(HttpStatusCode.Conflict, _api)).GetProperty("errorCode").GetString().ShouldBe("InvitationNotPending");

        await (await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { login = email, password = "Outra-s3nha!", installationId = "inst_d", preferredTenantId = owner.TenantId() }, Ct))
            .ExpectAsync(HttpStatusCode.OK, _api);
        (await CountAsync("SELECT count(*) FROM refresh_tokens t JOIN users u ON u.id = t.user_id WHERE u.normalized_email = upper(@e) AND t.revoked_at IS NULL AND t.installation_id = 'inst_func'", email))
            .ShouldBe(0);
    }

    [Fact]
    public async Task Own_pin_follows_the_policy()
    {
        var owner = await _api.SignUpAsync();
        var client = _api.Authenticated(owner.AccessToken());

        (await (await client.SendJsonAsync(HttpMethod.Put, "/api/v1/auth/me/pin", new { pin = "1234", currentPassword = "S3nha-forte!" }))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api)).GetProperty("errors").TryGetProperty("pin", out _).ShouldBeTrue();
        (await (await client.SendJsonAsync(HttpMethod.Put, "/api/v1/auth/me/pin", new { pin = "4821", currentPassword = "errada" }))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api)).GetProperty("errors").TryGetProperty("currentPassword", out _).ShouldBeTrue();
        (await client.SendJsonAsync(HttpMethod.Put, "/api/v1/auth/me/pin", new { pin = "4821", currentPassword = "S3nha-forte!" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await (await client.GetAsync("/api/v1/auth/me", Ct)).ExpectAsync(HttpStatusCode.OK, _api))
            .GetProperty("employee").GetProperty("hasPin").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Own_password_change_keeps_this_installation_and_revokes_the_others()
    {
        var owner = await _api.SignUpAsync();
        var email = owner.GetProperty("me").GetProperty("user").GetProperty("email").GetString()!;
        var anonymous = _api.CreateClient();
        var here = await (await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { login = email, password = "S3nha-forte!", installationId = "inst_here" }, Ct))
            .ExpectAsync(HttpStatusCode.OK, _api);
        var other = await (await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { login = email, password = "S3nha-forte!", installationId = "inst_other" }, Ct))
            .ExpectAsync(HttpStatusCode.OK, _api);
        var client = _api.Authenticated(here.AccessToken());

        (await (await client.SendJsonAsync(HttpMethod.Put, "/api/v1/auth/me/password", new { currentPassword = "errada", newPassword = "Outra-s3nha!" }))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api)).GetProperty("errors").TryGetProperty("currentPassword", out _).ShouldBeTrue();
        (await (await client.SendJsonAsync(HttpMethod.Put, "/api/v1/auth/me/password", new { currentPassword = "S3nha-forte!", newPassword = "curta" }))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api)).GetProperty("errors").TryGetProperty("newPassword", out _).ShouldBeTrue();
        (await (await client.SendJsonAsync(HttpMethod.Put, "/api/v1/auth/me/password", new { currentPassword = "S3nha-forte!", newPassword = "S3nha-forte!" }))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api)).GetProperty("errors").TryGetProperty("newPassword", out _).ShouldBeTrue();
        (await client.SendJsonAsync(HttpMethod.Put, "/api/v1/auth/me/password", new { currentPassword = "S3nha-forte!", newPassword = "Outra-s3nha!" }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await (await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = RefreshToken(here), installationId = "inst_here" }, Ct))
            .ExpectAsync(HttpStatusCode.OK, _api);
        (await (await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = RefreshToken(other), installationId = "inst_other" }, Ct))
            .ExpectAsync(HttpStatusCode.Unauthorized, _api)).GetProperty("errorCode").GetString().ShouldBe("InvalidRefreshToken");

        (await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { login = email, password = "S3nha-forte!", installationId = "inst_here" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await (await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { login = email, password = "Outra-s3nha!", installationId = "inst_here" }, Ct))
            .ExpectAsync(HttpStatusCode.OK, _api);
        (await CountAsync("SELECT count(*) FROM audit_logs a JOIN users u ON u.id = a.entity_id WHERE u.normalized_email = upper(@e) AND a.operation_type = 'PasswordChanged'", email))
            .ShouldBe(1);
    }

    private static string RefreshToken(JsonElement authResult) =>
        authResult.GetProperty("tokens").GetProperty("refreshToken").GetString()!;

    private async Task<string> EmployeeWithEmailAsync(JsonElement owner, string email)
    {
        var invitation = await (await _api.Authenticated(owner.AccessToken()).PostAsJsonAsync("/api/v1/employees/invitations", new { roles = new[] { "KITCHEN" } }, Ct))
            .ExpectAsync(HttpStatusCode.Created, _api);
        var accepted = await (await _api.CreateClient().PostAsJsonAsync($"/api/v1/invitations/{invitation.GetProperty("code").GetString()}/accept", new
        {
            newAccount = new { fullName = "Diego Souza", email, phoneNumber = (string?)null, password = "S3nha-forte!" },
            existingAccount = (object?)null,
            displayName = "Diego",
            installationId = "inst_func",
        }, Ct)).ExpectAsync(HttpStatusCode.Created, _api);
        return accepted.AccessToken();
    }

    private async Task<long> CountAsync(string sql, string parameter)
    {
        await using var connection = new Npgsql.NpgsqlConnection(db.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("e", parameter);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
