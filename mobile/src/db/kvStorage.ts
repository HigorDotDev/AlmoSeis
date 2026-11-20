/**
 * Armazenamento chave-valor por plataforma. Celular: `expo-sqlite/kv-store` para os dois usos.
 * Web (`kvStorage.web.ts`): preferências no `localStorage` e cache de consultas no IndexedDB (ADR-0053 §5).
 */
import Storage from 'expo-sqlite/kv-store';

import type { AsyncKv } from './kvStorage.types';

export type { AsyncKv } from './kvStorage.types';

/** Preferências pequenas (som, fonte da cozinha, última aba). */
export const preferencesStorage: AsyncKv = Storage;

/** Cache persistido do TanStack Query. */
export const queryCacheStorage: AsyncKv = Storage;
