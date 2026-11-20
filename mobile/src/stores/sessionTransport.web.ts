/**
 * Web (ADR-0053 §3): access token só em memória; refresh no cookie `HttpOnly` `__Secure-almoseis-rt`, que o
 * navegador manda sozinho para `/api/v1/auth`. Ao abrir ou recarregar a página, a sessão volta pelo
 * `POST /auth/refresh`. A renovação é coordenada entre abas (Web Locks + BroadcastChannel).
 */
import { isApiError } from '@/api/errors';
import { authApi } from '@/api/endpoints/auth';
import type { Me, Tokens } from '@/api/types';
import { createTabCoordinator, SESSION_CHANNEL_NAME, type ChannelLike, type LockManagerLike, type TabCoordinator } from '@/features/auth/tabCoordinator';

import type { SignOutReason } from './session';
import type { RemoteSessionHandlers } from './sessionTransport';

let installation = '';
let coordinator: TabCoordinator | null = null;

async function refreshByCookie(): Promise<Tokens | null> {
  try {
    return (await authApi.refresh(null, installation)).data;
  } catch (e) {
    if (isApiError(e) && e.status === 401) return null;
    throw e;
  }
}

function getCoordinator(): TabCoordinator {
  if (!coordinator) {
    const locks = typeof navigator !== 'undefined' && 'locks' in navigator ? (navigator.locks as unknown as LockManagerLike) : null;
    const channel = typeof BroadcastChannel !== 'undefined' ? (new BroadcastChannel(SESSION_CHANNEL_NAME) as unknown as ChannelLike) : null;
    coordinator = createTabCoordinator({ locks, channel, doRefresh: refreshByCookie });
  }
  return coordinator;
}

export const sessionTransport = {
  async restore(installationId: string, cachedMe: Me | null): Promise<{ tokens: Tokens; me: Me } | null> {
    installation = installationId;
    let tokens: Tokens | null;
    try {
      tokens = await getCoordinator().refresh(null);
    } catch {
      // Sem rede ao recarregar a página: não há sessão em memória (ADR-0053, "sem uso offline ao recarregar").
      return null;
    }
    if (!tokens) return null;
    try {
      const me = (await authApi.me({ Authorization: `Bearer ${tokens.accessToken}` })).data;
      return { tokens, me };
    } catch {
      return cachedMe ? { tokens, me: cachedMe } : null;
    }
  },

  async refresh(current: Tokens | null, installationId: string): Promise<Tokens | null> {
    installation = installationId;
    return getCoordinator().refresh(current?.accessToken ?? null);
  },

  /**
   * Na web, sair por QUALQUER motivo precisa apagar o cookie no servidor: só esquecer o token em memória não
   * basta, porque um F5 restauraria a sessão pelo cookie (ex.: inatividade no aparelho compartilhado).
   * `expired`: o servidor já recusou o refresh; chamar de novo só repetiria o 401.
   */
  async logout(_current: Tokens | null, reason: SignOutReason): Promise<void> {
    if (reason === 'expired') return;
    await authApi.logout(null);
  },

  announceSignedIn(tenantId: string): void {
    getCoordinator().announce({ type: 'signedIn', tenantId });
  },

  announceSignedOut(): void {
    getCoordinator().announce({ type: 'signedOut' });
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
