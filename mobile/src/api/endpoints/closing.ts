import { critical, sendCritical } from '../critical';
import { apiRequest, qs } from '../client';
import type {
  CashMovement,
  CashMovementType,
  ClosingAdjustment,
  ClosingHistoryItem,
  ClosingSummary,
  Items,
  MonthlyClosing,
  Page,
  Uuid,
} from '../types';

/** 06 §6.11. */
export const closingApi = {
  summary: (businessDayId?: Uuid) => apiRequest<ClosingSummary>('GET', `/closing/summary${qs({ businessDayId })}`),
  addCashMovement: (body: { movementType: CashMovementType; amount: string; description: string }, key: string) =>
    sendCritical<CashMovement>(critical.addCashMovement(body), key),
  cashMovements: (businessDayId?: Uuid) =>
    apiRequest<Items<CashMovement>>('GET', `/closing/cash-movements${qs({ businessDayId })}`),
  finalize: (body: { businessDayId: Uuid; countedCash: string; justification: string | null }, key: string, dayVersion: number) =>
    sendCritical<ClosingSummary>(critical.finalizeClosing(body, dayVersion), key),
  history: (f: { from?: string; to?: string; limit?: number; cursor?: string }) =>
    apiRequest<Page<ClosingHistoryItem>>('GET', `/closing/history${qs(f)}`),
  get: (id: Uuid) => apiRequest<ClosingSummary>('GET', `/closing/${id}`),
  monthly: (month: string) => apiRequest<MonthlyClosing>('GET', `/closing/monthly?month=${month}`),
  addAdjustment: (id: Uuid, body: { adjustmentType: string; amount: string; reason: string }, key: string) =>
    sendCritical<ClosingAdjustment>(critical.addAdjustment(id, body), key),
};
