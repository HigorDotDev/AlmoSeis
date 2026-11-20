/**
 * Renovação do token entre abas (ADR-0053 §3.6): nunca duas renovações ao mesmo tempo (o servidor revogaria a
 * família, ADR-0027), e quem esperou o lock reaproveita o token que outra aba acabou de obter.
 */
import type { Tokens } from '@/api/types';
import { createTabCoordinator, type ChannelLike, type LockManagerLike, type TabMessage } from '@/features/auth/tabCoordinator';

/** Web Locks: uma fila por nome, compartilhada entre as "abas". */
function fakeLocks(): LockManagerLike {
  const tails = new Map<string, Promise<unknown>>();
  return {
    request<T>(name: string, callback: () => Promise<T>): Promise<T> {
      const prev = tails.get(name) ?? Promise.resolve();
      const run = prev.then(callback, callback);
      tails.set(
        name,
        run.catch(() => {}),
      );
      return run;
    },
  };
}

/** BroadcastChannel: entrega às OUTRAS abas, na hora (o pior caso para a corrida é testado à parte). */
function fakeBus() {
  const channels = new Set<FakeChannel>();
  class FakeChannel implements ChannelLike {
    private listeners = new Set<(e: { data: unknown }) => void>();
    constructor() {
      channels.add(this);
    }
    postMessage(message: unknown) {
      for (const c of channels) if (c !== this) for (const l of c.listeners) l({ data: message });
    }
    addEventListener(_t: 'message', l: (e: { data: unknown }) => void) {
      this.listeners.add(l);
    }
    removeEventListener(_t: 'message', l: (e: { data: unknown }) => void) {
      this.listeners.delete(l);
    }
  }
  return { channel: () => new FakeChannel() };
}

const NOW = Date.parse('2026-11-19T12:00:00Z');

const tokens = (n: number): Tokens => ({
  tokenType: 'Bearer',
  accessToken: `at_${n}`,
  accessTokenExpiresAt: new Date(NOW + 15 * 60_000).toISOString(),
  refreshToken: null,
  refreshTokenExpiresAt: new Date(NOW + 30 * 86_400_000).toISOString(),
});

/** Um "servidor" que conta renovações e detecta duas ao mesmo tempo. */
function fakeServer() {
  let calls = 0;
  let inFlight = 0;
  let maxConcurrent = 0;
  return {
    get calls() {
      return calls;
    },
    get maxConcurrent() {
      return maxConcurrent;
    },
    doRefresh: async (): Promise<Tokens | null> => {
      calls++;
      inFlight++;
      maxConcurrent = Math.max(maxConcurrent, inFlight);
      await new Promise((r) => setTimeout(r, 5));
      inFlight--;
      return tokens(calls);
    },
  };
}

describe('createTabCoordinator', () => {
  it('duas abas renovando juntas: uma renova, a outra reaproveita o token novo', async () => {
    const locks = fakeLocks();
    const bus = fakeBus();
    const server = fakeServer();
    const a = createTabCoordinator({ locks, channel: bus.channel(), doRefresh: server.doRefresh, now: () => NOW });
    const b = createTabCoordinator({ locks, channel: bus.channel(), doRefresh: server.doRefresh, now: () => NOW });

    const [ta, tb] = await Promise.all([a.refresh('at_0'), b.refresh('at_0')]);

    expect(server.calls).toBe(1);
    expect(server.maxConcurrent).toBe(1);
    expect(ta).toEqual(tokens(1));
    expect(tb).toEqual(tokens(1));
  });

  it('sem o aviso a tempo, a segunda aba renova de novo, mas nunca ao mesmo tempo', async () => {
    const locks = fakeLocks();
    const server = fakeServer();
    // Sem canal: nenhuma aba fica sabendo da outra.
    const tabs = [1, 2, 3].map(() => createTabCoordinator({ locks, channel: null, doRefresh: server.doRefresh, now: () => NOW }));

    await Promise.all(tabs.map((t) => t.refresh('at_0')));

    expect(server.calls).toBe(3);
    expect(server.maxConcurrent).toBe(1);
  });

  it('na mesma aba, chamadas simultâneas viram uma renovação só', async () => {
    const server = fakeServer();
    const a = createTabCoordinator({ locks: fakeLocks(), channel: null, doRefresh: server.doRefresh, now: () => NOW });
    const results = await Promise.all([a.refresh('at_0'), a.refresh('at_0'), a.refresh('at_0')]);
    expect(server.calls).toBe(1);
    expect(new Set(results.map((t) => t?.accessToken))).toEqual(new Set(['at_1']));
  });

  it('não reaproveita o próprio token velho nem um que está vencendo', async () => {
    const bus = fakeBus();
    const server = fakeServer();
    const other = bus.channel();
    const a = createTabCoordinator({ locks: fakeLocks(), channel: bus.channel(), doRefresh: server.doRefresh, now: () => NOW });

    other.postMessage({ type: 'tokens', tokens: { ...tokens(9), accessTokenExpiresAt: new Date(NOW + 10_000).toISOString() } } satisfies TabMessage);
    expect(await a.refresh('at_0')).toEqual(tokens(1));

    other.postMessage({ type: 'tokens', tokens: tokens(7) } satisfies TabMessage);
    expect(await a.refresh('at_7')).toEqual(tokens(2));
  });

  it('refresh recusado devolve null e não avisa as outras abas', async () => {
    const bus = fakeBus();
    const heard: TabMessage[] = [];
    const b = createTabCoordinator({ locks: null, channel: bus.channel(), doRefresh: async () => null });
    b.subscribe((m) => heard.push(m));
    const a = createTabCoordinator({ locks: null, channel: bus.channel(), doRefresh: async () => null });

    expect(await a.refresh(null)).toBeNull();
    expect(heard).toEqual([]);
  });

  it('logout e entrada chegam às outras abas; depois do logout, o token antigo não é reaproveitado', async () => {
    const bus = fakeBus();
    const server = fakeServer();
    const heard: TabMessage[] = [];
    const a = createTabCoordinator({ locks: null, channel: bus.channel(), doRefresh: server.doRefresh, now: () => NOW });
    const b = createTabCoordinator({ locks: null, channel: bus.channel(), doRefresh: server.doRefresh, now: () => NOW });
    const off = b.subscribe((m) => heard.push(m));

    await a.refresh(null);
    a.announce({ type: 'signedOut' });
    a.announce({ type: 'signedIn', tenantId: 't2' });
    off();
    a.announce({ type: 'signedOut' });

    expect(heard.map((m) => m.type)).toEqual(['tokens', 'signedOut', 'signedIn']);
    // b soube do token de a, mas o logout o invalidou: renova de verdade.
    expect(await b.refresh(null)).toEqual(tokens(2));
  });
});
