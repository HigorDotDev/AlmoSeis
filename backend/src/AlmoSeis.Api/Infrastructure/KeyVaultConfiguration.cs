using Azure.Identity;

namespace AlmoSeis.Api.Infrastructure;

/// Key Vault opcional (ADR-0060). Só entra se `KeyVault:Uri` (variável `KeyVault__Uri`) estiver configurado; sem ele, a
/// configuração vem só de appsettings, variáveis de ambiente e `user-secrets`, como no piloto da VM. Os segredos
/// obrigatórios continuam conferidos ao subir pelos validadores de cada seção (07 §7.11).
public static class KeyVaultConfiguration
{
    public const string UriKey = "KeyVault:Uri";

    public static IConfigurationManager AddAlmoSeisKeyVault(this IConfigurationManager configuration)
    {
        var value = configuration[UriKey];
        if (string.IsNullOrWhiteSpace(value))
        {
            return configuration;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("KeyVault:Uri precisa ser a URL https do cofre (ex.: https://almoseis-prod.vault.azure.net/).");
        }

        // Nome do segredo com "--" no lugar de ":" (ex.: Jwt--SigningKeyPem), o padrão do KeyVaultSecretManager.
        configuration.AddAzureKeyVault(uri, new DefaultAzureCredential());
        return configuration;
    }
}
