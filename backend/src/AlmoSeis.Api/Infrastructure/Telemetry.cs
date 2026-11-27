using System.Diagnostics;
using System.Reflection;
using AlmoSeis.Infrastructure.Observability;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AlmoSeis.Api.Infrastructure;

public enum OtelProtocol
{
    HttpProtobuf,
    Grpc,
}

/// Seção `Otel` (ADR-0060). Sem `Endpoint`, nada é exportado por OTLP.
public sealed class OtelSettings
{
    public const string Section = "Otel";

    /// Coletor OTLP (Grafana Cloud, SigNoz, Jaeger, o endpoint OTLP do Sentry…). Em `HttpProtobuf`, a base: a API
    /// acrescenta `/v1/traces`, `/v1/metrics` e `/v1/logs`.
    public string Endpoint { get; set; } = string.Empty;

    public OtelProtocol Protocol { get; set; } = OtelProtocol.HttpProtobuf;

    /// Headers do OTLP no formato padrão `chave=valor,chave2=valor2` (ex.: `Authorization=Basic ...`). Segredo.
    public string Headers { get; set; } = string.Empty;

    public string ServiceName { get; set; } = "almoseis-api";

    /// Fração dos traces amostrados na raiz (1 = todos). Para caber em planos gratuitos.
    public double TraceSampleRatio { get; set; } = 1.0;
}

/// OpenTelemetry (ADR-0037, ADR-0060): traces, métricas e logs, exportados só para o que estiver configurado: OTLP
/// (`Otel:Endpoint`) e/ou Application Insights (`AzureMonitor:ConnectionString` ou `APPLICATIONINSIGHTS_CONNECTION_STRING`).
/// Sem nenhum dos dois, o OpenTelemetry nem é registrado e os logs vão só para o console (`docker logs`).
/// Nada de payload, telefone ou token (ADR-0024): sem corpo nem headers, query e caminho com token reescritos pelo
/// `TelemetryRedactionProcessor`, logs sem escopos (o escopo do ASP.NET Core leva o caminho da requisição).
public static class Telemetry
{
    public const string StandardOtlpEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// Sem o SDK do Sentry (07 §7.1, ADR-0060): um `Sentry:Dsn` configurado não faz nada, e isso precisa aparecer no log.
    public static WebApplication WarnAboutIgnoredTelemetrySettings(this WebApplication app)
    {
        if (!string.IsNullOrWhiteSpace(app.Configuration["Sentry:Dsn"]))
        {
            app.Logger.LogWarning(
                "Sentry:Dsn ignorado: a API não usa o SDK do Sentry. Para mandar ao Sentry, use o endpoint OTLP dele em Otel:Endpoint e Otel:Headers.");
        }

        return app;
    }

    public static WebApplicationBuilder AddAlmoSeisTelemetry(this WebApplicationBuilder builder)
    {
        var settings = builder.Configuration.GetSection(OtelSettings.Section).Get<OtelSettings>() ?? new OtelSettings();
        var otlp = OtlpEndpoint(settings);
        // Sem `Otel:Endpoint`, as variáveis padrão do OpenTelemetry (`OTEL_EXPORTER_OTLP_ENDPOINT`, `_PROTOCOL`, `_HEADERS`)
        // também ligam o OTLP; aí o exportador as lê sozinho.
        var useOtlp = otlp is not null || !string.IsNullOrWhiteSpace(builder.Configuration[StandardOtlpEndpointVariable]);
        var azureMonitor = AzureMonitorConnectionString(builder.Configuration);
        if (!useOtlp && azureMonitor is null)
        {
            return builder;
        }

        if (settings.TraceSampleRatio is < 0 or > 1)
        {
            throw new InvalidOperationException("Otel:TraceSampleRatio entre 0 e 1.");
        }

        var version = typeof(Telemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(settings.ServiceName, serviceVersion: version, serviceInstanceId: Environment.MachineName)
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(settings.TraceSampleRatio)))
                    .AddAspNetCoreInstrumentation(o => o.Filter = http => !http.Request.Path.StartsWithSegments("/health"))
                    .AddHttpClientInstrumentation()
                    .AddSource("Npgsql")
                    // Antes dos exportadores: eles recebem o span já sem query, caminho com token e URL de saída completa.
                    .AddProcessor(new TelemetryRedactionProcessor());
                if (useOtlp)
                {
                    tracing.AddOtlpExporter(o => ConfigureOtlp(o, settings, otlp, "v1/traces"));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    // `AlmoSeis`: outbox, idempotência, pedidos e sync (07 §7.4); `AlmoSeis.Maps`: falhas do provedor de mapas.
                    .AddMeter("System.Runtime", "Npgsql", AlmoSeisMetrics.MeterName, "AlmoSeis.Maps");
                if (useOtlp)
                {
                    metrics.AddOtlpExporter(o => ConfigureOtlp(o, settings, otlp, "v1/metrics"));
                }
            })
            .WithLogging(
                logging =>
                {
                    if (useOtlp)
                    {
                        logging.AddOtlpExporter(o => ConfigureOtlp(o, settings, otlp, "v1/logs"));
                    }
                },
                options =>
                {
                    options.IncludeScopes = false;
                    options.IncludeFormattedMessage = true;
                });

        if (azureMonitor is not null)
        {
            otel.UseAzureMonitorExporter(o => o.ConnectionString = azureMonitor);
        }

        return builder;
    }

    public static Uri? OtlpEndpoint(OtelSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            return null;
        }

        return Uri.TryCreate(settings.Endpoint.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : throw new InvalidOperationException("Otel:Endpoint precisa ser uma URL http(s) absoluta (ex.: https://otlp.example.com/otlp).");
    }

    public static string? AzureMonitorConnectionString(IConfiguration configuration) =>
        new[] { configuration["AzureMonitor:ConnectionString"], configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] }
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    public static void ConfigureOtlp(OtlpExporterOptions options, OtelSettings settings, Uri? endpoint, string signalPath)
    {
        if (endpoint is null)
        {
            return; // OTEL_EXPORTER_OTLP_*: o padrão do exportador já acrescenta o caminho de cada sinal.
        }

        options.Protocol = settings.Protocol == OtelProtocol.Grpc ? OtlpExportProtocol.Grpc : OtlpExportProtocol.HttpProtobuf;
        options.Endpoint = SignalEndpoint(settings.Protocol, endpoint, signalPath);
        if (!string.IsNullOrWhiteSpace(settings.Headers))
        {
            options.Headers = settings.Headers.Trim();
        }
    }

    /// Com o endpoint definido no código, o exportador HTTP usa a URL como está: o caminho de cada sinal vai aqui.
    public static Uri SignalEndpoint(OtelProtocol protocol, Uri endpoint, string signalPath) =>
        protocol == OtelProtocol.Grpc ? endpoint : new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/" + signalPath);
}

/// Reescreve o span antes de exportar (ADR-0024, ADR-0047): servidor sem `url.query` (o `access_token` do SignalR vai na
/// query) e com `url.path` = rota (o token de acompanhamento vai no caminho); cliente HTTP só com esquema e host (o
/// endereço vai na query do Nominatim e as coordenadas no caminho do OSRM).
public sealed class TelemetryRedactionProcessor : BaseProcessor<Activity>
{
    public const string TrackingPrefix = "/api/v1/public/tracking";

    public override void OnEnd(Activity data)
    {
        if (data.Kind == ActivityKind.Server)
        {
            data.SetTag("url.query", null);
            if (data.GetTagItem("http.route") is string { Length: > 0 } route)
            {
                data.SetTag("url.path", route.StartsWith('/') ? route : "/" + route);
            }
            else if (data.GetTagItem("url.path") is string path && path.StartsWith(TrackingPrefix, StringComparison.OrdinalIgnoreCase))
            {
                data.SetTag("url.path", TrackingPrefix + "/{token}");
            }
        }
        else if (data.Kind == ActivityKind.Client && data.GetTagItem("url.full") is string full
            && Uri.TryCreate(full, UriKind.Absolute, out var uri))
        {
            data.SetTag("url.full", $"{uri.Scheme}://{uri.Authority}/");
            data.SetTag("url.query", null);
            data.SetTag("url.path", null);
        }
    }
}
