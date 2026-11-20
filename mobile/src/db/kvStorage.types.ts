/** Tipos de `kvStorage.ts`/`kvStorage.web.ts` (08 §8.15). */
export interface AsyncKv {
  getItem(key: string): Promise<string | null> | string | null;
  setItem(key: string, value: string): Promise<void> | void;
  removeItem(key: string): Promise<void> | void;
}
