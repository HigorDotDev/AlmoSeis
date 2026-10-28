/** Repositórios em memória com a mesma interface dos de SQLite (o SQL em si roda no aparelho). */
import type { Draft, PendingOperation, PendingStatus } from '@/db/schema';

export const memory = {
  ops: new Map<string, PendingOperation>(),
  drafts: new Map<string, Draft>(),
  meta: new Map<string, string>(),
  reset() {
    this.ops.clear();
    this.drafts.clear();
    this.meta.clear();
  },
};

export const pendingOpsRepo = {
  insert: async (op: PendingOperation) => void memory.ops.set(op.idempotencyKey, { ...op }),
  setStatus: async (key: string, status: PendingStatus, lastError: string | null = null) => {
    const op = memory.ops.get(key);
    if (op) memory.ops.set(key, { ...op, status, lastError });
  },
  rekey: async (oldKey: string, newKey: string) => {
    const op = memory.ops.get(oldKey);
    if (!op) return;
    memory.ops.delete(oldKey);
    memory.ops.set(newKey, { ...op, idempotencyKey: newKey, status: 'SENDING', createdAt: new Date().toISOString() });
  },
  remove: async (key: string) => void memory.ops.delete(key),
  get: async (key: string) => memory.ops.get(key) ?? null,
  list: async (tenantId: string) => [...memory.ops.values()].filter((o) => o.tenantId === tenantId),
  clearTenant: async () => memory.ops.clear(),
};

export const draftsRepo = {
  save: async (d: Omit<Draft, 'updatedAt'>) => void memory.drafts.set(d.id, { ...d, updatedAt: new Date().toISOString() }),
  remove: async (id: string) => void memory.drafts.delete(id),
  get: async (id: string) => memory.drafts.get(id) ?? null,
};

export const metaRepo = {
  get: async (k: string) => memory.meta.get(k) ?? null,
  set: async (k: string, v: string) => void memory.meta.set(k, v),
  remove: async (k: string) => void memory.meta.delete(k),
  lastSeq: async (t: string) => (memory.meta.has(`lastSeq:${t}`) ? Number(memory.meta.get(`lastSeq:${t}`)) : null),
  setLastSeq: async (t: string, seq: number) => void memory.meta.set(`lastSeq:${t}`, String(seq)),
};
