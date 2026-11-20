/**
 * Sessão na web (ADR-0053 §3.5, 08 §8.15): o refresh mora num cookie `HttpOnly` que só o `POST /auth/logout`
 * apaga. Sair grava a marca de logout pendente antes de tudo, limpa a tela antes da rede, e nenhuma
 * restauração acontece enquanto o servidor não confirmar. Ao abrir, 429/5xx/rede = "reconectando", nunca login.
 */
import { fakeLocalStorage } from '@/test-utils/webGlobals';

import { configureApiClient } from '@/api/client';
import type { Me, Tokens } from '@/api/types';
import { PENDING_LOGOUT_KEY } from '@/features/auth/pendingLogout';
import { useSession } from '@/stores/session';
import { resetWebSessionTransportForTests } from '@/stores/sessionTransport.web';

import { memory } from '@/test-utils/fakeDb';

jest.mock('@/stores/sessionTransport', () => jest.requireActual('@/stores/sessionTransport.web'));
jest.mock('@/stores/secure', () => jest.requireActual('@/stores/secure.web'));
jest.mock('@/db/metaRepo', () => ({ metaRepo: jest.requireActual('@/test-utils/fakeDb').metaRepo }));

const tokens = (n: number, minutes = 15): Tokens => ({
  tokenType: 'Bearer',
  accessToken: `at_${n}`,
  accessTokenExpiresAt: new Date(Date.now() + minutes * 60_000).toISOString(),
  refreshToken: null,
  refreshTokenExpiresAt: new Date(Date.now() + 30 * 86_400_000).toISOString(),
});

const me = {
  tenant: { id: 't1', name: 'Restaurante', timeZone: 'America/Sao_Paulo' },
  employee: { id: 'e1', displayName: 'Ana', roles: ['CASHIER'], hasPin: true },
  permissions: [],
  subscription: { status: 'ACTIVE', trialEndsAt: null, daysLeft: null, canWrite: true },
} as unknown as Me;

const json = (status: number, body?: unknown, headers: Record<string, string> = {}) =>
  new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } });

type Route = (init: RequestInit) => Response | Promise<Response>;
let routes: Record<string, Route>;
const calls: string[] = [];

function deferred<T>() {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
}

const flush = () => new Promise((r) => setTimeout(r, 0));

beforeEach(() => {
  resetWebSessionTransportForTests();
  fakeLocalStorage.clear();
  memory.reset();
  calls.length = 0;
  routes = {};
  useSession.setState({ status: 'signedOut', tokens: null, me: null, reconnecting: false, signOutReason: null });
  configureApiClient({
    getAccessToken: () => useSession.getState().tokens?.accessToken ?? null,
    getAccessTokenExpiresAt: () => useSession.getState().tokens?.accessTokenExpiresAt ?? null,
    refresh: () => useSession.getState().refresh(),
    onSessionExpired: () => void useSession.getState().signOut('expired'),
    onUpgradeRequired: () => {},
    onSubscriptionInactive: () => {},
  });
  global.fetch = jest.fn(async (url: string, init: RequestInit) => {
    const path = new URL(url).pathname.replace('/api/v1', '');
    calls.push(path);
    const route = routes[path];
    if (!route) throw new Error(`rota inesperada ${path}`);
    return route(init);
  }) as unknown as typeof fetch;
});

afterEach(() => resetWebSessionTransportForTests());

async function signedIn() {
  await useSession.getState().signIn({ tokens: tokens(1), me });
  expect(useSession.getState().status).toBe('signedIn');
}

describe('sair na web (B1)', () => {
  it('marca antes da API, tela limpa antes da resposta; logout falhou → F5 não restaura', async () => {
    await signedIn();
    const logout = deferred<Response>();
    let seenAtSend: { mark: string | null; status: string } | null = null;
    routes['/auth/logout'] = (init) => {
      seenAtSend = { mark: fakeLocalStorage.getItem(PENDING_LOGOUT_KEY), status: useSession.getState().status };
      // Anônima: sem token (a sessão local já foi limpa) e sem refresh no corpo.
      expect((init.headers as Record<string, string>).Authorization).toBeUndefined();
      expect(init.credentials).toBe('include');
      return logout.promise;
    };

    const out = useSession.getState().signOut('idle');
    await flush();
    expect(seenAtSend).toEqual({ mark: expect.any(String), status: 'signedOut' });
    expect(useSession.getState()).toMatchObject({ status: 'signedOut', tokens: null, me: null, signOutReason: 'idle' });

    logout.resolve(json(503, { errorCode: 'ServiceUnavailable' }));
    await out;
    expect(fakeLocalStorage.getItem(PENDING_LOGOUT_KEY)).not.toBeNull();

    // F5: módulo novo, mesma marca no localStorage. Não pede refresh; vai direto para a entrada.
    resetWebSessionTransportForTests();
    calls.length = 0;
    routes['/auth/logout'] = () => json(503, {});
    routes['/auth/refresh'] = () => json(200, tokens(2));
    await useSession.getState().hydrate();
    expect(useSession.getState().status).toBe('signedOut');
    expect(calls).not.toContain('/auth/refresh');
    await flush();
    expect(calls).toContain('/auth/logout');
    expect(fakeLocalStorage.getItem(PENDING_LOGOUT_KEY)).not.toBeNull();
  });

  it('com o logout confirmado (204), a marca sai e a próxima abertura volta a restaurar', async () => {
    await signedIn();
    routes['/auth/logout'] = () => new Response(null, { status: 204 });
    await useSession.getState().signOut('idle');
    expect(fakeLocalStorage.getItem(PENDING_LOGOUT_KEY)).toBeNull();

    routes['/auth/refresh'] = () => json(401, { errorCode: 'InvalidRefreshToken' });
    await useSession.getState().hydrate();
    expect(calls).toContain('/auth/refresh');
    expect(useSession.getState().status).toBe('signedOut');
  });

  it('401 no logout também quer dizer "sem sessão": limpa a marca', async () => {
    await signedIn();
    routes['/auth/logout'] = () => json(401, { errorCode: 'Unauthenticated' });
    await useSession.getState().signOut('user');
    expect(fakeLocalStorage.getItem(PENDING_LOGOUT_KEY)).toBeNull();
  });

  it('`expired` (ex.: 401 depois de renovar) também apaga o cookie no servidor', async () => {
    await signedIn();
    routes['/auth/logout'] = () => new Response(null, { status: 204 });
    await useSession.getState().signOut('expired');
    expect(calls).toEqual(['/auth/logout']);
    expect(useSession.getState().status).toBe('signedOut');
  });

  it('sem rede no logout: a marca fica e um login espera a tentativa antes de pedir o cookie novo', async () => {
    await signedIn();
    routes['/auth/logout'] = () => {
      throw new TypeError('Failed to fetch');
    };
    await useSession.getState().signOut('idle');
    expect(fakeLocalStorage.getItem(PENDING_LOGOUT_KEY)).not.toBeNull();

    const logout = deferred<Response>();
    routes['/auth/logout'] = () => logout.promise;
    let ready = false;
    const prepared = useSession
      .getState()
      .prepareSignIn()
      .then(() => {
        ready = true;
      });
    await flush();
    expect(ready).toBe(false);
    logout.resolve(new Response(null, { status: 204 }));
    await prepared;
    expect(ready).toBe(true);
    expect(fakeLocalStorage.getItem(PENDING_LOGOUT_KEY)).toBeNull();
  });

  it('entrar limpa a marca (o cookie agora é de outra sessão)', async () => {
    fakeLocalStorage.setItem(PENDING_LOGOUT_KEY, '1');
    await signedIn();
    expect(fakeLocalStorage.getItem(PENDING_LOGOUT_KEY)).toBeNull();
  });
});

describe('abrir a página sem resposta do servidor (I1)', () => {
  it('429 com Retry-After, 503 e queda de rede → reconectando e tenta de novo, sem ir ao login', async () => {
    const answers: (() => Response)[] = [
      () => json(429, { errorCode: 'RateLimited' }, { 'Retry-After': '7' }),
      () => json(503, {}),
      () => {
        throw new TypeError('Failed to fetch');
      },
      () => json(200, tokens(5)),
    ];
    routes['/auth/refresh'] = () => answers.shift()!();
    routes['/auth/me'] = () => json(200, me);
    const waits: number[] = [];
    const statuses: string[] = [];
    await useSession.getState().hydrate({
      sleep: async (ms) => {
        waits.push(ms);
        statuses.push(`${useSession.getState().status}/${useSession.getState().reconnecting}`);
      },
    });
    expect(waits).toEqual([7000, 4000, 8000]);
    expect(statuses.every((s) => s === 'signedOut/true')).toBe(true);
    expect(useSession.getState()).toMatchObject({ status: 'signedIn', reconnecting: false });
    expect(useSession.getState().tokens?.accessToken).toBe('at_5');
  });

  it('`/auth/me` falhou sem cache: tenta de novo reaproveitando o token, sem outro refresh', async () => {
    let refreshes = 0;
    routes['/auth/refresh'] = () => {
      refreshes++;
      return json(200, tokens(6));
    };
    const meAnswers = [() => json(502, {}), () => json(200, me)];
    routes['/auth/me'] = () => meAnswers.shift()!();
    await useSession.getState().hydrate({ sleep: async () => {} });
    expect(refreshes).toBe(1);
    expect(useSession.getState().status).toBe('signedIn');
  });

  it('401 no refresh continua indo para a entrada', async () => {
    routes['/auth/refresh'] = () => json(401, { errorCode: 'InvalidRefreshToken' });
    const sleep = jest.fn(async () => {});
    await useSession.getState().hydrate({ sleep });
    expect(sleep).not.toHaveBeenCalled();
    expect(useSession.getState().status).toBe('signedOut');
  });
});
