/** 08 §8.6: feed incremental, 410 SyncCursorExpired e salto de `seq` no tempo real. */
import { QueryClient } from '@tanstack/react-query';

import { configureApiClient } from '@/api/client';
import { decideSeq, EventDeduper } from '@/features/realtime/realtime';
import { pullChanges, resetSyncThrottle, runSync } from '@/features/sync/syncEngine';

import { memory } from '@/test-utils/fakeDb';

jest.mock('@/db/metaRepo', () => ({ metaRepo: jest.requireActual('@/test-utils/fakeDb').metaRepo }));
jest.mock('@/db/pendingOpsRepo', () => ({ pendingOpsRepo: jest.requireActual('@/test-utils/fakeDb').pendingOpsRepo }));
jest.mock('@/db/draftsRepo', () => ({ draftsRepo: jest.requireActual('@/test-utils/fakeDb').draftsRepo }));

const respond = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

let handler: (url: string) => Response;
beforeEach(() => {
  memory.reset();
  resetSyncThrottle();
  configureApiClient({
    getAccessToken: () => 'tok',
    refresh: async () => true,
    onSessionExpired: () => {},
    onUpgradeRequired: () => {},
    onSubscriptionInactive: () => {},
  });
  global.fetch = jest.fn(async (url: string) => handler(url)) as unknown as typeof fetch;
});

describe('pullChanges', () => {
  it('pagina até hasMore=false, invalida pelo mapa e grava lastSeq', async () => {
    memory.meta.set('lastSeq:t1', '1040');
    const client = new QueryClient();
    const invalidate = jest.spyOn(client, 'invalidateQueries');
    const pages: Record<string, unknown> = {
      '1040': { changes: [{ seq: 1041, entityType: 'ORDER', entityId: 'o1', entityVersion: 5, changeKind: 'UPSERT' }], lastSeq: 1041, hasMore: true },
      '1041': { changes: [{ seq: 1042, entityType: 'MENU', entityId: 'm1', entityVersion: 3, changeKind: 'UPSERT' }], lastSeq: 1042, hasMore: false },
    };
    handler = (url) => respond(200, pages[new URL(url).searchParams.get('since')!]);
    await pullChanges({ queryClient: client, tenantId: 't1', ownEmployeeId: null });
    expect(memory.meta.get('lastSeq:t1')).toBe('1042');
    const keys = invalidate.mock.calls.map((c) => c[0]?.queryKey);
    expect(keys).toContainEqual(['orders', 'o1']);
    expect(keys).toContainEqual(['menus']);
  });

  it('410 SyncCursorExpired descarta o cache e segue de currentSeq', async () => {
    memory.meta.set('lastSeq:t1', '5');
    const client = new QueryClient();
    client.setQueryData(['orders', 'o1'], { id: 'o1' });
    handler = () => respond(410, { errorCode: 'SyncCursorExpired', currentSeq: 1043 });
    await pullChanges({ queryClient: client, tenantId: 't1', ownEmployeeId: null });
    expect(client.getQueryData(['orders', 'o1'])).toBeUndefined();
    expect(memory.meta.get('lastSeq:t1')).toBe('1043');
  });

  it('primeira execução só avança o cursor', async () => {
    const client = new QueryClient();
    const invalidate = jest.spyOn(client, 'invalidateQueries');
    handler = () => respond(200, { changes: [{ seq: 7, entityType: 'ORDER', entityId: 'o1', entityVersion: 1, changeKind: 'UPSERT' }], lastSeq: 7, hasMore: false });
    await pullChanges({ queryClient: client, tenantId: 't1', ownEmployeeId: null });
    expect(invalidate).not.toHaveBeenCalled();
    expect(memory.meta.get('lastSeq:t1')).toBe('7');
  });

  it('runSync respeita o intervalo mínimo de 10 s', async () => {
    handler = () => respond(200, { changes: [], lastSeq: 0, hasMore: false });
    const ctx = { queryClient: new QueryClient(), tenantId: 't1', ownEmployeeId: null };
    await runSync(ctx);
    await runSync(ctx);
    const changesCalls = (global.fetch as jest.Mock).mock.calls.filter((c) => String(c[0]).includes('/sync/changes'));
    expect(changesCalls).toHaveLength(1);
  });
});

describe('tempo real', () => {
  it('deduplica por eventId com memória limitada', () => {
    const d = new EventDeduper(2);
    expect(d.accept('a')).toBe(true);
    expect(d.accept('a')).toBe(false);
    d.accept('b');
    d.accept('c');
    expect(d.accept('a')).toBe(true);
  });

  it('salto de seq agenda o sync', () => {
    expect(decideSeq(10, 11)).toBe('advance');
    expect(decideSeq(10, 13)).toBe('gap');
    expect(decideSeq(10, 9)).toBe('ignore');
    expect(decideSeq(null, 5)).toBe('gap');
    expect(decideSeq(10, null)).toBe('ignore');
  });
});
