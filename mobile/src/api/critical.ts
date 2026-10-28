/**
 * Escritas [I] (06): descritas como dados para poderem ser gravadas em `pending_operations` e reenviadas
 * com a mesma chave e o mesmo corpo (08 §8.5). Única fonte desses caminhos.
 */
import { apiRequest, type ApiResult, type HttpMethod } from './client';
import type {
  CashMovementType,
  CreateOrderBody,
  NewItemInput,
  ProductionStatus,
  RegisterPaymentBody,
  UpdateOrderBody,
  Uuid,
} from './types';

export interface CriticalRequest {
  method: HttpMethod;
  path: string;
  body?: unknown;
  /** Versão em cache no momento da confirmação (ADR-0034). */
  ifMatch?: number;
}

export const critical = {
  openBusinessDay: (body: { openingBalance: string; confirmSameDate: boolean }): CriticalRequest => ({
    method: 'POST',
    path: '/business-days',
    body,
  }),
  createOrder: (body: CreateOrderBody): CriticalRequest => ({ method: 'POST', path: '/orders', body }),
  addItems: (orderId: Uuid, items: NewItemInput[], version: number): CriticalRequest => ({
    method: 'POST',
    path: `/orders/${orderId}/items`,
    body: { items },
    ifMatch: version,
  }),
  addWeighings: (orderId: Uuid, weighings: { weightGrams: number }[], version: number): CriticalRequest => ({
    method: 'POST',
    path: `/orders/${orderId}/weighings`,
    body: { weighings },
    ifMatch: version,
  }),
  registerPayment: (orderId: Uuid, body: RegisterPaymentBody, orderVersion: number): CriticalRequest => ({
    method: 'POST',
    path: `/orders/${orderId}/payments`,
    body,
    ifMatch: orderVersion,
  }),
  reversePayment: (paymentId: Uuid, reason: string, orderVersion: number): CriticalRequest => ({
    method: 'POST',
    path: `/payments/${paymentId}/reverse`,
    body: { reason },
    ifMatch: orderVersion,
  }),
  addCashMovement: (body: { movementType: CashMovementType; amount: string; description: string }): CriticalRequest => ({
    method: 'POST',
    path: '/closing/cash-movements',
    body,
  }),
  finalizeClosing: (body: { businessDayId: Uuid; countedCash: string; justification: string | null }, dayVersion: number): CriticalRequest => ({
    method: 'POST',
    path: '/closing/finalize',
    body,
    ifMatch: dayVersion,
  }),
  addAdjustment: (closingId: Uuid, body: { adjustmentType: string; amount: string; reason: string }): CriticalRequest => ({
    method: 'POST',
    path: `/closing/${closingId}/adjustments`,
    body,
  }),
};

/** Escritas só [M] (If-Match, sem Idempotency-Key): passam por `useCriticalMutation` com `idempotent: false`. */
export const versioned = {
  updateOrder: (orderId: Uuid, body: UpdateOrderBody, version: number): CriticalRequest => ({
    method: 'PATCH',
    path: `/orders/${orderId}`,
    body,
    ifMatch: version,
  }),
  updateItem: (orderId: Uuid, itemId: Uuid, body: { quantity?: number; notes?: string | null }, version: number): CriticalRequest => ({
    method: 'PATCH',
    path: `/orders/${orderId}/items/${itemId}`,
    body,
    ifMatch: version,
  }),
  removeItem: (orderId: Uuid, itemId: Uuid, version: number): CriticalRequest => ({
    method: 'DELETE',
    path: `/orders/${orderId}/items/${itemId}`,
    ifMatch: version,
  }),
  removeWeighing: (orderId: Uuid, weighingId: Uuid, version: number): CriticalRequest => ({
    method: 'DELETE',
    path: `/orders/${orderId}/weighings/${weighingId}`,
    ifMatch: version,
  }),
  setStatus: (orderId: Uuid, productionStatus: ProductionStatus, version: number): CriticalRequest => ({
    method: 'PATCH',
    path: `/orders/${orderId}/status`,
    body: { productionStatus },
    ifMatch: version,
  }),
  cancelOrder: (orderId: Uuid, reason: string, version: number): CriticalRequest => ({
    method: 'POST',
    path: `/orders/${orderId}/cancel`,
    body: { reason },
    ifMatch: version,
  }),
};

/** `idempotencyKey` nulo: escrita só [M], protegida pelo If-Match (reenviar com a mesma versão dá 409, nunca duplica). */
export function sendCritical<T>(request: CriticalRequest, idempotencyKey: string | null): Promise<ApiResult<T>> {
  return apiRequest<T>(request.method, request.path, {
    body: request.body,
    ifMatch: request.ifMatch,
    idempotencyKey: idempotencyKey ?? undefined,
  });
}
