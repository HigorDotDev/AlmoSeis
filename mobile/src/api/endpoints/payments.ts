import { critical, sendCritical } from '../critical';
import { apiRequest } from '../client';
import type { Items, Payment, PaymentResult, RegisterPaymentBody, ReversalResult, Uuid } from '../types';

/** 06 §6.10. O If-Match é sempre a versão do pedido. */
export const paymentsApi = {
  register: (orderId: Uuid, body: RegisterPaymentBody, key: string, orderVersion: number) =>
    sendCritical<PaymentResult>(critical.registerPayment(orderId, body, orderVersion), key),
  listForOrder: (orderId: Uuid) => apiRequest<Items<Payment>>('GET', `/orders/${orderId}/payments`),
  get: (id: Uuid) => apiRequest<Payment>('GET', `/payments/${id}`),
  reverse: (id: Uuid, reason: string, key: string, orderVersion: number) =>
    sendCritical<ReversalResult>(critical.reversePayment(id, reason, orderVersion), key),
};
