using AlmoSeis.Api.Infrastructure;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace AlmoSeis.UnitTests.Modules;

/// Transporte da sessão na web (ADR-0053): origens aceitas na configuração e leitura de cookie sempre com o `Origin` conferido.
public sealed class WebSessionTests
{
    private const string AppOrigin = "https://app.almoseis.app";

    [Theory]
    [InlineData("https://app.almoseis.app", true)]
    [InlineData("https://app-homolog.almoseis.app:8443", true)]
    [InlineData("http://localhost:8082", true)]
    [InlineData("http://localhost", true)]
    [InlineData("http://127.0.0.1:19006", true)]
    [InlineData("http://app.almoseis.app", false)]       // http fora do localhost
    [InlineData("http://192.168.0.10:8082", false)]
    [InlineData("http://localhost.evil.example", false)]
    [InlineData("*", false)]
    [InlineData("https://*.almoseis.app", false)]
    [InlineData("https://app.almoseis.app/login", false)] // com caminho
    [InlineData("https://app.almoseis.app?x=1", false)]
    [InlineData("https://user@app.almoseis.app", false)]
    [InlineData("ftp://app.almoseis.app", false)]
    [InlineData("app.almoseis.app", false)]
    public void Restaurant_app_origins_must_be_https_except_localhost(string origin, bool valid) =>
        RestaurantAppCors.IsValidOrigin(origin).ShouldBe(valid);

    [Theory]
    [InlineData(new[] { "10.0.0.0/23", "fd00::/8" }, new[] { "10.0.0.4" }, true)]
    [InlineData(new string[0], new string[0], true)]
    [InlineData(new[] { "10.0.0.0" }, new string[0], false)]
    [InlineData(new string[0], new[] { "10.0.0.0/23" }, false)]
    public void Forwarded_headers_settings_accept_only_cidr_and_ips(string[] networks, string[] proxies, bool valid) =>
        new ForwardedHeadersSettings { KnownNetworks = networks, KnownProxies = proxies }.IsValid().ShouldBe(valid);

    [Theory]
    [InlineData(null)]
    [InlineData("https://evil.example")]
    public void Web_cookie_reads_check_the_origin_even_without_the_attribute(string? origin)
    {
        var refresh = Request(origin, $"{WebSession.RefreshCookie}=rt_x");
        Should.Throw<BusinessException>(() => WebSession.RefreshToken(refresh, null, required: true)).ErrorCode.ShouldBe(ErrorCodes.OriginNotAllowed);

        var device = Request(origin, $"{WebSession.DeviceSecretCookie}=ds_x");
        Should.Throw<BusinessException>(() => WebSession.DeviceSecret(device, null)).ErrorCode.ShouldBe(ErrorCodes.OriginNotAllowed);
    }

    [Fact]
    public void Web_device_secret_comes_only_from_the_cookie()
    {
        WebSession.DeviceSecret(Request(AppOrigin, $"{WebSession.DeviceSecretCookie}=ds_cookie"), "ds_header").ShouldBe(("ds_cookie", true));
        WebSession.DeviceSecret(Request(AppOrigin, null), "ds_header").ShouldBe((null, false));

        // Fora da web: só o header, e o cookie é ignorado.
        WebSession.DeviceSecret(Request(AppOrigin, $"{WebSession.DeviceSecretCookie}=ds_cookie", platform: "ANDROID"), "ds_header").ShouldBe(("ds_header", false));
        WebSession.RefreshToken(Request(null, $"{WebSession.RefreshCookie}=rt_cookie", platform: "IOS"), "rt_body", required: true).ShouldBe("rt_body");
    }

    private static HttpRequest Request(string? origin, string? cookie, string platform = WebSession.WebPlatform)
    {
        var services = new ServiceCollection();
        services.Configure<RestaurantAppCorsOptions>(o => o.RestaurantAppOrigins = [AppOrigin]);
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Headers[WebSession.PlatformHeader] = platform;
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        if (cookie is not null)
        {
            context.Request.Headers.Cookie = cookie;
        }

        return context.Request;
    }
}
