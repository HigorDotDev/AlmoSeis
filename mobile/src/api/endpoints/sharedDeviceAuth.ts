/**
 * Aparelho compartilhado: lista de funcionários e entrada por PIN (06 §6.3, ADR-0027).
 * Celular: o `deviceSecret` do SecureStore vai no header `X-Device-Secret`. Web: `sharedDeviceAuth.web.ts`.
 */
import type { Uuid } from '../types';
import { authApi } from './auth';

export interface SharedDeviceRef {
  tenantId: Uuid;
  installationId: string;
  deviceSecret: string | null;
}

export const sharedDeviceAuth = {
  employees: (device: SharedDeviceRef) => authApi.deviceEmployees(device.installationId, device.tenantId, device.deviceSecret ?? ''),
  pinLogin: (device: SharedDeviceRef, employeeId: Uuid, pin: string) =>
    authApi.pinLogin({ tenantId: device.tenantId, installationId: device.installationId, employeeId, pin }, device.deviceSecret ?? ''),
  /** Deixar de usar como compartilhado. No celular, basta apagar o SecureStore. */
  forget: async (): Promise<void> => {},
};
