using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Identity;

/// App do restaurante no navegador (ADR-0053, 06 §6.3 "App no navegador"): refresh e segredo do aparelho em cookies
/// HttpOnly, conferência de Origin nas rotas que leem cookie, CORS e o mobile sem mudança.
[Collection(nameof(PostgresCollection))]
public sealed class WebSessionFlowTests(PostgresFixture db) : IDisposable
{
    private const string Password = "S3nha-forte!";
    private const string Refresh = "__Secure-almoseis-rt";
    private const string DeviceSecret = "__Secure-almoseis-ds";
    private readonly ApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Web_login_puts_the_refresh_token_only_in_an_http_only_cookie()
    {
        var (email, installation, _) = await AccountAsync();

        var response = await Web(HttpMethod.Post, "/api/v1/auth/login", new { login = email, password = Password, installationId = installation });
        var body = await response.ExpectAsync(HttpStatusCode.OK, _api);

        var tokens = body.GetProperty("tokens");
        tokens.GetProperty("refreshToken").ValueKind.ShouldBe(JsonValueKind.Null);
        tokens.GetProperty("accessToken").GetString().ShouldNotBeNullOrEmpty();
        tokens.GetProperty("refreshTokenExpiresAt").ValueKind.ShouldBe(JsonValueKind.String);

        var cookie = SetCookie(response, Refresh)!;
        cookie.ShouldStartWith(Refresh + "=rt_");
        foreach (var attribute in new[] { "path=/api/v1/auth", "secure", "samesite=strict", "httponly" })
        {
            cookie.ShouldContain(attribute, Case.Insensitive);
        }

        cookie.ShouldNotContain("domain", Case.Insensitive);
        MaxAge(cookie).ShouldBeInRange(30 * 86400 - 60, 30 * 86400);

        // CORS da origem do app, com credenciais.
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(ApiFactory.WebOrigin);
        response.Headers.GetValues("Access-Control-Allow-Credentials").Single().ShouldBe("true");
    }

    [Fact]
    public async Task Web_refresh_reads_and_rotates_the_cookie_and_reuse_revokes_the_family()
    {
        var (first, installation) = await WebLoginAsync();

        var rotated = await Web(HttpMethod.Post, "/api/v1/auth/refresh", new { installationId = installation }, cookie: first);
        (await rotated.ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("refreshToken").ValueKind.ShouldBe(JsonValueKind.Null);
        var second = CookieValue(rotated, Refresh)!;
        second.ShouldNotBe(first);

        // Reuso do cookie já trocado (duas abas sem o lock): 401, o cookie é apagado e a família inteira cai.
        var reuse = await Web(HttpMethod.Post, "/api/v1/auth/refresh", new { installationId = installation }, cookie: first);
        (await reuse.ExpectAsync(HttpStatusCode.Unauthorized, _api)).GetProperty("errorCode").GetString().ShouldBe("InvalidRefreshToken");
        MaxAge(SetCookie(reuse, Refresh)!).ShouldBe(0);

        (await (await Web(HttpMethod.Post, "/api/v1/auth/refresh", new { installationId = installation }, cookie: second))
            .ExpectAsync(HttpStatusCode.Unauthorized, _api)).GetProperty("errorCode").GetString().ShouldBe("InvalidRefreshToken");
    }

    [Fact]
    public async Task Web_refresh_requires_an_allowed_origin_and_the_cookie()
    {
        var (cookie, installation) = await WebLoginAsync();
        var body = new { installationId = installation };

        foreach (var origin in new[] { null, "https://evil.example", "http://localhost:8083" })
        {
            var rejected = await Web(HttpMethod.Post, "/api/v1/auth/refresh", body, cookie: cookie, origin: origin);
            (await rejected.ExpectAsync(HttpStatusCode.Forbidden, _api)).GetProperty("errorCode").GetString().ShouldBe("OriginNotAllowed");
            SetCookie(rejected, Refresh).ShouldBeNull();
        }

        // Refresh no corpo vindo da web é recusado; sem cookie, a sessão não existe.
        (await (await Web(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = "rt_qualquer", installationId = installation }, cookie: cookie))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api)).GetProperty("errors").TryGetProperty("refreshToken", out _).ShouldBeTrue();
        (await (await Web(HttpMethod.Post, "/api/v1/auth/refresh", body))
            .ExpectAsync(HttpStatusCode.Unauthorized, _api)).GetProperty("errorCode").GetString().ShouldBe("InvalidRefreshToken");

        // As recusas acima não consumiram o cookie: ele ainda renova.
        await (await Web(HttpMethod.Post, "/api/v1/auth/refresh", body, cookie: cookie)).ExpectAsync(HttpStatusCode.OK, _api);
    }

    [Fact]
    public async Task Mobile_keeps_the_refresh_token_in_the_body_and_ignores_cookies()
    {
        var (email, installation, _) = await AccountAsync();

        foreach (var platform in new[] { null, "ANDROID", "IOS" })
        {
            var login = await Send(HttpMethod.Post, "/api/v1/auth/login", new { login = email, password = Password, installationId = installation }, platform: platform);
            var refresh = (await login.ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("tokens").GetProperty("refreshToken").GetString()!;
            refresh.ShouldStartWith("rt_");
            login.Headers.Contains("Set-Cookie").ShouldBeFalse();

            var rotated = await Send(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = refresh, installationId = installation }, platform: platform);
            (await rotated.ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("refreshToken").GetString()!.ShouldStartWith("rt_");
            rotated.Headers.Contains("Set-Cookie").ShouldBeFalse();
        }

        // Um cookie de sessão sem `X-App-Platform: WEB` não é lido: o mobile continua exigindo o refresh no corpo.
        var (webCookie, webInstallation) = await WebLoginAsync();
        (await (await Send(HttpMethod.Post, "/api/v1/auth/refresh", new { installationId = webInstallation }, platform: "ANDROID", cookie: $"{Refresh}={webCookie}"))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api)).GetProperty("errors").TryGetProperty("refreshToken", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Web_logout_revokes_the_cookie_session_and_clears_the_cookie()
    {
        var (cookie, installation) = await WebLoginAsync();

        var logout = await Web(HttpMethod.Post, "/api/v1/auth/logout", new { }, cookie: cookie);
        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var cleared = SetCookie(logout, Refresh)!;
        cleared.ShouldStartWith(Refresh + "=;");
        MaxAge(cleared).ShouldBe(0);
        cleared.ShouldContain("path=/api/v1/auth", Case.Insensitive);

        await (await Web(HttpMethod.Post, "/api/v1/auth/refresh", new { installationId = installation }, cookie: cookie)).ExpectAsync(HttpStatusCode.Unauthorized, _api);

        // Sem corpo e sem cookie, também 204 (só apaga). Refresh no corpo vindo da web → 422.
        (await Web(HttpMethod.Post, "/api/v1/auth/logout", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Web(HttpMethod.Post, "/api/v1/auth/logout", new { refreshToken = "rt_x" })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await (await Web(HttpMethod.Post, "/api/v1/auth/logout", new { }, cookie: cookie, origin: "https://evil.example"))
            .ExpectAsync(HttpStatusCode.Forbidden, _api)).GetProperty("errorCode").GetString().ShouldBe("OriginNotAllowed");
    }

    [Fact]
    public async Task Web_shared_device_keeps_the_secret_in_a_cookie_for_the_employee_list_and_pin_login()
    {
        var (email, installation, owner) = await AccountAsync();
        var kitchenToken = await _api.EmployeeTokenAsync(db, owner, "KITCHEN");
        (await _api.Authenticated(kitchenToken).SendJsonAsync(HttpMethod.Put, "/api/v1/auth/me/pin", new { pin = "4821", currentPassword = Password }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var kitchenId = (await (await _api.Authenticated(kitchenToken).GetAsync("/api/v1/auth/me", Ct)).ExpectAsync(HttpStatusCode.OK, _api))
            .GetProperty("employee").GetProperty("id").GetString()!;

        var ownerLogin = await (await Web(HttpMethod.Post, "/api/v1/auth/login", new { login = email, password = Password, installationId = installation }))
            .ExpectAsync(HttpStatusCode.OK, _api);
        var bearer = ownerLogin.AccessToken();

        // `platform` e `X-App-Platform` precisam concordar.
        (await (await Web(HttpMethod.Post, "/api/v1/devices/shared", new { installationId = installation, name = "Balcão", platform = "ANDROID" }, bearer: bearer))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api)).GetProperty("errors").TryGetProperty("platform", out _).ShouldBeTrue();
        (await (await Send(HttpMethod.Post, "/api/v1/devices/shared", new { installationId = installation, name = "Balcão", platform = "WEB" }, platform: "ANDROID", bearer: bearer))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api)).GetProperty("errors").TryGetProperty("platform", out _).ShouldBeTrue();

        var register = await Web(HttpMethod.Post, "/api/v1/devices/shared", new { installationId = installation, name = "Balcão", platform = "WEB" }, bearer: bearer);
        var device = await register.ExpectAsync(HttpStatusCode.Created, _api);
        device.GetProperty("deviceSecret").ValueKind.ShouldBe(JsonValueKind.Null);
        device.GetProperty("platform").GetString().ShouldBe("WEB");
        var dsCookie = SetCookie(register, DeviceSecret)!;
        dsCookie.ShouldContain("httponly", Case.Insensitive);
        dsCookie.ShouldContain("path=/api/v1/auth", Case.Insensitive);
        MaxAge(dsCookie).ShouldBe(400 * 86400);
        var secret = CookieValue(register, DeviceSecret)!;
        secret.ShouldStartWith("ds_");

        var employeesUrl = $"/api/v1/auth/shared-device/employees?installationId={installation}";
        var list = await (await Web(HttpMethod.Get, employeesUrl, null, deviceSecret: secret, tenantId: owner.TenantId())).ExpectAsync(HttpStatusCode.OK, _api);
        list.GetProperty("device").GetProperty("name").GetString().ShouldBe("Balcão");
        list.GetProperty("employees").EnumerateArray().Select(e => e.GetProperty("id").GetString()).ShouldBe([kitchenId]);
        (await (await Web(HttpMethod.Get, employeesUrl, null, deviceSecret: secret, tenantId: owner.TenantId(), origin: null))
            .ExpectAsync(HttpStatusCode.Forbidden, _api)).GetProperty("errorCode").GetString().ShouldBe("OriginNotAllowed");

        var pinLogin = await Web(HttpMethod.Post, "/api/v1/auth/pin-login", new { tenantId = owner.TenantId(), installationId = installation, employeeId = kitchenId, pin = "4821" }, deviceSecret: secret);
        var session = await pinLogin.ExpectAsync(HttpStatusCode.OK, _api);
        session.GetProperty("tokens").GetProperty("refreshToken").ValueKind.ShouldBe(JsonValueKind.Null);
        session.GetProperty("me").GetProperty("sharedDevice").GetProperty("name").GetString().ShouldBe("Balcão");
        CookieValue(pinLogin, Refresh)!.ShouldStartWith("rt_");
        CookieValue(pinLogin, DeviceSecret).ShouldBe(secret); // reemitido: renova os 400 dias

        // Revogado no servidor: a lista responde 401 e apaga o cookie do segredo.
        var deviceId = device.GetProperty("id").GetString();
        (await _api.Authenticated(bearer).PostAsync($"/api/v1/devices/shared/{deviceId}/revoke", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var revoked = await Web(HttpMethod.Get, employeesUrl, null, deviceSecret: secret, tenantId: owner.TenantId());
        (await revoked.ExpectAsync(HttpStatusCode.Unauthorized, _api)).GetProperty("errorCode").GetString().ShouldBe("InvalidDeviceSecret");
        MaxAge(SetCookie(revoked, DeviceSecret)!).ShouldBe(0);
        var pinRevoked = await Web(HttpMethod.Post, "/api/v1/auth/pin-login", new { tenantId = owner.TenantId(), installationId = installation, employeeId = kitchenId, pin = "4821" }, deviceSecret: secret);
        (await pinRevoked.ExpectAsync(HttpStatusCode.Unauthorized, _api)).GetProperty("errorCode").GetString().ShouldBe("InvalidDeviceSecret");
        MaxAge(SetCookie(pinRevoked, DeviceSecret)!).ShouldBe(0);

        // "Deixar de usar como compartilhado" no navegador.
        var forget = await Web(HttpMethod.Delete, "/api/v1/auth/shared-device", null, deviceSecret: secret);
        forget.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        MaxAge(SetCookie(forget, DeviceSecret)!).ShouldBe(0);
    }

    [Fact]
    public async Task Web_signup_invitation_accept_and_switch_tenant_put_the_refresh_in_the_cookie_and_switch_revokes_the_previous_one()
    {
        // Autocadastro pela web.
        var signup = await Web(HttpMethod.Post, "/api/v1/auth/signup", ApiScenarios.SignUpBody());
        var owner = await signup.ExpectAsync(HttpStatusCode.Created, _api);
        owner.GetProperty("tokens").GetProperty("refreshToken").ValueKind.ShouldBe(JsonValueKind.Null);
        CookieValue(signup, Refresh)!.ShouldStartWith("rt_");

        // Carla já tem o próprio estabelecimento e aceita, pela web, o convite de outro.
        var email = $"carla.{Guid.NewGuid():N}@example.com";
        var carlaOwn = await _api.SignUpAsync(ApiScenarios.SignUpBody(email: email));
        var invitation = await (await _api.Authenticated(owner.AccessToken()).PostAsJsonAsync("/api/v1/employees/invitations", new { roles = new[] { "CASHIER" } }, Ct))
            .ExpectAsync(HttpStatusCode.Created, _api);
        var installation = Guid.NewGuid().ToString();
        var accept = await Web(HttpMethod.Post, $"/api/v1/invitations/{invitation.GetProperty("code").GetString()}/accept", new
        {
            newAccount = (object?)null,
            existingAccount = new { login = email, password = Password },
            displayName = "Carla",
            installationId = installation,
        });
        var accepted = await accept.ExpectAsync(HttpStatusCode.Created, _api);
        accepted.GetProperty("tokens").GetProperty("refreshToken").ValueKind.ShouldBe(JsonValueKind.Null);
        var acceptCookie = CookieValue(accept, Refresh)!;
        acceptCookie.ShouldStartWith("rt_");

        // Troca para o estabelecimento dela: cookie novo, e o refresh do aceite (mesma instalação) deixa de valer.
        var switched = await Web(HttpMethod.Post, "/api/v1/auth/switch-tenant", new { tenantId = carlaOwn.TenantId() }, bearer: accepted.AccessToken());
        var session = await switched.ExpectAsync(HttpStatusCode.OK, _api);
        session.GetProperty("me").GetProperty("tenant").GetProperty("id").GetString().ShouldBe(carlaOwn.TenantId());
        session.GetProperty("tokens").GetProperty("refreshToken").ValueKind.ShouldBe(JsonValueKind.Null);
        var switchCookie = CookieValue(switched, Refresh)!;
        switchCookie.ShouldNotBe(acceptCookie);

        (await (await Web(HttpMethod.Post, "/api/v1/auth/refresh", new { installationId = installation }, cookie: acceptCookie))
            .ExpectAsync(HttpStatusCode.Unauthorized, _api)).GetProperty("errorCode").GetString().ShouldBe("InvalidRefreshToken");
        await (await Web(HttpMethod.Post, "/api/v1/auth/refresh", new { installationId = installation }, cookie: switchCookie)).ExpectAsync(HttpStatusCode.OK, _api);
    }

    [Fact]
    public async Task Mobile_switch_tenant_revokes_the_previous_refresh_of_the_installation_only()
    {
        var email = $"duda.{Guid.NewGuid():N}@example.com";
        var own = await _api.SignUpAsync(ApiScenarios.SignUpBody(email: email));
        var other = await _api.SignUpAsync();
        var invitation = await (await _api.Authenticated(other.AccessToken()).PostAsJsonAsync("/api/v1/employees/invitations", new { roles = new[] { "ATTENDANT" } }, Ct))
            .ExpectAsync(HttpStatusCode.Created, _api);
        var accepted = await (await Send(HttpMethod.Post, $"/api/v1/invitations/{invitation.GetProperty("code").GetString()}/accept", new
        {
            newAccount = (object?)null,
            existingAccount = new { login = email, password = Password },
            displayName = "Duda",
            installationId = "inst_duda_phone",
        }, platform: "ANDROID")).ExpectAsync(HttpStatusCode.Created, _api);
        var previous = accepted.GetProperty("tokens").GetProperty("refreshToken").GetString()!;

        // Outra instalação da mesma pessoa (o tablet) não é afetada pela troca feita no celular.
        var tablet = await (await Send(HttpMethod.Post, "/api/v1/auth/login", new { login = email, password = Password, installationId = "inst_duda_tablet" }, platform: "ANDROID"))
            .ExpectAsync(HttpStatusCode.OK, _api);

        var switched = await (await Send(HttpMethod.Post, "/api/v1/auth/switch-tenant", new { tenantId = own.TenantId() }, platform: "ANDROID", bearer: accepted.AccessToken()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        var current = switched.GetProperty("tokens").GetProperty("refreshToken").GetString()!;

        (await (await Send(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = previous, installationId = "inst_duda_phone" }, platform: "ANDROID"))
            .ExpectAsync(HttpStatusCode.Unauthorized, _api)).GetProperty("errorCode").GetString().ShouldBe("InvalidRefreshToken");
        var rotated = (await (await Send(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = current, installationId = "inst_duda_phone" }, platform: "ANDROID"))
            .ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("refreshToken").GetString()!;
        await (await Send(HttpMethod.Post, "/api/v1/auth/refresh", new
        {
            refreshToken = tablet.GetProperty("tokens").GetProperty("refreshToken").GetString(),
            installationId = "inst_duda_tablet",
        }, platform: "ANDROID")).ExpectAsync(HttpStatusCode.OK, _api);

        // Vínculo inexistente: 404 e nada é revogado.
        (await Send(HttpMethod.Post, "/api/v1/auth/switch-tenant", new { tenantId = Guid.NewGuid() }, platform: "ANDROID", bearer: switched.AccessToken()))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await (await Send(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = rotated, installationId = "inst_duda_phone" }, platform: "ANDROID"))
            .ExpectAsync(HttpStatusCode.OK, _api);
    }

    [Fact]
    public async Task Web_shared_device_cookie_of_one_tenant_does_not_open_another_tenant()
    {
        var (_, installation, ownerA, secret) = await WebSharedDeviceAsync();

        // Tenant B, com uma pessoa com PIN.
        var ownerB = await _api.SignUpAsync();
        (await _api.Authenticated(ownerB.AccessToken()).SendJsonAsync(HttpMethod.Put, "/api/v1/auth/me/pin", new { pin = "4821", currentPassword = Password }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var employeeB = (await (await _api.Authenticated(ownerB.AccessToken()).GetAsync("/api/v1/auth/me", Ct)).ExpectAsync(HttpStatusCode.OK, _api))
            .GetProperty("employee").GetProperty("id").GetString()!;

        var list = await Web(HttpMethod.Get, $"/api/v1/auth/shared-device/employees?installationId={installation}", null, deviceSecret: secret, tenantId: ownerB.TenantId());
        var problem = await list.ExpectAsync(HttpStatusCode.Unauthorized, _api);
        problem.GetProperty("errorCode").GetString().ShouldBe("InvalidDeviceSecret");
        problem.TryGetProperty("employees", out _).ShouldBeFalse();
        problem.TryGetProperty("tenant", out _).ShouldBeFalse();

        var pin = await Web(HttpMethod.Post, "/api/v1/auth/pin-login", new { tenantId = ownerB.TenantId(), installationId = installation, employeeId = employeeB, pin = "4821" }, deviceSecret: secret);
        (await pin.ExpectAsync(HttpStatusCode.Unauthorized, _api)).GetProperty("errorCode").GetString().ShouldBe("InvalidDeviceSecret");
        SetCookie(pin, Refresh).ShouldBeNull();

        // O mesmo cookie continua valendo no próprio tenant.
        await (await Web(HttpMethod.Get, $"/api/v1/auth/shared-device/employees?installationId={installation}", null, deviceSecret: secret, tenantId: ownerA.TenantId()))
            .ExpectAsync(HttpStatusCode.OK, _api);
    }

    [Fact]
    public async Task Web_ignores_the_device_secret_header_and_forgetting_without_web_sets_no_cookie()
    {
        var (_, installation, owner, secret) = await WebSharedDeviceAsync();
        var url = $"/api/v1/auth/shared-device/employees?installationId={installation}";

        // Na web o segredo vem só do cookie: o header sozinho não autentica, e um header errado não atrapalha o cookie.
        var headerOnly = new HttpRequestMessage(HttpMethod.Get, url);
        headerOnly.Headers.Add("X-Device-Secret", secret);
        (await (await WebRequest(headerOnly, tenantId: owner.TenantId())).ExpectAsync(HttpStatusCode.Unauthorized, _api))
            .GetProperty("errorCode").GetString().ShouldBe("InvalidDeviceSecret");
        var wrongHeader = new HttpRequestMessage(HttpMethod.Get, url);
        wrongHeader.Headers.Add("X-Device-Secret", "ds_errado");
        await (await WebRequest(wrongHeader, tenantId: owner.TenantId(), deviceSecret: secret)).ExpectAsync(HttpStatusCode.OK, _api);

        // O mobile continua pelo header na mesma rota.
        var mobile = new HttpRequestMessage(HttpMethod.Get, url);
        mobile.Headers.Add("X-Device-Secret", secret);
        mobile.Headers.Add("X-Tenant-Id", owner.TenantId());
        mobile.Headers.Add("X-App-Platform", "ANDROID");
        (await _api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false }).SendAsync(mobile, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // DELETE /auth/shared-device sem WEB: 204 sem Set-Cookie.
        foreach (var platform in new[] { null, "ANDROID" })
        {
            var forget = await Send(HttpMethod.Delete, "/api/v1/auth/shared-device", null, platform: platform);
            forget.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            forget.Headers.Contains("Set-Cookie").ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Error_responses_of_auth_routes_carry_cors_for_the_app_origin()
    {
        var (_, installation, owner, _) = await WebSharedDeviceAsync();

        var responses = new[]
        {
            (await Web(HttpMethod.Post, "/api/v1/auth/refresh", new { installationId = installation }), HttpStatusCode.Unauthorized, "InvalidRefreshToken"),
            (await Web(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = "rt_x", installationId = installation }), HttpStatusCode.UnprocessableEntity, "ValidationFailed"),
            (await Web(HttpMethod.Get, $"/api/v1/auth/shared-device/employees?installationId={installation}", null, deviceSecret: "ds_errado", tenantId: owner.TenantId()),
                HttpStatusCode.Unauthorized, "InvalidDeviceSecret"),
            (await Web(HttpMethod.Post, "/api/v1/auth/login", new { login = "ninguem@example.com", password = "Errada-123", installationId = installation }),
                HttpStatusCode.Unauthorized, "InvalidCredentials"),
        };

        foreach (var (response, status, code) in responses)
        {
            (await response.ExpectAsync(status, _api)).GetProperty("errorCode").GetString().ShouldBe(code);
            response.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(ApiFactory.WebOrigin, code);
            response.Headers.GetValues("Access-Control-Allow-Credentials").Single().ShouldBe("true", code);
        }
    }

    [Fact]
    public async Task Cors_preflight_does_not_allow_headers_outside_the_contract()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/pin-login");
        request.Headers.Add("Origin", ApiFactory.WebOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-app-platform,x-device-secret,x-requested-with,x-signalr-user-agent");
        var response = await _api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false }).SendAsync(request, Ct);

        var allowed = response.Headers.TryGetValues("Access-Control-Allow-Headers", out var values) ? string.Join(",", values) : string.Empty;
        foreach (var header in new[] { "x-device-secret", "x-requested-with", "x-signalr-user-agent" })
        {
            allowed.ShouldNotContain(header, Case.Insensitive);
        }
    }

    [Fact]
    public async Task Cors_preflight_answers_only_the_configured_origin()
    {
        var allowed = await Preflight(ApiFactory.WebOrigin, "/api/v1/auth/refresh");
        allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        allowed.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(ApiFactory.WebOrigin);
        allowed.Headers.GetValues("Access-Control-Allow-Credentials").Single().ShouldBe("true");
        allowed.Headers.GetValues("Access-Control-Max-Age").Single().ShouldBe("600");
        string.Join(",", allowed.Headers.GetValues("Access-Control-Allow-Headers")).ShouldContain("x-app-platform", Case.Insensitive);

        var denied = await Preflight("https://evil.example", "/api/v1/auth/refresh");
        denied.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();

        // O negotiate do SignalR também passa pelo CORS.
        (await Preflight(ApiFactory.WebOrigin, "/hubs/realtime/negotiate")).Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(ApiFactory.WebOrigin);

        // Erros também levam CORS (o app lê o ProblemDetails) e os headers expostos.
        var error = await Web(HttpMethod.Get, "/api/v1/auth/me", null);
        error.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        error.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(ApiFactory.WebOrigin);
        string.Join(",", error.Headers.GetValues("Access-Control-Expose-Headers")).ShouldContain("ETag");
    }

    [Fact]
    public async Task Hub_rejects_a_foreign_origin_and_accepts_the_app_origin()
    {
        var token = (await _api.SignUpAsync()).AccessToken();

        var foreign = await Send(HttpMethod.Post, $"/hubs/realtime/negotiate?negotiateVersion=1&access_token={token}", null, platform: "WEB", origin: "https://evil.example");
        (await foreign.ExpectAsync(HttpStatusCode.Forbidden, _api)).GetProperty("errorCode").GetString().ShouldBe("OriginNotAllowed");

        var app = await Send(HttpMethod.Post, $"/hubs/realtime/negotiate?negotiateVersion=1&access_token={token}", null, platform: "WEB", origin: ApiFactory.WebOrigin);
        await app.ExpectAsync(HttpStatusCode.OK, _api);
        app.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(ApiFactory.WebOrigin);

        // Clientes nativos (sem Origin) seguem conectando.
        await (await Send(HttpMethod.Post, $"/hubs/realtime/negotiate?negotiateVersion=1&access_token={token}", null, platform: "ANDROID")).ExpectAsync(HttpStatusCode.OK, _api);
    }

    [Fact]
    public async Task Web_version_below_minimum_gets_426_without_store_url()
    {
        using var api = _api.WithWebHostBuilder(b => b.UseSetting("App:MinSupportedAppVersion:WEB", "9.0.0"));
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Add("X-App-Platform", "WEB");
        request.Headers.Add("X-App-Version", "1.4.0");
        var response = await api.CreateClient().SendAsync(request, Ct);

        ((int)response.StatusCode).ShouldBe(426);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("errorCode").GetString().ShouldBe("AppVersionUnsupported");
        problem.GetProperty("minVersion").GetString().ShouldBe("9.0.0");
        problem.GetProperty("storeUrl").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ----- apoio -----

    /// Conta nova (autocadastro pelo mobile) e um installationId de navegador (crypto.randomUUID()).
    private async Task<(string Email, string Installation, JsonElement Owner)> AccountAsync()
    {
        var email = $"web.{Guid.NewGuid():N}@example.com";
        var owner = await _api.SignUpAsync(ApiScenarios.SignUpBody(email: email));
        return (email, Guid.NewGuid().ToString(), owner);
    }

    /// Login web: devolve o valor do cookie de refresh e a instalação.
    private async Task<(string Cookie, string Installation)> WebLoginAsync()
    {
        var (email, installation, _) = await AccountAsync();
        var login = await Web(HttpMethod.Post, "/api/v1/auth/login", new { login = email, password = Password, installationId = installation });
        await login.ExpectAsync(HttpStatusCode.OK, _api);
        return (CookieValue(login, Refresh)!, installation);
    }

    /// Aparelho compartilhado registrado pela web: devolve o segredo que o navegador guardou no cookie.
    private async Task<(string Email, string Installation, JsonElement Owner, string Secret)> WebSharedDeviceAsync()
    {
        var (email, installation, owner) = await AccountAsync();
        var bearer = (await (await Web(HttpMethod.Post, "/api/v1/auth/login", new { login = email, password = Password, installationId = installation }))
            .ExpectAsync(HttpStatusCode.OK, _api)).AccessToken();
        var register = await Web(HttpMethod.Post, "/api/v1/devices/shared", new { installationId = installation, name = "Balcão", platform = "WEB" }, bearer: bearer);
        await register.ExpectAsync(HttpStatusCode.Created, _api);
        return (email, installation, owner, CookieValue(register, DeviceSecret)!);
    }

    /// Requisição web montada pelo teste (headers extras), com Origin, plataforma e cookie do segredo opcional.
    private Task<HttpResponseMessage> WebRequest(HttpRequestMessage request, string? tenantId = null, string? deviceSecret = null)
    {
        request.Headers.Add("X-App-Version", "1.4.0");
        request.Headers.Add("X-App-Platform", "WEB");
        request.Headers.Add("Origin", ApiFactory.WebOrigin);
        if (tenantId is not null)
        {
            request.Headers.Add("X-Tenant-Id", tenantId);
        }

        if (deviceSecret is not null)
        {
            request.Headers.Add("Cookie", $"{DeviceSecret}={deviceSecret}");
        }

        return _api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false }).SendAsync(request, Ct);
    }

    private Task<HttpResponseMessage> Web(
        HttpMethod method, string url, object? body, string? cookie = null, string? deviceSecret = null, string? tenantId = null,
        string? bearer = null, string? origin = ApiFactory.WebOrigin)
    {
        var cookies = new List<string>();
        if (cookie is not null)
        {
            cookies.Add($"{Refresh}={cookie}");
        }

        if (deviceSecret is not null)
        {
            cookies.Add($"{DeviceSecret}={deviceSecret}");
        }

        return Send(method, url, body, "WEB", origin, cookies.Count > 0 ? string.Join("; ", cookies) : null, tenantId, bearer);
    }

    /// Cliente sem CookieContainer: os cookies vão e voltam explicitamente, como o navegador faria.
    private Task<HttpResponseMessage> Send(
        HttpMethod method, string url, object? body, string? platform, string? origin = null, string? cookie = null, string? tenantId = null, string? bearer = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("X-App-Version", "1.4.0");
        if (platform is not null)
        {
            request.Headers.Add("X-App-Platform", platform);
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        if (tenantId is not null)
        {
            request.Headers.Add("X-Tenant-Id", tenantId);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new("Bearer", bearer);
        }

        return _api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false }).SendAsync(request, Ct);
    }

    private Task<HttpResponseMessage> Preflight(string origin, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, url);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-app-platform,x-app-version");
        return _api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false }).SendAsync(request, Ct);
    }

    private static string? SetCookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.LastOrDefault(v => v.StartsWith(name + "=", StringComparison.Ordinal)) : null;

    private static string? CookieValue(HttpResponseMessage response, string name) =>
        SetCookie(response, name)?.Split(';')[0][(name.Length + 1)..] is { Length: > 0 } value ? value : null;

    private static int MaxAge(string setCookie) =>
        int.Parse(setCookie.Split(';').Select(p => p.Trim()).Single(p => p.StartsWith("max-age=", StringComparison.OrdinalIgnoreCase))["max-age=".Length..],
            System.Globalization.CultureInfo.InvariantCulture);
}
