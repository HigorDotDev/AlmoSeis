/** Tipos de `secure.ts`/`secure.web.ts` (08 §8.15: o `.web` não importa o próprio nome). */
import type { Uuid } from '@/api/types';

export interface SharedDeviceCredentials {
  id: Uuid;
  tenantId: Uuid;
  tenantName: string;
  name: string;
  installationId: string;
  /** Web: `null` (cookie `HttpOnly`, ADR-0053 §3.8). */
  deviceSecret: string | null;
}
