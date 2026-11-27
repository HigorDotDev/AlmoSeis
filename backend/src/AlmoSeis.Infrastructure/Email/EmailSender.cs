using Microsoft.Extensions.Logging;

namespace AlmoSeis.Infrastructure.Email;

/// E-mail transacional (ADR-0040). Hoje só "esqueci a senha". O provedor sai de `Email:Provider` (07 §7.3).
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken);
}

/// Falha de envio sem o destinatário nem o corpo na mensagem: quem chama registra em log (o corpo leva o token de
/// redefinição, e a resposta do servidor SMTP costuma repetir o endereço).
public sealed class EmailDeliveryException(string message, Exception? inner = null) : Exception(message, inner);

/// `None`/`Fake` (desenvolvimento e testes): não envia, só registra que enviaria (sem o destinatário e sem o corpo,
/// que leva o token). Em Production o validador recusa os dois (`EmailOptionsValidator`).
internal sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        logger.LogInformation("E-mail não enviado (Email:Provider None/Fake): \"{Subject}\".", subject);
        return Task.CompletedTask;
    }
}
