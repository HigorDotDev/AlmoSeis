/**
 * Como a sessão é guardada, restaurada e renovada em cada plataforma (ADR-0027, ADR-0053 §3).
 * Celular (este arquivo): tokens no SecureStore, refresh token no corpo. Web: `sessionTransport.web.ts`.
 */
import { isApiError } from '@/api/errors';
import { authApi } from '@/api/endpoints/auth';
import type { Me, Tokens } from '@/api/types';

import type { SignOutReason } from './session';
import { secure } from './secure';

export interface RemoteSessionHandlers {
  /** Outra aba renovou: use o access token novo. */
  onTokens(tokens: Tokens): void;
  /** Outra aba saiu da conta. */
  onSignedOut(): void;
}

export const sessionTransport = {
  /** Na abertura do app: a sessão guardada, ou `null` para ir ao login. */
  async restore(_installationId: string, cachedMe: Me | null): Promise<{ tokens: Tokens; me: Me } | null> {
    const tokens = await secure.tokens();
    const valid = tokens !== null && new Date(tokens.refreshTokenExpiresAt).getTime() > Date.now() && cachedMe !== null;
    return valid ? { tokens, me: cachedMe } : null;
  },

  /** `null` quando não há o que renovar ou o servidor recusou (401). Erro de rede propaga. */
  async refresh(current: Tokens | null, installationId: string): Promise<Tokens | null> {
    if (!current?.refreshToken) return null;
    try {
      return (await authApi.refresh(current.refreshToken, installationId)).data;
    } catch (e) {
      if (isApiError(e) && e.status === 401) return null;
      throw e;
    }
  },

  /**
   * Ao sair. No celular só "Sair" revoga o refresh no servidor; nos outros motivos basta apagar o SecureStore
   * (é o que já acontecia antes da web).
   */
  async logout(current: Tokens | null, reason: SignOutReason): Promise<void> {
    if (reason === 'user' && current?.refreshToken) await authApi.logout(current.refreshToken);
  },

  /** Avisos para outras abas. No celular não há. */
  announceSignedIn(_tenantId: string): void {},
  announceSignedOut(): void {},
  subscribe(_handlers: RemoteSessionHandlers): () => void {
    return () => {};
  },
};
