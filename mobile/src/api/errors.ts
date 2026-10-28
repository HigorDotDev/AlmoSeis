/** ProblemDetails da API (06 §6.1) e erros de rede (08 §8.3/8.5). */
import { formatBRL } from '@/lib/money';
import { errorMessages, GENERIC_ERROR, NETWORK_ERROR } from '@/lib/strings';

export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  errorCode?: string;
  traceId?: string;
  errors?: Record<string, string[]>;
  [extension: string]: unknown;
}

export class ApiError extends Error {
  readonly status: number;
  readonly errorCode: string;
  readonly title: string;
  readonly detail: string | null;
  readonly errors: Record<string, string[]>;
  readonly traceId: string | null;
  /** Campos fora do padrão RFC 7807: `current`, `sources`, `lockedUntil`, `attemptsLeft`, `balance`… */
  readonly extensions: Record<string, unknown>;
  readonly retryAfterSeconds: number | null;

  constructor(status: number, problem: ProblemDetails, retryAfterSeconds: number | null = null) {
    super(problem.title ?? `HTTP ${status}`);
    this.name = 'ApiError';
    const { type: _t, title, status: _s, detail, errorCode, traceId, errors, instance: _i, ...extensions } = problem;
    this.status = status;
    this.errorCode = errorCode ?? `Http${status}`;
    this.title = title ?? GENERIC_ERROR;
    this.detail = detail ?? null;
    this.errors = errors ?? {};
    this.traceId = traceId ?? null;
    this.extensions = extensions;
    this.retryAfterSeconds = retryAfterSeconds;
  }

  is(code: string): boolean {
    return this.errorCode === code;
  }

  /** Mensagem de um campo no formato do servidor (`items[0].quantity`). */
  fieldError(field: string): string | undefined {
    const key = Object.keys(this.errors).find((k) => k.toLowerCase() === field.toLowerCase());
    return key ? this.errors[key]?.[0] : undefined;
  }
}

/**
 * Timeout, queda de rede ou resposta 5xx de uma escrita: o resultado é **desconhecido** (ADR-0010, 08 §8.5).
 * Não é tratado como falha; a escrita é verificada por `GET /operations/{key}` antes de qualquer reenvio.
 */
export class NetworkUnknownError extends Error {
  readonly cause?: unknown;

  constructor(message = NETWORK_ERROR, cause?: unknown) {
    super(message);
    this.name = 'NetworkUnknownError';
    this.cause = cause;
  }
}

export function isApiError(e: unknown, code?: string): e is ApiError {
  return e instanceof ApiError && (code === undefined || e.errorCode === code);
}

/** 08 §8.11: mensagem de tela para um erro. */
export function errorMessage(e: unknown): string {
  if (e instanceof NetworkUnknownError) {
    return NETWORK_ERROR;
  }
  if (!(e instanceof ApiError)) {
    return GENERIC_ERROR;
  }
  switch (e.errorCode) {
    case 'PaymentExceedsBalance': {
      const balance = e.extensions.balance;
      return typeof balance === 'string' ? `O valor passa do saldo (${formatBRL(balance)}).` : 'O valor passa do saldo.';
    }
    case 'ProductUnavailable':
      return e.detail ?? 'Um produto não está disponível hoje.';
    case 'TooManyRequests':
      return `Muitas tentativas. Aguarde ${e.retryAfterSeconds ?? 60} s.`;
    case 'ValidationFailed': {
      const first = Object.values(e.errors)[0]?.[0];
      return first ?? errorMessages.ValidationFailed!;
    }
    case 'InvalidCredentials': {
      const lockedUntil = e.extensions.lockedUntil;
      if (typeof lockedUntil === 'string') {
        const d = new Date(lockedUntil);
        return `Muitas tentativas. Tente novamente às ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}.`;
      }
      const attemptsLeft = e.extensions.attemptsLeft;
      if (typeof attemptsLeft === 'number') {
        return `PIN incorreto. ${attemptsLeft === 1 ? 'Resta 1 tentativa' : `Restam ${attemptsLeft} tentativas`}.`;
      }
      return errorMessages.InvalidCredentials!;
    }
    case 'PinLocked': {
      const lockedUntil = e.extensions.lockedUntil;
      if (typeof lockedUntil === 'string') {
        const d = new Date(lockedUntil);
        return `PIN bloqueado até ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}.`;
      }
      return 'PIN bloqueado.';
    }
  }
  const known = errorMessages[e.errorCode];
  if (known) return known;
  if (e.status >= 500 || !e.detail) {
    return `${GENERIC_ERROR} Código: ${shortTrace(e.traceId)}.`;
  }
  return e.detail;
}

function shortTrace(traceId: string | null): string {
  if (!traceId) return '—';
  const parts = traceId.split('-');
  return (parts[1] ?? traceId).slice(0, 8);
}
