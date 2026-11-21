/**
 * Push dos pedidos do site (ADR-0049, 06 §6.3): o que vale igual em todas as plataformas. Canal e som, a leitura do
 * `data` da notificação e o destino de um toque, guardado até haver sessão no estabelecimento certo.
 */
import { create } from 'zustand';

import type { PushRegistration, WebOrderPushData, WebOrderPushKind } from '@/api/types';

import type { PushPermission } from './notifications.types';

/** `Push:ChannelId` e `Push:Sound` do servidor (06 §6.3). O arquivo é `assets/sounds/web_order.wav`. */
export const WEB_ORDERS_CHANNEL_ID = 'web-orders';
export const WEB_ORDERS_SOUND = 'web_order.wav';

const KINDS: readonly WebOrderPushKind[] = ['WEB_ORDER_AWAITING_ACCEPTANCE', 'WEB_ORDER_REMINDER'];

/** `data` com só `tenantId`, `orderId` e `kind` conhecido; qualquer outra coisa é ignorada. */
export function parseWebOrderPushData(data: unknown): WebOrderPushData | null {
  if (!data || typeof data !== 'object') return null;
  const d = data as Record<string, unknown>;
  if (typeof d.tenantId !== 'string' || !d.tenantId) return null;
  if (typeof d.orderId !== 'string' || !/^[0-9a-fA-F-]{36}$/.test(d.orderId)) return null;
  if (typeof d.kind !== 'string' || !KINDS.includes(d.kind as WebOrderPushKind)) return null;
  return { tenantId: d.tenantId, orderId: d.orderId, kind: d.kind as WebOrderPushKind };
}

/** Um toque esquecido não abre um pedido velho depois (o prazo de aceite é de minutos). */
export const PUSH_TARGET_MAX_AGE_MS = 30 * 60_000;

export interface PushTarget extends WebOrderPushData {
  tappedAt: number;
}

export type PushTargetDecision = 'open' | 'drop' | 'wait';

/**
 * O que fazer com o toque: sem sessão → espera o login (ou o PIN); sessão em outro estabelecimento → ignora
 * (06 §6.3: notificação de outro `tenantId` é ignorada); mesmo estabelecimento → abre o pedido.
 */
export function decidePushTarget(target: PushTarget, sessionTenantId: string | null, now = Date.now()): PushTargetDecision {
  if (now - target.tappedAt > PUSH_TARGET_MAX_AGE_MS) return 'drop';
  if (!sessionTenantId) return 'wait';
  return target.tenantId === sessionTenantId ? 'open' : 'drop';
}

interface PushTargetState {
  target: PushTarget | null;
  setTarget(target: PushTarget | null): void;
}

export const usePushTarget = create<PushTargetState>((set) => ({
  target: null,
  setTarget: (target) => set({ target }),
}));

/** Por que o aparelho não está registrado (para a tela de configurações). */
export type PushUnavailable = 'web' | 'noProjectId' | 'error' | null;

interface PushStatusState {
  permission: PushPermission | null;
  /** Resposta do último `PUT /devices/push-token` desta sessão. */
  registration: PushRegistration | null;
  unavailable: PushUnavailable;
  errorMessage: string | null;
  set(patch: Partial<Omit<PushStatusState, 'set' | 'reset'>>): void;
  reset(): void;
}

export const usePushStatus = create<PushStatusState>((set) => ({
  permission: null,
  registration: null,
  unavailable: null,
  errorMessage: null,
  set: (patch) => set(patch),
  reset: () => set({ registration: null, errorMessage: null }),
}));
