import { critical, sendCritical } from '../critical';
import { apiRequest, qs } from '../client';
import type { BusinessDay, OpenBusinessDayResult, Page } from '../types';

/** 06 §6.6. */
export const businessDaysApi = {
  current: () => apiRequest<BusinessDay>('GET', '/business-days/current'),
  open: (body: { openingBalance: string; confirmSameDate: boolean }, key: string) =>
    sendCritical<OpenBusinessDayResult>(critical.openBusinessDay(body), key),
  list: (f: { from?: string; to?: string; limit?: number; cursor?: string }) =>
    apiRequest<Page<BusinessDay>>('GET', `/business-days${qs(f)}`),
};
