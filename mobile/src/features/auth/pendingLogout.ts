/**
 * Web (ADR-0053 §3.5, 08 §8.15): "logout pendente". O refresh fica num cookie `HttpOnly` que o JavaScript não
 * apaga; só o `POST /auth/logout` apaga. Se ele falhar (rede, timeout, 5xx, 429), um F5 restauraria a sessão
 * de quem saiu, o que no aparelho compartilhado é a sessão do funcionário anterior.
 *
 * Por isso, ao sair, a marca vai para o `localStorage` ANTES de qualquer outra coisa, e só sai quando o servidor
 * responder `204` (ou `401`, que também quer dizer "sem sessão"). Enquanto ela existir, nenhuma aba restaura a
 * sessão pelo cookie (o `localStorage` é o mesmo para todas as abas da origem).
 *
 * Sem dependência do DOM, para ser testável: recebe o armazenamento, o lock e a chamada por injeção.
 */
import { isApiError } from '@/api/errors';

export const PENDING_LOGOUT_KEY = 'almoseis.pendingLogout';

/** Espera entre as tentativas em segundo plano (a última se repete). */
export const LOGOUT_RETRY_DELAYS_MS = [2_000, 5_000, 15_000, 30_000, 60_000];

export interface KeyValueLike {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

export interface PendingLogoutDeps {
  /** `localStorage`. */
  storage: KeyValueLike | null;
  /** `POST /auth/logout` sem token (a rota é anônima). Resolve no `204`; lança no resto. */
  logout(): Promise<void>;
  /**
   * O mesmo Web Lock da renovação: um logout nunca corre junto com um refresh ou com uma entrada em outra aba
   * (a resposta do logout apaga o cookie; se chegasse depois da de um login, apagaria o cookie novo).
   */
  withLock?<T>(fn: () => Promise<T>): Promise<T>;
  retryDelaysMs?: number[];
  setTimer?(fn: () => void, ms: number): unknown;
  clearTimer?(handle: unknown): void;
}

export interface PendingLogout {
  has(): boolean;
  /** Grava a marca. Síncrono: vem antes de limpar a tela e de chamar a API. */
  mark(): void;
  /** Tenta agora; falhando, segue tentando em segundo plano. `true` = o servidor apagou a sessão. */
  drain(): Promise<boolean>;
  /** Antes de um login: espera o logout em andamento (nesta aba ou em outra) e para as tentativas agendadas. */
  beforeSignIn(): Promise<void>;
  /** Entrou: o cookie é de outra sessão; a marca não vale mais. */
  clear(): void;
  /** Para as tentativas agendadas sem apagar a marca (testes). */
  stop(): void;
}

export function createPendingLogout({
  storage,
  logout,
  withLock = (fn) => fn(),
  retryDelaysMs = LOGOUT_RETRY_DELAYS_MS,
  setTimer = (fn, ms) => setTimeout(fn, ms),
  clearTimer = (h) => clearTimeout(h as ReturnType<typeof setTimeout>),
}: PendingLogoutDeps): PendingLogout {
  let inFlight: Promise<boolean> | null = null;
  let timer: unknown = null;
  let attempt = 0;

  const has = () => storage?.getItem(PENDING_LOGOUT_KEY) != null;

  const cancelRetry = () => {
    if (timer !== null) clearTimer(timer);
    timer = null;
  };

  const clear = () => {
    storage?.removeItem(PENDING_LOGOUT_KEY);
    cancelRetry();
    attempt = 0;
  };

  const attemptOnce = (): Promise<boolean> => {
    if (!inFlight) {
      inFlight = withLock(async () => {
        // Outra aba concluiu enquanto esperávamos o lock.
        if (!has()) return true;
        try {
          await logout();
          return true;
        } catch (e) {
          return isApiError(e) && e.status === 401;
        }
      })
        .catch(() => false)
        .then((ok) => {
          if (ok) clear();
          return ok;
        })
        .finally(() => {
          inFlight = null;
        });
    }
    return inFlight;
  };

  const schedule = () => {
    cancelRetry();
    const delay = retryDelaysMs[Math.min(attempt, retryDelaysMs.length - 1)] ?? 60_000;
    attempt++;
    timer = setTimer(() => {
      timer = null;
      void drain();
    }, delay);
  };

  async function drain(): Promise<boolean> {
    cancelRetry();
    if (!has()) return true;
    const ok = await attemptOnce();
    if (!ok && has()) schedule();
    return ok;
  }

  return {
    has,
    mark() {
      storage?.setItem(PENDING_LOGOUT_KEY, String(Date.now()));
    },
    drain,
    async beforeSignIn() {
      cancelRetry();
      if (inFlight) await inFlight;
      else if (has()) await attemptOnce();
      else await withLock(async () => {});
    },
    clear,
    stop: cancelRetry,
  };
}
