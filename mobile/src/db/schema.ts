/** Banco local (08 §8.4): rascunhos, operações de resultado desconhecido e metadados de sync. Nada de token aqui. */

/** Subconjunto do `SQLiteDatabase` do expo-sqlite usado pelos repositórios (permite um banco falso nos testes). */
export interface LocalDb {
  execAsync(sql: string): Promise<void>;
  runAsync(sql: string, ...params: (string | number | null)[]): Promise<unknown>;
  getAllAsync<T>(sql: string, ...params: (string | number | null)[]): Promise<T[]>;
  getFirstAsync<T>(sql: string, ...params: (string | number | null)[]): Promise<T | null>;
}

export const SCHEMA_VERSION = 2;

export const MIGRATIONS: Record<number, string> = {
  1: `
    CREATE TABLE IF NOT EXISTS meta (
      key   TEXT PRIMARY KEY,
      value TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS drafts (
      id          TEXT PRIMARY KEY,
      tenant_id   TEXT NOT NULL,
      kind        TEXT NOT NULL,
      target_id   TEXT,
      form_json   TEXT NOT NULL,
      updated_at  TEXT NOT NULL
    );
    CREATE INDEX IF NOT EXISTS ix_drafts_tenant_kind ON drafts (tenant_id, kind, target_id);
    CREATE TABLE IF NOT EXISTS pending_operations (
      idempotency_key TEXT PRIMARY KEY,
      tenant_id       TEXT NOT NULL,
      draft_id        TEXT,
      method          TEXT NOT NULL,
      path            TEXT NOT NULL,
      body_json       TEXT,
      if_match        INTEGER,
      status          TEXT NOT NULL,
      description     TEXT NOT NULL,
      created_at      TEXT NOT NULL,
      last_error      TEXT
    );
  `,
  // 06 §6.12: `GET /operations` só enxerga as chaves de quem enviou; no aparelho compartilhado cada pessoa vê as suas.
  2: `
    ALTER TABLE pending_operations ADD COLUMN employee_id TEXT NOT NULL DEFAULT '';
    CREATE INDEX IF NOT EXISTS ix_pending_tenant_employee ON pending_operations (tenant_id, employee_id);
  `,
};

export type DraftKind = 'NEW_ORDER_MARMITA' | 'NEW_ORDER_PF' | 'NEW_ORDER_KILO' | 'ADD_ITEMS' | 'PAYMENT';

export type PendingStatus = 'SENDING' | 'UNKNOWN' | 'FAILED';

export interface Draft {
  id: string;
  tenantId: string;
  kind: DraftKind;
  targetId: string | null;
  formJson: string;
  updatedAt: string;
}

export interface PendingOperation {
  idempotencyKey: string;
  tenantId: string;
  employeeId: string;
  draftId: string | null;
  method: string;
  path: string;
  bodyJson: string | null;
  ifMatch: number | null;
  status: PendingStatus;
  description: string;
  createdAt: string;
  lastError: string | null;
}
