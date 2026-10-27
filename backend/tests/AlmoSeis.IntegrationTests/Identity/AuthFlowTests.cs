using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Identity;

/// Fluxos de 06 §6.3 contra a API real, com RLS e papéis de produção.
[Collection(nameof(PostgresCollection))]
public sealed class AuthFlowTests(PostgresFixture db) : IDisposable
{
    private readonly ApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Signup_creates_owner_with_trial_and_returns_me()
    {
        var result = await _api.SignUpAsync();

        var tokens = result.GetProperty("tokens");
        tokens.GetProperty("tokenType").GetString().ShouldBe("Bearer");
        tokens.GetProperty("refreshToken").GetString()!.ShouldStartWith("rt_");

        var me = result.GetProperty("me");
        me.GetProperty("employee").GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldBe(["OWNER"]);
        me.GetProperty("permissions").GetArrayLength().ShouldBe(22);
        me.GetProperty("memberships").GetArrayLength().ShouldBe(1);

        var subscription = me.GetProperty("subscription");
        subscription.GetProperty("status").GetString().ShouldBe("TRIALING");
        subscription.GetProperty("daysLeft").GetInt32().ShouldBe(7);
        subscription.GetProperty("canWrite").GetBoolean().ShouldBeTrue();
    }

    /// Ciclo de escrita (07 §7.3): o UnitOfWork grava feed (seq por tenant) e auditoria na mesma transação.
    [Fact]
    public async Task Signup_writes_change_feed_and_audit()
    {
        var signup = await _api.SignUpAsync();
        var tenantId = Guid.Parse(signup.GetProperty("me").GetProperty("tenant").GetProperty("id").GetString()!);

        await using var connection = new Npgsql.NpgsqlConnection(db.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(
            """
            SELECT string_agg(entity_type || ':' || seq, ',' ORDER BY seq),
                   (SELECT last_seq FROM tenant_sync_counters WHERE tenant_id = @t),
                   (SELECT count(*) FROM audit_logs WHERE tenant_id = @t AND operation_type = 'TenantSignedUp')
            FROM change_feed_entries WHERE tenant_id = @t
            """, connection);
        command.Parameters.AddWithValue("t", tenantId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);

        reader.GetString(0).Split(',').Select(e => e.Split(':')[0]).ShouldBe(["TENANT_SETTINGS", "EMPLOYEE"], ignoreOrder: true);
        reader.GetInt64(1).ShouldBe(2);
        reader.GetInt64(2).ShouldBe(1);
    }

    [Fact]
    public async Task Me_with_access_token_returns_the_same_tenant()
    {
        var signup = await _api.SignUpAsync();
        var tenantId = signup.GetProperty("me").GetProperty("tenant").GetProperty("id").GetString();

        var me = await _api.Authenticated(signup.AccessToken()).GetFromJsonAsync<JsonElement>("/api/v1/auth/me", Ct);

        me.GetProperty("tenant").GetProperty("id").GetString().ShouldBe(tenantId);
    }

    [Fact]
    public async Task Login_by_email_and_by_phone()
    {
        var email = $"login.{Guid.NewGuid():N}@example.com";
        var phone = "+55349" + ApiScenarios.RandomDigits(8);
        await _api.SignUpAsync(ApiScenarios.SignUpBody(email: email, phone: phone));

        foreach (var login in new[] { email.ToUpperInvariant(), phone })
        {
            var response = await _api.CreateClient().PostAsJsonAsync(
                "/api/v1/auth/login", new { login, password = "S3nha-forte!", installationId = "inst_login", preferredTenantId = (Guid?)null }, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, login);
        }
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account()
    {
        var email = $"lock.{Guid.NewGuid():N}@example.com";
        await _api.SignUpAsync(ApiScenarios.SignUpBody(email: email));
        var client = _api.CreateClient();

        JsonElement last = default;
        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { login = email, password = "errada", installationId = "inst_x" }, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            last = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        }

        last.GetProperty("errorCode").GetString().ShouldBe("InvalidCredentials");
        last.TryGetProperty("lockedUntil", out _).ShouldBeTrue("a 5ª falha bloqueia e informa até quando");

        // Nem a senha certa entra durante o bloqueio.
        var correct = await client.PostAsJsonAsync("/api/v1/auth/login", new { login = email, password = "S3nha-forte!", installationId = "inst_x" }, Ct);
        correct.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_rotates_and_reuse_revokes_the_family()
    {
        var signup = await _api.SignUpAsync();
        var installationId = "inst_" + signup.GetProperty("me").GetProperty("user").GetProperty("id").GetString()![..8];
        var first = signup.GetProperty("tokens").GetProperty("refreshToken").GetString();
        var client = _api.CreateClient();

        // O refresh do autocadastro foi emitido para a instalação do corpo; recria com a mesma instalação via login.
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            login = signup.GetProperty("me").GetProperty("user").GetProperty("email").GetString(),
            password = "S3nha-forte!",
            installationId,
        }, Ct);
        var original = (await login.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("tokens").GetProperty("refreshToken").GetString();

        var rotated = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = original, installationId }, Ct);
        rotated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = (await rotated.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("refreshToken").GetString();
        second.ShouldNotBe(original);

        // Reusar o token já trocado: 401 e a família inteira cai (inclusive o novo).
        var reuse = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = original, installationId }, Ct);
        reuse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await reuse.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errorCode").GetString().ShouldBe("InvalidRefreshToken");

        var afterTheft = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = second, installationId }, Ct);
        afterTheft.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        first.ShouldNotBeNull();
    }

    [Fact]
    public async Task Duplicate_email_and_reused_document_are_rejected()
    {
        var email = $"dup.{Guid.NewGuid():N}@example.com";
        var document = ApiScenarios.RandomDigits(14);
        await _api.SignUpAsync(ApiScenarios.SignUpBody(email: email, document: document));

        var sameEmail = await _api.CreateClient().PostAsJsonAsync("/api/v1/auth/signup", ApiScenarios.SignUpBody(email: email), Ct);
        sameEmail.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await sameEmail.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errorCode").GetString().ShouldBe("EmailAlreadyRegistered");

        var sameDocument = await _api.CreateClient().PostAsJsonAsync("/api/v1/auth/signup", ApiScenarios.SignUpBody(document: document), Ct);
        sameDocument.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await sameDocument.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errorCode").GetString().ShouldBe("TrialAlreadyClaimed");
    }

    [Fact]
    public async Task Invalid_signup_returns_422_with_field_errors()
    {
        var response = await _api.CreateClient().PostAsJsonAsync("/api/v1/auth/signup", new
        {
            newAccount = new { fullName = "Ana", email = (string?)null, phoneNumber = "349999", password = "123" },
            tenant = new { name = "X", documentType = "CNPJ", documentNumber = "123", city = "Udi", state = "MG" },
            acceptedTermsVersion = "2026-10",
            installationId = "inst_1",
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("errorCode").GetString().ShouldBe("ValidationFailed");
        var errors = problem.GetProperty("errors");
        errors.TryGetProperty("newAccount.email", out _).ShouldBeTrue(errors.ToString());
        errors.TryGetProperty("newAccount.password", out _).ShouldBeTrue(errors.ToString());
        errors.TryGetProperty("tenant.documentNumber", out _).ShouldBeTrue(errors.ToString());
    }

    [Fact]
    public async Task Protected_endpoint_without_token_returns_problem_401()
    {
        var response = await _api.CreateClient().GetAsync("/api/v1/roles", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errorCode").GetString().ShouldBe("Unauthenticated");
    }

    [Fact]
    public async Task Each_tenant_sees_only_itself()
    {
        var a = await _api.SignUpAsync();
        var b = await _api.SignUpAsync();

        var meB = await _api.Authenticated(b.AccessToken()).GetFromJsonAsync<JsonElement>("/api/v1/auth/me", Ct);

        meB.GetProperty("tenant").GetProperty("id").GetString().ShouldBe(b.GetProperty("me").GetProperty("tenant").GetProperty("id").GetString());
        meB.GetProperty("memberships").EnumerateArray()
            .Select(m => m.GetProperty("tenantId").GetString())
            .ShouldNotContain(a.GetProperty("me").GetProperty("tenant").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Roles_lists_the_permission_matrix()
    {
        var signup = await _api.SignUpAsync();

        var roles = await _api.Authenticated(signup.AccessToken()).GetFromJsonAsync<JsonElement>("/api/v1/roles", Ct);

        var kitchen = roles.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("role").GetString() == "KITCHEN");
        kitchen.GetProperty("permissions").EnumerateArray().Select(p => p.GetString())
            .ShouldBe(["orders.view", "production.view", "production.update"], ignoreOrder: true);
    }
}
