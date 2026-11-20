/** Metadados de sync na web (ADR-0053 §5): mesma interface do `metaRepo.ts`, sobre IndexedDB. */
import { deleteOne, getOne, putOne, STORE, transact } from './web/idb';

interface MetaRow {
  key: string;
  value: string;
}

export const metaRepo = {
  async get(key: string): Promise<string | null> {
    return (await getOne<MetaRow>(STORE.meta, key))?.value ?? null;
  },

  set: (key: string, value: string): Promise<void> => putOne(STORE.meta, { key, value } satisfies MetaRow),

  remove: (key: string): Promise<void> => deleteOne(STORE.meta, key),

  async lastSeq(tenantId: string): Promise<number | null> {
    const v = await this.get(`lastSeq:${tenantId}`);
    return v === null ? null : Number(v);
  },

  async setLastSeq(tenantId: string, seq: number): Promise<void> {
    await transact<void>(STORE.meta, 'readwrite', (s) => {
      s.put({ key: `lastSeq:${tenantId}`, value: String(seq) } satisfies MetaRow);
      s.put({ key: `lastSyncAt:${tenantId}`, value: new Date().toISOString() } satisfies MetaRow);
    });
  },

  async clearTenant(tenantId: string): Promise<void> {
    await transact<void>(STORE.meta, 'readwrite', (s) => {
      s.delete(`lastSeq:${tenantId}`);
      s.delete(`lastSyncAt:${tenantId}`);
    });
  },
};
