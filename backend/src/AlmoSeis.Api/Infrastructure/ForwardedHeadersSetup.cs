using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using IPNetwork = System.Net.IPNetwork;

namespace AlmoSeis.Api.Infrastructure;

/// `ForwardedHeaders`: de quem a API aceita `X-Forwarded-For`/`X-Forwarded-Proto` (ADR-0041: o IP real do cliente
/// atrás do ingress do Container Apps particiona o rate limit). Sem nada configurado, só o loopback é confiável
/// (padrão do ASP.NET Core): em Development, sem proxy, o header de um cliente qualquer é ignorado.
public sealed class ForwardedHeadersSettings
{
    public const string Section = "ForwardedHeaders";

    /// Redes CIDR do proxy confiável (ex.: a sub-rede de infraestrutura do Container Apps Environment, `10.0.0.0/23`).
    public string[] KnownNetworks { get; set; } = [];

    /// IPs individuais do proxy confiável.
    public string[] KnownProxies { get; set; } = [];

    public bool IsValid() =>
        KnownNetworks.All(n => IPNetwork.TryParse(n.Trim(), out _)) && KnownProxies.All(p => IPAddress.TryParse(p.Trim(), out _));
}

public static class ForwardedHeadersSetup
{
    public static IServiceCollection AddAlmoSeisForwardedHeaders(this IServiceCollection services)
    {
        services.AddOptions<ForwardedHeadersSettings>()
            .BindConfiguration(ForwardedHeadersSettings.Section)
            .Validate(s => s.IsValid(), "ForwardedHeaders:KnownNetworks aceita CIDR (10.0.0.0/23) e KnownProxies aceita IPs.")
            .ValidateOnStart();

        // Lido na resolução (não no registro): a configuração do Key Vault e a dos testes chegam depois do Program.
        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<ForwardedHeadersSettings>>((options, settings) =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            // Só a entrada mais à direita, a que o proxy confiável acrescentou: o que o cliente mandou antes dela é ignorado.
            options.ForwardLimit = 1;

            var networks = settings.Value.KnownNetworks.Select(n => IPNetwork.Parse(n.Trim())).ToList();
            var proxies = settings.Value.KnownProxies.Select(p => IPAddress.Parse(p.Trim())).ToList();
            if (networks.Count == 0 && proxies.Count == 0)
            {
                return; // padrão: 127.0.0.0/8 e ::1
            }

            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var network in networks)
            {
                options.KnownIPNetworks.Add(network);
            }

            foreach (var proxy in proxies)
            {
                options.KnownProxies.Add(proxy);
            }
        });
        return services;
    }
}
