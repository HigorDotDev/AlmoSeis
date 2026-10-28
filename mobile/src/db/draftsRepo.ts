/** Rascunhos (RN-014.3, 08 §8.4): salvos a cada mudança e apagados só depois de o servidor confirmar. */
import { getDb } from './migrations';
import type { Draft, DraftKind } from './schema';

const MAX_AGE_MS = 3 * 24 * 60 * 60 * 1000;

interface Row {
  id: string;
  tenant_id: string;
  kind: DraftKind;
  target_id: string | null;
  form_json: string;
  updated_at: string;
}

const toDraft = (r: Row): Draft => ({
  id: r.id,
  tenantId: r.tenant_id,
  kind: r.kind,
  targetId: r.target_id,
  formJson: r.form_json,
  updatedAt: r.updated_at,
});

export const draftsRepo = {
  async save(draft: Omit<Draft, 'updatedAt'>, now = new Date()): Promise<void> {
    await getDb().runAsync(
      `INSERT INTO drafts (id, tenant_id, kind, target_id, form_json, updated_at) VALUES (?, ?, ?, ?, ?, ?)
       ON CONFLICT (id) DO UPDATE SET form_json = excluded.form_json, updated_at = excluded.updated_at`,
      draft.id,
      draft.tenantId,
      draft.kind,
      draft.targetId,
      draft.formJson,
      now.toISOString(),
    );
  },

  async latest(tenantId: string, kind: DraftKind, targetId: string | null): Promise<Draft | null> {
    const row = await getDb().getFirstAsync<Row>(
      `SELECT * FROM drafts WHERE tenant_id = ? AND kind = ? AND (target_id = ? OR (target_id IS NULL AND ? IS NULL))
       ORDER BY updated_at DESC LIMIT 1`,
      tenantId,
      kind,
      targetId,
      targetId,
    );
    return row ? toDraft(row) : null;
  },

  async get(id: string): Promise<Draft | null> {
    const row = await getDb().getFirstAsync<Row>('SELECT * FROM drafts WHERE id = ?', id);
    return row ? toDraft(row) : null;
  },

  async list(tenantId: string): Promise<Draft[]> {
    const rows = await getDb().getAllAsync<Row>('SELECT * FROM drafts WHERE tenant_id = ? ORDER BY updated_at DESC', tenantId);
    return rows.map(toDraft);
  },

  async remove(id: string): Promise<void> {
    await getDb().runAsync('DELETE FROM drafts WHERE id = ?', id);
  },

  async count(tenantId: string): Promise<number> {
    const row = await getDb().getFirstAsync<{ n: number }>('SELECT count(*) AS n FROM drafts WHERE tenant_id = ?', tenantId);
    return row?.n ?? 0;
  },

  /** Ao abrir o app: rascunhos com mais de 3 dias são descartados. */
  async purgeOld(now = new Date()): Promise<void> {
    await getDb().runAsync('DELETE FROM drafts WHERE updated_at < ?', new Date(now.getTime() - MAX_AGE_MS).toISOString());
  },

  async clearTenant(tenantId: string): Promise<void> {
    await getDb().runAsync('DELETE FROM drafts WHERE tenant_id = ?', tenantId);
  },
};
