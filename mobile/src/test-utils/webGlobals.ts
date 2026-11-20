/**
 * Globais do navegador para testar módulos `.web.ts` no Jest (preset nativo). Importe ANTES dos módulos web:
 * `localStorage` em memória, sem `BroadcastChannel` e sem Web Locks (o Node 24 tem os dois, e o canal real
 * prenderia o processo do Jest).
 */
const store = new Map<string, string>();

export const fakeLocalStorage = {
  getItem: (k: string) => (store.has(k) ? store.get(k)! : null),
  setItem: (k: string, v: string) => void store.set(k, String(v)),
  removeItem: (k: string) => void store.delete(k),
  clear: () => store.clear(),
};

Object.defineProperty(globalThis, 'localStorage', { value: fakeLocalStorage, configurable: true, writable: true });
Object.defineProperty(globalThis, 'BroadcastChannel', { value: undefined, configurable: true, writable: true });
Object.defineProperty(globalThis, 'navigator', { value: {}, configurable: true, writable: true });
