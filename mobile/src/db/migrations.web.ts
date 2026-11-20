/**
 * Web (ADR-0053 §5): sem SQLite. "Abrir o banco" = abrir o IndexedDB (o `onupgradeneeded` faz as migrações)
 * e pedir armazenamento persistente uma vez. Mesmas exportações do `migrations.ts`.
 */
import type { LocalDb } from './schema';
import { openIdb } from './web/idb';

let persistRequested = false;

const noSql = (): never => {
  throw new Error('Na web não há SQL: use os repositórios (draftsRepo, pendingOpsRepo, metaRepo).');
};

const webDb: LocalDb = { execAsync: noSql, runAsync: noSql, getAllAsync: noSql, getFirstAsync: noSql };

export async function migrate(_db: LocalDb): Promise<void> {}

export async function openLocalDb(): Promise<LocalDb> {
  await openIdb();
  if (!persistRequested) {
    persistRequested = true;
    // Para o navegador não apagar rascunhos e pendências por falta de espaço (ADR-0053 §5.5).
    void navigator.storage?.persist?.().catch(() => false);
  }
  return webDb;
}

export function getDb(): LocalDb {
  return webDb;
}

export function setDbForTests(_db: LocalDb | null): void {}
