import { FlashList } from '@shopify/flash-list';
import { router } from 'expo-router';
import { useMemo, useState } from 'react';
import { RefreshControl, View } from 'react-native';

import { errorMessage } from '@/api/errors';
import type { OrderSummary } from '@/api/types';
import { EmptyState, ErrorState, Fab, SegmentedControl, Skeleton, space, useColors, WEB_CONTENT_MAX } from '@/components/ui';
import { useCurrentBusinessDay } from '@/features/businessDay/hooks';
import { useCurrentMenu } from '@/features/menu/hooks';
import { ALaCarteComingSoon } from '@/features/tenant/ComingSoon';
import { salaoKinds, type SalaoKind } from '@/features/tenant/establishment';
import { useCanWrite, useSession } from '@/stores/session';

import { MenuMissingCard, NoBusinessDayCard, OrderCard } from '../components';
import { useOrders } from '../hooks';

const kindLabels: Record<SalaoKind, string> = { PF: 'PF', KILO: 'Quilo', A_LA_CARTE: 'Comandas' };

/**
 * 08 §8.9 Aba Salão: as sub-abas seguem os módulos (ADR-0055 itens 2 a 4). Restaurante: PF e Quilo. Lanchonete:
 * só as comandas, sem sub-abas. Os dois: PF, Quilo e Comandas. As comandas à la carte chegam na fatia 3.
 */
export default function SalaoTab() {
  const me = useSession((s) => s.me);
  const kinds = salaoKinds(me);
  const [picked, setPicked] = useState<SalaoKind | null>(null);
  const type = picked && kinds.includes(picked) ? picked : (kinds[0] ?? 'PF');
  const segments = kinds.length > 1 ? kinds.map((k) => ({ value: k, label: kindLabels[k] })) : null;
  if (type === 'A_LA_CARTE') return <ALaCarteView segments={segments} onChange={setPicked} />;
  return <SalaoOrders type={type} segments={segments} onChange={setPicked} />;
}

type Segments = { value: SalaoKind; label: string }[] | null;

function ALaCarteView({ segments, onChange }: { segments: Segments; onChange: (k: SalaoKind) => void }) {
  const c = useColors();
  return (
    <View style={{ flex: 1, backgroundColor: c.bg }}>
      {segments ? (
        <View style={[{ padding: space.lg, paddingBottom: 0 }, WEB_CONTENT_MAX]}>
          <SegmentedControl value="A_LA_CARTE" onChange={onChange} options={segments} />
        </View>
      ) : null}
      <ALaCarteComingSoon />
    </View>
  );
}

/** Comandas de PF e de quilo do expediente, as não pagas primeiro. */
function SalaoOrders({ type, segments, onChange }: { type: 'PF' | 'KILO'; segments: Segments; onChange: (k: SalaoKind) => void }) {
  const c = useColors();
  const day = useCurrentBusinessDay();
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
      <View style={[{ flex: 1 }, WEB_CONTENT_MAX]}>
        <FlashList<OrderSummary>
          data={items}
          keyExtractor={(o) => o.id}
          contentContainerStyle={{ padding: space.lg, paddingBottom: 160 }}
          ItemSeparatorComponent={() => <View style={{ height: space.sm }} />}
          refreshControl={<RefreshControl refreshing={orders.isRefetching} onRefresh={() => void orders.refetch()} />}
          onEndReached={() => orders.hasNextPage && !orders.isFetchingNextPage && void orders.fetchNextPage()}
          ListHeaderComponent={
            <View style={{ gap: space.md, marginBottom: space.md }}>
              {segments ? <SegmentedControl value={type} onChange={onChange} options={segments} /> : null}
              {type === 'PF' && menu.data?.missing ? <MenuMissingCard menuType="PF" sources={menu.data.missing.sources} businessDate={menu.data.missing.businessDate} /> : null}
            </View>
          }
          ListEmptyComponent={
            orders.isLoading ? <Skeleton /> : orders.isError ? <ErrorState message={errorMessage(orders.error)} onRetry={() => void orders.refetch()} /> : <EmptyState title="Nenhuma comanda aberta" />
          }
          renderItem={({ item }) => <OrderCard order={item} />}
        />
      </View>
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
