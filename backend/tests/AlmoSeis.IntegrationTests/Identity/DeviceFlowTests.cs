using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlmoSeis.Infrastructure.Email;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Identity;

/// Aparelho compartilhado, login por PIN (ADR-0027) e "esqueci a senha" (ADR-0040) contra a API real.
[Collection(nameof(PostgresCollection))]
public sealed class DeviceFlowTests(PostgresFixture db) : IDisposable
{
    private const string Password = "S3nha-forte!";
    private readonly FakeEmail _email = new();
    private ApiFactory? _api;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApiFactory Api => _api ??= new ApiFactory(db, s => s.AddSingleton<IEmailSender>(_email));

    public void Dispose() => _api?.Dispose();

    [Fact]
    public async Task Shared_tablet_lists_pin_employees_and_logs_in_by_pin()
    {
        var (owner, tablet, installation) = await OwnerOnTabletAsync();
        var kitchenToken = await Api.EmployeeTokenAsync(db, owner, "KITCHEN");
        await SetPinAsync(Api.Authenticated(kitchenToken), "4821");
        var kitchenId = await EmployeeIdAsync(kitchenToken);

        (await (await tablet.PostAsJsonAsync("/api/v1/devices/shared", new { installationId = "inst_outro", name = "Tablet", platform = "ANDROID" }, Ct))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, Api)).GetProperty("errors").TryGetProperty("installationId", out _).ShouldBeTrue();
        var device = await (await tablet.PostAsJsonAsync("/api/v1/devices/shared", new { installationId = installation, name = "Tablet cozinha", platform = "ANDROID" }, Ct))
            .ExpectAsync(HttpStatusCode.Created, Api);
        var secret = device.GetProperty("deviceSecret").GetString()!;
        secret.ShouldStartWith("ds_");

        (await (await DeviceRequest(HttpMethod.Get, $"/api/v1/devices/shared/{installation}/employees", owner.TenantId(), "ds_errado"))
            .ExpectAsync(HttpStatusCode.Unauthorized, Api)).GetProperty("errorCode").GetString().ShouldBe("InvalidDeviceSecret");
        var list = await (await DeviceRequest(HttpMethod.Get, $"/api/v1/devices/shared/{installation}/employees", owner.TenantId(), secret))
            .ExpectAsync(HttpStatusCode.OK, Api);
        list.GetProperty("device").GetProperty("name").GetString().ShouldBe("Tablet cozinha");
        list.GetProperty("employees").EnumerateArray().Select(e => e.GetProperty("id").GetString()).ShouldBe([kitchenId]); // só quem tem PIN

        var session = await (await PinLogin(owner.TenantId(), installation, kitchenId, "4821", secret)).ExpectAsync(HttpStatusCode.OK, Api);
        session.GetProperty("me").GetProperty("sharedDevice").GetProperty("name").GetString().ShouldBe("Tablet cozinha");
        await (await Api.Authenticated(session.AccessToken()).GetAsync("/api/v1/production/orders", Ct)).ExpectAsync(HttpStatusCode.Conflict, Api); // sem expediente, mas autenticado

        // Revogar o aparelho derruba o login por PIN e as sessões emitidas nele.
        var deviceId = device.GetProperty("id").GetString();
        (await tablet.PostAsync($"/api/v1/devices/shared/{deviceId}/revoke", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await PinLogin(owner.TenantId(), installation, kitchenId, "4821", secret)).ExpectAsync(HttpStatusCode.Unauthorized, Api))
            .GetProperty("errorCode").GetString().ShouldBe("InvalidDeviceSecret");
        await (await Api.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh",
                new { refreshToken = session.GetProperty("tokens").GetProperty("refreshToken").GetString(), installationId = installation }, Ct))
            .ExpectAsync(HttpStatusCode.Unauthorized, Api);
    }

    [Fact]
    public async Task Wrong_pins_lock_the_employee_for_a_while()
    {
        var (owner, tablet, installation) = await OwnerOnTabletAsync();
        var cashierToken = await Api.EmployeeTokenAsync(db, owner, "CASHIER");
        await SetPinAsync(Api.Authenticated(cashierToken), "907135");
        var cashierId = await EmployeeIdAsync(cashierToken);
        var secret = (await (await tablet.PostAsJsonAsync("/api/v1/devices/shared", new { installationId = installation, name = "Caixa", platform = "IOS" }, Ct))
            .ExpectAsync(HttpStatusCode.Created, Api)).GetProperty("deviceSecret").GetString()!;

        for (var left = 4; left >= 1; left--)
        {
            (await (await PinLogin(owner.TenantId(), installation, cashierId, "111999", secret)).ExpectAsync(HttpStatusCode.Unauthorized, Api))
                .GetProperty("attemptsLeft").GetInt32().ShouldBe(left);
        }

        (await (await PinLogin(owner.TenantId(), installation, cashierId, "111999", secret)).ExpectAsync((HttpStatusCode)423, Api))
            .GetProperty("lockedUntil").ValueKind.ShouldBe(JsonValueKind.String);
        // Bloqueado: nem o PIN certo entra.
        (await (await PinLogin(owner.TenantId(), installation, cashierId, "907135", secret)).ExpectAsync((HttpStatusCode)423, Api))
            .GetProperty("errorCode").GetString().ShouldBe("PinLocked");

        // Registrar a mesma instalação de novo troca o segredo; o antigo deixa de valer.
        var renewed = (await (await tablet.PostAsJsonAsync("/api/v1/devices/shared", new { installationId = installation, name = "Caixa", platform = "IOS" }, Ct))
            .ExpectAsync(HttpStatusCode.Created, Api)).GetProperty("deviceSecret").GetString()!;
        renewed.ShouldNotBe(secret);
        await (await DeviceRequest(HttpMethod.Get, $"/api/v1/devices/shared/{installation}/employees", owner.TenantId(), secret)).ExpectAsync(HttpStatusCode.Unauthorized, Api);
        (await (await tablet.GetAsync("/api/v1/devices/shared", Ct)).ExpectAsync(HttpStatusCode.OK, Api))
            .GetProperty("items").EnumerateArray().Count(d => d.GetProperty("revokedAt").ValueKind == JsonValueKind.Null).ShouldBe(1);
    }

    [Fact]
    public async Task Forgot_password_mails_a_single_use_link()
    {
        var email = $"ana.{Guid.NewGuid():N}@example.com";
        await Api.SignUpAsync(ApiScenarios.SignUpBody(email: email));
        var anonymous = Api.CreateClient();

        (await anonymous.PostAsJsonAsync("/api/v1/auth/password/forgot", new { email = "ninguem@example.com" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        _email.Sent.ShouldNotContain(m => m.To == "ninguem@example.com");
        (await anonymous.PostAsJsonAsync("/api/v1/auth/password/forgot", new { email = email.ToUpperInvariant() }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var mail = _email.Sent.Single(m => m.To == email);
        var token = Uri.UnescapeDataString(mail.Body.Split("token=")[1].Split('\n')[0].Trim());
        (await anonymous.PostAsJsonAsync("/api/v1/auth/password/reset", new { emailToken = token, newPassword = "Nova-s3nha!" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await (await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { login = email, password = "Nova-s3nha!", installationId = "inst_x" }, Ct))
            .ExpectAsync(HttpStatusCode.OK, Api);

        // Uso único: a senha nova mudou o security stamp do token.
        (await (await anonymous.PostAsJsonAsync("/api/v1/auth/password/reset", new { emailToken = token, newPassword = "Outra-s3nha!" }, Ct))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, Api)).GetProperty("errors").TryGetProperty("emailToken", out _).ShouldBeTrue();
    }

    /// Dono logado no próprio tablet: o registro do aparelho exige a instalação do token.
    private async Task<(JsonElement Owner, HttpClient Tablet, string Installation)> OwnerOnTabletAsync()
    {
        var email = $"dona.{Guid.NewGuid():N}@example.com";
        var owner = await Api.SignUpAsync(ApiScenarios.SignUpBody(email: email));
        var installation = $"inst_tab_{Guid.NewGuid():N}"[..30];
        var login = await (await Api.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { login = email, password = Password, installationId = installation }, Ct))
            .ExpectAsync(HttpStatusCode.OK, Api);
        return (owner, Api.Authenticated(login.AccessToken()), installation);
    }

    private async Task SetPinAsync(HttpClient client, string pin) =>
        (await client.SendJsonAsync(HttpMethod.Put, "/api/v1/auth/me/pin", new { pin, currentPassword = Password })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

    private async Task<string> EmployeeIdAsync(string token) =>
        (await (await Api.Authenticated(token).GetAsync("/api/v1/auth/me", Ct)).ExpectAsync(HttpStatusCode.OK, Api))
            .GetProperty("employee").GetProperty("id").GetString()!;

    private Task<HttpResponseMessage> PinLogin(string tenantId, string installation, string employeeId, string pin, string secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/pin-login")
        {
            Content = JsonContent.Create(new { tenantId, installationId = installation, employeeId, pin }),
        };
        request.Headers.Add("X-Device-Secret", secret);
        return Api.CreateClient().SendAsync(request, Ct);
    }

    private Task<HttpResponseMessage> DeviceRequest(HttpMethod method, string url, string tenantId, string secret)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Tenant-Id", tenantId);
        request.Headers.Add("X-Device-Secret", secret);
        return Api.CreateClient().SendAsync(request, Ct);
    }

    private sealed class FakeEmail : IEmailSender
    {
        public ConcurrentBag<(string To, string Subject, string Body)> Sent { get; } = [];

        public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
        {
            Sent.Add((to, subject, body));
            return Task.CompletedTask;
        }
    }
}
