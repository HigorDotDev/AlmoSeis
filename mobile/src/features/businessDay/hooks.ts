/** Expediente (06 §6.6, ADR-0019/0039). Sem expediente aberto não há pedido nem pagamento. */
import { useQuery, useQueryClient } from '@tanstack/react-query';

import { critical } from '@/api/critical';
import { isApiError } from '@/api/errors';
import { businessDaysApi } from '@/api/endpoints/businessDays';
import { qk } from '@/api/queryKeys';
import type { BusinessDay, OpenBusinessDayResult } from '@/api/types';
import { formatBRL } from '@/lib/money';
import { useCriticalMutation } from '@/features/sync/useCriticalMutation';

/** `null` = nenhum expediente aberto (404 NoOpenBusinessDay não é erro de tela). */
export function useCurrentBusinessDay() {
  return useQuery({
    queryKey: qk.businessDay,
    queryFn: async (): Promise<BusinessDay | null> => {
      try {
        return (await businessDaysApi.current()).data;
      } catch (e) {
        if (isApiError(e, 'NoOpenBusinessDay')) return null;
        throw e;
      }
    },
    staleTime: 60_000,
  });
}

export function useOpenBusinessDay(onOpened: (result: OpenBusinessDayResult) => void, onClosedToday: () => void) {
  const client = useQueryClient();
  return useCriticalMutation<{ openingBalance: string; confirmSameDate: boolean }, OpenBusinessDayResult>({
    // Cada envio gera uma chave nova: com `confirmSameDate: true` o corpo muda (06 §6.6).
    request: (vars) => critical.openBusinessDay(vars),
    describe: (vars) => `Abrir expediente com ${formatBRL(vars.openingBalance)}`,
    onConfirmed: (result) => {
      client.setQueryData(qk.businessDay, result.businessDay);
      for (const key of [['menus'], ['orders'], ['production'], ['closing']]) {
        void client.invalidateQueries({ queryKey: key });
      }
      onOpened(result);
    },
    onResolvedCompleted: () => {
      void client.invalidateQueries({ queryKey: qk.businessDay });
    },
    onRejected: (error) => {
      if (error.is('BusinessDayClosedToday')) onClosedToday();
      if (error.is('BusinessDayAlreadyOpen')) void client.invalidateQueries({ queryKey: qk.businessDay });
    },
    successMessage: () => 'Expediente aberto.',
  });
}
