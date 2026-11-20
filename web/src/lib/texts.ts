import type {
  ExpectedPaymentMethod,
  FulfillmentType,
  PublicAddress,
  RejectionReasonCode,
  StoreStatus,
  TimelineStep,
  TrackingStatus,
} from './api/types';

/** Estado da página (ADR-0044 item 6). */
export const STORE_STATUS_TEXT: Record<Exclude<StoreStatus, 'OPEN'>, { title: string; body: string }> = {
  CLOSED: { title: 'Fechado agora', body: 'O restaurante não está recebendo pedidos neste horário.' },
  PAUSED: {
    title: 'Não estamos recebendo pedidos pela página agora',
    body: 'Você pode ver o cardápio, mas o envio está pausado. Tente de novo em alguns minutos.',
  },
  UNAVAILABLE: { title: 'Página indisponível', body: 'Esta página de pedidos não está disponível no momento.' },
};

export const PAYMENT_TEXT: Record<ExpectedPaymentMethod, string> = {
  CASH: 'Dinheiro',
  PIX: 'Pix',
  CARD: 'Cartão',
};

export const FULFILLMENT_TEXT: Record<FulfillmentType, string> = {
  PICKUP: 'Retirada no restaurante',
  DELIVERY: 'Entrega',
};

/** Status mostrado ao cliente (ADR-0047 item 7). */
export const TRACKING_STATUS_TEXT: Record<TrackingStatus, string> = {
  AWAITING_PAYMENT: 'Aguardando pagamento',
  AWAITING_ACCEPTANCE: 'Aguardando o restaurante confirmar',
  REJECTED: 'Pedido recusado',
  EXPIRED: 'Pedido não confirmado',
  WITHDRAWN: 'Você desistiu do pedido',
  CONFIRMED: 'Pedido confirmado',
  IN_PREPARATION: 'Em preparo',
  READY_FOR_DELIVERY: 'Pronto, aguardando o entregador',
  READY_FOR_PICKUP: 'Pronto para retirar',
  OUT_FOR_DELIVERY: 'Saiu para entrega',
  DELIVERED: 'Entregue',
  PICKED_UP: 'Retirado',
  CANCELED: 'Pedido cancelado pelo restaurante',
};

export const FINAL_TRACKING_STATUSES: ReadonlySet<TrackingStatus> = new Set<TrackingStatus>([
  'REJECTED',
  'EXPIRED',
  'WITHDRAWN',
  'DELIVERED',
  'PICKED_UP',
  'CANCELED',
]);

/** O pedido não virou venda: mostra o contato e "Fazer outro pedido" (ADR-0046 item 12). */
export const NOT_ACCEPTED_STATUSES: ReadonlySet<TrackingStatus> = new Set<TrackingStatus>([
  'REJECTED',
  'EXPIRED',
  'WITHDRAWN',
  'CANCELED',
]);

export const TIMELINE_TEXT: Record<TimelineStep, string> = {
  SUBMITTED: 'Pedido enviado',
  ACCEPTED: 'Confirmado pelo restaurante',
  IN_PREPARATION: 'Em preparo',
  READY: 'Pronto',
  OUT_FOR_DELIVERY: 'Saiu para entrega',
  DELIVERED: 'Entregue',
  PICKED_UP: 'Retirado',
  REJECTED: 'Recusado',
  EXPIRED: 'Não confirmado a tempo',
  WITHDRAWN: 'Você desistiu',
  CANCELED: 'Cancelado',
};

/** Recusa (ADR-0046 item 12): título fixo por motivo + o texto do restaurante (exceto em trote). */
export function rejectionText(reasonCode: RejectionReasonCode, message: string | null): { title: string; detail: string | null } {
  switch (reasonCode) {
    case 'ITEM_UNAVAILABLE':
      return { title: 'Um item do seu pedido acabou.', detail: message };
    case 'OUT_OF_DELIVERY_AREA':
      return { title: 'Não entregamos nesse endereço.', detail: message };
    case 'TOO_BUSY':
      return { title: 'Estamos com muitos pedidos agora.', detail: message };
    case 'CLOSING_SOON':
      return { title: 'Estamos fechando.', detail: message };
    case 'SUSPECTED_PRANK':
      return { title: 'O restaurante não pôde aceitar seu pedido.', detail: null };
    case 'BUSINESS_DAY_CLOSED':
      return { title: 'O restaurante encerrou o atendimento.', detail: message };
    case 'OTHER':
    default:
      return { title: message ?? 'O restaurante não pôde aceitar seu pedido.', detail: null };
  }
}

export const EXPIRED_TEXT = 'O restaurante não confirmou seu pedido a tempo. Nada foi cobrado.';
export const WITHDRAWN_TEXT = 'Você desistiu deste pedido. Nada foi cobrado.';
export const CANCELED_TEXT = 'O restaurante cancelou seu pedido. Fale com o restaurante se tiver dúvidas.';

export function formatAddress(address: PublicAddress | null | undefined): string | null {
  if (!address) return null;
  const first = [address.street, address.number].filter(Boolean).join(', ');
  const second = [address.neighborhood, [address.city, address.state].filter(Boolean).join(' - ')].filter(Boolean).join(', ');
  return [first, second].filter(Boolean).join(' · ');
}

const TIME_ZONE = 'America/Sao_Paulo';

export function formatTime(iso: string): string {
  try {
    return new Intl.DateTimeFormat('pt-BR', { hour: '2-digit', minute: '2-digit', timeZone: TIME_ZONE }).format(new Date(iso));
  } catch {
    return iso.slice(11, 16);
  }
}

export function formatDateTime(iso: string): string {
  try {
    return new Intl.DateTimeFormat('pt-BR', {
      day: '2-digit',
      month: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      timeZone: TIME_ZONE,
    }).format(new Date(iso));
  } catch {
    return iso;
  }
}
