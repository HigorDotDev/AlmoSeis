/**
 * M2: Enter repetido (web) ou toque duplo (celular) no "Confirmar" não gera duas operações. O `state` do React
 * só muda no próximo render, então a trava do `run` é síncrona (ref); e, entre telas, procurar a pendência igual
 * e gravá-la é uma seção exclusiva em `runCritical`.
 */
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react-native';
import type { ReactNode } from 'react';

import { configureApiClient } from '@/api/client';
import { critical } from '@/api/critical';
import type { Me } from '@/api/types';
import { runCritical } from '@/features/sync/operations';
import { useCriticalMutation } from '@/features/sync/useCriticalMutation';
import { usePendingOps } from '@/stores/pendingOps';
import { useSession } from '@/stores/session';

import { memory } from '@/test-utils/fakeDb';

jest.mock('@/db/pendingOpsRepo', () => ({ pendingOpsRepo: jest.requireActual('@/test-utils/fakeDb').pendingOpsRepo }));
jest.mock('@/db/draftsRepo', () => ({ draftsRepo: jest.requireActual('@/test-utils/fakeDb').draftsRepo }));
jest.mock('@/db/metaRepo', () => ({ metaRepo: jest.requireActual('@/test-utils/fakeDb').metaRepo }));

const payment = () => critical.registerPayment('o1', { amount: '35.00', paymentMethod: 'PIX', cashReceived: null, label: null }, 4);

let releases: (() => void)[] = [];

beforeEach(() => {
  memory.reset();
  usePendingOps.getState().clear();
  releases = [];
  useSession.setState({ me: { tenant: { id: 't1' }, employee: { id: 'e1' } } as unknown as Me, status: 'signedIn' });
  configureApiClient({
    getAccessToken: () => 'tok',
    refresh: async () => true,
    onSessionExpired: () => {},
    onUpgradeRequired: () => {},
    onSubscriptionInactive: () => {},
  });
  global.fetch = jest.fn(
    () =>
      new Promise<Response>((resolve) =>
        releases.push(() => resolve(new Response(JSON.stringify({ payment: { id: 'p1' }, order: { version: 5 } }), { status: 201, headers: { ETag: '"5"' } }))),
      ),
  ) as unknown as typeof fetch;
});

const flush = () => new Promise((r) => setTimeout(r, 0));

async function waitForFetches(n: number) {
  for (let i = 0; i < 20 && (global.fetch as jest.Mock).mock.calls.length < n; i++) await flush();
}

describe('useCriticalMutation: trava síncrona', () => {
  it('dois `run` no mesmo tick: o segundo é ignorado e só uma operação sai', async () => {
    const client = new QueryClient();
    const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
    const onConfirmed = jest.fn();
    const { result } = await renderHook(() => useCriticalMutation({ request: payment, describe: () => 'Pagamento', onConfirmed }), { wrapper });

    let first!: Promise<unknown>;
    let second!: Promise<unknown>;
    await act(async () => {
      first = result.current.run(undefined as never);
      second = result.current.run(undefined as never);
      await waitForFetches(1);
    });
    expect(await second).toEqual({ kind: 'ignored' });
    expect(global.fetch).toHaveBeenCalledTimes(1);
    expect(memory.ops.size).toBe(1);

    await act(async () => {
      releases.shift()!();
      await first;
    });
    expect((await first as { kind: string }).kind).toBe('confirmed');
    expect(onConfirmed).toHaveBeenCalledTimes(1);

    // Terminou: a trava solta e uma nova ação é enviada normalmente.
    await act(async () => {
      const third = result.current.run(undefined as never);
      await waitForFetches(2);
      releases.shift()!();
      expect((await third).kind).toBe('confirmed');
    });
    expect(global.fetch).toHaveBeenCalledTimes(2);
  });
});

describe('runCritical: procurar e gravar a pendência é atômico', () => {
  it('duas chamadas concorrentes da mesma ação: uma envia, a outra fica bloqueada pela pendência da primeira', async () => {
    const input = { tenantId: 't1', employeeId: 'e1', request: payment(), description: 'Pagamento' };
    const a = runCritical(input);
    const b = runCritical(input);
    await waitForFetches(1);
    const outB = await b;
    expect(outB.kind).toBe('blocked');
    expect(global.fetch).toHaveBeenCalledTimes(1);
    releases.shift()!();
    expect((await a).kind).toBe('confirmed');
    expect(memory.ops.size).toBe(0);
  });
});
