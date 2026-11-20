import { describe, expect, it, vi } from 'vitest';
import { createPublicApi, parseRetryAfter, slugFromLocation } from './client';
import type { PlaceWebOrderRequest } from './types';

type Call = { url: string; init: RequestInit };

function fakeFetch(response: () => Response) {
  const calls: Call[] = [];
  const fn = vi.fn(async (url: string | URL | Request, init?: RequestInit) => {
    calls.push({ url: String(url), init: init ?? {} });
    return response();
  });
  return { fn: fn as unknown as typeof fetch, calls };
}

const json = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } });

const header = (call: Call, name: string) => (call.init.headers as Record<string, string>)[name];

const body: PlaceWebOrderRequest = {
  fulfillmentType: 'PICKUP',
  customerName: 'Carla Souza',
  customerPhone: '(34) 98888-7777',
  deliveryAddress: null,
  deliveryQuoteId: null,
  expectedPaymentMethod: 'CASH',
  changeFor: '50.00',
  notes: null,
  items: [],
  expectedTotal: '50.00',
  website: null,
};

describe('cliente da API pública', () => {
  it('lê o perfil com os headers fixos do servidor (X-Web-Renderer-Key) e sem barra dupla na URL', async () => {
    const f = fakeFetch(() => json(200, { slug: 'marmitaria-da-ana', status: 'OPEN' }));
    const api = createPublicApi({ baseUrl: 'http://api:5080/', fetch: f.fn, headers: { 'X-Web-Renderer-Key': 'k' } });
    const result = await api.getStore('marmitaria-da-ana');
    expect(result.ok).toBe(true);
    expect(f.calls[0]!.url).toBe('http://api:5080/api/v1/public/stores/marmitaria-da-ana');
    expect(header(f.calls[0]!, 'X-Web-Renderer-Key')).toBe('k');
  });

  it('slug antigo: 301 com Location vira { kind: "moved" } quando o redirecionamento é manual', async () => {
    const f = fakeFetch(() => new Response(null, { status: 301, headers: { Location: '/api/v1/public/stores/marmitaria-da-ana' } }));
    const api = createPublicApi({ baseUrl: 'http://api', fetch: f.fn, redirect: 'manual' });
    const result = await api.getStore('marmitaria-ana');
    expect(result).toMatchObject({ ok: false, kind: 'moved', slug: 'marmitaria-da-ana' });
    expect(f.calls[0]!.init.redirect).toBe('manual');
  });

  it('envia o pedido com Idempotency-Key e X-Turnstile-Token', async () => {
    const f = fakeFetch(() => json(201, { trackingToken: 'tok', totalAmount: '50.00' }));
    const api = createPublicApi({ baseUrl: 'http://api', fetch: f.fn });
    const result = await api.placeOrder('ana', body, { idempotencyKey: 'key-1', turnstileToken: 'ts' });
    expect(result.ok).toBe(true);
    const call = f.calls[0]!;
    expect(call.url).toBe('http://api/api/v1/public/stores/ana/orders');
    expect(call.init.method).toBe('POST');
    expect(header(call, 'Idempotency-Key')).toBe('key-1');
    expect(header(call, 'X-Turnstile-Token')).toBe('ts');
    expect(header(call, 'Content-Type')).toBe('application/json');
    expect(JSON.parse(call.init.body as string)).toEqual(body);
  });

  it('sem token do Turnstile, não manda o header', async () => {
    const f = fakeFetch(() => json(201, {}));
    await createPublicApi({ baseUrl: 'http://api', fetch: f.fn }).placeOrder('ana', body, { idempotencyKey: 'k', turnstileToken: null });
    expect(header(f.calls[0]!, 'X-Turnstile-Token')).toBeUndefined();
  });

  it('devolve o Problem Details do 409 CartChanged com o carrinho recalculado', async () => {
    const f = fakeFetch(() =>
      json(409, { errorCode: 'CartChanged', title: 'Seu pedido mudou.', cart: { items: [], unavailableProductIds: [], totalAmount: '6.00' } }),
    );
    const result = await createPublicApi({ baseUrl: 'http://api', fetch: f.fn }).placeOrder('ana', body, {
      idempotencyKey: 'k',
      turnstileToken: null,
    });
    expect(result.ok).toBe(false);
    if (result.ok || result.kind !== 'problem') throw new Error('esperava problem');
    expect(result.status).toBe(409);
    expect(result.problem.errorCode).toBe('CartChanged');
    expect(result.problem.cart?.totalAmount).toBe('6.00');
  });

  it('429 traz o Retry-After em segundos', async () => {
    const f = fakeFetch(() => json(429, { errorCode: 'TooManyRequests' }, { 'Retry-After': '42' }));
    const result = await createPublicApi({ baseUrl: 'http://api', fetch: f.fn }).placeOrder('ana', body, {
      idempotencyKey: 'k',
      turnstileToken: null,
    });
    expect(result).toMatchObject({ ok: false, kind: 'problem', status: 429, retryAfterSeconds: 42 });
  });

  it('falha de rede vira { kind: "network" } (resultado do envio desconhecido)', async () => {
    const fn = vi.fn(async () => {
      throw new TypeError('Failed to fetch');
    }) as unknown as typeof fetch;
    const result = await createPublicApi({ baseUrl: 'http://api', fetch: fn }).getStore('ana');
    expect(result).toMatchObject({ ok: false, kind: 'network' });
  });

  it('acompanhamento: manda If-None-Match e entende o 304', async () => {
    const f = fakeFetch(() => new Response(null, { status: 304, headers: { ETag: '"3"' } }));
    const result = await createPublicApi({ baseUrl: 'http://api', fetch: f.fn }).getTracking('tok', '"3"');
    expect(result).toMatchObject({ ok: true, status: 304, notModified: true, etag: '"3"' });
    expect(header(f.calls[0]!, 'If-None-Match')).toBe('"3"');
    expect(f.calls[0]!.init.cache).toBe('no-store');
  });

  it('acompanhamento: 200 guarda o ETag; sem ETag exposto, usa a versão', async () => {
    const f = fakeFetch(() => json(200, { version: 7, status: 'IN_PREPARATION' }));
    const result = await createPublicApi({ baseUrl: 'http://api', fetch: f.fn }).getTracking('tok', null);
    expect(result).toMatchObject({ ok: true, status: 200, etag: '"7"' });
    expect(header(f.calls[0]!, 'If-None-Match')).toBeUndefined();
  });

  it('acompanhamento: 410 TrackingExpired', async () => {
    const f = fakeFetch(() => json(410, { errorCode: 'TrackingExpired' }));
    const result = await createPublicApi({ baseUrl: 'http://api', fetch: f.fn }).getTracking('tok', null);
    expect(result).toMatchObject({ ok: false, kind: 'problem', status: 410 });
  });

  it('desistência é POST em /withdraw', async () => {
    const f = fakeFetch(() => json(200, { status: 'WITHDRAWN' }));
    await createPublicApi({ baseUrl: 'http://api', fetch: f.fn }).withdraw('tok');
    expect(f.calls[0]!.url).toBe('http://api/api/v1/public/tracking/tok/withdraw');
    expect(f.calls[0]!.init.method).toBe('POST');
  });
});

describe('utilitários do cliente', () => {
  it('lê Retry-After em segundos ou data HTTP', () => {
    expect(parseRetryAfter('30')).toBe(30);
    expect(parseRetryAfter(null)).toBeNull();
    const now = Date.parse('2026-11-20T12:00:00Z');
    expect(parseRetryAfter('Fri, 20 Nov 2026 12:01:00 GMT', now)).toBe(60);
  });

  it('extrai o slug do Location, mantendo só o segmento do slug', () => {
    expect(slugFromLocation('/api/v1/public/stores/marmitaria-da-ana')).toBe('marmitaria-da-ana');
    expect(slugFromLocation('http://api/api/v1/public/stores/novo/orders?x=1')).toBe('novo');
    expect(slugFromLocation('/outra-coisa')).toBeNull();
  });
});
