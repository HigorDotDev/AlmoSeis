using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MimeKit;

namespace AlmoSeis.Infrastructure.Email;

/// `Email:Provider` (ADR-0040, ADR-0060). `Smtp` no piloto (VM com Docker Compose); `Acs` continua como opção na Azure;
/// `None` e `Fake` não enviam (só registram em log) e são recusados em Production.
public enum EmailProvider
{
    None,
    Fake,
    Smtp,
    Acs,
}

/// Seção `Email` (07 §7.11). Senha do SMTP e connection string do ACS são segredos: variável de ambiente no piloto
/// (`Email__Smtp__Password`), `user-secrets` local, Key Vault se `KeyVault:Uri` existir.
public sealed class EmailOptions
{
    public const string Section = "Email";

    /// Sem a chave na configuração: `None` (desenvolvimento e testes não enviam nada).
    public EmailProvider Provider { get; set; } = EmailProvider.None;

    /// Remetente: `nao-responda@almoseis.app` ou `AlmoSeis <nao-responda@almoseis.app>`. No ACS vale só o endereço (o nome
    /// de exibição é o do domínio verificado).
    public string From { get; set; } = string.Empty;

    public SmtpEmailOptions Smtp { get; set; } = new();

    public AcsEmailOptions Acs { get; set; } = new();
}

public sealed class SmtpEmailOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    /// Vazio = servidor sem autenticação (ex.: smtp4dev local). Com usuário, a senha é obrigatória.
    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// `true` (padrão): STARTTLS **obrigatório**; servidor que não oferece STARTTLS falha em vez de mandar em claro.
    /// `false` na porta 465: TLS implícito. `false` em outra porta: sem TLS (só fora de Production, ex.: smtp4dev).
    public bool UseStartTls { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 15;

    public SmtpSecurity Security => UseStartTls ? SmtpSecurity.StartTls : Port == 465 ? SmtpSecurity.ImplicitTls : SmtpSecurity.None;
}

public enum SmtpSecurity
{
    None,
    StartTls,
    ImplicitTls,
}

public sealed class AcsEmailOptions
{
    /// `endpoint=https://<recurso>.communication.azure.com/;accesskey=...`.
    public string ConnectionString { get; set; } = string.Empty;
}

/// A API não sobe com o provedor incompleto, nem sem envio real (ou SMTP sem TLS) em Production.
internal sealed class EmailOptionsValidator(IHostEnvironment environment) : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        var failures = new List<string>();
        var production = environment.IsProduction();
        if (production && options.Provider is EmailProvider.None or EmailProvider.Fake)
        {
            failures.Add("Email:Provider None/Fake não é permitido em produção (a recuperação de senha do proprietário depende do e-mail): use Smtp ou Acs.");
        }

        if (options.Provider is EmailProvider.Smtp or EmailProvider.Acs && !TryParseFrom(options.From, out _))
        {
            failures.Add("Email:From precisa ser um endereço de e-mail (ex.: nao-responda@almoseis.app).");
        }

        if (options.Provider == EmailProvider.Smtp)
        {
            var smtp = options.Smtp;
            if (string.IsNullOrWhiteSpace(smtp.Host))
            {
                failures.Add("Email:Smtp:Host é obrigatório com Email:Provider Smtp.");
            }

            if (smtp.Port is < 1 or > 65535)
            {
                failures.Add("Email:Smtp:Port entre 1 e 65535.");
            }

            if (string.IsNullOrEmpty(smtp.Username) != string.IsNullOrEmpty(smtp.Password))
            {
                failures.Add("Email:Smtp:Username e Email:Smtp:Password vão juntos (os dois ou nenhum).");
            }

            if (smtp.TimeoutSeconds is < 1 or > 120)
            {
                failures.Add("Email:Smtp:TimeoutSeconds entre 1 e 120.");
            }

            if (production && smtp.Security == SmtpSecurity.None)
            {
                failures.Add("Email:Smtp sem TLS não é permitido em produção: Email:Smtp:UseStartTls true (587) ou porta 465.");
            }
        }

        if (options.Provider == EmailProvider.Acs && string.IsNullOrWhiteSpace(options.Acs.ConnectionString))
        {
            failures.Add("Email:Acs:ConnectionString é obrigatória com Email:Provider Acs.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    internal static bool TryParseFrom(string from, out MailboxAddress mailbox)
    {
        mailbox = null!;
        if (string.IsNullOrWhiteSpace(from) || !MailboxAddress.TryParse(from.Trim(), out var parsed)
            || parsed.Address.IndexOf('@', StringComparison.Ordinal) <= 0)
        {
            return false;
        }

        mailbox = parsed;
        return true;
    }
}
