namespace AlmoSeis.Modules.Ordering.Contracts;

/// Mensagem interna do outbox que pede o push de um pedido do site (ADR-0049 itens 2 e 8). Gravada na mesma transação
/// do envio do pedido, junto do `OrderAwaitingAcceptance` do SignalR, e entregue pelo dispatcher ao consumidor do módulo
/// Notifications (não vai para o SignalR). O lembrete (ADR-0046 item 15) é uma segunda mensagem, agendada pelo
/// `available_at` para `WebOrders:ReminderMinutes` depois do envio.
///
/// `data`: `{ "kind": "WEB_ORDER_AWAITING_ACCEPTANCE" | "WEB_ORDER_REMINDER", "fulfillmentType": "DELIVERY" | "PICKUP",
/// "marmitaCount": 2 }`. Sem nome, telefone, endereço nem valor (ADR-0049 item 3, ADR-0052 item 7).
public static class WebOrderPushMessage
{
    public const string EventType = "WebOrderPushRequested";

    public const string AwaitingAcceptance = "WEB_ORDER_AWAITING_ACCEPTANCE";

    public const string Reminder = "WEB_ORDER_REMINDER";
}

/// Estado do aceite no momento do envio do push: pedido já aceito, recusado ou vencido não gera aviso.
public sealed record WebOrderAcceptanceState(Guid OrderId, AcceptanceStatus AcceptanceStatus, DateTimeOffset? AcceptanceDeadline);

/// Para o módulo Notifications, no tenant da sessão (RLS).
public interface IWebOrderAcceptanceReader
{
    Task<WebOrderAcceptanceState?> GetAsync(Guid orderId, CancellationToken cancellationToken);
}
