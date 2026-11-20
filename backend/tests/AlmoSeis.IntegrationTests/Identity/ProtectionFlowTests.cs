using System.Net;
using System.Net.Http.Json;
using AlmoSeis.Api.Infrastructure;
using AlmoSeis.IntegrationTests.Ordering;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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

    /// ADR-0041 + ADR-0053: a rota web da lista de funcionários do aparelho divide o limite de 30/min por instalação.
    [Fact]
    public async Task Web_shared_device_employee_list_is_limited_per_installation()
    {
        var client = Api.CreateClient();
        var installation = $"inst_rl_{Guid.NewGuid():N}";

        for (var i = 0; i < 30; i++)
        {
            (await Employees(client, installation)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var limited = await Employees(client, installation);
        (await limited.ExpectAsync(HttpStatusCode.TooManyRequests, Api)).GetProperty("errorCode").GetString().ShouldBe("TooManyRequests");
        limited.Headers.RetryAfter.ShouldNotBeNull();

        // A rota do mobile conta na mesma partição da instalação; outra instalação segue livre.
        (await client.GetAsync($"/api/v1/devices/shared/{installation}/employees", Ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await Employees(client, $"inst_rl_{Guid.NewGuid():N}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// ADR-0041 (07 §7.4): atrás do ingress, o IP do rate limit vem do `X-Forwarded-For` só quando quem conecta é um
    /// proxy de `ForwardedHeaders:KnownProxies`/`KnownNetworks`; de outro endereço, o header é ignorado.
    [Fact]
    public async Task Forwarded_for_from_a_trusted_proxy_partitions_the_rate_limit_by_client_ip()
    {
        using var api = new ApiFactory(db, s =>
        {
            s.Configure<RateLimitSettings>(o => o.Enabled = true);
            s.Configure<ForwardedHeadersSettings>(o => o.KnownNetworks = ["10.0.0.0/24"]);
            s.AddTransient<IStartupFilter, PeerAddressStartupFilter>();
        });
        var client = api.CreateClient();

        // Proxy confiável (10.0.0.7): cada cliente tem a sua partição de 10/min.
        for (var i = 0; i < 10; i++)
        {
            (await Refresh(client, peer: "10.0.0.7", forwardedFor: "203.0.113.10")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await Refresh(client, peer: "10.0.0.7", forwardedFor: "203.0.113.10")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await Refresh(client, peer: "10.0.0.7", forwardedFor: "203.0.113.11")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // ForwardLimit = 1: só a entrada que o proxy acrescentou (a da direita) conta; a que o cliente forjou antes dela, não.
        (await Refresh(client, peer: "10.0.0.7", forwardedFor: "198.51.100.77, 203.0.113.10")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // Endereço fora das redes confiáveis: o header é ignorado e todos caem na partição do próprio endereço.
        for (var i = 0; i < 10; i++)
        {
            (await Refresh(client, peer: "192.0.2.50", forwardedFor: $"198.51.100.{i + 1}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await Refresh(client, peer: "192.0.2.50", forwardedFor: "198.51.100.200")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
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

    private static Task<HttpResponseMessage> Employees(HttpClient client, string installation)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/auth/shared-device/employees?installationId={installation}");
        request.Headers.Add("X-Tenant-Id", Guid.NewGuid().ToString());
        return client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> Refresh(HttpClient client, string peer, string forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh")
        {
            Content = JsonContent.Create(new { refreshToken = "rt_inexistente", installationId = "inst_fwd" }),
        };
        request.Headers.Add(PeerAddressStartupFilter.Header, peer);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request, Ct);
    }

    /// O TestServer não tem endereço remoto: este filtro, antes de todo o pipeline, simula o IP de quem conecta.
    private sealed class PeerAddressStartupFilter : IStartupFilter
    {
        public const string Header = "X-Test-Peer-Address";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers[Header].FirstOrDefault() is { } peer)
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
