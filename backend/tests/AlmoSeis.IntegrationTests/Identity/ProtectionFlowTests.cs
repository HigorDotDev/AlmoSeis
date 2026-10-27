using System.Net;
using System.Net.Http.Json;
using AlmoSeis.Api.Infrastructure;
using AlmoSeis.IntegrationTests.Ordering;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Identity;

/// Versão mínima do app (ADR-0035), limites dos endpoints públicos (ADR-0041) e auditoria (06 §6.13).
[Collection(nameof(PostgresCollection))]
public sealed class ProtectionFlowTests(PostgresFixture db) : IDisposable
{
    private ApiFactory? _api;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApiFactory Api => _api ??= new ApiFactory(db, s =>
    {
        s.Configure<AppVersionOptions>(o =>
        {
            o.MinSupportedAppVersion["ANDROID"] = "1.4.0";
            o.StoreUrl["ANDROID"] = "https://play.google.com/store/apps/details?id=app.almoseis";
        });
        s.Configure<RateLimitSettings>(o => o.Enabled = true);
    });

    public void Dispose() => _api?.Dispose();

    [Fact]
    public async Task Old_app_versions_get_426()
    {
        var client = Api.CreateClient();

        var old = await Send(client, "1.3.9");
        var problem = await old.ExpectAsync((HttpStatusCode)426, Api);
        problem.GetProperty("errorCode").GetString().ShouldBe("AppVersionUnsupported");
        problem.GetProperty("minVersion").GetString().ShouldBe("1.4.0");
        problem.GetProperty("storeUrl").GetString()!.ShouldContain("play.google.com");

        (await Send(client, "1.4.0")).StatusCode.ShouldBe(HttpStatusCode.NotFound); // passou do filtro: convite inexistente
        (await client.GetAsync("/health/live", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Public_auth_endpoints_are_limited_per_login_and_per_ip()
    {
        var client = Api.CreateClient();
        var login = $"alvo.{Guid.NewGuid():N}@example.com";

        for (var i = 0; i < 5; i++)
        {
            (await Login(client, login)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var limited = await Login(client, login);
        (await limited.ExpectAsync(HttpStatusCode.TooManyRequests, Api)).GetProperty("errorCode").GetString().ShouldBe("TooManyRequests");
        limited.Headers.RetryAfter.ShouldNotBeNull();

        // Outro login ainda passa até o limite por IP (10/min).
        var other = $"outro.{Guid.NewGuid():N}@example.com";
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await Login(client, other)).StatusCode);
        }

        statuses.ShouldContain(HttpStatusCode.Unauthorized);
        statuses.Last().ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Audit_log_lists_the_tenant_history_for_who_can_see_it()
    {
        var s = await OrderingScenario.CreateAsync(Api, db);
        var order = await s.CreateOrderAsync(s.Pf());

        var all = await (await s.Client.GetAsync("/api/v1/audit-logs?limit=100", Ct)).ExpectAsync(HttpStatusCode.OK, Api);
        all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("operationType").GetString()).ShouldContain("BusinessDayOpened");

        var orders = await (await s.Client.GetAsync($"/api/v1/audit-logs?entityType=ORDER&entityId={order.Id()}", Ct)).ExpectAsync(HttpStatusCode.OK, Api);
        var created = orders.GetProperty("items").EnumerateArray().Single();
        created.GetProperty("operationType").GetString().ShouldBe("OrderCreated");
        created.GetProperty("origin").GetString().ShouldBe("APP");
        created.GetProperty("actor").GetProperty("displayName").GetString().ShouldNotBeNullOrEmpty();
        created.GetProperty("after").GetProperty("orderNumber").GetInt32().ShouldBe(1);

        var attendant = Api.Authenticated(await Api.EmployeeTokenAsync(db, s.Owner, "ATTENDANT"));
        await (await attendant.GetAsync("/api/v1/audit-logs", Ct)).ExpectAsync(HttpStatusCode.Forbidden, Api);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string version)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/invitations/ABCDEF");
        request.Headers.Add("X-App-Version", version);
        request.Headers.Add("X-App-Platform", "ANDROID");
        return client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> Login(HttpClient client, string login) =>
        client.PostAsJsonAsync("/api/v1/auth/login", new { login, password = "Errada-123", installationId = "inst_rl" }, Ct);
}
