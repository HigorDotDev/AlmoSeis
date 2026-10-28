/**
 * Operações de resultado desconhecido (08 §8.4/8.5). Não é fila offline: só existe para escrita que saiu e
 * não teve resposta. Gravada ANTES de enviar, para sobreviver a o app ser fechado no meio.
 */
import { getDb } from './migrations';
import type { PendingOperation, PendingStatus } from './schema';

interface Row {
  idempotency_key: string;
  tenant_id: string;
  draft_id: string | null;
  method: string;
  path: string;
  body_json: string | null;
  if_match: number | null;
  status: PendingStatus;
  description: string;
  created_at: string;
  last_error: string | null;
}

const toOp = (r: Row): PendingOperation => ({
  idempotencyKey: r.idempotency_key,
  tenantId: r.tenant_id,
  draftId: r.draft_id,
  method: r.method,
  path: r.path,
  bodyJson: r.body_json,
  ifMatch: r.if_match,
  status: r.status,
  description: r.description,
  createdAt: r.created_at,
  lastError: r.last_error,
});

export const pendingOpsRepo = {
  async insert(op: PendingOperation): Promise<void> {
    await getDb().runAsync(
      `INSERT INTO pending_operations
         (idempotency_key, tenant_id, draft_id, method, path, body_json, if_match, status, description, created_at, last_error)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      op.idempotencyKey,
      op.tenantId,
      op.draftId,
      op.method,
      op.path,
      op.bodyJson,
      op.ifMatch,
      op.status,
      op.description,
      op.createdAt,
      op.lastError,
    );
  },

  async setStatus(key: string, status: PendingStatus, lastError: string | null = null): Promise<void> {
    await getDb().runAsync('UPDATE pending_operations SET status = ?, last_error = ? WHERE idempotency_key = ?', status, lastError, key);
  },

  /** Reenvio de pendência com mais de 72 h: a chave expirou no servidor, então vai com uma nova (08 §8.5 passo 5). */
  async rekey(oldKey: string, newKey: string): Promise<void> {
    await getDb().runAsync(
      "UPDATE pending_operations SET idempotency_key = ?, status = 'SENDING', created_at = ? WHERE idempotency_key = ?",
      newKey,
      new Date().toISOString(),
      oldKey,
    );
  },

  async remove(key: string): Promise<void> {
    await getDb().runAsync('DELETE FROM pending_operations WHERE idempotency_key = ?', key);
  },

  async get(key: string): Promise<PendingOperation | null> {
    const row = await getDb().getFirstAsync<Row>('SELECT * FROM pending_operations WHERE idempotency_key = ?', key);
    return row ? toOp(row) : null;
  },

  async list(tenantId: string): Promise<PendingOperation[]> {
    const rows = await getDb().getAllAsync<Row>(
      'SELECT * FROM pending_operations WHERE tenant_id = ? ORDER BY created_at',
      tenantId,
    );
    return rows.map(toOp);
  },

  async clearTenant(tenantId: string): Promise<void> {
    await getDb().runAsync('DELETE FROM pending_operations WHERE tenant_id = ?', tenantId);
  },
};
