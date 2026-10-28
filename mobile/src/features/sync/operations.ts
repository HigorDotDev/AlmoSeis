/**
 * Ciclo de uma escrita crítica (08 §8.5, ADR-0010/0033/0034), sem React, para ser testável:
 *
 *   confirmou → key → INSERT pending (SENDING) → envia
 *     2xx            → confirmado: apaga pending e rascunho
 *     4xx            → recusado (409 VersionConflict à parte): apaga pending, rascunho fica
 *     rede/timeout/5xx → UNKNOWN: só sai daqui por GET /operations/{key}, nunca por reenvio automático
 */
import type { QueryClient } from '@tanstack/react-query';

import type { ApiResult } from '@/api/client';
import { sendCritical, type CriticalRequest } from '@/api/critical';
import { ApiError } from '@/api/errors';
import { syncApi } from '@/api/endpoints/sync';
import { draftsRepo } from '@/db/draftsRepo';
import { pendingOpsRepo } from '@/db/pendingOpsRepo';
import type { PendingOperation } from '@/db/schema';
import { newKey } from '@/lib/ids';
import { usePendingOps } from '@/stores/pendingOps';

export type CriticalOutcome<T> =
  | { kind: 'confirmed'; result: ApiResult<T> }
  | { kind: 'rejected'; error: ApiError }
  | { kind: 'conflict'; error: ApiError; current: unknown }
  | { kind: 'unknown'; key: string | null; error: unknown };

export interface RunCriticalInput {
  tenantId: string;
  request: CriticalRequest;
  /** Texto para a lista de pendências: "Pagamento de R$ 35,00 — pedido #12". */
  description: string;
  draftId?: string | null;
  /** false para escritas só [M] (sem Idempotency-Key): não há o que consultar depois; o If-Match evita duplicar. */
  idempotent?: boolean;
  /** Reenvio de uma pendência FAILED: mesma chave, mesmo corpo. */
  existingKey?: string;
}

/** TTL da idempotência no servidor (ADR-0033). */
export const IDEMPOTENCY_TTL_MS = 72 * 60 * 60 * 1000;

export async function runCritical<T>(input: RunCriticalInput): Promise<CriticalOutcome<T>> {
  const idempotent = input.idempotent ?? true;
  const store = usePendingOps.getState();
  let key: string | null = null;

  if (idempotent) {
    if (input.existingKey) {
      key = input.existingKey;
      await pendingOpsRepo.setStatus(key, 'SENDING');
      const op = await pendingOpsRepo.get(key);
      if (op) store.put(op);
    } else {
      key = newKey();
      const op: PendingOperation = {
        idempotencyKey: key,
        tenantId: input.tenantId,
        draftId: input.draftId ?? null,
        method: input.request.method,
        path: input.request.path,
        bodyJson: input.request.body === undefined ? null : JSON.stringify(input.request.body),
        ifMatch: input.request.ifMatch ?? null,
        status: 'SENDING',
        description: input.description,
        createdAt: new Date().toISOString(),
        lastError: null,
      };
      // Antes de enviar: se o app morrer no meio, a pendência continua lá para ser verificada.
      await pendingOpsRepo.insert(op);
      store.put(op);
    }
  }

  try {
    const result = await sendCritical<T>(input.request, key);
    await settle(key, input.draftId ?? null);
    return { kind: 'confirmed', result };
  } catch (error) {
    if (error instanceof ApiError && error.status < 500) {
      if (key) {
        await pendingOpsRepo.remove(key);
        store.drop(key);
      }
      if (error.is('VersionConflict')) {
        return { kind: 'conflict', error, current: error.extensions.current };
      }
      return { kind: 'rejected', error };
    }
    if (key) {
      await pendingOpsRepo.setStatus(key, 'UNKNOWN', error instanceof Error ? error.message : null);
      const op = await pendingOpsRepo.get(key);
      if (op) store.put(op);
    }
    return { kind: 'unknown', key, error };
  }
}

/** Confirmado: some a pendência e o rascunho (o rascunho só é apagado depois do servidor confirmar). */
async function settle(key: string | null, draftId: string | null): Promise<void> {
  if (key) {
    await pendingOpsRepo.remove(key);
    usePendingOps.getState().drop(key);
  }
  if (draftId) {
    await draftsRepo.remove(draftId);
  }
}

export type ResolveResult =
  | { kind: 'completed'; resourceId: string | null; responseStatus: number | null }
  | { kind: 'failed' }
  | { kind: 'stillUnknown' };

export interface ResolveOptions {
  /** Intervalo entre consultas com IN_PROGRESS (padrão 2 s, até 5 vezes). */
  retryDelayMs?: number;
  maxInProgressRetries?: number;
  sleep?: (ms: number) => Promise<void>;
}

const defaultSleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

/**
 * 08 §8.5 passo 4. COMPLETED com resposta de erro (4xx guardada pela idempotência) também encerra a pendência:
 * a operação foi processada e recusada, então reenviar daria a mesma recusa.
 */
export async function resolveUnknown(op: PendingOperation, options: ResolveOptions = {}): Promise<ResolveResult> {
  const sleep = options.sleep ?? defaultSleep;
  const maxRetries = options.maxInProgressRetries ?? 5;
  const store = usePendingOps.getState();

  for (let attempt = 0; ; attempt++) {
    let info;
    try {
      info = (await syncApi.operation(op.idempotencyKey)).data;
    } catch {
      return { kind: 'stillUnknown' };
    }

    switch (info.status) {
      case 'COMPLETED': {
        await pendingOpsRepo.remove(op.idempotencyKey);
        store.drop(op.idempotencyKey);
        const succeeded = info.responseStatus !== null && info.responseStatus < 300;
        if (succeeded && op.draftId) {
          await draftsRepo.remove(op.draftId);
        }
        return { kind: 'completed', resourceId: info.resourceId, responseStatus: info.responseStatus };
      }
      case 'NOT_FOUND': {
        // Nunca chegou: seguro reenviar com a mesma chave, mas só quando a pessoa pedir.
        await pendingOpsRepo.setStatus(op.idempotencyKey, 'FAILED', 'A operação não chegou ao servidor.');
        const updated = await pendingOpsRepo.get(op.idempotencyKey);
        if (updated) store.put(updated);
        return { kind: 'failed' };
      }
      case 'IN_PROGRESS':
        if (attempt >= maxRetries) {
          return { kind: 'stillUnknown' };
        }
        await sleep(options.retryDelayMs ?? 2000);
    }
  }
}

/** Invalida o que uma escrita pode ter mudado, a partir do caminho (usado quando a resposta original se perdeu). */
export function invalidateForPath(client: QueryClient, path: string): void {
  const keys: string[][] = [];
  if (path.startsWith('/orders') || path.startsWith('/payments')) {
    keys.push(['orders'], ['production'], ['customers'], ['closing']);
  }
  if (path.startsWith('/business-days')) {
    keys.push(['businessDay'], ['menus'], ['closing'], ['orders']);
  }
  if (path.startsWith('/closing')) {
    keys.push(['closing'], ['businessDay']);
  }
  for (const queryKey of keys) {
    void client.invalidateQueries({ queryKey });
  }
}

export function isExpired(op: PendingOperation, now = Date.now()): boolean {
  return now - new Date(op.createdAt).getTime() > IDEMPOTENCY_TTL_MS;
}

/** Reenvio pedido pela pessoa. Pendência com mais de 72 h vai com chave nova (a antiga expirou no servidor). */
export async function resendPending<T>(op: PendingOperation): Promise<CriticalOutcome<T>> {
  const request: CriticalRequest = {
    method: op.method as CriticalRequest['method'],
    path: op.path,
    body: op.bodyJson === null ? undefined : JSON.parse(op.bodyJson),
    ifMatch: op.ifMatch ?? undefined,
  };
  let key = op.idempotencyKey;
  if (isExpired(op)) {
    const fresh = newKey();
    await pendingOpsRepo.rekey(key, fresh);
    usePendingOps.getState().drop(key);
    key = fresh;
  }
  return runCritical<T>({ tenantId: op.tenantId, request, description: op.description, draftId: op.draftId, existingKey: key });
}

export async function discardPending(op: PendingOperation): Promise<void> {
  await pendingOpsRepo.remove(op.idempotencyKey);
  usePendingOps.getState().drop(op.idempotencyKey);
}
