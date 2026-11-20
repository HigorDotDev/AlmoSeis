/**
 * Coordenação da sessão entre abas do navegador (ADR-0053 §3.5–3.6). Usada só pela versão web do transporte
 * de sessão, mas sem dependência do DOM para poder ser testada: recebe o lock e o canal por injeção.
 *
 * - Uma renovação por vez entre TODAS as abas (Web Locks). Duas abas apresentando o mesmo refresh ao mesmo
 *   tempo fariam o servidor revogar a família inteira (ADR-0027).
 * - Quem obtém o lock e vê que outra aba acabou de renovar (aviso pelo canal) reaproveita o token novo.
 * - Logout e entrada em outra aba também chegam pelo canal.
 */
import type { Tokens } from '@/api/types';

export const REFRESH_LOCK_NAME = 'almoseis.auth.refresh';
export const SESSION_CHANNEL_NAME = 'almoseis.session';
/** Access token que vence em menos que isso não é reaproveitado. */
const MIN_REMAINING_MS = 30_000;

export interface LockManagerLike {
  request<T>(name: string, callback: () => Promise<T>): Promise<T>;
}

export interface ChannelLike {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (event: { data: unknown }) => void): void;
  removeEventListener(type: 'message', listener: (event: { data: unknown }) => void): void;
}

export type TabMessage = { type: 'tokens'; tokens: Tokens } | { type: 'signedOut' } | { type: 'signedIn'; tenantId: string };

export interface TabCoordinatorDeps {
  /** `navigator.locks`; sem ele (navegador antigo), só o single-flight da própria aba. */
  locks: LockManagerLike | null;
  /** `BroadcastChannel`; sem ele, cada aba por si. */
  channel: ChannelLike | null;
  /** O `POST /auth/refresh` de verdade. `null` = recusado (401). Erro de rede propaga. */
  doRefresh(): Promise<Tokens | null>;
  now?: () => number;
}

export interface TabCoordinator {
  /** `staleAccessToken`: o token que esta aba tem agora (null ao abrir a página). */
  refresh(staleAccessToken: string | null): Promise<Tokens | null>;
  announce(message: Exclude<TabMessage, { type: 'tokens' }>): void;
  subscribe(listener: (message: TabMessage) => void): () => void;
}

export function createTabCoordinator({ locks, channel, doRefresh, now = Date.now }: TabCoordinatorDeps): TabCoordinator {
  let latest: Tokens | null = null;
  let inFlight: Promise<Tokens | null> | null = null;
  const listeners = new Set<(message: TabMessage) => void>();

  channel?.addEventListener('message', (event) => {
    const message = event.data as TabMessage;
    if (!message || typeof message !== 'object' || !('type' in message)) return;
    if (message.type === 'tokens') latest = message.tokens;
    if (message.type === 'signedOut') latest = null;
    for (const l of listeners) l(message);
  });

  const usable = (t: Tokens | null, stale: string | null): t is Tokens =>
    t !== null && t.accessToken !== stale && new Date(t.accessTokenExpiresAt).getTime() - now() > MIN_REMAINING_MS;

  const underLock = async (stale: string | null): Promise<Tokens | null> => {
    // Outra aba renovou enquanto esperávamos o lock: o cookie já rodou, o token novo chegou pelo canal.
    if (usable(latest, stale)) return latest;
    const tokens = await doRefresh();
    latest = tokens;
    if (tokens) channel?.postMessage({ type: 'tokens', tokens } satisfies TabMessage);
    return tokens;
  };

  return {
    refresh(stale) {
      if (!inFlight) {
        const run = () => underLock(stale);
        inFlight = (locks ? locks.request(REFRESH_LOCK_NAME, run) : run()).finally(() => {
          inFlight = null;
        });
      }
      return inFlight;
    },
    announce(message) {
      if (message.type === 'signedOut') latest = null;
      channel?.postMessage(message);
    },
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
  };
}
