/** Caixa e fechamento (06 §6.11, ADR-0013/0014). */
import { useInfiniteQuery, useQuery, useQueryClient } from '@tanstack/react-query';

import { critical } from '@/api/critical';
import { closingApi } from '@/api/endpoints/closing';
import { qk } from '@/api/queryKeys';
import type { CashMovement, CashMovementType, ClosingAdjustment, ClosingSummary } from '@/api/types';
import { useCriticalMutation } from '@/features/sync/useCriticalMutation';
import { formatBRL } from '@/lib/money';

export function useClosingSummary(businessDayId?: string, enabled = true) {
  return useQuery({
    queryKey: qk.closingSummary(businessDayId),
    queryFn: async () => (await closingApi.summary(businessDayId)).data,
    staleTime: 15_000,
    enabled,
  });
}

export function useCashMovements(businessDayId?: string, enabled = true) {
  return useQuery({ queryKey: qk.cashMovements(businessDayId), queryFn: async () => (await closingApi.cashMovements(businessDayId)).data.items, enabled });
}

export function useClosingHistory(f: { from?: string; to?: string }) {
  return useInfiniteQuery({
    queryKey: qk.closingHistory(f),
    queryFn: async ({ pageParam }) => (await closingApi.history({ ...f, limit: 31, cursor: pageParam })).data,
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    staleTime: 5 * 60_000,
  });
}

export function useClosing(id: string | undefined) {
  return useQuery({ queryKey: qk.closing(id ?? ''), queryFn: async () => (await closingApi.get(id!)).data, enabled: !!id, staleTime: 5 * 60_000 });
}

export function useMonthlyClosing(month: string) {
  return useQuery({ queryKey: qk.closingMonthly(month), queryFn: async () => (await closingApi.monthly(month)).data, staleTime: 5 * 60_000 });
}

const movementNames: Record<CashMovementType, string> = { SUPPLY: 'Suprimento', WITHDRAWAL: 'Retirada' };

export function useAddCashMovement(onDone: () => void) {
  const client = useQueryClient();
  return useCriticalMutation<{ movementType: CashMovementType; amount: string; description: string }, CashMovement>({
    request: (body) => critical.addCashMovement(body),
    describe: (b) => `${movementNames[b.movementType]} de ${formatBRL(b.amount)}`,
    onConfirmed: () => {
      void client.invalidateQueries({ queryKey: ['closing'] });
      void client.invalidateQueries({ queryKey: ['businessDay'] });
      onDone();
    },
    successMessage: (m) => `${movementNames[m.movementType]} de ${formatBRL(m.amount)} registrado`,
  });
}

export function useFinalizeClosing(onFinalized: (closing: ClosingSummary) => void, onConflict: () => void) {
  const client = useQueryClient();
  return useCriticalMutation<{ businessDayId: string; countedCash: string; justification: string | null; version: number }, ClosingSummary>({
    request: (v) => critical.finalizeClosing({ businessDayId: v.businessDayId, countedCash: v.countedCash, justification: v.justification }, v.version),
    describe: (v) => `Fechar expediente — caixa contado ${formatBRL(v.countedCash)}`,
    onConfirmed: (closing) => {
      if (closing.dailyClosingId) client.setQueryData(qk.closing(closing.dailyClosingId), closing);
      client.setQueryData(qk.businessDay, null);
      for (const key of [['closing'], ['orders'], ['production'], ['menus']]) {
        void client.invalidateQueries({ queryKey: key });
      }
      onFinalized(closing);
    },
    onResolvedCompleted: () => {
      void client.invalidateQueries({ queryKey: ['businessDay'] });
      void client.invalidateQueries({ queryKey: ['closing'] });
    },
    // 409: houve movimentação enquanto a tela estava aberta → recarrega e confere de novo (08 §8.9).
    onConflict: () => {
      void client.invalidateQueries({ queryKey: ['closing'] });
      void client.invalidateQueries({ queryKey: ['businessDay'] });
      onConflict();
    },
    successMessage: () => 'Expediente fechado',
  });
}

export function useAddAdjustment(closingId: string, onDone: () => void) {
  const client = useQueryClient();
  return useCriticalMutation<{ adjustmentType: string; amount: string; reason: string }, ClosingAdjustment>({
    request: (body) => critical.addAdjustment(closingId, body),
    describe: (b) => `Ajuste de ${formatBRL(b.amount)} no fechamento`,
    onConfirmed: () => {
      void client.invalidateQueries({ queryKey: ['closing'] });
      onDone();
    },
    successMessage: () => 'Ajuste registrado',
  });
}
