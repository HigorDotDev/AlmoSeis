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

  hydrate(): Promise<void>;
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

  async hydrate() {
    const [installationId, sharedDevice, meJson] = await Promise.all([secure.installationId(), secure.sharedDevice(), metaRepo.get(ME_KEY)]);
    const cachedMe = meJson ? (JSON.parse(meJson) as Me) : null;
    const restored = await sessionTransport.restore(installationId, cachedMe);
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
    });
  },

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
    sessionTransport.announceSignedIn(session.me.tenant.id);
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
    if (tokens) {
      try {
        await sessionTransport.logout(tokens, reason);
      } catch {
        // Sem rede: o refresh expira sozinho no servidor.
      }
    }
    // Web: as outras abas saem também (ADR-0053 §3.5). No celular não faz nada.
    if (reason !== 'expired') sessionTransport.announceSignedOut();
    await clearLocalSession(reason);
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
