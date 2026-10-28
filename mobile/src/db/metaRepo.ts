import { getDb } from './migrations';

/** Metadados de sync por tenant (08 §8.4): `lastSeq:{tenantId}`, `lastSyncAt:{tenantId}`. */
export const metaRepo = {
  async get(key: string): Promise<string | null> {
    const row = await getDb().getFirstAsync<{ value: string }>('SELECT value FROM meta WHERE key = ?', key);
    return row?.value ?? null;
  },

  async set(key: string, value: string): Promise<void> {
    await getDb().runAsync('INSERT OR REPLACE INTO meta (key, value) VALUES (?, ?)', key, value);
  },

  async remove(key: string): Promise<void> {
    await getDb().runAsync('DELETE FROM meta WHERE key = ?', key);
  },

  async lastSeq(tenantId: string): Promise<number | null> {
    const v = await this.get(`lastSeq:${tenantId}`);
    return v === null ? null : Number(v);
  },

  async setLastSeq(tenantId: string, seq: number): Promise<void> {
    await this.set(`lastSeq:${tenantId}`, String(seq));
    await this.set(`lastSyncAt:${tenantId}`, new Date().toISOString());
  },

  async clearTenant(tenantId: string): Promise<void> {
    await getDb().runAsync('DELETE FROM meta WHERE key IN (?, ?)', `lastSeq:${tenantId}`, `lastSyncAt:${tenantId}`);
  },
};
