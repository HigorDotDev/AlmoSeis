/**
 * Repositório local da web sobre IndexedDB (ADR-0053 §5): mesma interface e mesmo comportamento dos
 * repositórios de SQLite. Roda contra o `fake-indexeddb`.
 */
import { IDBFactory } from 'fake-indexeddb';

import { draftsRepo } from '@/db/draftsRepo.web';
import { metaRepo } from '@/db/metaRepo.web';
import { pendingOpsRepo } from '@/db/pendingOpsRepo.web';
import type { PendingOperation } from '@/db/schema';
import { setIdbFactoryForTests } from '@/db/web/idb';

let factory: IDBFactory;

beforeEach(() => {
  factory = new IDBFactory();
  setIdbFactoryForTests(factory);
});

afterAll(() => setIdbFactoryForTests(null));

/** Fecha a conexão e reabre o mesmo banco, como um F5 na página. */
const reload = () => setIdbFactoryForTests(factory);

const op = (over: Partial<PendingOperation> = {}): PendingOperation => ({
  idempotencyKey: 'k1',
  tenantId: 't1',
  employeeId: 'e1',
  draftId: null,
  method: 'POST',
  path: '/orders/o1/payments',
  bodyJson: '{"amount":"35.50"}',
  ifMatch: 3,
  status: 'SENDING',
  description: 'Receber R$ 35,50',
  createdAt: '2026-11-19T12:00:00.000Z',
  lastError: null,
  ...over,
});

describe('draftsRepo (web)', () => {
  it('upsert pelo id: só o formulário e a hora mudam', async () => {
    await draftsRepo.save({ id: 'd1', tenantId: 't1', kind: 'NEW_ORDER_MARMITA', targetId: null, formJson: '{"a":1}' }, new Date('2026-11-19T10:00:00Z'));
    await draftsRepo.save({ id: 'd1', tenantId: 'outro', kind: 'PAYMENT', targetId: 'x', formJson: '{"a":2}' }, new Date('2026-11-19T10:05:00Z'));
    const d = await draftsRepo.get('d1');
    expect(d).toEqual({ id: 'd1', tenantId: 't1', kind: 'NEW_ORDER_MARMITA', targetId: null, formJson: '{"a":2}', updatedAt: '2026-11-19T10:05:00.000Z' });
  });

  it('latest filtra por tenant, tipo e alvo (null casa com null) e pega o mais novo', async () => {
    await draftsRepo.save({ id: 'a', tenantId: 't1', kind: 'PAYMENT', targetId: 'o1', formJson: '{}' }, new Date('2026-11-19T10:00:00Z'));
    await draftsRepo.save({ id: 'b', tenantId: 't1', kind: 'PAYMENT', targetId: 'o1', formJson: '{}' }, new Date('2026-11-19T11:00:00Z'));
    await draftsRepo.save({ id: 'c', tenantId: 't1', kind: 'PAYMENT', targetId: 'o2', formJson: '{}' }, new Date('2026-11-19T12:00:00Z'));
    await draftsRepo.save({ id: 'd', tenantId: 't2', kind: 'PAYMENT', targetId: 'o1', formJson: '{}' }, new Date('2026-11-19T13:00:00Z'));
    await draftsRepo.save({ id: 'e', tenantId: 't1', kind: 'NEW_ORDER_PF', targetId: null, formJson: '{}' }, new Date('2026-11-19T09:00:00Z'));

    expect((await draftsRepo.latest('t1', 'PAYMENT', 'o1'))?.id).toBe('b');
    expect((await draftsRepo.latest('t1', 'NEW_ORDER_PF', null))?.id).toBe('e');
    expect(await draftsRepo.latest('t1', 'NEW_ORDER_KILO', null)).toBeNull();
    expect((await draftsRepo.list('t1')).map((d) => d.id)).toEqual(['c', 'b', 'a', 'e']);
    expect(await draftsRepo.count('t1')).toBe(4);
  });

  it('descarta rascunhos com mais de 3 dias, apaga e limpa o tenant', async () => {
    const now = new Date('2026-11-19T12:00:00Z');
    await draftsRepo.save({ id: 'velho', tenantId: 't1', kind: 'PAYMENT', targetId: null, formJson: '{}' }, new Date(now.getTime() - 73 * 3600 * 1000));
    await draftsRepo.save({ id: 'novo', tenantId: 't1', kind: 'PAYMENT', targetId: null, formJson: '{}' }, new Date(now.getTime() - 3600 * 1000));
    await draftsRepo.save({ id: 'outro', tenantId: 't2', kind: 'PAYMENT', targetId: null, formJson: '{}' }, now);
    await draftsRepo.purgeOld(now);
    expect(await draftsRepo.get('velho')).toBeNull();
    expect(await draftsRepo.get('novo')).not.toBeNull();

    await draftsRepo.remove('novo');
    expect(await draftsRepo.count('t1')).toBe(0);
    await draftsRepo.clearTenant('t2');
    expect(await draftsRepo.count('t2')).toBe(0);
  });
});

describe('pendingOpsRepo (web)', () => {
  it('a pendência gravada sobrevive a recarregar a página (ADR-0010, ADR-0053 §5.4)', async () => {
    await pendingOpsRepo.insert(op());
    reload();
    expect(await pendingOpsRepo.get('k1')).toEqual(op());
  });

  it('chave repetida falha, como o INSERT do SQLite', async () => {
    await pendingOpsRepo.insert(op());
    await expect(pendingOpsRepo.insert(op())).rejects.toBeTruthy();
  });

  it('muda o status, lista as do funcionário por ordem de criação e remove', async () => {
    await pendingOpsRepo.insert(op({ idempotencyKey: 'k2', createdAt: '2026-11-19T12:02:00.000Z' }));
    await pendingOpsRepo.insert(op({ idempotencyKey: 'k1', createdAt: '2026-11-19T12:01:00.000Z' }));
    await pendingOpsRepo.insert(op({ idempotencyKey: 'k3', employeeId: 'e2' }));
    await pendingOpsRepo.insert(op({ idempotencyKey: 'k4', tenantId: 't2' }));

    await pendingOpsRepo.setStatus('k1', 'UNKNOWN', 'timeout');
    expect(await pendingOpsRepo.get('k1')).toMatchObject({ status: 'UNKNOWN', lastError: 'timeout' });
    await pendingOpsRepo.setStatus('nao-existe', 'FAILED');
    expect(await pendingOpsRepo.get('nao-existe')).toBeNull();

    expect((await pendingOpsRepo.list('t1', 'e1')).map((o) => o.idempotencyKey)).toEqual(['k1', 'k2']);
    expect((await pendingOpsRepo.list('t1')).map((o) => o.idempotencyKey).sort()).toEqual(['k1', 'k2', 'k3']);

    await pendingOpsRepo.remove('k2');
    await pendingOpsRepo.clearTenant('t2');
    expect(await pendingOpsRepo.get('k2')).toBeNull();
    expect(await pendingOpsRepo.list('t2')).toEqual([]);
  });
});

describe('metaRepo (web)', () => {
  it('guarda o cursor do feed por tenant e limpa só o do tenant', async () => {
    expect(await metaRepo.lastSeq('t1')).toBeNull();
    await metaRepo.setLastSeq('t1', 42);
    await metaRepo.setLastSeq('t2', 7);
    expect(await metaRepo.lastSeq('t1')).toBe(42);
    expect(await metaRepo.get('lastSyncAt:t1')).not.toBeNull();

    await metaRepo.clearTenant('t1');
    expect(await metaRepo.lastSeq('t1')).toBeNull();
    expect(await metaRepo.get('lastSyncAt:t1')).toBeNull();
    expect(await metaRepo.lastSeq('t2')).toBe(7);

    await metaRepo.set('me', '{"x":1}');
    expect(await metaRepo.get('me')).toBe('{"x":1}');
    await metaRepo.remove('me');
    expect(await metaRepo.get('me')).toBeNull();
  });
});
