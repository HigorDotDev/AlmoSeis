/** Web (ADR-0053 §5.1 e §5.3): preferências no `localStorage`; cache de consultas no IndexedDB (`kv`). */
import type { AsyncKv } from './kvStorage';
import { deleteOne, getOne, putOne, STORE } from './web/idb';

export const preferencesStorage: AsyncKv = {
  getItem: (key) => localStorage.getItem(key),
  setItem: (key, value) => localStorage.setItem(key, value),
  removeItem: (key) => localStorage.removeItem(key),
};

export const queryCacheStorage: AsyncKv = {
  getItem: async (key) => (await getOne<{ key: string; value: string }>(STORE.kv, key))?.value ?? null,
  setItem: (key, value) => putOne(STORE.kv, { key, value }),
  removeItem: (key) => deleteOne(STORE.kv, key),
};
