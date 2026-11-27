using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Trace;
using Shouldly;

namespace AlmoSeis.UnitTests.Hosting;

/// ADR-0060 (piloto numa VM com Docker Compose): Key Vault opcional, segredos obrigatórios ao subir, anel de chaves do
/// Data Protection em volume, telemetria opcional sem dado sensível e a API atrás do Caddy na rede do Compose.
public sealed class HostingConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "almoseis-unit", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // ---- Key Vault ----

    [Fact]
    public void Without_key_vault_uri_the_configuration_has_no_extra_source()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Issuer"] = "almoseis" });
        var before = ((IConfigurationBuilder)configuration).Sources.Count;

        configuration.AddAlmoSeisKeyVault();

        ((IConfigurationBuilder)configuration).Sources.Count.ShouldBe(before);
    }

    [Theory]
    [InlineData("http://almoseis.vault.azure.net/")]
    [InlineData("almoseis.vault.azure.net")]
    public void Key_vault_uri_must_be_https(string uri)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { [KeyVaultConfiguration.UriKey] = uri });

        Should.Throw<InvalidOperationException>(() => configuration.AddAlmoSeisKeyVault()).Message.ShouldContain("KeyVault:Uri");
    }

    // ---- JWT ----

    [Fact]
    public void Jwt_signing_key_is_required_at_startup_and_the_ephemeral_key_never_in_production()
    {
        var testing = new JwtOptionsValidator(new Env("Testing"));
        testing.Validate(null, new JwtOptions()).Failed.ShouldBeTrue("sem chave e sem efêmera");
        testing.Validate(null, new JwtOptions { AllowEphemeralKey = true }).Succeeded.ShouldBeTrue();
        new JwtOptionsValidator(new Env("Production")).Validate(null, new JwtOptions { AllowEphemeralKey = true }).Failed.ShouldBeTrue();
        testing.Validate(null, new JwtOptions { SigningKeyPem = "-----BEGIN PRIVATE KEY-----\nlixo\n-----END PRIVATE KEY-----" }).Failed.ShouldBeTrue();

        using var weak = RSA.Create(1024);
        testing.Validate(null, new JwtOptions { SigningKeyPem = weak.ExportPkcs8PrivateKeyPem() }).Failed.ShouldBeTrue("1024 bits");

        using var rsa = RSA.Create(2048);
        var result = new JwtOptionsValidator(new Env("Production")).Validate(null, new JwtOptions { SigningKeyPem = rsa.ExportPkcs8PrivateKeyPem() });
        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Connection_strings_are_checked_at_startup()
    {
        static IOptions<ConnectionStringsCheck> Check(string? app, string? platform)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:App"] = app,
                ["ConnectionStrings:Platform"] = platform,
            }).Build();
            var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration);
            services.AddRequiredSecretsValidation();
            return services.BuildServiceProvider().GetRequiredService<IOptions<ConnectionStringsCheck>>();
        }

        Should.Throw<OptionsValidationException>(() => Check("REPLACE_ME", "Host=db").Value).Message.ShouldContain("ConnectionStrings__App");
        Should.Throw<OptionsValidationException>(() => Check("Host=db", null).Value).Message.ShouldContain("ConnectionStrings__Platform");
        Check("Host=db", "Host=db").Value.App.ShouldBe("Host=db");
    }

    // ---- Data Protection ----

    [Fact]
    public void Data_protection_keys_path_is_required_outside_development_and_must_be_absolute_and_writable()
    {
        new DataProtectionSettingsValidator(new Env("Production")).Validate(null, new DataProtectionSettings()).Failed.ShouldBeTrue();
        new DataProtectionSettingsValidator(new Env("Testing")).Validate(null, new DataProtectionSettings()).Failed.ShouldBeTrue();
        new DataProtectionSettingsValidator(new Env("Development")).Validate(null, new DataProtectionSettings()).Succeeded.ShouldBeTrue();

        var production = new DataProtectionSettingsValidator(new Env("Production"));
        production.Validate(null, new DataProtectionSettings { KeysPath = "dp-keys" }).Failed.ShouldBeTrue("relativo");
        production.Validate(null, new DataProtectionSettings { KeysPath = _directory }).Succeeded.ShouldBeTrue();
        Directory.EnumerateFileSystemEntries(_directory).ShouldBeEmpty("a sonda de escrita é apagada");
    }

    [Fact]
    public void Keys_survive_a_restart_through_the_keys_path()
    {
        var protectedToken = Protector(Settings()).Protect("reset:usuario:stamp");

        // "Reinício": outro contêiner de serviços, mesmo diretório.
        Protector(Settings()).Unprotect(protectedToken).ShouldBe("reset:usuario:stamp");
        Directory.GetFiles(_directory, "key-*.xml").Length.ShouldBe(1);
    }

    [Fact]
    public void Optional_certificate_encrypts_the_keys_at_rest()
    {
        var pfx = Path.Combine(_directory, "dp.pfx");
        Directory.CreateDirectory(_directory);
        using (var rsa = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=almoseis-dataprotection", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(100));
            File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx, "senha-do-pfx"));
        }

        var settings = Settings(("DataProtection:CertificatePath", pfx), ("DataProtection:CertificatePassword", "senha-do-pfx"));
        var protectedToken = Protector(settings).Protect("reset");

        var key = File.ReadAllText(Directory.GetFiles(_directory, "key-*.xml").Single());
        key.ShouldContain("encryptedSecret");
        Protector(settings).Unprotect(protectedToken).ShouldBe("reset");

        var wrongPassword = Settings(("DataProtection:CertificatePath", pfx), ("DataProtection:CertificatePassword", "errada"));
        Should.Throw<InvalidOperationException>(() => Protector(wrongPassword)).Message.ShouldContain("DataProtection:CertificatePath");
    }

    // ---- Telemetria ----

    [Fact]
    public void Telemetry_is_off_without_an_exporter_and_on_with_the_otlp_endpoint()
    {
        Registered().ShouldBeFalse();
        Registered(("Otel:Endpoint", "https://otlp.example.com/otlp")).ShouldBeTrue();
        Registered(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://otel-collector:4318")).ShouldBeTrue("variável padrão do OpenTelemetry");
        Registered(("AzureMonitor:ConnectionString", "InstrumentationKey=00000000-0000-0000-0000-000000000000")).ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => Registered(("Otel:Endpoint", "otlp.example.com"))).Message.ShouldContain("Otel:Endpoint");
    }

    [Theory]
    [InlineData(OtelProtocol.HttpProtobuf, "https://otlp.example.com/otlp/", "https://otlp.example.com/otlp/v1/traces")]
    [InlineData(OtelProtocol.HttpProtobuf, "http://collector:4318", "http://collector:4318/v1/traces")]
    [InlineData(OtelProtocol.Grpc, "http://collector:4317", "http://collector:4317/")]
    public void Otlp_signal_endpoint_follows_the_protocol(OtelProtocol protocol, string endpoint, string expected) =>
        Telemetry.SignalEndpoint(protocol, new Uri(endpoint), "v1/traces").AbsoluteUri.ShouldBe(expected);

    [Fact]
    public void Redaction_removes_tokens_from_server_and_client_spans()
    {
        using var source = new ActivitySource("AlmoSeis.UnitTests.Redaction");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        var processor = new TelemetryRedactionProcessor();

        using (var hub = source.StartActivity("GET", ActivityKind.Server)!)
        {
            hub.SetTag("url.path", "/hubs/realtime").SetTag("url.query", "?access_token=eyJhbGciOi").SetTag("http.route", "/hubs/realtime");
            processor.OnEnd(hub);
            hub.GetTagItem("url.query").ShouldBeNull();
        }

        using (var tracking = source.StartActivity("GET", ActivityKind.Server)!)
        {
            tracking.SetTag("url.path", "/api/v1/public/tracking/AbCdEfGhIjKlMnOpQrStUv").SetTag("http.route", "api/v1/public/tracking/{token}");
            processor.OnEnd(tracking);
            tracking.GetTagItem("url.path").ShouldBe("/api/v1/public/tracking/{token}");
        }

        using (var unmatched = source.StartActivity("GET", ActivityKind.Server)!)
        {
            unmatched.SetTag("url.path", "/api/v1/public/tracking/AbCdEfGhIjKlMnOpQrStUv/x");
            processor.OnEnd(unmatched);
            unmatched.GetTagItem("url.path").ShouldBe("/api/v1/public/tracking/{token}");
        }

        using (var nominatim = source.StartActivity("GET", ActivityKind.Client)!)
        {
            nominatim.SetTag("url.full", "https://nominatim.openstreetmap.org/search?street=Rua%20das%20Flores%2C%20123&city=Uberl%C3%A2ndia");
            processor.OnEnd(nominatim);
            nominatim.GetTagItem("url.full").ShouldBe("https://nominatim.openstreetmap.org/");
        }

        using (var osrm = source.StartActivity("GET", ActivityKind.Client)!)
        {
            osrm.SetTag("url.full", "https://router.project-osrm.org/route/v1/driving/-48.27,-18.91;-48.28,-18.92").SetTag("url.path", "/route/v1/driving/-48.27,-18.91");
            processor.OnEnd(osrm);
            osrm.GetTagItem("url.full").ShouldBe("https://router.project-osrm.org/");
            osrm.GetTagItem("url.path").ShouldBeNull();
        }
    }

    // ---- Proxy reverso (Caddy na mesma rede do Compose) ----

    [Fact]
    public async Task Caddy_on_the_compose_network_forwards_the_client_ip_and_https()
    {
        var middleware = ForwardedHeaders(("ForwardedHeaders:KnownNetworks:0", "172.30.0.0/24"));

        // Caddy (172.30.0.2) terminou o HTTPS: acrescenta o IP do cliente e o esquema original.
        var context = await Forward(middleware, peer: "172.30.0.2", forwardedFor: "198.51.100.1, 203.0.113.9", proto: "https");
        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse("203.0.113.9"));
        context.Request.Scheme.ShouldBe("https");
        context.Request.IsHttps.ShouldBeTrue();

        // Docker com IPv6 ligado entrega o endereço do Caddy mapeado em IPv6.
        context = await Forward(middleware, peer: "::ffff:172.30.0.2", forwardedFor: "203.0.113.9", proto: "https");
        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse("203.0.113.9"));

        // Fora da rede do Compose (ex.: alguém batendo direto na porta da API): headers ignorados.
        context = await Forward(middleware, peer: "192.0.2.50", forwardedFor: "203.0.113.9", proto: "https");
        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse("192.0.2.50"));
        context.Request.Scheme.ShouldBe("http");
    }

    [Fact]
    public async Task Without_known_networks_only_loopback_is_trusted()
    {
        var middleware = ForwardedHeaders();
        var context = await Forward(middleware, peer: "172.30.0.2", forwardedFor: "203.0.113.9", proto: "https");
        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse("172.30.0.2"));
        context.Request.IsHttps.ShouldBeFalse();
    }

    private static ForwardedHeadersMiddleware ForwardedHeaders(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value)).Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration);
        services.AddAlmoSeisForwardedHeaders();
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<ForwardedHeadersOptions>>();
        return new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, options);
    }

    private static async Task<HttpContext> Forward(ForwardedHeadersMiddleware middleware, string peer, string forwardedFor, string proto)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        context.Request.Headers["X-Forwarded-Proto"] = proto;
        await middleware.Invoke(context);
        return context;
    }

    private static bool Registered(params (string Key, string Value)[] settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value));
        builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] ??= string.Empty;
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ??= string.Empty;
        builder.AddAlmoSeisTelemetry();
        return builder.Services.Any(d => d.ServiceType == typeof(TracerProvider));
    }

    private IConfiguration Settings(params (string Key, string Value)[] extra) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataProtection:KeysPath"] = _directory })
            .AddInMemoryCollection(extra.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

    private static IDataProtector Protector(IConfiguration configuration)
    {
        var services = new ServiceCollection().AddLogging().AddSingleton(configuration).AddSingleton<IHostEnvironment>(new Env("Testing"));
        services.AddAlmoSeisDataProtection(configuration);
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>().CreateProtector("AlmoSeis.UnitTests");
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "AlmoSeis.Api";

        public string ContentRootPath { get; set; } = ".";

        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
