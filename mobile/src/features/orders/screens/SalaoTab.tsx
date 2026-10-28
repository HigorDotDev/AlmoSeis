import { FlashList } from '@shopify/flash-list';
import { router } from 'expo-router';
import { useMemo, useState } from 'react';
import { RefreshControl, View } from 'react-native';

import { errorMessage } from '@/api/errors';
import type { OrderSummary } from '@/api/types';
import { EmptyState, ErrorState, Fab, SegmentedControl, Skeleton, space, useColors } from '@/components/ui';
import { useCurrentBusinessDay } from '@/features/businessDay/hooks';
import { useCurrentMenu } from '@/features/menu/hooks';
import { useCanWrite } from '@/stores/session';

import { MenuMissingCard, NoBusinessDayCard, OrderCard } from '../components';
import { useOrders } from '../hooks';

/** 08 §8.9 Aba Salão: comandas abertas do expediente, as não pagas primeiro. */
export default function SalaoTab() {
  const c = useColors();
  const day = useCurrentBusinessDay();
  const [type, setType] = useState<'PF' | 'KILO'>('PF');
  const open = !!day.data;
  const menu = useCurrentMenu('PF', open && type === 'PF');
  const orders = useOrders({ orderType: type, cancellationStatus: 'ACTIVE' }, open);
  const canCreate = useCanWrite('orders.create');

  const items = useMemo(() => {
    const all = orders.data?.pages.flatMap((p) => p.items) ?? [];
    return [...all.filter((o) => !o.isPaid), ...all.filter((o) => o.isPaid)];
  }, [orders.data]);

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
        contentContainerStyle={{ padding: space.lg, paddingBottom: 160 }}
        ItemSeparatorComponent={() => <View style={{ height: space.sm }} />}
        refreshControl={<RefreshControl refreshing={orders.isRefetching} onRefresh={() => void orders.refetch()} />}
        onEndReached={() => orders.hasNextPage && !orders.isFetchingNextPage && void orders.fetchNextPage()}
        ListHeaderComponent={
          <View style={{ gap: space.md, marginBottom: space.md }}>
            <SegmentedControl
              value={type}
              onChange={setType}
              options={[
                { value: 'PF', label: 'PF' },
                { value: 'KILO', label: 'Quilo' },
              ]}
            />
            {type === 'PF' && menu.data?.missing ? <MenuMissingCard menuType="PF" sources={menu.data.missing.sources} businessDate={menu.data.missing.businessDate} /> : null}
          </View>
        }
        ListEmptyComponent={
          orders.isLoading ? <Skeleton /> : orders.isError ? <ErrorState message={errorMessage(orders.error)} onRetry={() => void orders.refetch()} /> : <EmptyState title="Nenhuma comanda aberta" />
        }
        renderItem={({ item }) => <OrderCard order={item} />}
      />
      {/* Um botão só: a ação segue o filtro (docs/10-ux.md D6). */}
      {canCreate ? (
        type === 'PF' ? (
          <Fab label="Nova mesa" icon="restaurant-outline" onPress={() => router.push('/orders/new/pf')} />
        ) : (
          <Fab label="Nova comanda" icon="scale-outline" onPress={() => router.push('/orders/new/kilo')} />
        )
      ) : null}
    </View>
  );
}
