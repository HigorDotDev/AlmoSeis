/** Pedidos, produção e alertas (06 §6.9). */
import { useInfiniteQuery, useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';

import { critical, versioned } from '@/api/critical';
import { ordersApi } from '@/api/endpoints/orders';
import { qk } from '@/api/queryKeys';
import type {
  CreateOrderBody,
  NewItemInput,
  Order,
  OrderBalance,
  OrderFilters,
  ProductionStatus,
  UpdateOrderBody,
} from '@/api/types';
import { useCriticalMutation } from '@/features/sync/useCriticalMutation';
import { formatBRL } from '@/lib/money';
import { orderTypeNames, productionNames } from '@/lib/strings';
import { gramsToKgLabel } from '@/lib/weight';

const STALE = 30_000;

export function useOrders(filters: OrderFilters, enabled = true) {
  return useInfiniteQuery({
    queryKey: qk.orders(filters),
    queryFn: async ({ pageParam }) => (await ordersApi.list({ ...filters, limit: filters.limit ?? 50, cursor: pageParam })).data,
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    staleTime: STALE,
    enabled,
  });
}

export function useOrder(id: string | undefined) {
  return useQuery({ queryKey: qk.order(id ?? ''), queryFn: async () => (await ordersApi.get(id!)).data, enabled: !!id, staleTime: STALE });
}

export function useOrderAlerts(id: string, enabled = true) {
  return useQuery({ queryKey: qk.orderAlerts(id), queryFn: async () => (await ordersApi.alerts(id)).data.items, enabled, staleTime: STALE });
}

export function useOrderHistory(id: string) {
  return useInfiniteQuery({
    queryKey: qk.orderHistory(id),
    queryFn: async ({ pageParam }) => (await ordersApi.history(id, pageParam)).data,
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    staleTime: STALE,
  });
}

export function useProduction(businessDayId: string | undefined, enabled = true) {
  return useQuery({
    queryKey: qk.production(businessDayId),
    queryFn: async () => (await ordersApi.production()).data,
    enabled,
    staleTime: 15_000,
    refetchInterval: 60_000,
  });
}

/** A escrita devolveu o pedido inteiro: ele vira o cache, e a próxima escrita já usa a versão nova. */
export function setOrderInCache(client: QueryClient, order: Order): void {
  client.setQueryData(qk.order(order.id), order);
  void client.invalidateQueries({ queryKey: ['orders', 'list'] });
  void client.invalidateQueries({ queryKey: ['production'] });
  void client.invalidateQueries({ queryKey: ['customers'] });
}

/** Pagamento/estorno devolvem só o saldo: aplica no cache e busca o pedido completo de novo. */
export function applyOrderBalance(client: QueryClient, balance: OrderBalance): void {
  client.setQueryData<Order>(qk.order(balance.id), (old) =>
    old
      ? { ...old, version: balance.version, totalAmount: balance.totalAmount, paidAmount: balance.paidAmount, balance: balance.balance, isPaid: balance.isPaid }
      : old,
  );
  void client.invalidateQueries({ queryKey: qk.order(balance.id) });
  void client.invalidateQueries({ queryKey: ['orders', 'list'] });
  void client.invalidateQueries({ queryKey: ['production'] });
  void client.invalidateQueries({ queryKey: ['customers'] });
  void client.invalidateQueries({ queryKey: ['closing'] });
}

export function describeOrder(body: CreateOrderBody, previewCents: number | null): string {
  const who = body.customerName ?? (body.tableLabel ? `Mesa ${body.tableLabel}` : null);
  return [`Pedido de ${orderTypeNames[body.orderType].toLowerCase()}`, who, previewCents !== null ? formatBRL(previewCents) : null]
    .filter(Boolean)
    .join(' — ');
}

export function useCreateOrder(options: { draftId: string | null; previewCents: number | null; onCreated: (order: Order) => void }) {
  const client = useQueryClient();
  return useCriticalMutation<CreateOrderBody, Order>({
    request: (body) => critical.createOrder(body),
    describe: (body) => describeOrder(body, options.previewCents),
    draftId: options.draftId,
    onConfirmed: (order) => {
      setOrderInCache(client, order);
      options.onCreated(order);
    },
    successMessage: (order) => `Pedido #${order.orderNumber} registrado`,
  });
}

export function useAddItems(order: Order | undefined, draftId: string | null, onDone: () => void) {
  const client = useQueryClient();
  return useCriticalMutation<{ items: NewItemInput[]; version: number }, Order>({
    request: (v) => critical.addItems(order!.id, v.items, v.version),
    describe: (v) => `Incluir ${v.items.length} ${v.items.length === 1 ? 'item' : 'itens'} — pedido #${order?.orderNumber}`,
    draftId,
    onConfirmed: (o) => {
      setOrderInCache(client, o);
      onDone();
    },
    onConflict: (current) => {
      if (current) client.setQueryData(qk.order(order!.id), current as Order);
    },
    successMessage: (o) => `Itens incluídos no pedido #${o.orderNumber}`,
  });
}

export function useAddWeighings(order: Order | undefined, onDone: () => void) {
  const client = useQueryClient();
  return useCriticalMutation<{ weighings: { weightGrams: number }[]; version: number }, Order>({
    request: (v) => critical.addWeighings(order!.id, v.weighings, v.version),
    describe: (v) => `Pesagem de ${v.weighings.map((w) => gramsToKgLabel(w.weightGrams)).join(', ')} — pedido #${order?.orderNumber}`,
    onConfirmed: (o) => {
      setOrderInCache(client, o);
      onDone();
    },
    onConflict: (current) => {
      if (current) client.setQueryData(qk.order(order!.id), current as Order);
    },
    successMessage: () => 'Pesagem registrada',
  });
}

/** Escritas só [M] no pedido: If-Match protege contra aplicar duas vezes. */
function useOrderVersioned<TVars>(orderId: string, build: (v: TVars) => ReturnType<typeof versioned.setStatus>, describe: (v: TVars) => string, success?: string) {
  const client = useQueryClient();
  return useCriticalMutation<TVars, Order>({
    request: build,
    describe,
    idempotent: false,
    onConfirmed: (o) => setOrderInCache(client, o),
    onConflict: (current) => {
      if (current) client.setQueryData(qk.order(orderId), current as Order);
      void client.invalidateQueries({ queryKey: ['production'] });
    },
    successMessage: () => success ?? null,
  });
}

export function useSetProductionStatus(orderId: string) {
  return useOrderVersioned<{ status: ProductionStatus; version: number }>(
    orderId,
    (v) => versioned.setStatus(orderId, v.status, v.version),
    (v) => `Status: ${productionNames[v.status]}`,
  );
}

export function useUpdateOrder(orderId: string) {
  return useOrderVersioned<{ body: UpdateOrderBody; version: number }>(orderId, (v) => versioned.updateOrder(orderId, v.body, v.version), () => 'Editar pedido', 'Pedido atualizado');
}

export function useUpdateItem(orderId: string) {
  return useOrderVersioned<{ itemId: string; body: { quantity?: number; notes?: string | null }; version: number }>(
    orderId,
    (v) => versioned.updateItem(orderId, v.itemId, v.body, v.version),
    () => 'Alterar item',
    'Item alterado',
  );
}

export function useRemoveItem(orderId: string) {
  return useOrderVersioned<{ itemId: string; version: number }>(orderId, (v) => versioned.removeItem(orderId, v.itemId, v.version), () => 'Remover item', 'Item removido');
}

export function useRemoveWeighing(orderId: string) {
  return useOrderVersioned<{ weighingId: string; version: number }>(
    orderId,
    (v) => versioned.removeWeighing(orderId, v.weighingId, v.version),
    () => 'Remover pesagem',
    'Pesagem removida',
  );
}

export function useCancelOrder(orderId: string) {
  return useOrderVersioned<{ reason: string; version: number }>(orderId, (v) => versioned.cancelOrder(orderId, v.reason, v.version), () => 'Cancelar pedido', 'Pedido cancelado');
}

export function useAcknowledgeAlerts(orderId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (eventIds: string[]) => ordersApi.acknowledgeAlerts(orderId, eventIds),
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: ['orders'] });
      void client.invalidateQueries({ queryKey: ['production'] });
    },
  });
}

/** Resumo legível do `current` de um 409 (ConflictSheet). */
export function summarizeOrder(order: Order): string[] {
  return [
    `Total: ${formatBRL(order.totalAmount)} (pago ${formatBRL(order.paidAmount)})`,
    `${order.items.length} ${order.items.length === 1 ? 'item' : 'itens'}: ${order.items.map((i) => `${i.quantity}× ${i.name}`).join(', ')}`,
    order.productionStatus ? `Produção: ${productionNames[order.productionStatus]}` : null,
    order.isPaid ? 'Pedido quitado' : null,
  ].filter((l): l is string => l !== null);
}
