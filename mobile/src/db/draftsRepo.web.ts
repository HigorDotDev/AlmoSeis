/** Rascunhos na web (ADR-0053 §5): mesma interface do `draftsRepo.ts` (SQLite), sobre IndexedDB. */
import type { Draft, DraftKind } from './schema';
import { deleteOne, deleteWhere, getAll, getOne, STORE, updateOne } from './web/idb';

const MAX_AGE_MS = 3 * 24 * 60 * 60 * 1000;

const byUpdatedDesc = (a: Draft, b: Draft) => (a.updatedAt < b.updatedAt ? 1 : a.updatedAt > b.updatedAt ? -1 : 0);

export const draftsRepo = {
  /** Como o `ON CONFLICT (id) DO UPDATE` do SQLite: num id existente, só o formulário e a hora mudam. */
  async save(draft: Omit<Draft, 'updatedAt'>, now = new Date()): Promise<void> {
    const updatedAt = now.toISOString();
    await updateOne<Draft>(STORE.drafts, draft.id, (current) => (current ? { ...current, formJson: draft.formJson, updatedAt } : { ...draft, updatedAt }));
  },

  async latest(tenantId: string, kind: DraftKind, targetId: string | null): Promise<Draft | null> {
    const rows = await getAll<Draft>(STORE.drafts, tenantId);
    return rows.filter((d) => d.kind === kind && d.targetId === targetId).sort(byUpdatedDesc)[0] ?? null;
  },

  get: (id: string): Promise<Draft | null> => getOne<Draft>(STORE.drafts, id),

  async list(tenantId: string): Promise<Draft[]> {
    return (await getAll<Draft>(STORE.drafts, tenantId)).sort(byUpdatedDesc);
  },

  remove: (id: string): Promise<void> => deleteOne(STORE.drafts, id),

  async count(tenantId: string): Promise<number> {
    return (await getAll<Draft>(STORE.drafts, tenantId)).length;
  },

  /** Ao abrir o app: rascunhos com mais de 3 dias são descartados. */
  async purgeOld(now = new Date()): Promise<void> {
    const cutoff = new Date(now.getTime() - MAX_AGE_MS).toISOString();
    await deleteWhere<Draft>(STORE.drafts, (d) => d.updatedAt < cutoff);
  },

  clearTenant: (tenantId: string): Promise<void> => deleteWhere<Draft>(STORE.drafts, (d) => d.tenantId === tenantId),
};
