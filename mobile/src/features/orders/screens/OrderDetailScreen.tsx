import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useState } from 'react';
import { RefreshControl, ScrollView, View } from 'react-native';

import { errorMessage } from '@/api/errors';
import type { Order, OrderItem } from '@/api/types';
import {
  Banner,
  BottomSheet,
  Button,
  Card,
  ConfirmDialog,
  ConflictSheet,
  Divider,
  ErrorState,
  IconButton,
  KeyValue,
  PaymentBadge,
  ProductionBadge,
  QuantityStepper,
  Row,
  Section,
  Skeleton,
  SyncStatePill,
  Text,
  TextField,
  space,
  useColors,
} from '@/components/ui';
import { formatBusinessDate, formatTime } from '@/lib/dates';
import { formatBRL } from '@/lib/money';
import { fulfillmentNames, orderTypeNames, paymentMethodNames } from '@/lib/strings';
import { gramsToKgLabel } from '@/lib/weight';
import { useIsOnline } from '@/stores/connectivity';
import { useBlockedBy } from '@/stores/pendingOps';
import { useCanWrite, useSession } from '@/stores/session';

import { orderLabel } from '../components';
import { summarizeOrder, useCancelOrder, useOrder, useRemoveItem, useRemoveWeighing, useUpdateItem } from '../hooks';

/** 08 §8.9 `orders/[id]`. */
export default function OrderDetailScreen() {
  const { id } = useLocalSearchParams<{ id: string }>();
  const c = useColors();
  const order = useOrder(id);
  const pending = useBlockedBy(`/orders/${id}`);

  if (order.isLoading) return <View style={{ padding: space.lg }}><Skeleton lines={5} /></View>;
  if (!order.data) return <ErrorState message={errorMessage(order.error)} onRetry={() => void order.refetch()} />;
  const o = order.data;

  return (
    <View style={{ flex: 1, backgroundColor: c.bg }}>
      <Stack.Screen options={{ title: `#${o.orderNumber} · ${orderTypeNames[o.orderType]}` }} />
      <ScrollView contentContainerStyle={{ padding: space.lg, gap: space.md, paddingBottom: 140 }} refreshControl={<RefreshControl refreshing={order.isRefetching} onRefresh={() => void order.refetch()} />}>
        <OrderHeader order={o} pending={!!pending} />
        <ItemsBlock order={o} />
        <FinancialBlock order={o} />
      </ScrollView>
      <ActionBar order={o} />
    </View>
  );
}

function OrderHeader({ order: o, pending }: { order: Order; pending: boolean }) {
  const timeZone = useSession((s) => s.me?.tenant.timeZone ?? 'America/Sao_Paulo');
  const canceled = o.cancellationStatus === 'CANCELED';
  return (
    <Card>
      <Text variant="title">
        #{o.orderNumber} · {orderTypeNames[o.orderType]} · {fulfillmentNames[o.fulfillmentType]}
      </Text>
      <Text>{orderLabel({ customerName: o.customer?.name ?? null, tableLabel: o.tableLabel, tabNumber: o.tabNumber })}</Text>
      {o.deliveryAddress ? (
        <Text variant="small" muted>
          {[o.deliveryAddress.street, o.deliveryAddress.number, o.deliveryAddress.complement, o.deliveryAddress.neighborhood].filter(Boolean).join(', ')}
          {o.deliveryAddress.reference ? ` · ${o.deliveryAddress.reference}` : ''}
        </Text>
      ) : null}
      <Text variant="small" muted>
        {formatBusinessDate(o.businessDate)} · {formatTime(o.createdAt, timeZone)} · por {o.createdBy.displayName}
      </Text>
      <Row style={{ flexWrap: 'wrap' }}>
        {!canceled ? <ProductionBadge status={o.productionStatus} /> : null}
        <PaymentBadge totalAmount={o.totalAmount} paidAmount={o.paidAmount} isPaid={o.isPaid} canceled={canceled} />
        {pending ? <SyncStatePill state="unknown" /> : null}
      </Row>
      {o.notes ? <Text>Obs.: {o.notes}</Text> : null}
      {canceled && o.cancellation ? <Banner tone="danger" text={`Cancelado por ${o.cancellation.canceledBy.displayName}: ${o.cancellation.reason}`} /> : null}
      {o.pendingAlertCount > 0 ? (
        <Banner tone="warning" text="Este pedido mudou." action={{ label: 'Ver mudanças', onPress: () => router.push(`/orders/${o.id}/alerts`) }} />
      ) : null}
      {o.isPaid && !canceled ? <Banner tone="success" text="Pedido quitado. Para corrigir, estorne um pagamento." /> : null}
    </Card>
  );
}

function ItemsBlock({ order: o }: { order: Order }) {
  const canEdit = useCanWrite('orders.edit');
  const online = useIsOnline();
  const editable = canEdit && online && !o.isPaid && o.cancellationStatus === 'ACTIVE';
  const [editing, setEditing] = useState<OrderItem | null>(null);
  const [removing, setRemoving] = useState<{ kind: 'item' | 'weighing'; id: string; label: string } | null>(null);
  const [conflict, setConflict] = useState<Order | null>(null);
  const update = useUpdateItem(o.id);
  const removeItem = useRemoveItem(o.id);
  const removeWeighing = useRemoveWeighing(o.id);

  const onOutcome = (outcome: { kind: string; current?: unknown }) => {
    if (outcome.kind === 'conflict' && outcome.current) setConflict(outcome.current as Order);
  };

  return (
    <Card>
      <Text variant="subtitle">Itens</Text>
      {o.items.map((item) => (
        <View key={item.id} style={{ gap: 2 }}>
          <Row style={{ justifyContent: 'space-between', alignItems: 'flex-start' }}>
            <View style={{ flex: 1 }}>
              <Text style={{ fontWeight: '700' }}>
                {item.quantity}× {item.name}
              </Text>
              {item.composition ? <Text variant="small" muted>{item.composition}</Text> : null}
              {item.addons.map((a) => (
                <Text key={a.id} variant="small" muted>
                  + {a.quantity}× {a.name} ({formatBRL(a.lineTotal)})
                </Text>
              ))}
              {item.notes ? <Text variant="small">Obs.: {item.notes}</Text> : null}
            </View>
            <Text>{formatBRL(item.lineTotal)}</Text>
            {editable ? <IconButton icon="create-outline" label={`Alterar ${item.name}`} onPress={() => setEditing(item)} /> : null}
          </Row>
          <Divider />
        </View>
      ))}
      {o.weighings.length ? <Text variant="subtitle">Pesagens</Text> : null}
      {o.weighings.map((w) => (
        <Row key={w.id} style={{ justifyContent: 'space-between' }}>
          <Text>
            {gramsToKgLabel(w.weightGrams)} × {formatBRL(w.pricePerKg)}/kg
          </Text>
          <Text>{formatBRL(w.amount)}</Text>
          {editable ? <IconButton icon="trash-outline" label="Remover pesagem" onPress={() => setRemoving({ kind: 'weighing', id: w.id, label: gramsToKgLabel(w.weightGrams) })} /> : null}
        </Row>
      ))}

      <BottomSheet visible={editing !== null} title={editing ? editing.name : ''} onClose={() => setEditing(null)}>
        {editing ? (
          <EditItem
            item={editing}
            busy={update.busy}
            error={update.error}
            onSave={async (body) => {
              const outcome = await update.run({ itemId: editing.id, body, version: o.version });
              onOutcome(outcome as never);
              if (outcome.kind === 'confirmed') setEditing(null);
            }}
            onRemove={() => {
              setRemoving({ kind: 'item', id: editing.id, label: `${editing.quantity}× ${editing.name}` });
              setEditing(null);
            }}
          />
        ) : null}
      </BottomSheet>

      <ConfirmDialog
        visible={removing !== null}
        title={removing?.kind === 'item' ? 'Remover item?' : 'Remover pesagem?'}
        lines={removing ? [{ label: removing.kind === 'item' ? 'Item' : 'Pesagem', value: removing.label }] : []}
        warning={removeItem.error ?? removeWeighing.error}
        confirmLabel="Remover"
        danger
        loading={removeItem.busy || removeWeighing.busy}
        onCancel={() => setRemoving(null)}
        onConfirm={async () => {
          const outcome =
            removing!.kind === 'item'
              ? await removeItem.run({ itemId: removing!.id, version: o.version })
              : await removeWeighing.run({ weighingId: removing!.id, version: o.version });
          onOutcome(outcome as never);
          if (outcome.kind !== 'rejected') setRemoving(null);
        }}
      />
      <ConflictSheet visible={conflict !== null} summary={conflict ? summarizeOrder(conflict) : []} onClose={() => setConflict(null)} onRedo={() => setConflict(null)} />
    </Card>
  );
}

function EditItem({ item, busy, error, onSave, onRemove }: { item: OrderItem; busy: boolean; error: string | null; onSave: (b: { quantity?: number; notes?: string | null }) => void; onRemove: () => void }) {
  const [quantity, setQuantity] = useState(item.quantity);
  const [notes, setNotes] = useState(item.notes ?? '');
  const [confirm, setConfirm] = useState(false);
  return (
    <>
      <Row style={{ justifyContent: 'space-between' }}>
        <Text>Quantidade</Text>
        <QuantityStepper label="Quantidade" min={1} value={quantity} onChange={setQuantity} />
      </Row>
      <TextField label="Observação" value={notes} onChangeText={setNotes} />
      {error ? <Banner tone="danger" text={error} /> : null}
      <Button title="Salvar alteração" onPress={() => setConfirm(true)} disabled={quantity === item.quantity && notes === (item.notes ?? '')} />
      <Button title="Remover item" variant="danger" onPress={onRemove} />
      <ConfirmDialog
        visible={confirm}
        title="Alterar item?"
        lines={[
          { label: item.name, value: quantity !== item.quantity ? `${item.quantity} → ${quantity}` : `${quantity}` },
          ...(notes !== (item.notes ?? '') ? [{ label: 'Observação', value: notes || '(sem)' }] : []),
        ]}
        confirmLabel="Salvar alteração"
        loading={busy}
        onCancel={() => setConfirm(false)}
        onConfirm={() => {
          setConfirm(false);
          onSave({ quantity: quantity !== item.quantity ? quantity : undefined, notes: notes !== (item.notes ?? '') ? notes || null : undefined });
        }}
      />
    </>
  );
}

function FinancialBlock({ order: o }: { order: Order }) {
  const c = useColors();
  return (
    <Card>
      <Text variant="subtitle">Valores</Text>
      <KeyValue label="Itens" value={formatBRL(o.itemsTotal)} />
      {o.weighings.length ? <KeyValue label="Pesagens" value={formatBRL(o.weighingsTotal)} /> : null}
      {o.fulfillmentType === 'DELIVERY' ? <KeyValue label="Entrega" value={formatBRL(o.deliveryFee)} /> : null}
      <KeyValue label="Total" value={formatBRL(o.totalAmount)} strong />
      <KeyValue label="Pago" value={formatBRL(o.paidAmount)} />
      <KeyValue label="Saldo" value={formatBRL(o.balance)} strong color={o.isPaid ? c.success : c.danger} />
      {o.payments.length ? (
        <Section title="Pagamentos" right={<Button title="Ver" variant="ghost" onPress={() => router.push(`/orders/${o.id}/pay`)} />}>
          {o.payments.map((p) => (
            <Row key={p.id} style={{ justifyContent: 'space-between' }}>
              <Text style={p.status === 'REVERSED' ? { textDecorationLine: 'line-through', color: c.textMuted } : undefined}>
                {paymentMethodNames[p.paymentMethod]}
                {p.label ? ` · ${p.label}` : ''} · {formatBusinessDate(p.businessDate)}
              </Text>
              <Text style={p.status === 'REVERSED' ? { textDecorationLine: 'line-through', color: c.textMuted } : undefined}>{formatBRL(p.amount)}</Text>
            </Row>
          ))}
        </Section>
      ) : null}
    </Card>
  );
}

function ActionBar({ order: o }: { order: Order }) {
  const c = useColors();
  const online = useIsOnline();
  const canEdit = useCanWrite('orders.edit');
  const canPay = useCanWrite('payments.register');
  const canCancel = useCanWrite('orders.cancel');
  const [menu, setMenu] = useState(false);
  const [cancelOpen, setCancelOpen] = useState(false);
  const active = o.cancellationStatus === 'ACTIVE';
  const editable = active && !o.isPaid && online;

  return (
    <View style={{ position: 'absolute', left: 0, right: 0, bottom: 0, backgroundColor: c.surface, borderTopWidth: 1, borderTopColor: c.border, padding: space.sm, gap: space.sm }}>
      <Row style={{ flexWrap: 'wrap' }}>
        {canPay && active && !o.isPaid ? <Button title="Receber" icon="cash-outline" disabled={!online} onPress={() => router.push(`/orders/${o.id}/pay`)} style={{ flex: 1 }} /> : null}
        {canEdit && editable && o.orderType !== 'KILO' ? <Button title="Adicionar itens" variant="secondary" onPress={() => router.push(`/orders/${o.id}/add-items`)} style={{ flex: 1 }} /> : null}
        {canEdit && editable && o.orderType === 'KILO' ? <Button title="Pesagem" icon="scale-outline" variant="secondary" onPress={() => router.push(`/orders/${o.id}/weighings`)} style={{ flex: 1 }} /> : null}
        <IconButton icon="ellipsis-vertical" label="Mais ações" onPress={() => setMenu(true)} />
      </Row>
      <BottomSheet visible={menu} title={`Pedido #${o.orderNumber}`} onClose={() => setMenu(false)}>
        {canEdit && active ? <Button title="Editar dados" variant="secondary" disabled={!online} onPress={() => { setMenu(false); router.push(`/orders/${o.id}/edit`); }} /> : null}
        {canEdit && active && !o.isPaid && o.orderType === 'KILO' ? (
          <Button title="Adicionar bebidas" variant="secondary" disabled={!online} onPress={() => { setMenu(false); router.push(`/orders/${o.id}/add-items`); }} />
        ) : null}
        <Button title="Histórico" variant="secondary" onPress={() => { setMenu(false); router.push(`/orders/${o.id}/history`); }} />
        {o.payments.length ? <Button title="Pagamentos e estornos" variant="secondary" onPress={() => { setMenu(false); router.push(`/orders/${o.id}/pay`); }} /> : null}
        {canCancel && active ? <Button title="Cancelar pedido" variant="danger" disabled={!online} onPress={() => { setMenu(false); setCancelOpen(true); }} /> : null}
      </BottomSheet>
      <CancelDialog order={o} visible={cancelOpen} onClose={() => setCancelOpen(false)} />
    </View>
  );
}

function CancelDialog({ order: o, visible, onClose }: { order: Order; visible: boolean; onClose: () => void }) {
  const [reason, setReason] = useState('');
  const cancel = useCancelOrder(o.id);
  const hasPayments = o.payments.some((p) => p.status === 'CONFIRMED');
  return (
    <ConfirmDialog
      visible={visible}
      title="Cancelar pedido?"
      description={hasPayments ? 'Este pedido tem pagamentos. Estorne os pagamentos antes de cancelar (ADR-0022).' : undefined}
      lines={[
        { label: 'Pedido', value: `#${o.orderNumber}` },
        { label: 'Total', value: formatBRL(o.totalAmount) },
      ]}
      warning={cancel.error}
      confirmLabel="Cancelar pedido"
      danger
      loading={cancel.busy}
      onCancel={onClose}
      onConfirm={async () => {
        if (reason.trim().length < 3) return;
        const outcome = await cancel.run({ reason: reason.trim(), version: o.version });
        if (outcome.kind === 'confirmed') onClose();
      }}
    >
      <TextField label="Motivo (obrigatório)" value={reason} onChangeText={setReason} placeholder="Ex.: cliente desistiu" />
      {hasPayments ? (
        <Button
          title="Ir para os pagamentos"
          variant="secondary"
          onPress={() => {
            onClose();
            router.push(`/orders/${o.id}/pay`);
          }}
        />
      ) : null}
    </ConfirmDialog>
  );
}
