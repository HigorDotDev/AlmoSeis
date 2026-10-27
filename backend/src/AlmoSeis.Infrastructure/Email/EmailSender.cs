using Microsoft.Extensions.Logging;

namespace AlmoSeis.Infrastructure.Email;

/// E-mail transacional (ADR-0040). Hoje só "esqueci a senha".
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken);
}

/// Padrão de desenvolvimento e testes: não envia, só registra que enviaria (sem o corpo, que leva o token).
/// O provedor do ADR-0040 substitui este registro quando a infraestrutura de e-mail existir.
internal sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        logger.LogInformation("E-mail não enviado (sem provedor configurado): \"{Subject}\".", subject);
        return Task.CompletedTask;
    }
}
