/**
 * Aba Delivery da lanchonete (08 §8.9, ADR-0055 item 3, ADR-0059 itens 10 a 12, RF-315): entrega e retirada à la
 * carte do expediente aberto, do telefone e do site, com a faixa "Novos do site" no topo, filtros por atendimento
 * e por status, e o card com a forma prevista, o troco que o motoboy leva, o saldo e o próximo passo do atendimento.
 */
import { FlashList } from '@shopify/flash-list';
import { router } from 'expo-router';
import { useMemo, useState } from 'react';
import { RefreshControl, View } from 'react-native';

import { errorMessage } from '@/api/errors';
import type { FulfillmentStatus, FulfillmentType, OrderFilters, OrderSummary } from '@/api/types';
import { Chip, EmptyState, ErrorState, Fab, Row, SegmentedControl, Skeleton, space, useColors, WEB_CONTENT_MAX } from '@/components/ui';
import { useCurrentBusinessDay } from '@/features/businessDay/hooks';
import { NoBusinessDayCard, OrderCard } from '@/features/orders/components';
import { useOrders } from '@/features/orders/hooks';
import { NewWebOrdersSection } from '@/features/orders/webOrderComponents';
import { useCanWrite } from '@/stores/session';

export type DeliveryKind = 'ALL' | 'DELIVERY' | 'PICKUP';
export type DeliveryStatusFilter = 'ALL' | FulfillmentStatus;

/** Os pedidos da aba: `orderType=A_LA_CARTE` com entrega e/ou retirada (06 §6.9, `fulfillmentType` repetível). */
export const DELIVERY_SCOPE: OrderFilters = { orderType: 'A_LA_CARTE', fulfillmentType: ['DELIVERY', 'PICKUP'] };

/** Filtros da lista → query do `GET /orders`. O status que não existe no atendimento escolhido não é oferecido. */
export function deliveryFilters(kind: DeliveryKind, status: DeliveryStatusFilter): OrderFilters {
  const fulfillmentType: FulfillmentType[] = kind === 'ALL' ? ['DELIVERY', 'PICKUP'] : [kind];
  return { orderType: 'A_LA_CARTE', fulfillmentType, ...(status === 'ALL' ? {} : { fulfillmentStatus: status }) };
}

const STATUS_OPTIONS: Record<DeliveryKind, { value: DeliveryStatusFilter; label: string }[]> = {
  ALL: [
    { value: 'ALL', label: 'Todos' },
    { value: 'PENDING', label: 'Pendentes' },
    { value: 'OUT_FOR_DELIVERY', label: 'Saiu para entrega' },
    { value: 'DELIVERED', label: 'Entregues' },
    { value: 'PICKED_UP', label: 'Retirados' },
  ],
  DELIVERY: [
    { value: 'ALL', label: 'Todos' },
    { value: 'PENDING', label: 'Pendentes' },
    { value: 'OUT_FOR_DELIVERY', label: 'Saiu para entrega' },
    { value: 'DELIVERED', label: 'Entregues' },
  ],
  PICKUP: [
    { value: 'ALL', label: 'Todos' },
    { value: 'PENDING', label: 'Pendentes' },
    { value: 'PICKED_UP', label: 'Retirados' },
  ],
};

export default function DeliveryTab() {
  const c = useColors();
  const day = useCurrentBusinessDay();
  const open = !!day.data;
  const [kind, setKind] = useState<DeliveryKind>('ALL');
  const [status, setStatus] = useState<DeliveryStatusFilter>('ALL');
  const effectiveStatus = STATUS_OPTIONS[kind].some((o) => o.value === status) ? status : 'ALL';
  const orders = useOrders(deliveryFilters(kind, effectiveStatus), open);
  const canCreate = useCanWrite('orders.create');
  const items = useMemo(() => orders.data?.pages.flatMap((p) => p.items) ?? [], [orders.data]);

  if (day.isLoading) return <View style={{ padding: space.lg }}><Skeleton /></View>;
  if (!open) {
    return (
      <View style={{ flex: 1, padding: space.lg, backgroundColor: c.bg }}>
        <NoBusinessDayCard />
      </View>
    );
  }

  return (
    <View style={{ flex: 1, backgroundColor: c.bg }}>
      <View style={[{ flex: 1 }, WEB_CONTENT_MAX]}>
        <FlashList<OrderSummary>
          data={items}
          keyExtractor={(o) => o.id}
          contentContainerStyle={{ padding: space.lg, paddingBottom: 96 }}
          ItemSeparatorComponent={() => <View style={{ height: space.sm }} />}
          refreshControl={<RefreshControl refreshing={orders.isRefetching} onRefresh={() => void orders.refetch()} />}
          onEndReached={() => orders.hasNextPage && !orders.isFetchingNextPage && void orders.fetchNextPage()}
          ListHeaderComponent={
            <View style={{ gap: space.md, marginBottom: space.md }}>
              {/* ADR-0046 item 21 complementado pelo ADR-0059 item 10: os pedidos do site da lanchonete aguardando aceite. */}
              <NewWebOrdersSection filters={DELIVERY_SCOPE} />
              <SegmentedControl
                value={kind}
                onChange={setKind}
                options={[
                  { value: 'ALL', label: 'Todos' },
                  { value: 'DELIVERY', label: 'Entrega' },
                  { value: 'PICKUP', label: 'Retirada' },
                ]}
              />
              <Row style={{ flexWrap: 'wrap' }}>
                {STATUS_OPTIONS[kind].map((o) => (
                  <Chip key={o.value} label={o.label} selected={effectiveStatus === o.value} onPress={() => setStatus(o.value)} />
                ))}
              </Row>
            </View>
          }
          ListEmptyComponent={
            orders.isLoading ? (
              <Skeleton />
            ) : orders.isError ? (
              <ErrorState message={errorMessage(orders.error)} onRetry={() => void orders.refetch()} />
            ) : (
              <EmptyState icon="bicycle-outline" title={effectiveStatus === 'ALL' ? 'Nenhum pedido de entrega ou retirada ainda' : 'Nenhum pedido neste filtro'} />
            )
          }
          renderItem={({ item }) => <OrderCard order={item} />}
        />
      </View>
      {canCreate ? <Fab label="Novo pedido" icon="call-outline" onPress={() => router.push('/orders/new/delivery')} /> : null}
    </View>
  );
}
