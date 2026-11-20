/** Logout pendente na web (ADR-0053 §3.5): marca compartilhada entre abas, tentativas e espera do login. */
import { ApiError, NetworkUnknownError } from '@/api/errors';
import { createPendingLogout, PENDING_LOGOUT_KEY, type KeyValueLike } from '@/features/auth/pendingLogout';

function memoryStorage(): KeyValueLike & { map: Map<string, string> } {
  const map = new Map<string, string>();
  return { map, getItem: (k) => map.get(k) ?? null, setItem: (k, v) => void map.set(k, v), removeItem: (k) => void map.delete(k) };
}

function fakeTimers() {
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
    fire() {
      const t = pending.shift();
      t?.fn();
    },
  };
}

const flush = () => new Promise((r) => setTimeout(r, 0));

describe('createPendingLogout', () => {
  it('falha → mantém a marca e tenta de novo com espera crescente até o 204', async () => {
    const storage = memoryStorage();
    const timers = fakeTimers();
    const results: (() => Promise<void>)[] = [
      () => Promise.reject(new NetworkUnknownError()),
      () => Promise.reject(new ApiError(429, { errorCode: 'RateLimited' }, 30)),
      () => Promise.reject(new ApiError(503, {})),
      () => Promise.resolve(),
    ];
    const logout = jest.fn(() => results.shift()!());
    const p = createPendingLogout({ storage, logout, retryDelaysMs: [10, 20], ...timers });

    p.mark();
    expect(await p.drain()).toBe(false);
    expect(timers.pending.map((t) => t.ms)).toEqual([10]);
    timers.fire();
    await flush();
    expect(timers.pending.map((t) => t.ms)).toEqual([20]);
    timers.fire();
    await flush();
    expect(timers.pending.map((t) => t.ms)).toEqual([20]);
    expect(p.has()).toBe(true);
    timers.fire();
    await flush();
    expect(logout).toHaveBeenCalledTimes(4);
    expect(p.has()).toBe(false);
    expect(timers.pending).toEqual([]);
  });

  it('401 conta como "sem sessão"; 403 (origem) não', async () => {
    const storage = memoryStorage();
    const timers = fakeTimers();
    const forbidden = createPendingLogout({ storage, logout: () => Promise.reject(new ApiError(403, { errorCode: 'OriginNotAllowed' })), ...timers });
    forbidden.mark();
    expect(await forbidden.drain()).toBe(false);
    forbidden.stop();

    const unauthorized = createPendingLogout({ storage, logout: () => Promise.reject(new ApiError(401, {})), ...timers });
    expect(await unauthorized.drain()).toBe(true);
    expect(storage.map.has(PENDING_LOGOUT_KEY)).toBe(false);
  });

  it('a marca vale para as outras abas (mesmo localStorage); a outra aba vê que já foi concluído', async () => {
    const storage = memoryStorage();
    const a = createPendingLogout({ storage, logout: () => Promise.resolve(), ...fakeTimers() });
    const bLogout = jest.fn(() => Promise.resolve());
    const b = createPendingLogout({ storage, logout: bLogout, ...fakeTimers() });

    a.mark();
    expect(b.has()).toBe(true);
    await a.drain();
    expect(b.has()).toBe(false);
    expect(await b.drain()).toBe(true);
    expect(bLogout).not.toHaveBeenCalled();
  });

  it('chamadas simultâneas na aba viram um logout só', async () => {
    let release!: () => void;
    const logout = jest.fn(() => new Promise<void>((r) => (release = r)));
    const p = createPendingLogout({ storage: memoryStorage(), logout, ...fakeTimers() });
    p.mark();
    const both = Promise.all([p.drain(), p.drain(), p.beforeSignIn()]);
    await flush();
    release();
    await both;
    expect(logout).toHaveBeenCalledTimes(1);
  });

  it('sem marca, o login ainda espera o lock (logout de outra aba em andamento)', async () => {
    let releaseOther!: () => void;
    const tail = new Promise<void>((r) => (releaseOther = r));
    const withLock = <T,>(fn: () => Promise<T>) => tail.then(fn);
    const p = createPendingLogout({ storage: memoryStorage(), logout: () => Promise.resolve(), withLock, ...fakeTimers() });
    let done = false;
    const before = p.beforeSignIn().then(() => (done = true));
    await flush();
    expect(done).toBe(false);
    releaseOther();
    await before;
    expect(done).toBe(true);
  });

  it('entrar (clear) para as tentativas agendadas', async () => {
    const timers = fakeTimers();
    const p = createPendingLogout({ storage: memoryStorage(), logout: () => Promise.reject(new NetworkUnknownError()), ...timers });
    p.mark();
    await p.drain();
    expect(timers.pending).toHaveLength(1);
    p.clear();
    expect(timers.pending).toHaveLength(0);
    expect(p.has()).toBe(false);
  });
});
