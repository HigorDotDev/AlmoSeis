import { apiRequest, qs } from '../client';
import type { AuditLogEntry, ChangesPage, OperationInfo, Page } from '../types';

/** 06 §6.12 e §6.13. */
export const syncApi = {
  changes: (since: number, limit = 500) => apiRequest<ChangesPage>('GET', `/sync/changes?since=${since}&limit=${limit}`),
  operation: (key: string) => apiRequest<OperationInfo>('GET', `/operations/${encodeURIComponent(key)}`),
};

export const auditApi = {
  list: (f: {
    entityType?: string;
    entityId?: string;
    actorEmployeeId?: string;
    from?: string;
    to?: string;
    limit?: number;
    cursor?: string;
  }) => apiRequest<Page<AuditLogEntry>>('GET', `/audit-logs${qs(f)}`),
};
