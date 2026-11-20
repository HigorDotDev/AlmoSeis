/** Pedidos do site (ADR-0046), sem React: prazo, conflito de aceite, validação da recusa. */
import type { EstimatedReadyMinutes, Order, RejectionReasonCode } from '@/api/types';

export const ESTIMATED_MINUTES: EstimatedReadyMinutes[] = [15, 30, 45, 60];

/** Motivos que a pessoa escolhe (ADR-0046 item 12); `BUSINESS_DAY_CLOSED` é só do sistema. */
export type RejectReason = Exclude<RejectionReasonCode, 'BUSINESS_DAY_CLOSED'>;
export const REJECT_REASONS: RejectReason[] = ['ITEM_UNAVAILABLE', 'OUT_OF_DELIVERY_AREA', 'TOO_BUSY', 'CLOSING_SOON', 'SUSPECTED_PRANK', 'OTHER'];

export const REJECT_MESSAGE_MIN = 5;
export const REJECT_MESSAGE_MAX = 200;

/** Lembrete do ADR-0046 item 15: aos 3 minutos do fim o card fica em destaque. */
export const URGENT_MS = 3 * 60_000;

/** "#12", ou "Pedido do site" enquanto não há número (só nasce no aceite). */
export function orderNumberLabel(orderNumber: number | null): string {
  return orderNumber === null ? 'Pedido do site' : `#${orderNumber}`;
}

export interface Countdown {
  /** "9:41"; "0:00" depois do prazo. */
  label: string;
  remainingMs: number;
  expired: boolean;
  urgent: boolean;
}

/**
 * Contagem até `acceptanceDeadline` (ADR-0046 item 15) pelo relógio do aparelho. É só aviso: quem decide se o prazo
 * venceu é o servidor (`409 WebOrderExpired`), então os botões não somem no zero.
 */
export function countdown(deadline: string | null, now: number = Date.now()): Countdown | null {
  if (!deadline) return null;
  const remainingMs = Math.max(0, new Date(deadline).getTime() - now);
  const totalSeconds = Math.ceil(remainingMs / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return {
    label: `${minutes}:${String(seconds).padStart(2, '0')}`,
    remainingMs,
    expired: remainingMs === 0,
    urgent: remainingMs > 0 && remainingMs <= URGENT_MS,
  };
}

/** Texto do `409 VersionConflict` no aceite/recusa, a partir do `current` (ADR-0046 item 9: "Já aceito por Ana"). */
export function acceptanceConflictText(current: Order | null | undefined): string {
  if (!current) return 'Outra pessoa mexeu neste pedido. Confira e refaça.';
  switch (current.acceptanceStatus) {
    case 'ACCEPTED':
      return current.acceptedBy ? `Já aceito por ${current.acceptedBy.displayName}.` : 'Este pedido já foi aceito.';
    case 'REJECTED':
      return current.rejection?.rejectedBy ? `Já recusado por ${current.rejection.rejectedBy.displayName}.` : 'Este pedido já foi recusado.';
    case 'EXPIRED':
      return 'O prazo acabou, o pedido expirou.';
    case 'WITHDRAWN':
      return 'O cliente desistiu deste pedido.';
    default:
      return 'O pedido mudou enquanto você decidia. Confira e refaça.';
  }
}

/** `message` da recusa: opcional, 5 a 200 caracteres; obrigatória em `OTHER` (06 §6.9). */
export function rejectionMessageError(reason: RejectReason | null, message: string): string | null {
  const text = message.trim();
  if (reason === 'OTHER' && text.length === 0) return 'Escreva o motivo para o cliente.';
  if (text.length > 0 && text.length < REJECT_MESSAGE_MIN) return `Pelo menos ${REJECT_MESSAGE_MIN} caracteres.`;
  if (text.length > REJECT_MESSAGE_MAX) return `No máximo ${REJECT_MESSAGE_MAX} caracteres.`;
  return null;
}
