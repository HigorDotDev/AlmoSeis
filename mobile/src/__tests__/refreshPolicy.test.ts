/**
 * I1 (ADR-0041, 08 §8.6): o sync só renova quando o access token vence em menos de `SYNC_REFRESH_MARGIN_MS`.
 * Na web o sync roda a cada volta da aba para a frente; renovar sempre esgotaria o limite de 10/min por IP.
 */
import { QueryClient } from '@tanstack/react-query';

import { configureApiClient, refreshIfExpiring, SYNC_REFRESH_MARGIN_MS } from '@/api/client';
import { resetSyncThrottle, runSync } from '@/features/sync/syncEngine';

import { memory } from '@/test-utils/fakeDb';

jest.mock('@/db/metaRepo', () => ({ metaRepo: jest.requireActual('@/test-utils/fakeDb').metaRepo }));
jest.mock('@/db/pendingOpsRepo', () => ({ pendingOpsRepo: jest.requireActual('@/test-utils/fakeDb').pendingOpsRepo }));
jest.mock('@/db/draftsRepo', () => ({ draftsRepo: jest.requireActual('@/test-utils/fakeDb').draftsRepo }));

const inMinutes = (m: number) => new Date(Date.now() + m * 60_000).toISOString();

function setup(token: string | null, expiresAt: string | null | undefined) {
  const refresh = jest.fn(async () => true);
  configureApiClient({
    getAccessToken: () => token,
    ...(expiresAt === undefined ? {} : { getAccessTokenExpiresAt: () => expiresAt }),
    refresh,
    onSessionExpired: () => {},
    onUpgradeRequired: () => {},
    onSubscriptionInactive: () => {},
  });
  return refresh;
}

beforeEach(() => {
  memory.reset();
  resetSyncThrottle();
  global.fetch = jest.fn(async () => new Response(JSON.stringify({ changes: [], lastSeq: 10, hasMore: false }), { status: 200 })) as unknown as typeof fetch;
});

describe('refreshIfExpiring', () => {
  it('margem de 5 min', () => {
    expect(SYNC_REFRESH_MARGIN_MS).toBe(5 * 60_000);
  });

  it('token com mais que a margem: não renova', async () => {
    const refresh = setup('at', inMinutes(12));
    expect(await refreshIfExpiring()).toBe(true);
    expect(refresh).not.toHaveBeenCalled();
  });

  it('token vencendo, vencido, sem validade conhecida ou sem token: renova', async () => {
    for (const [token, exp] of [
      ['at', inMinutes(4)],
      ['at', inMinutes(-1)],
      ['at', null],
      ['at', undefined],
      ['at', 'lixo'],
      [null, inMinutes(12)],
    ] as const) {
      const refresh = setup(token, exp);
      await refreshIfExpiring();
      expect(refresh).toHaveBeenCalledTimes(1);
    }
  });
});

describe('runSync', () => {
  it('com token válido, sincroniza sem renovar; vencendo, renova antes', async () => {
    const ctx = { queryClient: new QueryClient(), tenantId: 't1', ownEmployeeId: 'e1' };
    const fresh = setup('at', inMinutes(14));
    await runSync(ctx, true);
    expect(fresh).not.toHaveBeenCalled();
    expect(global.fetch).toHaveBeenCalledTimes(1);

    const expiring = setup('at', inMinutes(2));
    await runSync(ctx, true);
    expect(expiring).toHaveBeenCalledTimes(1);
  });
});
