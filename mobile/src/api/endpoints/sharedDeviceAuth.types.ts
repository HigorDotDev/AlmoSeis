/** Tipos de `sharedDeviceAuth.ts`/`sharedDeviceAuth.web.ts` (08 §8.15). */
import type { Uuid } from '../types';

export interface SharedDeviceRef {
  tenantId: Uuid;
  installationId: string;
  deviceSecret: string | null;
}
