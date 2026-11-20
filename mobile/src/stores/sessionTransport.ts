/**
 * Como a sessão é guardada, restaurada e renovada em cada plataforma (ADR-0027, ADR-0053 §3).
 * Celular (este arquivo): tokens no SecureStore, refresh token no corpo. Web: `sessionTransport.web.ts`.
 */
import { isApiError } from '@/api/errors';
import { authApi } from '@/api/endpoints/auth';
import type { Me, Tokens } from '@/api/types';

import type { SignOutReason } from './session';
import { secure } from './secure';
import type { RemoteSessionHandlers, RestoreResult } from './sessionTransport.types';

export type { RemoteSessionHandlers, RestoreResult } from './sessionTransport.types';

export const sessionTransport = {
  /** Na abertura do app: a sessão guardada, ou `signedOut` para ir ao login. No celular nunca é `retry`. */
  async restore(_installationId: string, cachedMe: Me | null): Promise<RestoreResult> {
    const tokens = await secure.tokens();
    const valid = tokens !== null && new Date(tokens.refreshTokenExpiresAt).getTime() > Date.now() && cachedMe !== null;
    return valid ? { kind: 'restored', tokens, me: cachedMe } : { kind: 'signedOut' };
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
   * Sair. No celular só "Sair" revoga o refresh no servidor (antes de limpar, como sempre foi); nos outros
   * motivos basta apagar o SecureStore. Sem rede, o refresh expira sozinho no servidor.
   */
  async endSession(current: Tokens | null, reason: SignOutReason, clearLocal: () => Promise<void>): Promise<void> {
    if (reason === 'user' && current?.refreshToken) {
      try {
        await authApi.logout(current.refreshToken);
      } catch {
        // Sem rede: o refresh expira sozinho no servidor.
      }
    }
    await clearLocal();
  },

  /** Antes de login, cadastro, convite ou PIN. No celular não há o que esperar. */
  async beforeSignIn(): Promise<void> {},

  /** Depois de entrar. Web: limpa o logout pendente e avisa as outras abas. No celular não faz nada. */
  afterSignIn(_tenantId: string): void {},

  subscribe(_handlers: RemoteSessionHandlers): () => void {
    return () => {};
  },
};
