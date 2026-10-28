/** Pagamentos e estornos (06 §6.10, ADR-0021/0022). If-Match = versão do pedido. */
import { useQuery, useQueryClient } from '@tanstack/react-query';

import { critical } from '@/api/critical';
import { paymentsApi } from '@/api/endpoints/payments';
import { qk } from '@/api/queryKeys';
import type { Order, PaymentResult, RegisterPaymentBody, ReversalResult } from '@/api/types';
import { applyOrderBalance } from '@/features/orders/hooks';
import { useCriticalMutation } from '@/features/sync/useCriticalMutation';
import { formatBRL, toCents } from '@/lib/money';
import { paymentMethodNames } from '@/lib/strings';

export function useOrderPayments(orderId: string) {
  return useQuery({ queryKey: qk.orderPayments(orderId), queryFn: async () => (await paymentsApi.listForOrder(orderId)).data.items, staleTime: 30_000 });
}

export interface RegisterVars {
  body: RegisterPaymentBody;
  version: number;
}

export function useRegisterPayment(order: Order | undefined, onConfirmed: (result: PaymentResult) => void) {
  const client = useQueryClient();
  return useCriticalMutation<RegisterVars, PaymentResult>({
    request: (v) => critical.registerPayment(order!.id, v.body, v.version),
    describe: (v) => `Pagamento de ${formatBRL(v.body.amount)} em ${paymentMethodNames[v.body.paymentMethod].toLowerCase()} — pedido #${order?.orderNumber}`,
    onConfirmed: (result) => {
      applyOrderBalance(client, result.order);
      void client.invalidateQueries({ queryKey: qk.orderPayments(result.order.id) });
      onConfirmed(result);
    },
    onConflict: (current) => {
      if (current) client.setQueryData(qk.order(order!.id), current as Order);
    },
    successMessage: (r) =>
      r.payment.changeGiven && toCents(r.payment.changeGiven) > 0
        ? `Recebido. Troco: ${formatBRL(r.payment.changeGiven)}`
        : `Recebido ${formatBRL(r.payment.amount)}`,
  });
}

export function useReversePayment(order: Order | undefined) {
  const client = useQueryClient();
  return useCriticalMutation<{ paymentId: string; reason: string; amount: string; version: number }, ReversalResult>({
    request: (v) => critical.reversePayment(v.paymentId, v.reason, v.version),
    describe: (v) => `Estorno de ${formatBRL(v.amount)} — pedido #${order?.orderNumber}`,
    onConfirmed: (result) => {
      applyOrderBalance(client, result.order);
      void client.invalidateQueries({ queryKey: qk.orderPayments(result.order.id) });
    },
    onConflict: (current) => {
      if (current) client.setQueryData(qk.order(order!.id), current as Order);
    },
    successMessage: (r) => `Estorno de ${formatBRL(r.reversal.amount)} registrado`,
  });
}
