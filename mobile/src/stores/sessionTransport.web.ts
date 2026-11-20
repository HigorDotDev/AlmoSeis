/**
 * Web (ADR-0053 §3): access token só em memória; refresh no cookie `HttpOnly` `__Secure-almoseis-rt`, que o
 * navegador manda sozinho para `/api/v1/auth`. Ao abrir ou recarregar a página, a sessão volta pelo
 * `POST /auth/refresh`. A renovação é coordenada entre abas (Web Locks + BroadcastChannel).
 *
 * Sair usa o "logout pendente" (`pendingLogout.ts`): marca no `localStorage` → avisa as outras abas → limpa a
 * tela → `POST /auth/logout`. Enquanto o servidor não confirmar, nenhuma aba restaura a sessão pelo cookie.
 */
import { isApiError, NetworkUnknownError } from '@/api/errors';
import { authApi } from '@/api/endpoints/auth';
import type { Me, Tokens } from '@/api/types';
import { createPendingLogout, type KeyValueLike, type PendingLogout } from '@/features/auth/pendingLogout';
import {
  createTabCoordinator,
  REFRESH_LOCK_NAME,
  SESSION_CHANNEL_NAME,
  type ChannelLike,
  type LockManagerLike,
  type TabCoordinator,
} from '@/features/auth/tabCoordinator';

import type { SignOutReason } from './session';
import type { RemoteSessionHandlers, RestoreResult } from './sessionTransport.types';

export type { RemoteSessionHandlers, RestoreResult } from './sessionTransport.types';

let installation = '';
let coordinator: TabCoordinator | null = null;
let pending: PendingLogout | null = null;

async function refreshByCookie(): Promise<Tokens | null> {
  try {
    return (await authApi.refresh(null, installation)).data;
  } catch (e) {
    if (isApiError(e) && e.status === 401) return null;
    throw e;
  }
}

function locks(): LockManagerLike | null {
  return typeof navigator !== 'undefined' && 'locks' in navigator ? (navigator.locks as unknown as LockManagerLike) : null;
}

function getCoordinator(): TabCoordinator {
  if (!coordinator) {
    const channel = typeof BroadcastChannel !== 'undefined' ? (new BroadcastChannel(SESSION_CHANNEL_NAME) as unknown as ChannelLike) : null;
    coordinator = createTabCoordinator({ locks: locks(), channel, doRefresh: refreshByCookie });
  }
  return coordinator;
}

function getPendingLogout(): PendingLogout {
  if (!pending) {
    const lockManager = locks();
    pending = createPendingLogout({
      storage: typeof localStorage !== 'undefined' ? (localStorage as KeyValueLike) : null,
      // Sem token e sem refresh no corpo: a rota é anônima e lê o cookie (06 §6.3 "App no navegador").
      logout: async () => {
        await authApi.logout(null, { anonymous: true });
      },
      withLock: lockManager ? (fn) => lockManager.request(REFRESH_LOCK_NAME, fn) : undefined,
    });
  }
  return pending;
}

/** 429, 5xx e falha de rede: o servidor não disse que a sessão acabou. Tenta de novo, nunca vai ao login. */
function retryable(e: unknown): RestoreResult | null {
  if (e instanceof NetworkUnknownError) return { kind: 'retry', retryAfterMs: null };
  if (isApiError(e) && (e.status === 429 || e.status >= 500)) {
    return { kind: 'retry', retryAfterMs: e.retryAfterSeconds !== null && e.retryAfterSeconds > 0 ? e.retryAfterSeconds * 1000 : null };
  }
  return null;
}

export const sessionTransport = {
  async restore(installationId: string, cachedMe: Me | null): Promise<RestoreResult> {
    installation = installationId;
    const logout = getPendingLogout();
    if (logout.has()) {
      // Alguém saiu e o servidor ainda não confirmou: o cookie pode ser do funcionário anterior. Não restaura;
      // a tela de entrada aparece já, e o logout segue tentando em segundo plano.
      void logout.drain();
      return { kind: 'signedOut' };
    }

    let tokens: Tokens | null;
    try {
      tokens = await getCoordinator().refresh(null);
    } catch (e) {
      return retryable(e) ?? { kind: 'signedOut' };
    }
    if (!tokens) return { kind: 'signedOut' };
    try {
      const me = (await authApi.me({ Authorization: `Bearer ${tokens.accessToken}` })).data;
      return { kind: 'restored', tokens, me };
    } catch (e) {
      if (cachedMe) return { kind: 'restored', tokens, me: cachedMe };
      // Na próxima tentativa o coordenador reaproveita este token (ainda válido), sem renovar de novo.
      return retryable(e) ?? { kind: 'signedOut' };
    }
  },

  async refresh(current: Tokens | null, installationId: string): Promise<Tokens | null> {
    installation = installationId;
    return getCoordinator().refresh(current?.accessToken ?? null);
  },

  /**
   * Na web, sair por QUALQUER motivo (inclusive `expired`) precisa apagar o cookie no servidor: só esquecer o
   * token em memória não basta, porque um F5 restauraria a sessão pelo cookie. Sem cookie, o logout só
   * responde `204`. Ordem: marca → outras abas → tela limpa → API (a tela anterior não fica visível esperando
   * a rede).
   */
  async endSession(_current: Tokens | null, _reason: SignOutReason, clearLocal: () => Promise<void>): Promise<void> {
    const logout = getPendingLogout();
    logout.mark();
    getCoordinator().announce({ type: 'signedOut' });
    await clearLocal();
    await logout.drain();
  },

  /** Um logout ainda no ar apagaria o cookie que este login vai receber: espera ele terminar. */
  async beforeSignIn(): Promise<void> {
    await getPendingLogout().beforeSignIn();
  },

  afterSignIn(tenantId: string): void {
    getPendingLogout().clear();
    getCoordinator().announce({ type: 'signedIn', tenantId });
  },

  subscribe(handlers: RemoteSessionHandlers): () => void {
    return getCoordinator().subscribe((m) => {
      if (m.type === 'tokens') handlers.onTokens(m.tokens);
      else if (m.type === 'signedOut') handlers.onSignedOut();
      // Entrou (ou trocou de estabelecimento) em outra aba: o cookie mudou; esta aba recomeça com a sessão nova.
      else if (m.type === 'signedIn') window.location.reload();
    });
  },
};

/** Testes: zera os singletons (o módulo é carregado uma vez por arquivo de teste). */
export function resetWebSessionTransportForTests(): void {
  pending?.stop();
  coordinator = null;
  pending = null;
  installation = '';
}
