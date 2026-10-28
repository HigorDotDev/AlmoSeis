import { FlashList } from '@shopify/flash-list';
import { router } from 'expo-router';
import { useMemo, useState } from 'react';
import { RefreshControl, View } from 'react-native';

import { errorMessage } from '@/api/errors';
import type { OrderSummary } from '@/api/types';
import { EmptyState, ErrorState, Fab, SegmentedControl, Skeleton, TextField, space, useColors } from '@/components/ui';
import { useCurrentBusinessDay } from '@/features/businessDay/hooks';
import { useCurrentMenu } from '@/features/menu/hooks';
import { useCanWrite } from '@/stores/session';

import { MenuMissingCard, NoBusinessDayCard, OrderCard } from '../components';
import { useOrders } from '../hooks';

type Filter = 'ALL' | 'UNPAID' | 'DELIVERY' | 'PICKUP';

/** 08 §8.9 Aba Marmitas. */
export default function MarmitasTab() {
  const c = useColors();
  const day = useCurrentBusinessDay();
  const [filter, setFilter] = useState<Filter>('ALL');
  const [search, setSearch] = useState('');
  const open = !!day.data;
  const menu = useCurrentMenu('MARMITA', open);
  const orders = useOrders({ orderType: 'MARMITA', isPaid: filter === 'UNPAID' ? false : undefined, search: search.trim() || undefined }, open);
  const canCreate = useCanWrite('orders.create');

  // A API não filtra por entrega/retirada: filtra no que já veio.
  const items = useMemo(() => {
    const all = orders.data?.pages.flatMap((p) => p.items) ?? [];
    return filter === 'DELIVERY' || filter === 'PICKUP' ? all.filter((o) => o.fulfillmentType === filter) : all;
  }, [orders.data, filter]);

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
      <FlashList<OrderSummary>
        data={items}
        keyExtractor={(o) => o.id}
        contentContainerStyle={{ padding: space.lg, paddingBottom: 96 }}
        ItemSeparatorComponent={() => <View style={{ height: space.sm }} />}
        refreshControl={<RefreshControl refreshing={orders.isRefetching} onRefresh={() => void orders.refetch()} />}
        onEndReached={() => orders.hasNextPage && !orders.isFetchingNextPage && void orders.fetchNextPage()}
        ListHeaderComponent={
          <View style={{ gap: space.md, marginBottom: space.md }}>
            {menu.data?.missing ? <MenuMissingCard menuType="MARMITA" sources={menu.data.missing.sources} businessDate={menu.data.missing.businessDate} /> : null}
            <SegmentedControl
              value={filter}
              onChange={setFilter}
              options={[
                { value: 'ALL', label: 'Todos' },
                { value: 'UNPAID', label: 'A pagar' },
                { value: 'DELIVERY', label: 'Entrega' },
                { value: 'PICKUP', label: 'Retirada' },
              ]}
            />
            <TextField label="Buscar por nome" value={search} onChangeText={setSearch} placeholder="Nome do cliente" />
          </View>
        }
        ListEmptyComponent={
          orders.isLoading ? <Skeleton /> : orders.isError ? <ErrorState message={errorMessage(orders.error)} onRetry={() => void orders.refetch()} /> : <EmptyState title="Nenhuma marmita ainda" />
        }
        renderItem={({ item }) => <OrderCard order={item} />}
      />
      {canCreate ? <Fab label="Nova marmita" icon="add" onPress={() => router.push('/orders/new/marmita')} /> : null}
    </View>
  );
}
