using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace AlmoSeis.Infrastructure.Email;

/// `Email:Provider Smtp` (ADR-0060): MailKit, uma conexão por envio (o volume é o da recuperação de senha). STARTTLS
/// obrigatório por padrão, certificado validado pela cadeia do sistema. Sem log de protocolo (levaria o corpo, com o
/// token, e a senha do AUTH).
internal sealed class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var smtp = settings.Smtp;
        EmailOptionsValidator.TryParseFrom(settings.From, out var from);

        using var message = new MimeMessage();
        message.From.Add(from);
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient { Timeout = smtp.TimeoutSeconds * 1000 };
        try
        {
            await client.ConnectAsync(smtp.Host, smtp.Port, ToMailKit(smtp.Security), cancellationToken);
            if (!string.IsNullOrEmpty(smtp.Username))
            {
                await client.AuthenticateAsync(smtp.Username, smtp.Password, cancellationToken);
            }

            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (SmtpCommandException ex)
        {
            // A resposta do servidor costuma repetir o endereço do destinatário: fica só o código.
            throw new EmailDeliveryException($"O servidor SMTP recusou o envio ({(int)ex.StatusCode} {ex.ErrorCode}).");
        }
        catch (AuthenticationException ex)
        {
            throw new EmailDeliveryException("Falha de autenticação no servidor SMTP (Email:Smtp:Username/Password).", ex);
        }
        catch (Exception ex) when (ex is SmtpProtocolException or SslHandshakeException or System.Net.Sockets.SocketException
            or ServiceNotConnectedException or NotSupportedException or IOException or TimeoutException)
        {
            throw new EmailDeliveryException($"Falha ao falar com o servidor SMTP {smtp.Host}:{smtp.Port}.", ex);
        }

        logger.LogInformation("E-mail enviado por SMTP: \"{Subject}\".", subject);
    }

    internal static SecureSocketOptions ToMailKit(SmtpSecurity security) => security switch
    {
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.ImplicitTls => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.None,
    };
}
