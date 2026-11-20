/**
 * Operações de resultado desconhecido na web (ADR-0053 §5.4): mesma interface do `pendingOpsRepo.ts`.
 * `insert` usa durabilidade `strict` e só resolve depois do `complete` da transação: a pendência está no
 * disco ANTES de a requisição sair. Um F5 no meio de "Receber" cai no fluxo de `GET /operations/{key}`.
 */
import type { PendingOperation, PendingStatus } from './schema';
import { deleteOne, deleteWhere, getAll, getOne, STORE, transact, updateOne } from './web/idb';

const byCreatedAsc = (a: PendingOperation, b: PendingOperation) => (a.createdAt < b.createdAt ? -1 : a.createdAt > b.createdAt ? 1 : 0);

export const pendingOpsRepo = {
  /** `add` (não `put`): chave repetida falha, como o INSERT do SQLite. */
  insert: (op: PendingOperation): Promise<void> => transact<void>(STORE.pendingOperations, 'readwrite', (s) => void s.add({ ...op }), 'strict'),

  setStatus: (key: string, status: PendingStatus, lastError: string | null = null): Promise<void> =>
    updateOne<PendingOperation>(STORE.pendingOperations, key, (op) => (op ? { ...op, status, lastError } : null), 'strict'),

  remove: (key: string): Promise<void> => deleteOne(STORE.pendingOperations, key),

  get: (key: string): Promise<PendingOperation | null> => getOne<PendingOperation>(STORE.pendingOperations, key),

  /** As do próprio funcionário (06 §6.12); sem `employeeId`, todas do tenant. */
  async list(tenantId: string, employeeId?: string): Promise<PendingOperation[]> {
    const rows = await getAll<PendingOperation>(STORE.pendingOperations, tenantId);
    return rows.filter((o) => employeeId === undefined || o.employeeId === employeeId).sort(byCreatedAsc);
  },

  clearTenant: (tenantId: string): Promise<void> => deleteWhere<PendingOperation>(STORE.pendingOperations, (o) => o.tenantId === tenantId),
};
