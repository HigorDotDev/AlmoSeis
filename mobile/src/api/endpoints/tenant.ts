import { apiRequest } from '../client';
import type {
  Employee,
  ExistingAccount,
  Invitation,
  InvitationPreview,
  Items,
  NewAccount,
  Platform,
  PushRegistration,
  PushTokenBody,
  Role,
  RoleInfo,
  Session,
  SharedDevice,
  SharedDeviceSummary,
  Subscription,
  Tenant,
  TenantPatch,
  TenantSettings,
  Uuid,
} from '../types';

/** 06 §6.2 e §6.4. */
export const tenantApi = {
  get: () => apiRequest<Tenant>('GET', '/tenant'),
  /** `labels` troca o conjunto inteiro (06 §6.4); o comportamento não é alterável pelo app. */
  update: (body: TenantPatch, version: number) =>
    apiRequest<Tenant>('PATCH', '/tenant', { body, ifMatch: version }),
  settings: () => apiRequest<TenantSettings>('GET', '/tenant/settings'),
  updateSettings: (body: Partial<Omit<TenantSettings, 'version'>>, version: number) =>
    apiRequest<TenantSettings>('PATCH', '/tenant/settings', { body, ifMatch: version }),
  subscription: () => apiRequest<Subscription>('GET', '/subscription'),
  roles: () => apiRequest<Items<RoleInfo>>('GET', '/roles'),
};

export const employeesApi = {
  list: (status?: 'ACTIVE' | 'INACTIVE') => apiRequest<Items<Employee>>('GET', `/employees${status ? `?status=${status}` : ''}`),
  get: (id: Uuid) => apiRequest<Employee>('GET', `/employees/${id}`),
  update: (id: Uuid, body: { displayName?: string; roles?: Role[] }, version: number) =>
    apiRequest<Employee>('PATCH', `/employees/${id}`, { body, ifMatch: version }),
  deactivate: (id: Uuid, version: number) => apiRequest<Employee>('POST', `/employees/${id}/deactivate`, { ifMatch: version }),
  reactivate: (id: Uuid, version: number) => apiRequest<Employee>('POST', `/employees/${id}/reactivate`, { ifMatch: version }),
  passwordResetCode: (id: Uuid) => apiRequest<Invitation>('POST', `/employees/${id}/password-reset-code`),
  invite: (roles: Role[]) => apiRequest<Invitation>('POST', '/employees/invitations', { body: { roles } }),
  invitations: (status?: 'PENDING') =>
    apiRequest<Items<Invitation>>('GET', `/employees/invitations${status ? `?status=${status}` : ''}`),
  revokeInvitation: (id: Uuid) => apiRequest<void>('POST', `/employees/invitations/${id}/revoke`),
};

export const invitationsApi = {
  preview: (code: string) => apiRequest<InvitationPreview>('GET', `/invitations/${encodeURIComponent(code)}`, { anonymous: true }),
  accept: (
    code: string,
    body: { newAccount: NewAccount | null; existingAccount: ExistingAccount | null; displayName: string; installationId: string },
  ) => apiRequest<Session>('POST', `/invitations/${encodeURIComponent(code)}/accept`, { body, anonymous: true }),
};

export const devicesApi = {
  register: (body: { installationId: string; name: string; platform: Platform }) =>
    apiRequest<SharedDevice>('POST', '/devices/shared', { body }),
  list: () => apiRequest<Items<SharedDeviceSummary>>('GET', '/devices/shared'),
  revoke: (id: Uuid) => apiRequest<void>('POST', `/devices/shared/${id}/revoke`),
};

/** 06 §6.3 "Push dos pedidos do site" (ADR-0049): só o próprio aparelho escreve; sem `Idempotency-Key` nem `If-Match`. */
export const pushTokenApi = {
  put: (body: PushTokenBody, timeoutMs?: number) => apiRequest<PushRegistration>('PUT', '/devices/push-token', { body, timeoutMs }),
  remove: (timeoutMs?: number) => apiRequest<void>('DELETE', '/devices/push-token', { timeoutMs }),
};
