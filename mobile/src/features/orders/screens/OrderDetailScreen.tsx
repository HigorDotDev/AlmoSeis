import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useState } from 'react';
import { RefreshControl, ScrollView, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';

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
  FulfillmentBadge,
  IconButton,
  KeyValue,
  PaymentBadge,
  ProductionBadge,
  QuantityStepper,
  Row,
  Section,
  SiteBadge,
  Skeleton,
  SyncStatePill,
  Text,
  TextField,
  space,
  useColors,
  WEB_CONTENT_MAX,
} from '@/components/ui';
import { formatBusinessDate, formatTime } from '@/lib/dates';
import { formatBRL, toCents } from '@/lib/money';
import { fulfillmentNames, fulfillmentStatusNames, orderTypeNames, paymentMethodNames, READY_FOR_PICKUP } from '@/lib/strings';
import { gramsToKgLabel } from '@/lib/weight';
import { useIsOnline } from '@/stores/connectivity';
import { useBlockedBy } from '@/stores/pendingOps';
import { useCanWrite, useSession } from '@/stores/session';

import { itemWithChildrenCents } from '../aLaCarte';
import { orderLabel } from '../components';
import { changeToCarryText } from '../delivery';
import { ChangeBelowTotalAlert } from '../deliveryComponents';
import { isDispatched } from '../fulfillment';
import { summarizeOrder, useCancelOrder, useOrder, useRemoveItem, useRemoveWeighing, useUpdateItem } from '../hooks';
import { AcceptanceCountdown, acceptanceOutcomeText, AcceptRejectActions, DistanceFeeLine, FulfillmentActions, PhoneLine, WebOrderPaymentInfo } from '../webOrderComponents';
import { orderNumberLabel } from '../webOrders';

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
      <Stack.Screen options={{ title: `${orderNumberLabel(o.orderNumber)} · ${orderTypeNames[o.orderType]}` }} />
      <ScrollView contentContainerStyle={[{ padding: space.lg, gap: space.md, paddingBottom: 140 }, WEB_CONTENT_MAX]} refreshControl={<RefreshControl refreshing={order.isRefetching} onRefresh={() => void order.refetch()} />}>
        <OrderHeader order={o} pending={!!pending} />
        <AcceptanceBlock order={o} />
        <FulfillmentBlock order={o} />
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
  const accepted = o.acceptanceStatus === 'ACCEPTED';
  return (
    <Card>
      <Text variant="title">
        {orderNumberLabel(o.orderNumber)} · {orderTypeNames[o.orderType]} · {fulfillmentNames[o.fulfillmentType]}
      </Text>
      <Text>{orderLabel({ customerName: o.customer?.name ?? null, tableLabel: o.tableLabel, tabNumber: o.tabNumber })}</Text>
      {o.customer?.phoneNumber ? <PhoneLine phone={o.customer.phoneNumber} unverified={o.origin === 'WEB'} /> : null}
      {o.deliveryAddress ? (
        <Text variant="small" muted>
          {[o.deliveryAddress.street, o.deliveryAddress.number, o.deliveryAddress.complement, o.deliveryAddress.neighborhood].filter(Boolean).join(', ')}
          {o.deliveryAddress.reference ? ` · ${o.deliveryAddress.reference}` : ''}
        </Text>
      ) : null}
      <DistanceFeeLine order={o} />
      <Text variant="small" muted>
        {formatBusinessDate(o.businessDate)} · {formatTime(o.createdAt, timeZone)} · {o.createdBy ? `por ${o.createdBy.displayName}` : 'pelo site'}
      </Text>
      <Row style={{ flexWrap: 'wrap' }}>
        {o.origin === 'WEB' ? <SiteBadge /> : null}
        {!canceled ? <ProductionBadge status={o.productionStatus} /> : null}
        {accepted ? <PaymentBadge totalAmount={o.totalAmount} paidAmount={o.paidAmount} isPaid={o.isPaid} canceled={canceled} /> : null}
        {!canceled ? <FulfillmentBadge fulfillmentType={o.fulfillmentType} status={o.fulfillmentStatus} isReadyForPickup={o.isReadyForPickup} /> : null}
        {pending ? <SyncStatePill state="unknown" /> : null}
      </Row>
      {o.notes ? <Text>Obs.: {o.notes}</Text> : null}
      {/* Forma prevista: do site (ADR-0044 item 19) e do delivery por telefone (ADR-0059 item 5). Não é pagamento. */}
      <WebOrderPaymentInfo order={o} />
      <ChangeToCarry order={o} />
      {canceled && o.cancellation ? <Banner tone="danger" text={`Cancelado por ${o.cancellation.canceledBy.displayName}: ${o.cancellation.reason}`} /> : null}
      {o.pendingAlertCount > 0 ? (
        <Banner tone="warning" text="Este pedido mudou." action={{ label: 'Ver mudanças', onPress: () => router.push(`/orders/${o.id}/alerts`) }} />
      ) : null}
      {o.isPaid && !canceled ? <Banner tone="success" text="Pedido quitado. Para corrigir, estorne um pagamento." /> : null}
    </Card>
  );
}

/**
 * "Levar troco para R$ 100,00 → troco R$ 43,00" (ADR-0059 item 12): `changeFor − balance`, só sem quitação. Com o
 * "troco para" abaixo do total (I2), o alerta com o atalho para editar, no lugar do troco.
 */
function ChangeToCarry({ order: o }: { order: Order }) {
  const text = o.cancellationStatus === 'ACTIVE' ? changeToCarryText(o) : null;
  if (!text) return <ChangeBelowTotalAlert order={o} />;
  return (
    <Text testID="change-to-carry" style={{ fontWeight: '700' }}>
      {text}
    </Text>
  );
}

/**
 * Aceite do pedido do site (ADR-0046): aguardando → prazo e "Aceitar"/"Recusar"; aceito → quem aceitou e o tempo
 * estimado; recusado, expirado ou desistência → o desfecho (o pedido não é venda e não aceita escrita).
 */
function AcceptanceBlock({ order: o }: { order: Order }) {
  const timeZone = useSession((s) => s.me?.tenant.timeZone ?? 'America/Sao_Paulo');
  if (o.origin !== 'WEB') return null;
  if (o.acceptanceStatus === 'AWAITING_ACCEPTANCE') {
    return (
      <Card>
        <Row style={{ justifyContent: 'space-between', flexWrap: 'wrap' }}>
          <Text variant="subtitle">Aguardando aceite</Text>
          <AcceptanceCountdown deadline={o.acceptanceDeadline} />
        </Row>
        <Text variant="small" muted>
          Até ser aceito, o pedido não tem número, não vai para a cozinha e não entra no fechamento.
        </Text>
        <AcceptRejectActions
          orderId={o.id}
          version={o.version}
          lines={[
            { label: 'Cliente', value: o.customer?.name ?? 'Sem nome' },
            { label: 'Atendimento', value: fulfillmentNames[o.fulfillmentType] },
            { label: 'Total', value: formatBRL(o.totalAmount) },
          ]}
        />
      </Card>
    );
  }
  if (o.acceptanceStatus === 'ACCEPTED') {
    if (!o.acceptedAt) return null;
    const who = o.acceptedBy ? ` por ${o.acceptedBy.displayName}` : '';
    const eta = o.estimatedReadyMinutes ? ` · pronto em ~${o.estimatedReadyMinutes} min` : '';
    return <Banner tone="info" text={`Pedido do site aceito${who} às ${formatTime(o.acceptedAt, timeZone)}${eta}.`} />;
  }
  const outcome = acceptanceOutcomeText(o);
  return outcome ? <Banner tone="danger" text={`${outcome} Não é venda: não vai para a cozinha nem para o fechamento.`} /> : null;
}

/** Atendimento de entrega/retirada (ADR-0046 item 18), em pedidos do site e do app; uso opcional. */
function FulfillmentBlock({ order: o }: { order: Order }) {
  const timeZone = useSession((s) => s.me?.tenant.timeZone ?? 'America/Sao_Paulo');
  if (o.acceptanceStatus !== 'ACCEPTED' || o.cancellationStatus !== 'ACTIVE' || o.fulfillmentType === 'DINE_IN' || o.fulfillmentStatus === null) return null;
  const status = o.isReadyForPickup ? READY_FOR_PICKUP : fulfillmentStatusNames[o.fulfillmentStatus];
  const at = o.deliveredAt ?? o.pickedUpAt ?? o.outForDeliveryAt;
  return (
    <Card>
      <Row style={{ justifyContent: 'space-between' }}>
        <Text variant="subtitle">Atendimento</Text>
        <Text style={{ fontWeight: '700' }}>
          {status}
          {at && o.fulfillmentStatus !== 'PENDING' ? ` · ${formatTime(at, timeZone)}` : ''}
        </Text>
      </Row>
      <FulfillmentActions order={o} />
    </Card>
  );
}

function ItemsBlock({ order: o }: { order: Order }) {
  const canEdit = useCanWrite('orders.edit');
  const online = useIsOnline();
  const editable = canEdit && online && !o.isPaid && o.cancellationStatus === 'ACTIVE' && o.acceptanceStatus === 'ACCEPTED';
  const [editing, setEditing] = useState<OrderItem | null>(null);
  const [removing, setRemoving] = useState<{ kind: 'item' | 'weighing'; id: string; label: string; combo?: boolean } | null>(null);
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
              <AddonLines addons={item.addons} />
              {item.notes ? <Text variant="small">Obs.: {item.notes}</Text> : null}
            </View>
            <Text>{formatBRL(itemTreeTotal(item))}</Text>
            {editable && itemEditMode(item) ? <IconButton icon="create-outline" label={`Alterar ${item.name}`} onPress={() => setEditing(item)} /> : null}
          </Row>
          {(item.parts ?? []).map((part) => (
            <ComboPartRow key={part.id} part={part} combo={item} editable={editable && itemEditMode(part) !== null} onEdit={() => setEditing(part)} />
          ))}
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
            mode={itemEditMode(editing) ?? 'NOTES_ONLY'}
            busy={update.busy}
            error={update.error}
            onSave={async (body) => {
              const outcome = await update.run({ itemId: editing.id, body, version: o.version });
              onOutcome(outcome as never);
              if (outcome.kind === 'confirmed') setEditing(null);
            }}
            onRemove={() => {
              setRemoving({ kind: 'item', id: editing.id, label: `${editing.quantity}× ${editing.name}`, combo: editing.itemKind === 'COMBO' });
              setEditing(null);
            }}
          />
        ) : null}
      </BottomSheet>

      <ConfirmDialog
        visible={removing !== null}
        title={removing?.combo ? 'Remover combo?' : removing?.kind === 'item' ? 'Remover item?' : 'Remover pesagem?'}
        description={removing?.combo ? 'O combo sai inteiro: as partes e os adicionais delas também.' : undefined}
        lines={removing ? [{ label: removing.kind === 'item' ? (removing.combo ? 'Combo' : 'Item') : 'Pesagem', value: removing.label }] : []}
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
          if (outcome.kind !== 'rejected' && outcome.kind !== 'ignored') setRemoving(null);
        }}
      />
      <ConflictSheet visible={conflict !== null} summary={conflict ? summarizeOrder(conflict) : []} onClose={() => setConflict(null)} onRedo={() => setConflict(null)} />
    </Card>
  );
}

/**
 * O que dá para alterar em cada item (06 §6.9 `PATCH …/items/{itemId}`, ADR-0058 item 10):
 * - `FULL`: quantidade, observação e remover (marmita, PF, bebida, prato avulso);
 * - `COMBO`: só a quantidade (reprecifica a árvore) e remover o combo inteiro; o `COMBO` não tem observação;
 * - `NOTES_ONLY`: parte **de prato** do combo, só a observação (a quantidade é a do componente; não sai sozinha).
 * Parte de bebida: nada (`null`). Trocar o sabor é remover e incluir o combo de novo (Q-322).
 */
export type ItemEditMode = 'FULL' | 'COMBO' | 'NOTES_ONLY';

export function itemEditMode(item: OrderItem): ItemEditMode | null {
  if (item.itemKind === 'COMBO') return 'COMBO';
  if (item.itemKind === 'COMBO_PART') return item.dishId ? 'NOTES_ONLY' : null;
  return 'FULL';
}

/**
 * Valor mostrado na linha (valores da API, nunca recalculados): o `lineTotal` do item; no combo, a árvore inteira
 * (o combo, as partes e os adicionais delas: 2× "X-Burguer + batata + refri" = R$ 73,00, 06 §6.9). Os adicionais
 * do prato avulso e da marmita aparecem cada um com o seu valor, como antes.
 */
export function itemTreeTotal(item: OrderItem): number {
  return item.itemKind === 'COMBO' ? itemWithChildrenCents(item) : toCents(item.lineTotal);
}

function AddonLines({ addons }: { addons: OrderItem['addons'] }) {
  return (
    <>
      {addons.map((a) => (
        <Text key={a.id} variant="small" muted>
          + {a.quantity}× {a.name} ({formatBRL(a.lineTotal)})
        </Text>
      ))}
    </>
  );
}

/** Parte do combo, recuada sob o combo: nome (com tamanho/sabor), adicionais, observação e o acréscimo, se houver. */
function ComboPartRow({ part, combo, editable, onEdit }: { part: OrderItem; combo: OrderItem; editable: boolean; onEdit: () => void }) {
  const c = useColors();
  return (
    <Row style={{ justifyContent: 'space-between', alignItems: 'flex-start', paddingLeft: space.md, borderLeftWidth: 2, borderLeftColor: c.border, marginLeft: space.xs }}>
      <View style={{ flex: 1 }} accessibilityLabel={`Parte do combo ${combo.name}: ${part.quantity}× ${part.name}`}>
        <Text variant="small" style={{ fontWeight: '600' }}>
          {part.quantity}× {part.name}
        </Text>
        <AddonLines addons={part.addons} />
        {part.notes ? <Text variant="small">Obs.: {part.notes}</Text> : null}
      </View>
      {toCents(part.lineTotal) > 0 ? <Text variant="small">+ {formatBRL(part.lineTotal)}</Text> : null}
      {editable ? <IconButton icon="create-outline" label={`Observação de ${part.name}`} onPress={onEdit} /> : null}
    </Row>
  );
}

function EditItem({
  item,
  mode,
  busy,
  error,
  onSave,
  onRemove,
}: {
  item: OrderItem;
  mode: ItemEditMode;
  busy: boolean;
  error: string | null;
  onSave: (b: { quantity?: number; notes?: string | null }) => void;
  onRemove: () => void;
}) {
  const [quantity, setQuantity] = useState(item.quantity);
  const [notes, setNotes] = useState(item.notes ?? '');
  const [confirm, setConfirm] = useState(false);
  const withQuantity = mode !== 'NOTES_ONLY';
  const withNotes = mode !== 'COMBO';
  return (
    <>
      {withQuantity ? (
        <Row style={{ justifyContent: 'space-between' }}>
          <Text>Quantidade</Text>
          <QuantityStepper label="Quantidade" min={1} value={quantity} onChange={setQuantity} />
        </Row>
      ) : null}
      {withNotes ? <TextField label="Observação" value={notes} onChangeText={setNotes} /> : null}
      {mode === 'COMBO' ? (
        <Text variant="small" muted>
          A quantidade do combo vale para as partes e os adicionais delas. Observação e adicionais ficam em cada prato do combo. Para trocar o sabor de uma bebida, remova o combo e inclua de novo.
        </Text>
      ) : null}
      {mode === 'NOTES_ONLY' ? (
        <Text variant="small" muted>
          Parte de combo: a quantidade é a do combo, e ela não sai sozinha (remova o combo inteiro).
        </Text>
      ) : null}
      {error ? <Banner tone="danger" text={error} /> : null}
      <Button title="Salvar alteração" onPress={() => setConfirm(true)} disabled={quantity === item.quantity && notes === (item.notes ?? '')} />
      {mode !== 'NOTES_ONLY' ? <Button title={mode === 'COMBO' ? 'Remover combo' : 'Remover item'} variant="danger" onPress={onRemove} /> : null}
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
      {/* ADR-0057 item 7: pedido do site cobra o preço de site; o `unitPrice` de cada linha já é o cobrado. */}
      {o.priceList === 'SITE' ? <Text variant="small" muted>Preços do site</Text> : null}
      <KeyValue label="Itens" value={formatBRL(o.itemsTotal)} />
      {o.weighings.length ? <KeyValue label="Pesagens" value={formatBRL(o.weighingsTotal)} /> : null}
      {o.fulfillmentType === 'DELIVERY' ? <KeyValue label={o.deliveryFeeSource === 'MANUAL' ? 'Entrega (taxa digitada)' : 'Entrega'} value={formatBRL(o.deliveryFee)} /> : null}
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
  // Edge-to-edge: a barra fica acima da navegação do Android.
  const insets = useSafeAreaInsets();
  const online = useIsOnline();
  const canEdit = useCanWrite('orders.edit');
  const canPay = useCanWrite('payments.register');
  const canCancel = useCanWrite('orders.cancel');
  const [menu, setMenu] = useState(false);
  const [cancelOpen, setCancelOpen] = useState(false);
  // Pedido do site não aceito: nenhuma escrita além de aceitar/recusar (`409 OrderNotAccepted`, ADR-0046 item 4).
  const accepted = o.acceptanceStatus === 'ACCEPTED';
  const active = o.cancellationStatus === 'ACTIVE' && accepted;
  const editable = active && !o.isPaid && online;
  // Depois da saída não entra item novo (`409 OrderAlreadyDispatched`, ADR-0046 item 19).
  const dispatched = isDispatched(o.fulfillmentStatus);

  return (
    <View style={{ position: 'absolute', left: 0, right: 0, bottom: 0, backgroundColor: c.surface, borderTopWidth: 1, borderTopColor: c.border, padding: space.sm, paddingBottom: space.sm + insets.bottom, gap: space.sm }}>
      <Row style={[{ flexWrap: 'wrap' }, WEB_CONTENT_MAX]}>
        {canPay && active && !o.isPaid ? <Button title="Receber" icon="cash-outline" disabled={!online} onPress={() => router.push(`/orders/${o.id}/pay`)} style={{ flex: 1 }} /> : null}
        {canEdit && editable && o.orderType !== 'KILO' && !dispatched ? <Button title="Adicionar itens" variant="secondary" onPress={() => router.push(`/orders/${o.id}/add-items`)} style={{ flex: 1 }} /> : null}
        {canEdit && editable && o.orderType === 'KILO' ? <Button title="Pesagem" icon="scale-outline" variant="secondary" onPress={() => router.push(`/orders/${o.id}/weighings`)} style={{ flex: 1 }} /> : null}
        <IconButton icon="ellipsis-vertical" label="Mais ações" onPress={() => setMenu(true)} />
      </Row>
      {canEdit && editable && o.orderType !== 'KILO' && dispatched ? (
        <Text variant="small" muted style={WEB_CONTENT_MAX}>
          O pedido já saiu: para incluir itens, crie outro pedido.
        </Text>
      ) : null}
      <BottomSheet visible={menu} title={`Pedido ${orderNumberLabel(o.orderNumber)}`} onClose={() => setMenu(false)}>
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
        { label: 'Pedido', value: orderNumberLabel(o.orderNumber) },
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
