/**
 * Sessão (08 §8.7). Tokens em memória e no SecureStore; `me` também em cache local para abrir offline.
 * Renovação: `refresh()` é chamado só pelo single-flight do cliente (`refreshSingleFlight`).
 * Onde e como os tokens vivem em cada plataforma fica no `sessionTransport` (web: cookie + memória, ADR-0053).
 */
import { create } from 'zustand';

import { sharedDeviceAuth } from '@/api/endpoints/sharedDeviceAuth';
import type { Me, Permission, Session, Tokens } from '@/api/types';
import { metaRepo } from '@/db/metaRepo';

import { secure, type SharedDeviceCredentials } from './secure';
import { sessionTransport } from './sessionTransport';

export type SessionStatus = 'loading' | 'signedOut' | 'signedIn';

export type SignOutReason = 'user' | 'expired' | 'revoked' | 'idle' | 'deviceRegistered' | 'deviceRevoked';

interface SessionState {
  status: SessionStatus;
  tokens: Tokens | null;
  me: Me | null;
  installationId: string | null;
  sharedDevice: SharedDeviceCredentials | null;
  /** 402 ou `canWrite = false`: escritas desabilitadas (ADR-0017). */
  readOnly: boolean;
  upgradeRequired: { minVersion?: string; storeUrl?: string } | null;
  signOutReason: SignOutReason | null;
  /** Web: abrindo a página sem resposta do servidor (rede, 5xx, 429); tentando de novo, sem ir ao login. */
  reconnecting: boolean;

  hydrate(options?: HydrateOptions): Promise<void>;
  /** Antes de pedir login/cadastro/convite/PIN. Web: espera um logout ainda no ar (ele apagaria o cookie novo). */
  prepareSignIn(): Promise<void>;
  signIn(session: Session): Promise<void>;
  setMe(me: Me): void;
  refresh(): Promise<boolean>;
  signOut(reason: SignOutReason): Promise<void>;
  /** Web: outra aba saiu da conta; limpa esta sem chamar a API de novo. */
  applyRemoteSignOut(): Promise<void>;
  /** Web: outra aba renovou o token. */
  applyRemoteTokens(tokens: Tokens): void;
  markReadOnly(): void;
  setUpgradeRequired(info: { minVersion?: string; storeUrl?: string }): void;
  setSharedDevice(device: SharedDeviceCredentials | null): Promise<void>;
}

export interface HydrateOptions {
  /** Testes. */
  sleep?: (ms: number) => Promise<void>;
}

/** Espera entre tentativas de restaurar quando o servidor não manda `Retry-After`. */
export const RESTORE_RETRY_DELAYS_MS = [2_000, 4_000, 8_000, 15_000, 30_000];

const defaultSleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

/** Limpa caches do tenant (registrado pelo _layout raiz, que conhece o QueryClient). */
let onTenantDataCleared: () => void = () => {};
export function registerTenantDataClearer(fn: () => void): void {
  onTenantDataCleared = fn;
}

const ME_KEY = 'me';

export const useSession = create<SessionState>((set, get) => ({
  status: 'loading',
  tokens: null,
  me: null,
  installationId: null,
  sharedDevice: null,
  readOnly: false,
  upgradeRequired: null,
  signOutReason: null,
  reconnecting: false,

  async hydrate(options = {}) {
    const sleep = options.sleep ?? defaultSleep;
    const [installationId, sharedDevice, meJson] = await Promise.all([secure.installationId(), secure.sharedDevice(), metaRepo.get(ME_KEY)]);
    const cachedMe = meJson ? (JSON.parse(meJson) as Me) : null;
    let result = await sessionTransport.restore(installationId, cachedMe);
    // Web: 429/5xx/rede ao recarregar não é "sem sessão" (ADR-0041). Fica em "Reconectando…" até o servidor responder.
    for (let attempt = 0; result.kind === 'retry'; attempt++) {
      set({ reconnecting: true });
      await sleep(result.retryAfterMs ?? RESTORE_RETRY_DELAYS_MS[Math.min(attempt, RESTORE_RETRY_DELAYS_MS.length - 1)]!);
      result = await sessionTransport.restore(installationId, cachedMe);
    }
    const restored = result.kind === 'restored' ? result : null;
    if (restored) {
      await secure.setTokens(restored.tokens);
      if (restored.me !== cachedMe) await metaRepo.set(ME_KEY, JSON.stringify(restored.me));
    }
    set({
      installationId,
      sharedDevice,
      tokens: restored?.tokens ?? null,
      me: restored?.me ?? null,
      readOnly: restored ? !restored.me.subscription.canWrite : false,
      status: restored ? 'signedIn' : 'signedOut',
      reconnecting: false,
    });
  },

  prepareSignIn: () => sessionTransport.beforeSignIn(),

  async signIn(session) {
    const previousTenant = get().me?.tenant.id ?? (await secure.lastTenantId());
    if (previousTenant && previousTenant !== session.me.tenant.id) {
      onTenantDataCleared();
    }
    await secure.setTokens(session.tokens);
    await secure.setLastTenantId(session.me.tenant.id);
    await metaRepo.set(ME_KEY, JSON.stringify(session.me));
    set({
      tokens: session.tokens,
      me: session.me,
      status: 'signedIn',
      readOnly: !session.me.subscription.canWrite,
      signOutReason: null,
    });
    sessionTransport.afterSignIn(session.me.tenant.id);
  },

  setMe(me) {
    void metaRepo.set(ME_KEY, JSON.stringify(me));
    set({ me, readOnly: !me.subscription.canWrite });
  },

  async refresh() {
    const { tokens, installationId } = get();
    if (!tokens || !installationId) return false;
    // 401 → null; rede → lança: a sessão continua e quem chamou trata como falha de rede.
    const data = await sessionTransport.refresh(tokens, installationId);
    if (!data) return false;
    await secure.setTokens(data);
    set({ tokens: data });
    return true;
  },

  async signOut(reason) {
    const { tokens } = get();
    // A ordem entre limpar a tela e chamar a API é da plataforma: na web a marca de logout pendente vem
    // primeiro e a tela limpa antes da rede (ADR-0053 §3.5); no celular, como sempre foi.
    await sessionTransport.endSession(tokens, reason, () => clearLocalSession(reason));
  },

  async applyRemoteSignOut() {
    if (get().status !== 'signedIn') return;
    await clearLocalSession('user');
  },

  applyRemoteTokens(tokens) {
    if (get().status !== 'signedIn') return;
    void secure.setTokens(tokens);
    set({ tokens });
  },

  markReadOnly() {
    set({ readOnly: true });
  },

  setUpgradeRequired(info) {
    set({ upgradeRequired: info });
  },

  async setSharedDevice(device) {
    // Web: apaga o cookie do segredo (`DELETE /auth/shared-device`). No celular não faz nada.
    if (!device && get().sharedDevice) await sharedDeviceAuth.forget().catch(() => {});
    await secure.setSharedDevice(device);
    set({ sharedDevice: device });
  },
}));

async function clearLocalSession(reason: SignOutReason): Promise<void> {
  await secure.setTokens(null);
  await metaRepo.remove(ME_KEY);
  onTenantDataCleared();
  useSession.setState({ tokens: null, me: null, status: 'signedOut', readOnly: false, signOutReason: reason });
}

// Web: renovação e saída feitas em outra aba chegam aqui (no celular, `subscribe` não faz nada).
sessionTransport.subscribe({
  onTokens: (tokens) => useSession.getState().applyRemoteTokens(tokens),
  onSignedOut: () => void useSession.getState().applyRemoteSignOut(),
});

export function hasPermission(me: Me | null, permission: Permission): boolean {
  return me?.permissions.includes(permission) ?? false;
}

export function usePermission(permission: Permission): boolean {
  return useSession((s) => hasPermission(s.me, permission));
}

/** Escrita possível agora: com a permissão e fora do modo somente leitura. */
export function useCanWrite(permission?: Permission): boolean {
  return useSession((s) => !s.readOnly && (permission === undefined || hasPermission(s.me, permission)));
}

export function useMe(): Me {
  const me = useSession((s) => s.me);
  if (!me) {
    throw new Error('useMe fora de uma sessão.');
  }
  return me;
}
