/**
 * Web (ADR-0053 §3): nada de segredo legível pelo JavaScript fora da memória.
 * - access token: só em memória, nesta aba (o refresh fica no cookie `HttpOnly`);
 * - `installationId`: não é segredo, `localStorage`;
 * - aparelho compartilhado: só os dados não secretos (id, nomes); o `deviceSecret` fica no cookie
 *   `__Secure-almoseis-ds`. Guardados no IndexedDB (`meta`).
 */
import type { Tokens } from '@/api/types';
import { metaRepo } from '@/db/metaRepo';

import type { SharedDeviceCredentials } from './secure.types';

export type { SharedDeviceCredentials } from './secure.types';

const KEYS = {
  installationId: 'almoseis.installationId',
  sharedDevice: 'almoseis.sharedDevice',
  lastTenantId: 'almoseis.lastTenantId',
} as const;

let memoryTokens: Tokens | null = null;

/** 06 §6.3 "App no navegador": UUID gerado pelo navegador. */
function newInstallationId(): string {
  return crypto.randomUUID();
}

export const secure = {
  async installationId(): Promise<string> {
    const existing = localStorage.getItem(KEYS.installationId);
    if (existing) return existing;
    const id = newInstallationId();
    localStorage.setItem(KEYS.installationId, id);
    return id;
  },

  tokens: async (): Promise<Tokens | null> => memoryTokens,
  setTokens: async (tokens: Tokens | null): Promise<void> => {
    // Defesa: mesmo que um refresh venha no corpo, ele não fica nem em memória.
    memoryTokens = tokens ? { ...tokens, refreshToken: null } : null;
  },

  async sharedDevice(): Promise<SharedDeviceCredentials | null> {
    const raw = await metaRepo.get(KEYS.sharedDevice);
    if (!raw) return null;
    try {
      return JSON.parse(raw) as SharedDeviceCredentials;
    } catch {
      return null;
    }
  },
  setSharedDevice: async (device: SharedDeviceCredentials | null): Promise<void> => {
    if (device) await metaRepo.set(KEYS.sharedDevice, JSON.stringify({ ...device, deviceSecret: null }));
    else await metaRepo.remove(KEYS.sharedDevice);
  },

  lastTenantId: async (): Promise<string | null> => localStorage.getItem(KEYS.lastTenantId),
  setLastTenantId: async (id: string): Promise<void> => localStorage.setItem(KEYS.lastTenantId, id),
};
