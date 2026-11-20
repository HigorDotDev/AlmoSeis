/**
 * Web (ADR-0053 §3.8, 06 §6.3 "App no navegador"): o `deviceSecret` está no cookie `HttpOnly`
 * `__Secure-almoseis-ds` e só é enviado para rotas sob `/api/v1/auth`. Por isso a lista de funcionários usa
 * `GET /auth/shared-device/employees` (installationId na query, tenant no `X-Tenant-Id`) e o `pin-login` vai
 * sem `X-Device-Secret`. `credentials: 'include'` vem do `transport.web.ts` (todas as rotas `/auth/*`).
 */
import { apiRequest, qs } from '../client';
import type { DeviceEmployees, Session, Uuid } from '../types';
import type { SharedDeviceRef } from './sharedDeviceAuth';

export const sharedDeviceAuth = {
  employees: (device: SharedDeviceRef) =>
    apiRequest<DeviceEmployees>('GET', `/auth/shared-device/employees${qs({ installationId: device.installationId })}`, {
      anonymous: true,
      headers: { 'X-Tenant-Id': device.tenantId },
    }),
  pinLogin: (device: SharedDeviceRef, employeeId: Uuid, pin: string) =>
    apiRequest<Session>('POST', '/auth/pin-login', {
      body: { tenantId: device.tenantId, installationId: device.installationId, employeeId, pin },
      anonymous: true,
    }),
  /** "Deixar de usar como compartilhado": apaga o cookie do segredo (a revogação é outra rota). */
  forget: async (): Promise<void> => {
    await apiRequest<void>('DELETE', '/auth/shared-device', { anonymous: true });
  },
};
