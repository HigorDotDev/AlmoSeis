/** Componentes de pedido: card da lista, avisos de expediente e de cardápio. */
import { router } from 'expo-router';
import { View } from 'react-native';

import { errorMessage } from '@/api/errors';
import type { MenuSources, MenuType, OrderSummary } from '@/api/types';
import {
  AlertDot,
  Banner,
  Button,
  Card,
  EmptyState,
  PaymentBadge,
  ProductionBadge,
  Row,
  SyncStatePill,
  Text,
  space,
} from '@/components/ui';
import { useCurrentBusinessDay } from '@/features/businessDay/hooks';
import { useCloneMenu } from '@/features/menu/hooks';
import { formatBRL } from '@/lib/money';
import { fulfillmentNames } from '@/lib/strings';
import { formatTime } from '@/lib/dates';
import { useBlockedBy } from '@/stores/pendingOps';
import { usePermission, useSession } from '@/stores/session';

export function orderLabel(o: Pick<OrderSummary, 'customerName' | 'tableLabel' | 'tabNumber'>): string {
  if (o.tableLabel) return `Mesa ${o.tableLabel}${o.tabNumber ? ` · Comanda ${o.tabNumber}` : ''}`;
  if (o.tabNumber) return `Comanda ${o.tabNumber}`;
  return o.customerName ?? 'Sem nome';
}

/**
 * Número, cliente, atendimento, total, selo financeiro, selo de produção e alerta (08 §8.9).
 * Cliente e total em destaque; nº, hora e atendimento numa linha só (docs/10-ux.md D8). O tipo já vem da aba.
 */
export function OrderCard({ order }: { order: OrderSummary }) {
  const timeZone = useSession((s) => s.me?.tenant.timeZone ?? 'America/Sao_Paulo');
  const pending = useBlockedBy(`/orders/${order.id}`);
  const canceled = order.cancellationStatus === 'CANCELED';
  return (
    <Card onPress={() => router.push(`/orders/${order.id}`)} accessibilityLabel={`Pedido ${order.orderNumber}, ${orderLabel(order)}`} style={canceled ? { opacity: 0.6 } : undefined}>
      <Row style={{ justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <Row style={{ flex: 1 }}>
          <Text variant="title" numberOfLines={1} style={{ flexShrink: 1 }}>
            {orderLabel(order)}
          </Text>
          <AlertDot count={order.pendingAlertCount} />
        </Row>
        <Text variant="title">{formatBRL(order.totalAmount)}</Text>
      </Row>
      <Text muted style={{ fontWeight: '500' }}>
        #{order.orderNumber} · {formatTime(order.createdAt, timeZone)} · {fulfillmentNames[order.fulfillmentType]} · {order.itemCount}{' '}
        {order.itemCount === 1 ? 'item' : 'itens'}
      </Text>
      <Row style={{ flexWrap: 'wrap' }}>
        <PaymentBadge totalAmount={order.totalAmount} paidAmount={order.paidAmount} isPaid={order.isPaid} canceled={canceled} />
        {!canceled ? <ProductionBadge status={order.productionStatus} /> : null}
        {pending ? <SyncStatePill state="unknown" /> : null}
      </Row>
    </Card>
  );
}

/** Sem expediente aberto: abrir (com `cash.open`) ou pedir ao caixa (08 §8.8). */
export function NoBusinessDayCard() {
  const canOpen = usePermission('cash.open');
  return (
    <Card>
      <EmptyState
        icon="lock-closed-outline"
        title="Nenhum expediente aberto"
        text={canOpen ? 'Abra o expediente para registrar pedidos e pagamentos.' : 'Peça para o caixa abrir o expediente.'}
        action={canOpen ? <Button title="Abrir expediente" icon="lock-open-outline" onPress={() => router.push('/business-day/open')} /> : undefined}
      />
    </Card>
  );
}

/** 404 MenuNotFound (RN-003.2): reaproveitar ontem, semana passada, modelo semanal ou montar agora. */
export function MenuMissingCard({ menuType, sources, businessDate }: { menuType: MenuType; sources: MenuSources; businessDate: string | null }) {
  const canManage = usePermission('menu.manage');
  const clone = useCloneMenu();
  const day = useCurrentBusinessDay();
  const date = businessDate ?? day.data?.businessDate ?? null;
  const use = (id: string) => date && clone.mutate({ id, body: { kind: 'DAILY', date, dayOfWeek: null } });
  const label = menuType === 'MARMITA' ? 'marmitas' : 'PF';
  if (!canManage) {
    return <Banner tone="warning" text={`Ainda não há cardápio de ${label} para hoje. Peça a um gerente para montar.`} />;
  }
  return (
    <Card>
      <Text variant="subtitle">Ainda não há cardápio de {label} para hoje</Text>
      <View style={{ gap: space.sm }}>
        {sources.yesterdayMenuId ? <Button title="Usar o de ontem" variant="secondary" loading={clone.isPending} onPress={() => use(sources.yesterdayMenuId!)} /> : null}
        {sources.lastWeekSameDayMenuId ? (
          <Button title="Usar o da semana passada" variant="secondary" loading={clone.isPending} onPress={() => use(sources.lastWeekSameDayMenuId!)} />
        ) : null}
        {sources.weeklyTemplateId ? <Button title="Usar modelo semanal" variant="secondary" loading={clone.isPending} onPress={() => use(sources.weeklyTemplateId!)} /> : null}
        <Button title="Montar agora" icon="add" onPress={() => router.push({ pathname: '/menu/menus/today', params: { menuType } })} />
      </View>
      {clone.error ? <Banner tone="danger" text={errorMessage(clone.error)} /> : null}
    </Card>
  );
}
