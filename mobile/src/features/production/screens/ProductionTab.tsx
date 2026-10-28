import { useAudioPlayer } from 'expo-audio';
import { useKeepAwake } from 'expo-keep-awake';
import { useEffect, useState } from 'react';
import { Animated, RefreshControl, ScrollView, View, useWindowDimensions } from 'react-native';

import { errorMessage, isApiError } from '@/api/errors';
import type { ProductionOrder, ProductionStatus } from '@/api/types';
import {
  AlertDot,
  Banner,
  Button,
  Card,
  ConfirmDialog,
  EmptyState,
  ErrorState,
  Row,
  Skeleton,
  Text,
  space,
  useColors,
} from '@/components/ui';
import { useCurrentBusinessDay } from '@/features/businessDay/hooks';
import { useAcknowledgeAlerts, useOrderAlerts, useProduction, useSetProductionStatus } from '@/features/orders/hooks';
import { onRealtimeEvent } from '@/features/realtime/realtime';
import { relativeTime } from '@/lib/dates';
import { productionNames } from '@/lib/strings';
import { useIsOnline } from '@/stores/connectivity';
import { usePreferences } from '@/stores/preferences';
import { useCanWrite, useSession } from '@/stores/session';

const ORDER: ProductionStatus[] = ['OPEN', 'IN_PROGRESS', 'COMPLETED'];
const NEXT: Record<ProductionStatus, ProductionStatus | null> = { OPEN: 'IN_PROGRESS', IN_PROGRESS: 'COMPLETED', COMPLETED: null };
const PREV: Record<ProductionStatus, ProductionStatus | null> = { OPEN: null, IN_PROGRESS: 'OPEN', COMPLETED: 'IN_PROGRESS' };
const ADVANCE_LABEL: Record<ProductionStatus, string> = { OPEN: 'Iniciar', IN_PROGRESS: 'Concluir', COMPLETED: '' };

/** 08 §8.9 Aba Pedidos (produção): Abertos → Em produção → Concluídos, do mais antigo ao mais novo. */
export default function ProductionTab() {
  useKeepAwake();
  const c = useColors();
  const { width } = useWindowDimensions();
  const day = useCurrentBusinessDay();
  const board = useProduction(day.data?.id, !!day.data);
  const ownId = useSession((s) => s.me?.employee.id);
  const sound = usePreferences((s) => s.orderSound);
  const player = useAudioPlayer(require('../../../../assets/sounds/new-order.wav'));
  const [flash, setFlash] = useState<Set<string>>(new Set());
  const [showCompleted, setShowCompleted] = useState(false);

  // ADR-0036: aviso sonoro e destaque para pedido novo ou mudança que afeta a cozinha, exceto as próprias (RN-008.6).
  useEffect(
    () =>
      onRealtimeEvent((e) => {
        if (e.actorEmployeeId && e.actorEmployeeId === ownId) return;
        const affects = e.eventType === 'OrderCreated' || (e.eventType === 'OrderAlertCreated' && e.data?.affectsProduction === true);
        if (!affects) return;
        if (e.data?.orderType === 'KILO') return;
        if (sound) {
          player.seekTo(0);
          player.play();
        }
        if (e.entityId) setFlash((s) => new Set(s).add(e.entityId!));
      }),
    [ownId, sound, player],
  );

  if (day.isLoading) return <View style={{ padding: space.lg }}><Skeleton /></View>;
  if (!day.data) return <EmptyState icon="moon-outline" title="Nenhum expediente aberto" />;
  if (board.isLoading) return <View style={{ padding: space.lg }}><Skeleton lines={4} /></View>;
  if (board.isError) {
    if (isApiError(board.error, 'BusinessDayRequired')) return <EmptyState icon="moon-outline" title="Nenhum expediente aberto" />;
    return <ErrorState message={errorMessage(board.error)} onRetry={() => void board.refetch()} />;
  }

  const groups = board.data!.groups;
  const wide = width >= 720;
  const column = (status: ProductionStatus) => {
    const orders = groups[status];
    const collapsed = status === 'COMPLETED' && !showCompleted && !wide;
    return (
      <View key={status} style={{ flex: wide ? 1 : undefined, gap: space.sm }}>
        <Row style={{ justifyContent: 'space-between' }}>
          <Text variant="subtitle">
            {productionNames[status]} ({orders.length})
          </Text>
          {status === 'COMPLETED' && !wide ? <Button title={showCompleted ? 'Recolher' : 'Mostrar'} variant="ghost" onPress={() => setShowCompleted(!showCompleted)} /> : null}
        </Row>
        {collapsed ? null : orders.length === 0 ? <Text muted>Nenhum.</Text> : orders.map((o) => <ProductionCard key={o.id} order={o} status={status} flash={flash.has(o.id)} onFlashed={() => setFlash((s) => { const n = new Set(s); n.delete(o.id); return n; })} />)}
      </View>
    );
  };

  return (
    <ScrollView
      style={{ flex: 1, backgroundColor: c.bg }}
      contentContainerStyle={{ padding: space.lg, gap: space.lg, flexDirection: wide ? 'row' : 'column', alignItems: wide ? 'flex-start' : undefined }}
      refreshControl={<RefreshControl refreshing={board.isRefetching} onRefresh={() => void board.refetch()} />}
    >
      {ORDER.map(column)}
    </ScrollView>
  );
}

function ProductionCard({ order: o, status, flash, onFlashed }: { order: ProductionOrder; status: ProductionStatus; flash: boolean; onFlashed: () => void }) {
  const c = useColors();
  const scale = usePreferences((s) => s.kitchenFontScale);
  const canUpdate = useCanWrite('production.update');
  const online = useIsOnline();
  const setStatus = useSetProductionStatus(o.id);
  const [target, setTarget] = useState<ProductionStatus | null>(null);
  const [showAlerts, setShowAlerts] = useState(false);
  const [opacity] = useState(() => new Animated.Value(1));

  // O card pisca uma vez (ADR-0036).
  useEffect(() => {
    if (!flash) return;
    Animated.sequence([
      Animated.timing(opacity, { toValue: 0.3, duration: 250, useNativeDriver: true }),
      Animated.timing(opacity, { toValue: 1, duration: 250, useNativeDriver: true }),
    ]).start(onFlashed);
  }, [flash, opacity, onFlashed]);

  const next = NEXT[status];
  const prev = PREV[status];
  const big = { fontSize: 17 * scale };

  return (
    <Animated.View style={{ opacity }}>
      <Card style={o.pendingAlertCount > 0 ? { borderColor: c.alert, borderWidth: 2 } : undefined}>
        <Row style={{ justifyContent: 'space-between' }}>
          <Row>
            <Text style={{ fontSize: 26 * scale, fontWeight: '800' }} maxFontSizeMultiplier={1.3}>
              #{o.orderNumber}
            </Text>
            <AlertDot count={o.pendingAlertCount} />
          </Row>
          <Text muted maxFontSizeMultiplier={1.3}>
            {relativeTime(o.createdAt)}
          </Text>
        </Row>
        <Text style={[big, { fontWeight: '700' }]} maxFontSizeMultiplier={1.3}>
          {o.label ?? (o.orderType === 'PF' ? 'Salão' : 'Marmita')}
          {o.fulfillmentType === 'DELIVERY' ? ' · Entrega' : o.fulfillmentType === 'PICKUP' ? ' · Retirada' : ''}
        </Text>
        {o.items.map((i) => (
          <View key={i.id}>
            <Text style={[big, !i.requiresPreparation && { color: c.textMuted }]} maxFontSizeMultiplier={1.3}>
              {i.quantity}× {i.name}
            </Text>
            {i.composition ? <Text maxFontSizeMultiplier={1.3}>{i.composition}</Text> : null}
            {i.addons.map((a) => (
              <Text key={a.id} maxFontSizeMultiplier={1.3}>
                + {a.quantity}× {a.name}
              </Text>
            ))}
            {i.notes ? <Text style={[big, { color: c.danger, fontWeight: '700' }]} maxFontSizeMultiplier={1.3}>⚠ {i.notes}</Text> : null}
          </View>
        ))}
        {o.notes ? <Text style={{ color: c.danger, fontWeight: '700' }}>Obs.: {o.notes}</Text> : null}
        {o.pendingAlertCount > 0 ? <Button title={showAlerts ? 'Esconder mudanças' : 'Ver mudanças'} variant="secondary" onPress={() => setShowAlerts(!showAlerts)} /> : null}
        {showAlerts ? <InlineAlerts orderId={o.id} onDone={() => setShowAlerts(false)} /> : null}
        {canUpdate ? (
          <Row>
            {next ? <Button title={ADVANCE_LABEL[status]} onPress={() => setTarget(next)} disabled={!online} style={{ flex: 1 }} /> : null}
            {prev ? <Button title="Voltar status" variant="secondary" onPress={() => setTarget(prev)} disabled={!online} style={{ flex: next ? undefined : 1 }} /> : null}
          </Row>
        ) : null}
      </Card>
      <ConfirmDialog
        visible={target !== null}
        title={target ? `${productionNames[target]}?` : ''}
        lines={[
          { label: 'Pedido', value: `#${o.orderNumber}` },
          { label: 'Status', value: target ? `${productionNames[status]} → ${productionNames[target]}` : '' },
        ]}
        warning={setStatus.error}
        confirmLabel={target === NEXT[status] ? ADVANCE_LABEL[status] : 'Voltar status'}
        loading={setStatus.busy}
        onCancel={() => setTarget(null)}
        onConfirm={async () => {
          const outcome = await setStatus.run({ status: target!, version: o.version });
          if (outcome.kind !== 'rejected') setTarget(null);
        }}
      />
    </Animated.View>
  );
}

function InlineAlerts({ orderId, onDone }: { orderId: string; onDone: () => void }) {
  const alerts = useOrderAlerts(orderId);
  const ack = useAcknowledgeAlerts(orderId);
  if (alerts.isLoading) return <Skeleton lines={1} />;
  return (
    <View style={{ gap: space.xs }}>
      {alerts.data?.map((a) => (
        <Text key={a.eventId} style={{ fontWeight: '700' }}>
          • {a.summary}
        </Text>
      ))}
      {ack.error ? <Banner tone="danger" text={errorMessage(ack.error)} /> : null}
      {alerts.data?.length ? <Button title="Ciente" icon="checkmark" loading={ack.isPending} onPress={() => ack.mutate(alerts.data!.map((a) => a.eventId), { onSuccess: onDone })} /> : null}
    </View>
  );
}
