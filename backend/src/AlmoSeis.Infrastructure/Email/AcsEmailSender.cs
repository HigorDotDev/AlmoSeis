using Azure;
using Azure.Communication.Email;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Infrastructure.Email;

/// `Email:Provider Acs` (ADR-0040): Azure Communication Services Email, para quando a API voltar à Azure. O envio é
/// aceito pelo serviço (`WaitUntil.Started`) e entregue de forma assíncrona; o cliente é criado uma vez.
internal sealed class AcsEmailSender(IOptions<EmailOptions> options, ILogger<AcsEmailSender> logger) : IEmailSender
{
    private readonly Lazy<EmailClient> _client = new(() => new EmailClient(options.Value.Acs.ConnectionString));

    public async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        EmailOptionsValidator.TryParseFrom(options.Value.From, out var from);
        var message = new EmailMessage(from.Address, to, new EmailContent(subject) { PlainText = body });
        try
        {
            await _client.Value.SendAsync(WaitUntil.Started, message, cancellationToken);
        }
        catch (RequestFailedException ex)
        {
            throw new EmailDeliveryException($"O Azure Communication Services recusou o envio ({ex.Status} {ex.ErrorCode}).");
        }

        logger.LogInformation("E-mail enviado pelo ACS: \"{Subject}\".", subject);
    }
}
