using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Infrastructure.Security;

/// Seção `DataProtection` (ADR-0060). O anel de chaves protege o token de "esqueci a senha" (30 min): sem persistência,
/// cada reinício do contêiner invalidaria os links já enviados.
public sealed class DataProtectionSettings
{
    public const string Section = "DataProtection";

    /// Diretório do anel de chaves (no piloto, um volume Docker montado em `/var/lib/almoseis/dataprotection`). Vazio só em
    /// Development (o padrão do ASP.NET Core, no perfil do usuário).
    public string KeysPath { get; set; } = string.Empty;

    /// Certificado PFX opcional (com chave privada) que cifra as chaves em repouso. Sem ele, as chaves ficam em claro no
    /// volume: quem lê o volume forja tokens de redefinição de senha (risco aceito no piloto, 07 §7.11).
    public string CertificatePath { get; set; } = string.Empty;

    public string CertificatePassword { get; set; } = string.Empty;

    internal X509Certificate2 LoadCertificate() =>
        X509CertificateLoader.LoadPkcs12FromFile(CertificatePath, string.IsNullOrEmpty(CertificatePassword) ? null : CertificatePassword,
            X509KeyStorageFlags.EphemeralKeySet);
}

public static class DataProtectionSetup
{
    /// Nome fixo: o discriminador padrão é o caminho do conteúdo, que muda entre imagem, `dotnet run` e testes.
    public const string ApplicationName = "AlmoSeis";

    /// O diretório é lido na resolução (os testes sobrescrevem a configuração depois do Program). O certificado é lido no
    /// registro, das variáveis de ambiente/appsettings/Key Vault: o ASP.NET Core só aceita o certificado de decifrar pelo
    /// builder (`ProtectKeysWithCertificate`), e um PFX ilegível já impede a API de subir aqui.
    public static IServiceCollection AddAlmoSeisDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);
        var settings = configuration.GetSection(DataProtectionSettings.Section).Get<DataProtectionSettings>() ?? new DataProtectionSettings();
        if (!string.IsNullOrWhiteSpace(settings.CertificatePath))
        {
            builder.ProtectKeysWithCertificate(LoadProtectionCertificate(settings));
        }

        services.AddOptions<DataProtectionSettings>().BindConfiguration(DataProtectionSettings.Section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<DataProtectionSettings>, DataProtectionSettingsValidator>();
        services.AddOptions<KeyManagementOptions>()
            .Configure<IOptions<DataProtectionSettings>, ILoggerFactory>((options, current, loggers) =>
            {
                if (!string.IsNullOrWhiteSpace(current.Value.KeysPath))
                {
                    options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(current.Value.KeysPath), loggers);
                }
            });
        return services;
    }

    internal static X509Certificate2 LoadProtectionCertificate(DataProtectionSettings settings)
    {
        X509Certificate2 certificate;
        try
        {
            certificate = settings.LoadCertificate();
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "DataProtection:CertificatePath não pôde ser lido (arquivo ou DataProtection:CertificatePassword).", ex);
        }

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InvalidOperationException("DataProtection:CertificatePath precisa de um PFX com a chave privada.");
        }

        return certificate;
    }
}

/// Fora de Development a API não sobe sem `KeysPath` absoluto e gravável.
internal sealed class DataProtectionSettingsValidator(IHostEnvironment environment) : IValidateOptions<DataProtectionSettings>
{
    public ValidateOptionsResult Validate(string? name, DataProtectionSettings options)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.KeysPath))
        {
            if (!environment.IsDevelopment())
            {
                failures.Add("DataProtection:KeysPath é obrigatório fora de Development (diretório persistente, ex.: volume Docker).");
            }
        }
        else if (!Path.IsPathFullyQualified(options.KeysPath))
        {
            failures.Add("DataProtection:KeysPath precisa ser um caminho absoluto.");
        }
        else if (!IsWritable(options.KeysPath))
        {
            failures.Add($"DataProtection:KeysPath ({options.KeysPath}) não existe ou não é gravável pelo usuário da API.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsWritable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
