import { critical, sendCritical } from '../critical';
import { apiRequest, qs } from '../client';
import type {
  CreateOrderBody,
  Items,
  NewItemInput,
  Order,
  OrderAlert,
  OrderFilters,
  OrderHistoryEntry,
  OrderSummary,
  Page,
  ProductionBoard,
  ProductionStatus,
  UpdateOrderBody,
  Uuid,
} from '../types';

/** 06 §6.9. */
export const ordersApi = {
  list: (f: OrderFilters) => apiRequest<Page<OrderSummary>>('GET', `/orders${qs(f)}`),
  get: (id: Uuid) => apiRequest<Order>('GET', `/orders/${id}`),
  create: (body: CreateOrderBody, key: string) => sendCritical<Order>(critical.createOrder(body), key),
  update: (id: Uuid, body: UpdateOrderBody, version: number) =>
    apiRequest<Order>('PATCH', `/orders/${id}`, { body, ifMatch: version }),
  addItems: (id: Uuid, items: NewItemInput[], key: string, version: number) =>
    sendCritical<Order>(critical.addItems(id, items, version), key),
  updateItem: (id: Uuid, itemId: Uuid, body: { quantity?: number; notes?: string | null }, version: number) =>
    apiRequest<Order>('PATCH', `/orders/${id}/items/${itemId}`, { body, ifMatch: version }),
  removeItem: (id: Uuid, itemId: Uuid, version: number) =>
    apiRequest<Order>('DELETE', `/orders/${id}/items/${itemId}`, { ifMatch: version }),
  addWeighings: (id: Uuid, weighings: { weightGrams: number }[], key: string, version: number) =>
    sendCritical<Order>(critical.addWeighings(id, weighings, version), key),
  removeWeighing: (id: Uuid, weighingId: Uuid, version: number) =>
    apiRequest<Order>('DELETE', `/orders/${id}/weighings/${weighingId}`, { ifMatch: version }),
  setStatus: (id: Uuid, productionStatus: ProductionStatus, version: number) =>
    apiRequest<Order>('PATCH', `/orders/${id}/status`, { body: { productionStatus }, ifMatch: version }),
  cancel: (id: Uuid, reason: string, version: number) =>
    apiRequest<Order>('POST', `/orders/${id}/cancel`, { body: { reason }, ifMatch: version }),
  history: (id: Uuid, cursor?: string) => apiRequest<Page<OrderHistoryEntry>>('GET', `/orders/${id}/history${qs({ cursor })}`),
  alerts: (id: Uuid) => apiRequest<Items<OrderAlert>>('GET', `/orders/${id}/alerts`),
  acknowledgeAlerts: (id: Uuid, eventIds: Uuid[]) =>
    apiRequest<void>('POST', `/orders/${id}/alerts/acknowledge`, { body: { eventIds } }),
  production: () => apiRequest<ProductionBoard>('GET', '/production/orders'),
};
