import type {
  DeliveryAddressInput,
  DeliveryQuote,
  PlacedWebOrder,
  PlaceWebOrderRequest,
  ProblemDetails,
  PublicStore,
  WebOrderTracking,
} from './types';

/**
 * Cliente das rotas públicas (06 §6.19). Sem dependência de Next: recebe `fetch` e a URL base, o que permite usar
 * o mesmo código no servidor da página (com `X-Web-Renderer-Key`) e no navegador (direto na API, ADR-0051 item 2).
 */

export type ApiFailure =
  /** Resposta de erro da API com Problem Details. */
  | { kind: 'problem'; status: number; problem: ProblemDetails; retryAfterSeconds: number | null }
  /** Slug antigo: a API respondeu 301/308 para o slug atual (só com `redirect: 'manual'`, no servidor). */
  | { kind: 'moved'; status: number; slug: string }
  /** Sem resposta (rede caiu, CORS, tempo esgotado). O resultado do envio é desconhecido. */
  | { kind: 'network'; message: string };

export type ApiResult<T> =
  | { ok: true; status: number; data: T; headers: Headers }
  | ({ ok: false } & ApiFailure);

export type TrackingResult =
  | { ok: true; status: 200; data: WebOrderTracking; etag: string | null }
  | { ok: true; status: 304; notModified: true; etag: string | null }
  | ({ ok: false } & ApiFailure);

export interface PublicApiOptions {
  baseUrl: string;
  fetch?: typeof fetch;
  /** Headers fixos (ex.: `X-Web-Renderer-Key` no servidor). */
  headers?: Record<string, string>;
  /** `manual` no servidor para detectar o slug antigo; `follow` no navegador. */
  redirect?: RequestRedirect;
  /** Opções extras do `fetch` (ex.: `cache: 'no-store'`). */
  init?: RequestInit;
  timeoutMs?: number;
}

export const API_PREFIX = '/api/v1/public';

export function parseRetryAfter(value: string | null, now: number = Date.now()): number | null {
  if (!value) return null;
  const trimmed = value.trim();
  if (/^\d+$/.test(trimmed)) return Number(trimmed);
  const date = Date.parse(trimmed);
  if (Number.isNaN(date)) return null;
  return Math.max(0, Math.ceil((date - now) / 1000));
}

/** `Location: /api/v1/public/stores/{slug}[/resto]` → `{slug}`. */
export function slugFromLocation(location: string): string | null {
  const match = /\/api\/v1\/public\/stores\/([^/?#]+)/i.exec(location);
  return match?.[1] ? decodeURIComponent(match[1]) : null;
}

export function createPublicApi(options: PublicApiOptions) {
  const baseUrl = options.baseUrl.replace(/\/+$/, '');
  const doFetch = options.fetch ?? fetch;
  const timeoutMs = options.timeoutMs ?? 15000;

  async function request(path: string, init: RequestInit): Promise<Response | ApiFailure> {
    const controller = typeof AbortController !== 'undefined' ? new AbortController() : null;
    const timer = controller ? setTimeout(() => controller.abort(), timeoutMs) : null;
    try {
      return await doFetch(`${baseUrl}${API_PREFIX}${path}`, {
        ...options.init,
        ...init,
        redirect: options.redirect ?? 'follow',
        signal: controller?.signal,
        headers: { Accept: 'application/json', ...options.headers, ...(init.headers as Record<string, string>) },
      });
    } catch (err) {
      return { kind: 'network', message: err instanceof Error ? err.message : String(err) };
    } finally {
      if (timer) clearTimeout(timer);
    }
  }

  async function failureFrom(response: Response): Promise<ApiFailure> {
    if (response.status === 301 || response.status === 308) {
      const slug = slugFromLocation(response.headers.get('Location') ?? '');
      if (slug) return { kind: 'moved', status: response.status, slug };
    }
    let problem: ProblemDetails = { status: response.status };
    try {
      const text = await response.text();
      if (text) problem = { ...JSON.parse(text), status: response.status } as ProblemDetails;
    } catch {
      // corpo vazio ou fora do formato: fica só o status
    }
    return {
      kind: 'problem',
      status: response.status,
      problem,
      retryAfterSeconds: parseRetryAfter(response.headers.get('Retry-After')),
    };
  }

  async function json<T>(path: string, init: RequestInit): Promise<ApiResult<T>> {
    const response = await request(path, init);
    if (!(response instanceof Response)) return { ok: false, ...response };
    if (!response.ok) return { ok: false, ...(await failureFrom(response)) };
    const data = (await response.json()) as T;
    return { ok: true, status: response.status, data, headers: response.headers };
  }

  const enc = encodeURIComponent;

  return {
    getStore(slug: string): Promise<ApiResult<PublicStore>> {
      return json<PublicStore>(`/stores/${enc(slug)}`, { method: 'GET' });
    },

    placeOrder(
      slug: string,
      body: PlaceWebOrderRequest,
      opts: { idempotencyKey: string; turnstileToken: string | null },
    ): Promise<ApiResult<PlacedWebOrder>> {
      const headers: Record<string, string> = {
        'Content-Type': 'application/json',
        'Idempotency-Key': opts.idempotencyKey,
      };
      if (opts.turnstileToken) headers['X-Turnstile-Token'] = opts.turnstileToken;
      return json<PlacedWebOrder>(`/stores/${enc(slug)}/orders`, { method: 'POST', headers, body: JSON.stringify(body) });
    },

    quoteDelivery(slug: string, address: DeliveryAddressInput): Promise<ApiResult<DeliveryQuote>> {
      return json<DeliveryQuote>(`/stores/${enc(slug)}/delivery-quotes`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ address }),
      });
    },

    async getTracking(token: string, etag: string | null): Promise<TrackingResult> {
      const headers: Record<string, string> = {};
      if (etag) headers['If-None-Match'] = etag;
      // `no-store`: o navegador não usa cache próprio e devolve o 304 do If-None-Match manual ao JavaScript.
      const response = await request(`/tracking/${enc(token)}`, { method: 'GET', headers, cache: 'no-store' });
      if (!(response instanceof Response)) return { ok: false, ...response };
      const newEtag = response.headers.get('ETag') ?? etag;
      if (response.status === 304) return { ok: true, status: 304, notModified: true, etag: newEtag };
      if (!response.ok) return { ok: false, ...(await failureFrom(response)) };
      const data = (await response.json()) as WebOrderTracking;
      return { ok: true, status: 200, data, etag: response.headers.get('ETag') ?? `"${data.version}"` };
    },

    withdraw(token: string): Promise<ApiResult<WebOrderTracking>> {
      return json<WebOrderTracking>(`/tracking/${enc(token)}/withdraw`, { method: 'POST', cache: 'no-store' });
    },
  };
}

export type PublicApi = ReturnType<typeof createPublicApi>;

export const DEFAULT_API_BASE_URL = 'http://localhost:5080';

/** Cliente do navegador: direto na API, sem headers de servidor. */
export function browserApi(): PublicApi {
  return createPublicApi({ baseUrl: process.env.NEXT_PUBLIC_API_BASE_URL || DEFAULT_API_BASE_URL });
}
