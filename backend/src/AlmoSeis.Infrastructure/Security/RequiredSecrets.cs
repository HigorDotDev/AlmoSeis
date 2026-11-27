using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Infrastructure.Security;

/// A chave do JWT é validada ao subir (e não só na primeira requisição autenticada): sem ela, ou com a chave efêmera em
/// produção, a API não sobe. A mensagem nunca leva o conteúdo da chave.
internal sealed class JwtOptionsValidator(IHostEnvironment environment) : IValidateOptions<JwtOptions>
{
    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SigningKeyPem))
        {
            if (!options.AllowEphemeralKey)
            {
                return ValidateOptionsResult.Fail("Jwt:SigningKeyPem não configurada (variável Jwt__SigningKeyPem, chave RSA privada em PEM).");
            }

            return environment.IsProduction()
                ? ValidateOptionsResult.Fail("Jwt:AllowEphemeralKey não é permitido em produção: configure Jwt:SigningKeyPem.")
                : ValidateOptionsResult.Success;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(options.SigningKeyPem);
            return rsa.KeySize >= 2048
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail("Jwt:SigningKeyPem precisa de uma chave RSA de pelo menos 2048 bits.");
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return ValidateOptionsResult.Fail("Jwt:SigningKeyPem não é uma chave RSA privada em PEM válida.");
        }
    }
}

/// `ConnectionStrings:App` e `:Platform` conferidas ao subir, em todos os ambientes (a resolução do `DbContext` também
/// confere, mas só na primeira consulta).
public sealed class ConnectionStringsCheck
{
    public string? App { get; set; }

    public string? Platform { get; set; }

    internal static bool IsSet(string? value) => !string.IsNullOrWhiteSpace(value) && value != "REPLACE_ME";
}

internal static class RequiredSecretsRegistration
{
    public static void AddRequiredSecretsValidation(this IServiceCollection services)
    {
        services.AddOptions<ConnectionStringsCheck>()
            .Configure<IConfiguration>((check, configuration) =>
            {
                check.App = configuration.GetConnectionString("App");
                check.Platform = configuration.GetConnectionString("Platform");
            })
            .Validate(c => ConnectionStringsCheck.IsSet(c.App), "ConnectionStrings:App não configurada (variável ConnectionStrings__App).")
            .Validate(c => ConnectionStringsCheck.IsSet(c.Platform), "ConnectionStrings:Platform não configurada (variável ConnectionStrings__Platform).")
            .ValidateOnStart();
    }
}
