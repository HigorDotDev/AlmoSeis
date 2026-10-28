/**
 * 08 §8.5: sucesso, 4xx, 409 com `current`, timeout → UNKNOWN → /operations COMPLETED/NOT_FOUND/IN_PROGRESS,
 * e reenvio só com a mesma chave e o mesmo corpo.
 */
import { configureApiClient } from '@/api/client';
import { critical } from '@/api/critical';
import { resendPending, resolveUnknown, runCritical } from '@/features/sync/operations';
import { usePendingOps } from '@/stores/pendingOps';

import { memory } from '@/test-utils/fakeDb';

jest.mock('@/db/pendingOpsRepo', () => ({ pendingOpsRepo: jest.requireActual('@/test-utils/fakeDb').pendingOpsRepo }));
jest.mock('@/db/draftsRepo', () => ({ draftsRepo: jest.requireActual('@/test-utils/fakeDb').draftsRepo }));

const TENANT = 't1';
const respond = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } });

type Handler = (url: string, init: RequestInit) => Response | Promise<Response>;
let handler: Handler;
const calls: { url: string; init: RequestInit }[] = [];

beforeEach(() => {
  memory.reset();
  usePendingOps.getState().clear();
  calls.length = 0;
  configureApiClient({
    getAccessToken: () => 'tok',
    refresh: async () => true,
    onSessionExpired: () => {},
    onUpgradeRequired: () => {},
    onSubscriptionInactive: () => {},
  });
  global.fetch = jest.fn(async (url: string, init: RequestInit) => {
    calls.push({ url, init });
    return handler(url, init);
  }) as unknown as typeof fetch;
});

const payment = () => critical.registerPayment('o1', { amount: '35.00', paymentMethod: 'CASH', cashReceived: '50.00', label: null }, 4);
const header = (i: number, name: string) => (calls[i]!.init.headers as Record<string, string>)[name];

describe('runCritical', () => {
  it('grava a pendência antes de enviar e apaga pendência e rascunho no 2xx', async () => {
    memory.drafts.set('d1', { id: 'd1', tenantId: TENANT, kind: 'PAYMENT', targetId: 'o1', formJson: '{}', updatedAt: '' });
    handler = () => {
      // No momento do envio a pendência já existe (sobrevive a o app morrer no meio).
      expect([...memory.ops.values()][0]).toMatchObject({ status: 'SENDING', path: '/orders/o1/payments', ifMatch: 4 });
      return respond(201, { payment: { id: 'p1' }, order: { version: 5 } }, { ETag: '"5"' });
    };
    const outcome = await runCritical({ tenantId: TENANT, request: payment(), description: 'Pagamento', draftId: 'd1' });
    expect(outcome.kind).toBe('confirmed');
    expect(memory.ops.size).toBe(0);
    expect(memory.drafts.has('d1')).toBe(false);
    expect(header(0, 'Idempotency-Key')).toMatch(/[0-9a-f-]{36}/);
    expect(header(0, 'If-Match')).toBe('"4"');
  });

  it('4xx de negócio: sem pendência, rascunho fica', async () => {
    memory.drafts.set('d1', { id: 'd1', tenantId: TENANT, kind: 'PAYMENT', targetId: 'o1', formJson: '{}', updatedAt: '' });
    handler = () => respond(422, { errorCode: 'PaymentExceedsBalance', balance: '20.00' });
    const outcome = await runCritical({ tenantId: TENANT, request: payment(), description: 'Pagamento', draftId: 'd1' });
    expect(outcome.kind).toBe('rejected');
    expect(memory.ops.size).toBe(0);
    expect(memory.drafts.has('d1')).toBe(true);
  });

  it('409 VersionConflict devolve o `current`', async () => {
    handler = () => respond(409, { errorCode: 'VersionConflict', expectedVersion: 4, currentVersion: 5, current: { id: 'o1', version: 5 } });
    const outcome = await runCritical({ tenantId: TENANT, request: payment(), description: 'Pagamento' });
    expect(outcome).toMatchObject({ kind: 'conflict', current: { id: 'o1', version: 5 } });
    expect(memory.ops.size).toBe(0);
  });

  it('timeout/rede → UNKNOWN; COMPLETED resolve sem reenviar', async () => {
    handler = () => {
      throw new TypeError('Network request failed');
    };
    const outcome = await runCritical({ tenantId: TENANT, request: payment(), description: 'Pagamento', draftId: 'd1' });
    expect(outcome.kind).toBe('unknown');
    const op = [...memory.ops.values()][0]!;
    expect(op.status).toBe('UNKNOWN');
    expect(usePendingOps.getState().ops).toHaveLength(1);

    handler = (url) => {
      expect(url).toContain(`/operations/${op.idempotencyKey}`);
      return respond(200, { key: op.idempotencyKey, status: 'COMPLETED', responseStatus: 201, resourceId: 'p1', endpoint: null, createdAt: null, completedAt: null });
    };
    const resolved = await resolveUnknown(op);
    expect(resolved).toEqual({ kind: 'completed', resourceId: 'p1', responseStatus: 201 });
    expect(memory.ops.size).toBe(0);
    // Só a consulta: nenhum POST de pagamento repetido.
    expect(calls.filter((c) => c.init.method === 'POST')).toHaveLength(1);
  });

  it('5xx também é desconhecido', async () => {
    handler = () => respond(503, { errorCode: 'Http503' });
    const outcome = await runCritical({ tenantId: TENANT, request: payment(), description: 'Pagamento' });
    expect(outcome.kind).toBe('unknown');
  });

  it('NOT_FOUND → FAILED; reenvio usa a mesma chave e o mesmo corpo', async () => {
    handler = () => {
      throw new TypeError('offline');
    };
    await runCritical({ tenantId: TENANT, request: payment(), description: 'Pagamento' });
    const op = [...memory.ops.values()][0]!;

    handler = () => respond(200, { key: op.idempotencyKey, status: 'NOT_FOUND', responseStatus: null, resourceId: null, endpoint: null, createdAt: null, completedAt: null });
    expect(await resolveUnknown(op)).toEqual({ kind: 'failed' });
    expect(memory.ops.get(op.idempotencyKey)!.status).toBe('FAILED');

    handler = () => respond(201, { payment: { id: 'p1' }, order: { version: 5 } });
    const resent = await resendPending(memory.ops.get(op.idempotencyKey)!);
    expect(resent.kind).toBe('confirmed');
    const last = calls[calls.length - 1]!;
    expect((last.init.headers as Record<string, string>)['Idempotency-Key']).toBe(op.idempotencyKey);
    expect(last.init.body).toBe(calls[0]!.init.body);
    expect(memory.ops.size).toBe(0);
  });

  it('IN_PROGRESS consulta de novo e desiste depois do limite, mantendo UNKNOWN', async () => {
    handler = () => {
      throw new TypeError('offline');
    };
    await runCritical({ tenantId: TENANT, request: payment(), description: 'Pagamento' });
    const op = [...memory.ops.values()][0]!;
    handler = () => respond(200, { key: op.idempotencyKey, status: 'IN_PROGRESS', responseStatus: null, resourceId: null, endpoint: null, createdAt: null, completedAt: null });
    const sleep = jest.fn(async () => {});
    expect(await resolveUnknown(op, { sleep, maxInProgressRetries: 5 })).toEqual({ kind: 'stillUnknown' });
    expect(sleep).toHaveBeenCalledTimes(5);
    expect(memory.ops.get(op.idempotencyKey)!.status).toBe('UNKNOWN');
  });

  it('pendência com mais de 72 h é reenviada com chave nova', async () => {
    const old = new Date(Date.now() - 73 * 3600 * 1000).toISOString();
    memory.ops.set('k-old', {
      idempotencyKey: 'k-old',
      tenantId: TENANT,
      draftId: null,
      method: 'POST',
      path: '/orders',
      bodyJson: '{"orderType":"KILO"}',
      ifMatch: null,
      status: 'FAILED',
      description: 'Pedido',
      createdAt: old,
      lastError: null,
    });
    handler = () => respond(201, { id: 'o9' });
    const outcome = await resendPending(memory.ops.get('k-old')!);
    expect(outcome.kind).toBe('confirmed');
    expect(header(0, 'Idempotency-Key')).not.toBe('k-old');
  });

  it('escrita só [M] não grava pendência', async () => {
    handler = () => {
      throw new TypeError('offline');
    };
    const outcome = await runCritical({
      tenantId: TENANT,
      request: { method: 'PATCH', path: '/orders/o1/status', body: { productionStatus: 'COMPLETED' }, ifMatch: 3 },
      description: 'Status',
      idempotent: false,
    });
    expect(outcome).toMatchObject({ kind: 'unknown', key: null });
    expect(memory.ops.size).toBe(0);
    expect(header(0, 'Idempotency-Key')).toBeUndefined();
  });
});
