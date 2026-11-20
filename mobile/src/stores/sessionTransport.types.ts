/** Tipos de `sessionTransport.ts`/`sessionTransport.web.ts` (08 §8.15). */
import type { Me, Tokens } from '@/api/types';

export interface RemoteSessionHandlers {
  /** Outra aba renovou: use o access token novo. */
  onTokens(tokens: Tokens): void;
  /** Outra aba saiu da conta. */
  onSignedOut(): void;
}

/**
 * Resultado da restauração na abertura do app.
 * `retry`: o servidor não respondeu (rede, timeout, 5xx, 429). Não é "sem sessão": tenta de novo depois de
 * `retryAfterMs` (do `Retry-After`, ADR-0041) ou da espera padrão, e nunca manda para o login.
 */
export type RestoreResult =
  | { kind: 'restored'; tokens: Tokens; me: Me }
  | { kind: 'signedOut' }
  | { kind: 'retry'; retryAfterMs: number | null };
