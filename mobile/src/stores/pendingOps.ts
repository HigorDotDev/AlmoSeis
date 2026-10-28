/** Espelho em memória das operações pendentes do SQLite, para selos e bloqueios na interface (08 §8.5). */
import { create } from 'zustand';

import { pendingOpsRepo } from '@/db/pendingOpsRepo';
import type { PendingOperation } from '@/db/schema';

interface PendingOpsState {
  ops: PendingOperation[];
  load(tenantId: string): Promise<void>;
  put(op: PendingOperation): void;
  drop(key: string): void;
  clear(): void;
}

export const usePendingOps = create<PendingOpsState>((set) => ({
  ops: [],
  async load(tenantId) {
    set({ ops: await pendingOpsRepo.list(tenantId) });
  },
  put: (op) => set((s) => ({ ops: [...s.ops.filter((o) => o.idempotencyKey !== op.idempotencyKey), op] })),
  drop: (key) => set((s) => ({ ops: s.ops.filter((o) => o.idempotencyKey !== key) })),
  clear: () => set({ ops: [] }),
}));

/**
 * Há operação UNKNOWN/FAILED nesse escopo? O escopo é um prefixo do caminho (`/orders/{id}`): bloqueia repetir a
 * mesma ação enquanto o resultado não é conhecido, o que evita pagamento em dobro (RN-009.9).
 */
export function useBlockedBy(pathPrefix: string | null): PendingOperation | undefined {
  return usePendingOps((s) =>
    pathPrefix ? s.ops.find((o) => o.status !== 'SENDING' && o.path.startsWith(pathPrefix)) : undefined,
  );
}
