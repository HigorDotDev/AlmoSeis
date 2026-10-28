/** Segredos só no SecureStore (ADR-0027, docs/02-padroes.md §2.3): tokens, installationId e deviceSecret. */
import * as SecureStore from 'expo-secure-store';

import type { Tokens, Uuid } from '@/api/types';
import { newKey } from '@/lib/ids';

const KEYS = {
  tokens: 'almoseis.tokens',
  installationId: 'almoseis.installationId',
  sharedDevice: 'almoseis.sharedDevice',
  lastTenantId: 'almoseis.lastTenantId',
} as const;

export interface SharedDeviceCredentials {
  id: Uuid;
  tenantId: Uuid;
  tenantName: string;
  name: string;
  installationId: string;
  deviceSecret: string;
}

async function readJson<T>(key: string): Promise<T | null> {
  const raw = await SecureStore.getItemAsync(key);
  if (!raw) return null;
  try {
    return JSON.parse(raw) as T;
  } catch {
    return null;
  }
}

export const secure = {
  async installationId(): Promise<string> {
    const existing = await SecureStore.getItemAsync(KEYS.installationId);
    if (existing) return existing;
    const id = `inst_${newKey().replace(/-/g, '')}`;
    await SecureStore.setItemAsync(KEYS.installationId, id);
    return id;
  },

  tokens: () => readJson<Tokens>(KEYS.tokens),
  setTokens: (tokens: Tokens | null) =>
    tokens ? SecureStore.setItemAsync(KEYS.tokens, JSON.stringify(tokens)) : SecureStore.deleteItemAsync(KEYS.tokens),

  sharedDevice: () => readJson<SharedDeviceCredentials>(KEYS.sharedDevice),
  setSharedDevice: (device: SharedDeviceCredentials | null) =>
    device ? SecureStore.setItemAsync(KEYS.sharedDevice, JSON.stringify(device)) : SecureStore.deleteItemAsync(KEYS.sharedDevice),

  lastTenantId: () => SecureStore.getItemAsync(KEYS.lastTenantId),
  setLastTenantId: (id: string) => SecureStore.setItemAsync(KEYS.lastTenantId, id),
};
