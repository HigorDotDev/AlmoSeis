/**
 * Cliente da API (08 §8.3). Um `fetch` encapsulado: headers comuns, timeout de 15 s, refresh single-flight no 401,
 * 426 → tela de atualização, 402 → somente leitura, ProblemDetails → ApiError, ETag → `version`.
 */
import { API_BASE_URL, APP_PLATFORM, APP_VERSION, REQUEST_TIMEOUT_MS } from '@/config';

import { ApiError, NetworkUnknownError, type ProblemDetails } from './errors';

export type HttpMethod = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';

export interface RequestOptions {
  body?: unknown;
  idempotencyKey?: string;
  /** Versão do recurso em cache; vai como `If-Match: "{n}"` (ADR-0034). */
  ifMatch?: number;
  signal?: AbortSignal;
  /** Rotas públicas (login, cadastro, convite, PIN) não mandam token nem tentam refresh. */
  anonymous?: boolean;
  headers?: Record<string, string>;
  timeoutMs?: number;
}

export interface ApiResult<T> {
  data: T;
  status: number;
  /** Do `ETag`: a versão nova do recurso (ou do pedido, nas rotas de pagamento). */
  version: number | null;
  replayed: boolean;
}

export interface ApiClientHooks {
  getAccessToken(): string | null;
  /** Renova os tokens. Devolve false quando não há sessão a renovar ou o refresh foi recusado. */
  refresh(): Promise<boolean>;
  onSessionExpired(): void;
  onUpgradeRequired(info: { minVersion?: string; storeUrl?: string }): void;
  onSubscriptionInactive(): void;
  onNetworkFailure?(): void;
}

let hooks: ApiClientHooks = {
  getAccessToken: () => null,
  refresh: async () => false,
  onSessionExpired: () => {},
  onUpgradeRequired: () => {},
  onSubscriptionInactive: () => {},
};

export function configureApiClient(next: ApiClientHooks): void {
  hooks = next;
}

let refreshing: Promise<boolean> | null = null;

/** Uma renovação por vez; quem chegar durante ela espera o mesmo resultado (08 §8.3 passo 3). */
export function refreshSingleFlight(): Promise<boolean> {
  if (!refreshing) {
    refreshing = hooks.refresh().finally(() => {
      refreshing = null;
    });
  }
  return refreshing;
}

export function apiUrl(path: string): string {
  return `${API_BASE_URL}/api/v1${path}`;
}

export async function apiRequest<T>(method: HttpMethod, path: string, options: RequestOptions = {}): Promise<ApiResult<T>> {
  const first = await send(method, path, options);
  if (first.status === 401 && !options.anonymous) {
    const problem = await readProblem(first);
    if (problem.errorCode === 'Unauthenticated' || problem.errorCode === undefined) {
      if (await refreshSingleFlight()) {
        return handle<T>(await send(method, path, options), options);
      }
      hooks.onSessionExpired();
    }
    throw new ApiError(401, problem);
  }
  return handle<T>(first, options);
}

async function send(method: HttpMethod, path: string, options: RequestOptions): Promise<Response> {
  const headers: Record<string, string> = {
    Accept: 'application/json',
    'X-App-Version': APP_VERSION,
    'X-App-Platform': APP_PLATFORM,
    ...options.headers,
  };
  if (options.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }
  const token = options.anonymous ? null : hooks.getAccessToken();
  if (token) {
    headers.Authorization = `Bearer ${token}`;
  }
  if (options.idempotencyKey) {
    headers['Idempotency-Key'] = options.idempotencyKey;
  }
  if (options.ifMatch !== undefined) {
    headers['If-Match'] = `"${options.ifMatch}"`;
  }

  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), options.timeoutMs ?? REQUEST_TIMEOUT_MS);
  const abort = () => controller.abort();
  options.signal?.addEventListener('abort', abort);
  try {
    return await fetch(apiUrl(path), {
      method,
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      signal: controller.signal,
    });
  } catch (e) {
    if (options.signal?.aborted) {
      throw e;
    }
    // Timeout ou queda: a requisição pode ter chegado. Resultado desconhecido (08 §8.5).
    hooks.onNetworkFailure?.();
    throw new NetworkUnknownError(undefined, e);
  } finally {
    clearTimeout(timer);
    options.signal?.removeEventListener('abort', abort);
  }
}

async function handle<T>(response: Response, options: RequestOptions): Promise<ApiResult<T>> {
  if (response.ok) {
    const text = await response.text();
    return {
      data: (text ? JSON.parse(text) : undefined) as T,
      status: response.status,
      version: parseETag(response.headers.get('ETag')),
      replayed: response.headers.get('Idempotent-Replayed') === 'true',
    };
  }

  const problem = await readProblem(response);
  if (response.status === 426) {
    hooks.onUpgradeRequired({
      minVersion: typeof problem.minVersion === 'string' ? problem.minVersion : undefined,
      storeUrl: typeof problem.storeUrl === 'string' ? problem.storeUrl : undefined,
    });
  } else if (response.status === 402) {
    hooks.onSubscriptionInactive();
  } else if (response.status === 401 && !options.anonymous) {
    hooks.onSessionExpired();
  }
  const retryAfter = response.headers.get('Retry-After');
  throw new ApiError(response.status, problem, retryAfter ? Number(retryAfter) : null);
}

async function readProblem(response: Response): Promise<ProblemDetails> {
  if ((response as Response & { _problem?: ProblemDetails })._problem) {
    return (response as Response & { _problem: ProblemDetails })._problem;
  }
  let problem: ProblemDetails = { status: response.status };
  try {
    const text = await response.text();
    if (text) {
      problem = JSON.parse(text) as ProblemDetails;
    }
  } catch {
    // Corpo vazio ou não-JSON (proxy, 502): fica só o status.
  }
  (response as Response & { _problem?: ProblemDetails })._problem = problem;
  return problem;
}

export function parseETag(etag: string | null): number | null {
  if (!etag) return null;
  const match = /^(?:W\/)?"?(\d+)"?$/.exec(etag.trim());
  return match ? Number(match[1]) : null;
}

/** Query string sem os campos vazios. */
export function qs(params: object): string {
  const entries = Object.entries(params).filter(([, v]) => v !== undefined && v !== null && v !== '');
  if (entries.length === 0) return '';
  return `?${entries.map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`).join('&')}`;
}
