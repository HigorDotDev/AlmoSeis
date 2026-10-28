import { MIGRATIONS, SCHEMA_VERSION, type LocalDb } from './schema';

let current: LocalDb | null = null;

/** Aplica as migrações pendentes, controladas por `meta.schemaVersion`. */
export async function migrate(db: LocalDb): Promise<void> {
  await db.execAsync('CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);');
  const row = await db.getFirstAsync<{ value: string }>("SELECT value FROM meta WHERE key = 'schemaVersion'");
  let version = row ? Number(row.value) : 0;
  while (version < SCHEMA_VERSION) {
    version++;
    await db.execAsync(MIGRATIONS[version]!);
    await db.runAsync("INSERT OR REPLACE INTO meta (key, value) VALUES ('schemaVersion', ?)", String(version));
  }
}

export async function openLocalDb(): Promise<LocalDb> {
  if (current) return current;
  const { openDatabaseAsync } = await import('expo-sqlite');
  const db = await openDatabaseAsync('almoseis.db');
  await db.execAsync('PRAGMA journal_mode = WAL;');
  await migrate(db);
  current = db;
  return db;
}

/** O banco já aberto no _layout raiz. */
export function getDb(): LocalDb {
  if (!current) {
    throw new Error('Banco local ainda não foi aberto.');
  }
  return current;
}

/** Testes: injeta um banco em memória. */
export function setDbForTests(db: LocalDb | null): void {
  current = db;
}
