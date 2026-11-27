using System.Net;
using System.Net.Http.Json;
using AlmoSeis.Api.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace AlmoSeis.IntegrationTests;

/// ADR-0060: a API no Compose, atrás do Caddy na mesma rede Docker, com o HTTPS terminado no Caddy; health do Compose.
[Collection(nameof(PostgresCollection))]
public sealed class ReverseProxyFlowTests(PostgresFixture db)
{
    private const string CaddyAddress = "172.30.0.2";
    private const string AppOrigin = "https://app.almoseis.app";
    private const string PeerHeader = "X-Test-Peer-Address";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Health_live_answers_without_the_database_and_ready_checks_it()
    {
        using var api = new ApiFactory(db);
        var client = api.CreateClient();

        var live = await client.GetAsync("/health/live", Ct);
        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await live.Content.ReadAsStringAsync(Ct)).ShouldBe("Healthy");

        var ready = await client.GetAsync("/health/ready", Ct);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ready.Content.ReadAsStringAsync(Ct)).ShouldBe("Healthy");
    }

    [Fact]
    public async Task Health_ready_fails_when_the_database_is_unreachable()
    {
        using var api = new ApiFactory(db).WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Platform"] = "Host=127.0.0.1;Port=1;Database=almoseis;Username=x;Password=x;Timeout=1" })));
        var client = api.CreateClient();

        (await client.GetAsync("/health/live", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/health/ready", Ct)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    /// O Caddy (na rede do Compose, `ForwardedHeaders:KnownNetworks`) termina o TLS e repassa `X-Forwarded-Proto: https`:
    /// a API vê HTTPS (HSTS sai), o login web grava os cookies `__Secure-` e o CORS com credenciais responde à origem do app.
    [Fact]
    public async Task Https_terminated_at_caddy_keeps_secure_cookies_cors_and_hsts()
    {
        using var api = new ApiFactory(db, s =>
        {
            s.Configure<ForwardedHeadersSettings>(o => o.KnownNetworks = ["172.30.0.0/24"]);
            s.Configure<RestaurantAppCorsOptions>(o => o.RestaurantAppOrigins = [AppOrigin]);
            s.AddTransient<IStartupFilter, PeerAddressStartupFilter>();
        });
        var email = $"proxy.{Guid.NewGuid():N}@example.com";
        await api.SignUpAsync(ApiScenarios.SignUpBody(email: email));
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://api.almoseis.app"), HandleCookies = false });

        var login = await client.SendAsync(WebLogin(email, CaddyAddress), Ct);
        await login.ExpectAsync(HttpStatusCode.OK, api);
        login.Headers.GetValues("Set-Cookie").ShouldContain(c =>
            c.StartsWith("__Secure-almoseis-rt=", StringComparison.Ordinal) && c.Contains("secure", StringComparison.OrdinalIgnoreCase)
            && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        login.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([AppOrigin]);
        login.Headers.GetValues("Access-Control-Allow-Credentials").ShouldBe(["true"]);
        login.Headers.Contains("Strict-Transport-Security").ShouldBeTrue("a API viu o esquema https repassado pelo Caddy");

        // Quem conecta direto na porta da API (fora da rede do Caddy) não consegue se passar por HTTPS.
        var direct = await client.SendAsync(WebLogin(email, "192.0.2.50"), Ct);
        await direct.ExpectAsync(HttpStatusCode.OK, api);
        direct.Headers.Contains("Strict-Transport-Security").ShouldBeFalse();
    }

    private static HttpRequestMessage WebLogin(string email, string peer)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { login = email, password = "S3nha-forte!", installationId = $"inst_{Guid.NewGuid():N}"[..20] }),
        };
        request.Headers.Add("X-App-Version", "1.4.0");
        request.Headers.Add("X-App-Platform", "WEB");
        request.Headers.Add("Origin", AppOrigin);
        request.Headers.Add("X-Forwarded-For", "203.0.113.9");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add(PeerHeader, peer);
        return request;
    }

    /// O TestServer não tem endereço remoto: simula o IP de quem conecta (o Caddy ou um cliente direto).
    private sealed class PeerAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers[PeerHeader].FirstOrDefault() is { } peer)
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
