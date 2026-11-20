/**
 * IndexedDB do app web (ADR-0053 §5): um object store por tabela do SQLite do celular (`meta`, `drafts`,
 * `pending_operations`) e `kv` para o cache persistido do TanStack Query. Sem SQL, sem WASM.
 *
 * Toda escrita resolve só no `complete` da transação: quando a promessa volta, o dado está gravado. É o que
 * permite à pendência ser "gravada e confirmada antes do envio" (ADR-0010, ADR-0053 §5.4).
 */
export const IDB_NAME = 'almoseis';
export const IDB_VERSION = 1;

export const STORE = {
  meta: 'meta',
  drafts: 'drafts',
  pendingOperations: 'pending_operations',
  kv: 'kv',
} as const;

export type StoreName = (typeof STORE)[keyof typeof STORE];

type Durability = 'strict' | 'default';

let dbPromise: Promise<IDBDatabase> | null = null;
let factoryOverride: IDBFactory | null = null;

function upgrade(db: IDBDatabase, oldVersion: number): void {
  // Versão 1: as tabelas da versão 2 do SQLite (pending_operations já com employeeId).
  if (oldVersion < 1) {
    db.createObjectStore(STORE.meta, { keyPath: 'key' });
    const drafts = db.createObjectStore(STORE.drafts, { keyPath: 'id' });
    drafts.createIndex('tenantId', 'tenantId');
    const ops = db.createObjectStore(STORE.pendingOperations, { keyPath: 'idempotencyKey' });
    ops.createIndex('tenantId', 'tenantId');
    db.createObjectStore(STORE.kv, { keyPath: 'key' });
  }
}

export function openIdb(): Promise<IDBDatabase> {
  if (!dbPromise) {
    const factory = factoryOverride ?? indexedDB;
    dbPromise = new Promise<IDBDatabase>((resolve, reject) => {
      const request = factory.open(IDB_NAME, IDB_VERSION);
      request.onupgradeneeded = (event) => upgrade(request.result, event.oldVersion);
      request.onsuccess = () => {
        const db = request.result;
        // Outra aba com uma versão nova do app quer migrar: fecha para não bloquear; a próxima chamada reabre.
        db.onversionchange = () => {
          db.close();
          dbPromise = null;
        };
        resolve(db);
      };
      request.onerror = () => {
        dbPromise = null;
        reject(request.error ?? new Error('IndexedDB indisponível.'));
      };
    });
  }
  return dbPromise;
}

/** Testes: usa o `fake-indexeddb` e zera a conexão. */
export function setIdbFactoryForTests(factory: IDBFactory | null): void {
  factoryOverride = factory;
  dbPromise = null;
}

type Body<T> = (store: IDBObjectStore, setResult: (value: T) => void) => void;

/**
 * Uma transação num store. `body` faz as requisições (callbacks do próprio IndexedDB, sem `await` no meio,
 * para a transação não fechar antes da hora) e chama `setResult`. Resolve no `complete`.
 */
export async function transact<T>(store: StoreName, mode: IDBTransactionMode, body: Body<T>, durability: Durability = 'default'): Promise<T> {
  const db = await openIdb();
  return new Promise<T>((resolve, reject) => {
    const tx = db.transaction(store, mode, { durability });
    let result: T;
    tx.oncomplete = () => resolve(result);
    tx.onerror = () => reject(tx.error ?? new Error('Falha no IndexedDB.'));
    tx.onabort = () => reject(tx.error ?? new Error('Transação do IndexedDB abortada.'));
    body(tx.objectStore(store), (value) => {
      result = value;
    });
  });
}

export function getOne<T>(store: StoreName, key: IDBValidKey): Promise<T | null> {
  return transact<T | null>(store, 'readonly', (s, done) => {
    const r = s.get(key);
    r.onsuccess = () => done((r.result as T | undefined) ?? null);
  });
}

/** Todos os registros; com `tenantId`, pelo índice. */
export function getAll<T>(store: StoreName, tenantId?: string): Promise<T[]> {
  return transact<T[]>(store, 'readonly', (s, done) => {
    const r = tenantId === undefined ? s.getAll() : s.index('tenantId').getAll(tenantId);
    r.onsuccess = () => done(r.result as T[]);
  });
}

export function putOne(store: StoreName, value: unknown, durability: Durability = 'default'): Promise<void> {
  return transact<void>(store, 'readwrite', (s) => void s.put(value), durability);
}

export function deleteOne(store: StoreName, key: IDBValidKey): Promise<void> {
  return transact<void>(store, 'readwrite', (s) => void s.delete(key), 'strict');
}

/** Lê, transforma e grava na mesma transação (equivale a um UPDATE). `null` do `update` = não grava. */
export function updateOne<T>(store: StoreName, key: IDBValidKey, update: (current: T | null) => T | null, durability: Durability = 'default'): Promise<void> {
  return transact<void>(
    store,
    'readwrite',
    (s) => {
      const r = s.get(key);
      r.onsuccess = () => {
        const next = update((r.result as T | undefined) ?? null);
        if (next !== null) s.put(next);
      };
    },
    durability,
  );
}

/** DELETE ... WHERE: percorre com cursor e apaga o que casar. */
export function deleteWhere<T>(store: StoreName, predicate: (value: T) => boolean): Promise<void> {
  return transact<void>(store, 'readwrite', (s) => {
    const r = s.openCursor();
    r.onsuccess = () => {
      const cursor = r.result;
      if (!cursor) return;
      if (predicate(cursor.value as T)) cursor.delete();
      cursor.continue();
    };
  });
}
