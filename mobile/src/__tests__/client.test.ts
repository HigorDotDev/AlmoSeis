import { apiRequest, configureApiClient, parseETag, qs } from '@/api/client';
import { ApiError, NetworkUnknownError } from '@/api/errors';

function json(status: number, body: unknown, headers: Record<string, string> = {}) {
  return new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } });
}

describe('cliente da API (08 §8.3)', () => {
  let token = 'old';
  const hooks = {
    getAccessToken: () => token,
    refresh: jest.fn(async () => {
      await new Promise((r) => setTimeout(r, 10));
      token = 'new';
      return true;
    }),
    onSessionExpired: jest.fn(),
    onUpgradeRequired: jest.fn(),
    onSubscriptionInactive: jest.fn(),
    onNetworkFailure: jest.fn(),
  };

  beforeEach(() => {
    token = 'old';
    jest.clearAllMocks();
    configureApiClient(hooks);
  });

  it('envia os headers e lê ETag/replay', async () => {
    const fetchMock = jest.fn(async () => json(201, { id: '1' }, { ETag: '"5"', 'Idempotent-Replayed': 'true' }));
    global.fetch = fetchMock as unknown as typeof fetch;
    const r = await apiRequest('POST', '/orders/1/payments', { body: { amount: '1.00' }, idempotencyKey: 'k1', ifMatch: 4 });
    expect(r).toMatchObject({ status: 201, version: 5, replayed: true, data: { id: '1' } });
    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    const headers = init.headers as Record<string, string>;
    expect(headers['Idempotency-Key']).toBe('k1');
    expect(headers['If-Match']).toBe('"4"');
    expect(headers.Authorization).toBe('Bearer old');
    expect(headers['X-App-Version']).toBe('1.0.0');
    expect(headers['X-App-Platform']).toMatch(/ANDROID|IOS/);
  });

  it('refresh single-flight: 3 requisições com 401 renovam uma vez só e repetem', async () => {
    global.fetch = jest.fn(async (_url: string, init: RequestInit) => {
      const auth = (init.headers as Record<string, string>).Authorization;
      return auth === 'Bearer new' ? json(200, { ok: true }) : json(401, { errorCode: 'Unauthenticated' });
    }) as unknown as typeof fetch;
    const results = await Promise.all([apiRequest('GET', '/a'), apiRequest('GET', '/b'), apiRequest('GET', '/c')]);
    expect(hooks.refresh).toHaveBeenCalledTimes(1);
    expect(results.map((r) => r.status)).toEqual([200, 200, 200]);
    expect(hooks.onSessionExpired).not.toHaveBeenCalled();
  });

  it('refresh recusado encerra a sessão', async () => {
    hooks.refresh.mockImplementationOnce(async () => false);
    global.fetch = jest.fn(async () => json(401, { errorCode: 'Unauthenticated' })) as unknown as typeof fetch;
    await expect(apiRequest('GET', '/a')).rejects.toBeInstanceOf(ApiError);
    expect(hooks.onSessionExpired).toHaveBeenCalled();
  });

  it('426 pede atualização e 402 liga o somente leitura', async () => {
    global.fetch = jest.fn(async () => json(426, { errorCode: 'AppVersionUnsupported', minVersion: '1.5.0', storeUrl: 'https://loja' })) as unknown as typeof fetch;
    await expect(apiRequest('GET', '/a')).rejects.toMatchObject({ status: 426 });
    expect(hooks.onUpgradeRequired).toHaveBeenCalledWith({ minVersion: '1.5.0', storeUrl: 'https://loja' });

    global.fetch = jest.fn(async () => json(402, { errorCode: 'SubscriptionInactive' })) as unknown as typeof fetch;
    await expect(apiRequest('POST', '/orders', { body: {} })).rejects.toMatchObject({ errorCode: 'SubscriptionInactive' });
    expect(hooks.onSubscriptionInactive).toHaveBeenCalled();
  });

  it('falha de rede vira resultado desconhecido, não erro de negócio', async () => {
    global.fetch = jest.fn(async () => {
      throw new TypeError('Network request failed');
    }) as unknown as typeof fetch;
    await expect(apiRequest('POST', '/orders', { body: {} })).rejects.toBeInstanceOf(NetworkUnknownError);
    expect(hooks.onNetworkFailure).toHaveBeenCalled();
  });

  it('timeout de 15 s também é desconhecido', async () => {
    global.fetch = jest.fn(
      (_url: string, init: RequestInit) =>
        new Promise((_resolve, reject) => init.signal?.addEventListener('abort', () => reject(new Error('aborted')))),
    ) as unknown as typeof fetch;
    await expect(apiRequest('POST', '/orders', { body: {}, timeoutMs: 20 })).rejects.toBeInstanceOf(NetworkUnknownError);
  });

  it('utilitários', () => {
    expect(parseETag('"12"')).toBe(12);
    expect(parseETag('W/"3"')).toBe(3);
    expect(parseETag(null)).toBeNull();
    expect(qs({ a: 1, b: undefined, c: '', d: 'x y' })).toBe('?a=1&d=x%20y');
    expect(qs({})).toBe('');
  });
});
