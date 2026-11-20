/**
 * Armazenamento chave-valor por plataforma. Celular: `expo-sqlite/kv-store` para os dois usos.
 * Web (`kvStorage.web.ts`): preferências no `localStorage` e cache de consultas no IndexedDB (ADR-0053 §5).
 */
import Storage from 'expo-sqlite/kv-store';

export interface AsyncKv {
  getItem(key: string): Promise<string | null> | string | null;
  setItem(key: string, value: string): Promise<void> | void;
  removeItem(key: string): Promise<void> | void;
}

/** Preferências pequenas (som, fonte da cozinha, última aba). */
export const preferencesStorage = Storage;

/** Cache persistido do TanStack Query. */
export const queryCacheStorage = Storage;
