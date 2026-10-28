/**
 * Sincronização (08 §8.6): ao voltar online, ao voltar para o primeiro plano, a cada reconexão do SignalR e em
 * salto de `seq`, com no mínimo 10 s entre execuções.
 *
 *   1. renova a sessão → 2. resolve pendências UNKNOWN → 3. GET /sync/changes até hasMore=false
 *   (410 → descarta o cache e segue de currentSeq) → 4. garante o SignalR → 5. libera os rascunhos
 */
import type { QueryClient } from '@tanstack/react-query';

import { refreshSingleFlight } from '@/api/client';
import { isApiError } from '@/api/errors';
import { syncApi } from '@/api/endpoints/sync';
import { invalidateEntity } from '@/api/queryKeys';
import { metaRepo } from '@/db/metaRepo';
import { pendingOpsRepo } from '@/db/pendingOpsRepo';
import { useConnectivity } from '@/stores/connectivity';

import { invalidateForPath, resolveUnknown } from './operations';

export const MIN_INTERVAL_MS = 10_000;
const PAGE_SIZE = 500;

export interface SyncContext {
  queryClient: QueryClient;
  tenantId: string;
  ownEmployeeId: string | null;
  ensureRealtime?: () => void;
}

let running: Promise<void> | null = null;
let lastRunAt = 0;

/** `force` ignora o intervalo mínimo (ex.: puxar para atualizar). */
export function runSync(ctx: SyncContext, force = false): Promise<void> {
  if (running) return running;
  if (!force && Date.now() - lastRunAt < MIN_INTERVAL_MS) return Promise.resolve();
  lastRunAt = Date.now();
  running = doSync(ctx).finally(() => {
    running = null;
  });
  return running;
}

/** Testes. */
export function resetSyncThrottle(): void {
  lastRunAt = 0;
  running = null;
}

async function doSync(ctx: SyncContext): Promise<void> {
  const connectivity = useConnectivity.getState();
  connectivity.setSyncState('syncing');
  try {
    try {
      await refreshSingleFlight();
    } catch {
      // Rede caiu de novo: o restante falharia também.
      connectivity.setSyncState('error');
      return;
    }

    for (const op of ctx.ownEmployeeId ? await pendingOpsRepo.list(ctx.tenantId, ctx.ownEmployeeId) : []) {
      // SENDING recente ainda pode estar em voo nesta sessão; antigo é de um app fechado no meio do envio.
      const stale = op.status === 'SENDING' && Date.now() - new Date(op.createdAt).getTime() > 30_000;
      if (op.status === 'UNKNOWN' || stale) {
        const result = await resolveUnknown(op);
        if (result.kind === 'completed') {
          invalidateForPath(ctx.queryClient, op.path);
        }
      }
    }

    await pullChanges(ctx);
    ctx.ensureRealtime?.();
    connectivity.setSyncState('synced');
  } catch {
    connectivity.setSyncState('error');
  }
}

/** Passo 3. Primeira execução no aparelho (sem lastSeq): só avança o cursor; o cache ainda está vazio. */
export async function pullChanges(ctx: SyncContext): Promise<void> {
  const stored = await metaRepo.lastSeq(ctx.tenantId);
  let since = stored ?? 0;
  const invalidate = stored !== null;

  for (;;) {
    let page;
    try {
      page = (await syncApi.changes(since, PAGE_SIZE)).data;
    } catch (e) {
      if (isApiError(e, 'SyncCursorExpired')) {
        const currentSeq = Number(e.extensions.currentSeq ?? 0);
        ctx.queryClient.removeQueries();
        await metaRepo.setLastSeq(ctx.tenantId, currentSeq);
        void ctx.queryClient.invalidateQueries();
        return;
      }
      throw e;
    }

    if (invalidate) {
      for (const change of page.changes) {
        invalidateEntity(ctx.queryClient, change.entityType, change.entityId, ctx.ownEmployeeId);
      }
    }
    since = page.lastSeq;
    await metaRepo.setLastSeq(ctx.tenantId, since);
    if (!page.hasMore) return;
  }
}
