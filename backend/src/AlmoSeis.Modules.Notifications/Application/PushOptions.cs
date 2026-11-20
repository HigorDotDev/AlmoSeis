using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Modules.Notifications.Application;

/// `Push:Provider` (ADR-0049 item 1). `Expo` em homologação e produção; `Fake` em desenvolvimento e testes (sem rede);
/// `None` desliga o envio (a mensagem do outbox sai da fila sem push).
public enum PushProvider
{
    None,
    Fake,
    Expo,
}

/// Seção `Push` (07 §7.11). O token de acesso da Expo vem do Key Vault (homologação/produção) ou do `user-secrets`
/// (desenvolvimento), nunca do repositório.
public sealed class PushOptions
{
    public const string Section = "Push";

    /// Sem a chave na configuração: `Fake`, como o `Maps:Provider` (o `appsettings.json` versionado diz `Expo`).
    public PushProvider Provider { get; set; } = PushProvider.Fake;

    /// Canal Android criado pelo app, com importância alta e o som próprio (item 7).
    public string ChannelId { get; set; } = "web-orders";

    /// Arquivo de som do pacote do app (iOS; no Android o som é o do canal).
    public string Sound { get; set; } = "web_order.wav";

    /// Tentativas da mensagem do outbox antes de desistir (item 8).
    public int MaxAttempts { get; set; } = 3;

    public int RetryDelaySeconds { get; set; } = 30;

    /// Recibos conferidos depois disso (item 8).
    public int ReceiptDelayMinutes { get; set; } = 15;

    public bool MaintenanceJobEnabled { get; set; } = true;

    public int MaintenanceIntervalMinutes { get; set; } = 5;

    /// Registro sem uso (sem novo `PUT /devices/push-token`) há esse tempo é apagado (item 4).
    public int RegistrationRetentionDays { get; set; } = 30;

    public int DeliveryRetentionDays { get; set; } = 7;

    public ExpoPushOptions Expo { get; set; } = new();
}

public sealed class ExpoPushOptions
{
    /// "Enhanced push security" da Expo: opcional. Vazio = sem `Authorization`.
    public string AccessToken { get; set; } = string.Empty;

    public string SendUrl { get; set; } = "https://exp.host/--/api/v2/push/send";

    public string ReceiptsUrl { get; set; } = "https://exp.host/--/api/v2/push/getReceipts";

    public int TimeoutSeconds { get; set; } = 5;
}

/// A API não sobe com `Fake` em produção, URL inválida ou tempos fora da faixa.
internal sealed class PushOptionsValidator(IHostEnvironment environment) : IValidateOptions<PushOptions>
{
    public ValidateOptionsResult Validate(string? name, PushOptions options)
    {
        var failures = new List<string>();
        if (options.Provider == PushProvider.Fake && environment.IsProduction())
        {
            failures.Add("Push:Provider Fake não é permitido em produção.");
        }

        if (options.Provider == PushProvider.Expo)
        {
            foreach (var (key, url) in new[] { ("SendUrl", options.Expo.SendUrl), ("ReceiptsUrl", options.Expo.ReceiptsUrl) })
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
                {
                    failures.Add($"Push:Expo:{key} precisa ser uma URL https.");
                }
            }
        }

        if (options.Expo.TimeoutSeconds is < 1 or > 30)
        {
            failures.Add("Push:Expo:TimeoutSeconds entre 1 e 30.");
        }

        if (options.MaxAttempts < 1 || options.RetryDelaySeconds < 0 || options.ReceiptDelayMinutes < 1 || options.MaintenanceIntervalMinutes < 1
            || options.RegistrationRetentionDays < 1 || options.DeliveryRetentionDays < 1)
        {
            failures.Add("Push: tentativas, intervalos e retenções precisam ser positivos.");
        }

        if (string.IsNullOrWhiteSpace(options.ChannelId) || string.IsNullOrWhiteSpace(options.Sound))
        {
            failures.Add("Push:ChannelId e Push:Sound são obrigatórios.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
