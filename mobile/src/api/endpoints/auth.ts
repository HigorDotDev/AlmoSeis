import { apiRequest } from '../client';
import type { DeviceEmployees, LoginBody, Me, Session, SignupBody, Tokens, Uuid } from '../types';

/** 06 §6.3. */
export const authApi = {
  signup: (body: SignupBody) => apiRequest<Session>('POST', '/auth/signup', { body, anonymous: true }),
  login: (body: LoginBody) => apiRequest<Session>('POST', '/auth/login', { body, anonymous: true }),
  refresh: (refreshToken: string, installationId: string) =>
    apiRequest<Tokens>('POST', '/auth/refresh', { body: { refreshToken, installationId }, anonymous: true }),
  logout: (refreshToken: string) => apiRequest<void>('POST', '/auth/logout', { body: { refreshToken } }),
  switchTenant: (tenantId: Uuid) => apiRequest<Session>('POST', '/auth/switch-tenant', { body: { tenantId } }),
  me: () => apiRequest<Me>('GET', '/auth/me'),
  setPin: (pin: string, currentPassword: string) => apiRequest<void>('PUT', '/auth/me/pin', { body: { pin, currentPassword } }),
  /** Os outros aparelhos da pessoa saem no próximo refresh; este continua logado. */
  changePassword: (currentPassword: string, newPassword: string) =>
    apiRequest<void>('PUT', '/auth/me/password', { body: { currentPassword, newPassword } }),
  resetPassword: (body: { code: string; login: string; newPassword: string } | { emailToken: string; newPassword: string }) =>
    apiRequest<void>('POST', '/auth/password/reset', { body, anonymous: true }),
  forgotPassword: (email: string) => apiRequest<void>('POST', '/auth/password/forgot', { body: { email }, anonymous: true }),
  deviceEmployees: (installationId: string, tenantId: Uuid, deviceSecret: string) =>
    apiRequest<DeviceEmployees>('GET', `/devices/shared/${encodeURIComponent(installationId)}/employees`, {
      anonymous: true,
      headers: { 'X-Device-Secret': deviceSecret, 'X-Tenant-Id': tenantId },
    }),
  pinLogin: (body: { tenantId: Uuid; installationId: string; employeeId: Uuid; pin: string }, deviceSecret: string) =>
    apiRequest<Session>('POST', '/auth/pin-login', { body, anonymous: true, headers: { 'X-Device-Secret': deviceSecret } }),
};
