using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Notifications.Contracts;
using AlmoSeis.Modules.Notifications.Domain;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Modules.Notifications.Application;

/// Uma notificação para um token (o formato da API de push da Expo, 06 §6.3 "Push").
internal sealed record PushMessage(
    string To,
    string Title,
    string Body,
    IReadOnlyDictionary<string, string> Data,
    string Sound,
    string ChannelId,
    int TtlSeconds);

/// Resultado de uma mensagem do lote, na mesma ordem: `Id` do ticket ou o erro (`details.error` da Expo).
internal sealed record PushTicket(string? Id, string? Error);

/// Recibo de um ticket: nulo = entregue ao FCM/APNs; senão o erro.
internal sealed record PushReceipt(string? Error);

/// Provedor fora do ar, lento, com cota estourada ou que recusou o lote inteiro: a mensagem do outbox tenta de novo.
internal sealed class PushUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// Envio de push (ADR-0049 item 1). `ExpoPushSender` (HTTPS na API da Expo) ou `FakePushSender` (desenvolvimento e
/// testes). Implementações nunca registram o token inteiro em log.
internal interface IPushSender
{
    /// Até `MaxBatch` mensagens; um ticket por mensagem, na mesma ordem.
    Task<IReadOnlyList<PushTicket>> SendAsync(IReadOnlyList<PushMessage> messages, CancellationToken cancellationToken);

    /// Até `MaxReceiptBatch` tickets. Ticket ausente do resultado = recibo ainda não disponível.
    Task<IReadOnlyDictionary<string, PushReceipt>> GetReceiptsAsync(IReadOnlyList<string> ticketIds, CancellationToken cancellationToken);
}

internal static class PushLimits
{
    /// Limite da API de push da Expo por requisição.
    public const int MaxBatch = 100;

    public const int MaxReceiptBatch = 1000;
}

/// Mensagem `WebOrderPushRequested` do outbox, já lida pelo consumidor.
internal sealed record WebOrderPushRequest(Guid EventId, Guid OrderId, PushKind Kind, string FulfillmentType, int MarmitaCount, int ItemCount = 0);

/// Envia o push de um pedido do site no tenant da sessão (`InitializeForSystem`, RLS). Pelo menos uma vez, como o outbox:
/// o reenvio da mesma mensagem pula os registros que já têm entrega gravada (`push_deliveries`), e cada lote de até 100
/// é gravado na sua própria transação, então uma falha no segundo lote não reenvia o primeiro.
internal sealed class WebOrderPushService(
    AppDbContext db,
    ITenantContext tenant,
    IWebOrderAcceptanceReader orders,
    IEmployeeAccessReader access,
    IQueryHandler<GetTenantQuery, TenantDto> tenants,
    IPushSender sender,
    IOptions<PushOptions> options,
    TimeProvider time,
    ILogger<WebOrderPushService> logger)
{
    /// Quantos pushes saíram (testes e log).
    public async Task<int> SendAsync(WebOrderPushRequest request, CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (o.Provider == PushProvider.None)
        {
            return 0;
        }

        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Push sem tenant.");
        var now = time.GetUtcNow();

        // Aviso, não estado (item 10): pedido já aceito, recusado ou vencido não toca o celular de ninguém.
        var state = await orders.GetAsync(request.OrderId, cancellationToken);
        if (state is not { AcceptanceStatus: AcceptanceStatus.AwaitingAcceptance, AcceptanceDeadline: { } deadline } || deadline <= now)
        {
            return 0;
        }

        var minutesLeft = WebOrderPushContent.MinutesLeft(deadline, now);
        if (minutesLeft <= 0)
        {
            return 0;
        }

        var already = await db.Set<PushDelivery>().AsNoTracking()
            .Where(d => d.EventId == request.EventId)
            .Select(d => d.RegistrationId)
            .ToListAsync(cancellationToken);
        var registrations = await db.Set<PushRegistration>().AsNoTracking()
            .Where(r => r.WebOrdersEnabled && r.Platform != PushPlatform.Web && !already.Contains(r.Id))
            .OrderBy(r => r.Id)
            .ToListAsync(cancellationToken);

        // Permissões atuais do funcionário do registro (item 5): quem perdeu `orders.accept` deixa de receber na hora.
        var recipients = new List<PushRegistration>();
        foreach (var registration in registrations)
        {
            var employee = await access.GetAsync(registration.TenantEmployeeId, cancellationToken);
            if (employee is { IsActive: true } && employee.Permissions.Contains(Permissions.Orders.Accept))
            {
                recipients.Add(registration);
            }
        }

        if (recipients.Count == 0)
        {
            return 0;
        }

        var tenantName = recipients.Any(r => r.ShowTenantName) ? await TenantNameAsync(cancellationToken) : null;
        var data = new Dictionary<string, string>
        {
            ["tenantId"] = tenantId.ToString(),
            ["orderId"] = request.OrderId.ToString(),
            ["kind"] = UpperSnakeEnumConverter<PushKind>.ToText(request.Kind),
        };
        var ttl = Math.Max(1, (int)(deadline - now).TotalSeconds);

        var sent = 0;
        foreach (var batch in recipients.Chunk(PushLimits.MaxBatch))
        {
            var messages = batch.Select(r =>
            {
                var (title, body) = WebOrderPushContent.For(request.Kind, request.MarmitaCount, request.FulfillmentType, minutesLeft, r.ShowTenantName ? tenantName : null, request.ItemCount);
                return new PushMessage(r.ExpoPushToken, title, body, data, o.Sound, o.ChannelId, ttl);
            }).ToList();

            var tickets = await sender.SendAsync(messages, cancellationToken);
            if (tickets.Count != batch.Length)
            {
                throw new PushUnavailableException($"A Expo devolveu {tickets.Count} tickets para {batch.Length} mensagens.");
            }

            sent += await RecordAsync(request, batch, tickets, cancellationToken);
        }

        return sent;
    }

    private async Task<int> RecordAsync(WebOrderPushRequest request, PushRegistration[] batch, IReadOnlyList<PushTicket> tickets, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var unregistered = new List<Guid>();
        var ok = 0;
        for (var i = 0; i < batch.Length; i++)
        {
            var ticket = tickets[i];
            db.Add(PushDelivery.Record(request.EventId, batch[i].Id, batch[i].TenantId, request.OrderId, request.Kind, ticket.Id, ticket.Error, now));
            if (ticket.Id is not null)
            {
                ok++;
            }
            else if (ticket.Error == ExpoErrors.DeviceNotRegistered)
            {
                unregistered.Add(batch[i].Id);
            }
            else
            {
                logger.LogWarning("Push recusado pela Expo para o registro {RegistrationId}: {Error}.", batch[i].Id, ticket.Error);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (unregistered.Count > 0)
        {
            await db.Set<PushRegistration>().Where(r => unregistered.Contains(r.Id)).ExecuteDeleteAsync(cancellationToken);
            logger.LogInformation("{Count} registro(s) de push apagado(s): DeviceNotRegistered.", unregistered.Count);
        }

        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return ok;
    }

    private async Task<string> TenantNameAsync(CancellationToken cancellationToken) =>
        (await tenants.HandleAsync(new GetTenantQuery(), cancellationToken)).Name;
}

/// Recibos da Expo conferidos depois de `Push:ReceiptDelayMinutes` (ADR-0049 item 8), no tenant da sessão:
/// `DeviceNotRegistered` apaga o registro; recibo que não aparece em 24 h encerra a conferência sem resultado.
internal sealed class PushReceiptChecker(AppDbContext db, IPushSender sender, TimeProvider time, ILogger<PushReceiptChecker> logger)
{
    public static readonly TimeSpan ReceiptLifetime = TimeSpan.FromHours(24);

    public async Task<int> CheckAsync(IReadOnlyList<string> ticketIds, CancellationToken cancellationToken)
    {
        var deliveries = await db.Set<PushDelivery>()
            .Where(d => d.TicketId != null && ticketIds.Contains(d.TicketId) && d.ReceiptCheckedAt == null)
            .ToListAsync(cancellationToken);
        if (deliveries.Count == 0)
        {
            return 0;
        }

        var receipts = await sender.GetReceiptsAsync(deliveries.Select(d => d.TicketId!).ToList(), cancellationToken);
        var now = time.GetUtcNow();
        var unregistered = new List<Guid>();
        foreach (var delivery in deliveries)
        {
            if (receipts.TryGetValue(delivery.TicketId!, out var receipt))
            {
                delivery.CheckReceipt(receipt.Error, now);
                if (receipt.Error == ExpoErrors.DeviceNotRegistered)
                {
                    unregistered.Add(delivery.RegistrationId);
                }
                else if (receipt.Error is not null)
                {
                    logger.LogWarning("Recibo de push com erro para o registro {RegistrationId}: {Error}.", delivery.RegistrationId, receipt.Error);
                }
            }
            else if (now - delivery.SentAt >= ReceiptLifetime)
            {
                delivery.CheckReceipt(ExpoErrors.ReceiptUnavailable, now);
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (unregistered.Count > 0)
        {
            await db.Set<PushRegistration>().Where(r => unregistered.Contains(r.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return deliveries.Count(d => d.ReceiptCheckedAt is not null);
    }
}
