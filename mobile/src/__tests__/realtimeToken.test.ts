/**
 * I5: o hub pode fechar a conexão quando o access token vence. O token do SignalR vem de uma função assíncrona
 * que renova antes de (re)conectar se faltar menos de 1 min, e uma conexão que desistiu agenda nova tentativa.
 */
import { configureApiClient, refreshIfExpiring } from '@/api/client';
import { REALTIME_REFRESH_MARGIN_MS, RealtimeConnection, realtimeAccessToken } from '@/features/realtime/realtime';

type Factory = () => Promise<string>;
const mockHub = {
  factories: [] as Factory[],
  instances: [] as MockConnection[],
  startResults: [] as (() => Promise<void>)[],
};

class MockConnection {
  state = 'Disconnected';
  closeHandlers: (() => void)[] = [];
  constructor(readonly accessTokenFactory: Factory) {
    mockHub.instances.push(this);
  }
  on() {}
  onreconnected() {}
  onclose(fn: () => void) {
    this.closeHandlers.push(fn);
  }
  async start() {
    const next = mockHub.startResults.shift() ?? (() => Promise.resolve());
    await next();
    this.state = 'Connected';
  }
  async stop() {
    this.state = 'Disconnected';
    for (const h of this.closeHandlers) h();
  }
  /** O servidor fechou e a reconexão automática desistiu. */
  serverClosed() {
    this.state = 'Disconnected';
    for (const h of this.closeHandlers) h();
  }
}

jest.mock('@microsoft/signalr', () => {
  class HubConnectionBuilder {
    private factory: Factory = async () => '';
    withUrl(_url: string, options: { accessTokenFactory: Factory }) {
      this.factory = options.accessTokenFactory;
      mockHub.factories.push(options.accessTokenFactory);
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      return new MockConnection(this.factory);
    }
  }
  return {
    HubConnectionBuilder,
    HttpTransportType: { WebSockets: 1 },
    HubConnectionState: { Connected: 'Connected', Disconnected: 'Disconnected' },
    LogLevel: { None: 6 },
  };
});

const inMinutes = (m: number) => new Date(Date.now() + m * 60_000).toISOString();

beforeEach(() => {
  mockHub.factories.length = 0;
  mockHub.instances.length = 0;
  mockHub.startResults.length = 0;
});

describe('token do hub', () => {
  function session(expiresAt: string) {
    const state = { token: 'at_old', expiresAt };
    const refresh = jest.fn(async () => {
      state.token = 'at_new';
      state.expiresAt = inMinutes(15);
      return true;
    });
    configureApiClient({
      getAccessToken: () => state.token,
      getAccessTokenExpiresAt: () => state.expiresAt,
      refresh,
      onSessionExpired: () => {},
      onUpgradeRequired: () => {},
      onSubscriptionInactive: () => {},
    });
    return { state, refresh };
  }

  it('vencendo em menos de 1 min (ou vencido): renova antes e devolve o token novo', async () => {
    for (const exp of [inMinutes(0.5), inMinutes(-3)]) {
      const { state, refresh } = session(exp);
      expect(await realtimeAccessToken(refreshIfExpiring, () => state.token)).toBe('at_new');
      expect(refresh).toHaveBeenCalledTimes(1);
    }
  });

  it('com folga: não renova', async () => {
    const { state, refresh } = session(inMinutes(10));
    expect(await realtimeAccessToken(refreshIfExpiring, () => state.token)).toBe('at_old');
    expect(refresh).not.toHaveBeenCalled();
  });

  it('sem rede na renovação: segue com o token atual', async () => {
    const refreshFn = jest.fn(async () => {
      throw new Error('offline');
    });
    expect(await realtimeAccessToken(refreshFn, () => 'at_old')).toBe('at_old');
    expect(refreshFn).toHaveBeenCalledWith(REALTIME_REFRESH_MARGIN_MS);
  });

  it('o accessTokenFactory da conexão é assíncrono e passa pelo handler a cada (re)conexão', async () => {
    const getAccessToken = jest.fn(async () => 'at_x');
    const conn = new RealtimeConnection({ getAccessToken, onEvent: () => {}, onReconnected: () => {}, onGaveUp: () => {} });
    await conn.start();
    expect(await mockHub.factories[0]!()).toBe('at_x');
    expect(await mockHub.factories[0]!()).toBe('at_x');
    expect(getAccessToken).toHaveBeenCalledTimes(2);
    await conn.stop();
  });
});

describe('RealtimeConnection depois de desistir', () => {
  function timers() {
    const pending: { fn: () => void; ms: number }[] = [];
    return {
      pending,
      setTimer: (fn: () => void, ms: number) => {
        const t = { fn, ms };
        pending.push(t);
        return t;
      },
      clearTimer: (h: unknown) => {
        const i = pending.indexOf(h as never);
        if (i >= 0) pending.splice(i, 1);
      },
    };
  }

  it('agenda nova tentativa com espera crescente e zera ao conectar', async () => {
    const t = timers();
    const onGaveUp = jest.fn();
    const conn = new RealtimeConnection({ getAccessToken: () => 'at', onEvent: () => {}, onReconnected: () => {}, onGaveUp }, { retryDelaysMs: [10, 20], ...t });

    mockHub.startResults.push(() => Promise.reject(new Error('401')));
    await conn.start();
    expect(onGaveUp).toHaveBeenCalledTimes(1);
    expect(t.pending.map((p) => p.ms)).toEqual([10]);

    mockHub.startResults.push(() => Promise.reject(new Error('rede')));
    await (async () => t.pending.shift()!.fn())();
    await new Promise((r) => setTimeout(r, 0));
    expect(t.pending.map((p) => p.ms)).toEqual([20]);

    t.pending.shift()!.fn();
    await new Promise((r) => setTimeout(r, 0));
    expect(mockHub.instances.at(-1)!.state).toBe('Connected');
    expect(t.pending).toEqual([]);

    // Conectada, o servidor fecha (token vencido) e a reconexão automática desiste: volta à primeira espera.
    mockHub.instances.at(-1)!.serverClosed();
    expect(t.pending.map((p) => p.ms)).toEqual([10]);
    await conn.stop();
    expect(t.pending).toEqual([]);
  });

  it('`stop()` não agenda nada', async () => {
    const t = timers();
    const conn = new RealtimeConnection({ getAccessToken: () => 'at', onEvent: () => {}, onReconnected: () => {}, onGaveUp: () => {} }, t);
    await conn.start();
    await conn.stop();
    expect(t.pending).toEqual([]);
  });
});
