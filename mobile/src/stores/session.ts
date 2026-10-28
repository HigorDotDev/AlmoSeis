/**
 * Sessão (08 §8.7). Tokens em memória e no SecureStore; `me` também em cache local para abrir offline.
 * Renovação: `refresh()` é chamado só pelo single-flight do cliente (`refreshSingleFlight`).
 */
import { create } from 'zustand';

import { isApiError } from '@/api/errors';
import { authApi } from '@/api/endpoints/auth';
import type { Me, Permission, Session, Tokens } from '@/api/types';
import { metaRepo } from '@/db/metaRepo';

import { secure, type SharedDeviceCredentials } from './secure';

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
    const [installationId, tokens, sharedDevice, meJson] = await Promise.all([
      secure.installationId(),
      secure.tokens(),
      secure.sharedDevice(),
      metaRepo.get(ME_KEY),
    ]);
    const me = meJson ? (JSON.parse(meJson) as Me) : null;
    const valid = tokens !== null && new Date(tokens.refreshTokenExpiresAt).getTime() > Date.now() && me !== null;
    set({
      installationId,
      sharedDevice,
      tokens: valid ? tokens : null,
      me: valid ? me : null,
      readOnly: valid ? !me!.subscription.canWrite : false,
      status: valid ? 'signedIn' : 'signedOut',
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
  },

  setMe(me) {
    void metaRepo.set(ME_KEY, JSON.stringify(me));
    set({ me, readOnly: !me.subscription.canWrite });
  },

  async refresh() {
    const { tokens, installationId } = get();
    if (!tokens || !installationId) return false;
    try {
      const { data } = await authApi.refresh(tokens.refreshToken, installationId);
      await secure.setTokens(data);
      set({ tokens: data });
      return true;
    } catch (e) {
      if (isApiError(e) && e.status === 401) {
        return false;
      }
      // Rede: a sessão continua; quem chamou trata como falha de rede.
      throw e;
    }
  },

  async signOut(reason) {
    const { tokens } = get();
    if (tokens && reason === 'user') {
      try {
        await authApi.logout(tokens.refreshToken);
      } catch {
        // Sem rede: o refresh expira sozinho no servidor.
      }
    }
    await secure.setTokens(null);
    await metaRepo.remove(ME_KEY);
    onTenantDataCleared();
    set({ tokens: null, me: null, status: 'signedOut', readOnly: false, signOutReason: reason });
  },

  markReadOnly() {
    set({ readOnly: true });
  },

  setUpgradeRequired(info) {
    set({ upgradeRequired: info });
  },

  async setSharedDevice(device) {
    await secure.setSharedDevice(device);
    set({ sharedDevice: device });
  },
}));

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
