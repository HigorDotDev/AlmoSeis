/** Subtelas de `orders/[id]` (08 §8.9): add-items, weighings, history, alerts, edit. */
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useMemo, useState } from 'react';
import { View } from 'react-native';

import { errorMessage } from '@/api/errors';
import type { NewItemInput, Order } from '@/api/types';
import {
  Banner,
  Button,
  Card,
  Chip,
  ConfirmDialog,
  ConflictSheet,
  EmptyState,
  ErrorState,
  IconButton,
  MoneyInput,
  QuantityStepper,
  Row,
  Screen,
  Section,
  SegmentedControl,
  Skeleton,
  Text,
  TextField,
  WeightInput,
  space,
} from '@/components/ui';
import { useMarmitaSizes, useProducts } from '@/features/catalog/hooks';
import { useDraft } from '@/features/drafts/useDraft';
import { useCurrentMenu } from '@/features/menu/hooks';
import { useSettings } from '@/features/session/hooks';
import { formatDateTime } from '@/lib/dates';
import { formatBRL, toCents } from '@/lib/money';
import { maskBR, toE164, formatPhone } from '@/lib/phone';
import { gramsToKgLabel, previewKiloAmountCents } from '@/lib/weight';
import { useIsOnline } from '@/stores/connectivity';
import { useCanWrite, usePermission, useSession } from '@/stores/session';

import {
  beverageItems,
  beverageLines,
  marmitaLineCents,
  marmitaLineItem,
  marmitaLineLabel,
  pfLineCents,
  pfLineItem,
  priceBook,
  type MarmitaLine,
  type PfLine,
} from '../compose';
import { AddonPicker, BeverageGrid } from '../formParts';
import { summarizeOrder, useAcknowledgeAlerts, useAddItems, useAddWeighings, useOrder, useOrderAlerts, useOrderHistory, useUpdateOrder } from '../hooks';
import { MarmitaBuilderSheet } from '../MarmitaBuilderSheet';
import { newKey } from '@/lib/ids';

function useLoadedOrder() {
  const { id } = useLocalSearchParams<{ id: string }>();
  return useOrder(id);
}

function Loading({ query }: { query: ReturnType<typeof useOrder> }) {
  if (query.isLoading) return <View style={{ padding: space.lg }}><Skeleton lines={4} /></View>;
  return <ErrorState message={errorMessage(query.error)} onRetry={() => void query.refetch()} />;
}

interface AddItemsForm {
  marmitas: MarmitaLine[];
  pfs: PfLine[];
  beverages: Record<string, number>;
  /** Adicionais em linhas já existentes: itemId → adicionais. */
  addons: Record<string, { productId: string; quantity: number }[]>;
}

/** Várias inclusões numa só confirmação (RN-012.3). */
export function AddItemsScreen() {
  const order = useLoadedOrder();
  if (!order.data) return <Loading query={order} />;
  return <AddItems order={order.data} />;
}

function AddItems({ order: o }: { order: Order }) {
  const online = useIsOnline();
  const products = useProducts();
  const sizes = useMarmitaSizes();
  const menuType = o.orderType === 'PF' ? 'PF' : 'MARMITA';
  const menu = useCurrentMenu(menuType, o.orderType !== 'KILO');
  const draft = useDraft<AddItemsForm>('ADD_ITEMS', o.id);
  const [form, setForm] = useState<AddItemsForm>({ marmitas: [], pfs: [], beverages: {}, addons: {} });
  const [builder, setBuilder] = useState(false);
  const [addonFor, setAddonFor] = useState<string | null>(null);
  const [confirm, setConfirm] = useState(false);
  const [conflict, setConflict] = useState<Order | null>(null);
  const book = useMemo(() => priceBook(sizes.data, products.data, menu.data?.menu), [sizes.data, products.data, menu.data]);
  const beverages = (products.data ?? []).filter((p) => p.kind === 'BEVERAGE' && p.isActive && p.isAvailable);
  const addons = (products.data ?? []).filter((p) => p.kind === 'ADDON' && p.isActive && p.isAvailable);

  const update = (next: AddItemsForm) => {
    setForm(next);
    draft.save(next);
  };

  const items: NewItemInput[] = [
    ...form.marmitas.map(marmitaLineItem),
    ...form.pfs.map(pfLineItem),
    ...beverageItems(form.beverages),
    ...Object.entries(form.addons).flatMap(([parentOrderItemId, list]) =>
      list.filter((a) => a.quantity > 0).map((a) => ({ itemKind: 'ADDON' as const, parentOrderItemId, productId: a.productId, quantity: a.quantity, notes: null })),
    ),
  ];

  const add = useAddItems(o, draft.draftId, () => router.back());

  const lines = [
    ...form.marmitas.map((l) => ({ label: `${l.quantity}× ${marmitaLineLabel(l, book)}`, value: formatBRL(marmitaLineCents(book, l)) })),
    ...form.pfs.map((l) => ({ label: `${l.quantity}× PF ${book.products.get(l.productId)?.name ?? '?'}`, value: formatBRL(pfLineCents(book, l)) })),
    ...beverageLines(book, form.beverages),
    ...Object.entries(form.addons).flatMap(([itemId, list]) => {
      const parent = o.items.find((i) => i.id === itemId);
      return list
        .filter((a) => a.quantity > 0)
        .map((a) => ({
          label: `+${a.quantity}× ${book.products.get(a.productId)?.name ?? '?'} em ${parent?.name ?? ''}`,
          value: formatBRL(toCents(book.products.get(a.productId)?.defaultPrice ?? '0.00') * a.quantity * (parent?.quantity ?? 1)),
        }));
    }),
  ];

  return (
    <Screen>
      <Stack.Screen options={{ title: `Adicionar itens · #${o.orderNumber}` }} />
      {o.orderType === 'MARMITA' ? (
        <Section title="Marmitas" right={<Button title="Adicionar" icon="add" variant="secondary" disabled={!menu.data?.menu} onPress={() => setBuilder(true)} />}>
          {form.marmitas.map((l) => (
            <Row key={l.key} style={{ justifyContent: 'space-between' }}>
              <Text style={{ flex: 1 }}>
                {l.quantity}× {marmitaLineLabel(l, book)}
              </Text>
              <IconButton icon="trash-outline" label="Remover" onPress={() => update({ ...form, marmitas: form.marmitas.filter((x) => x.key !== l.key) })} />
            </Row>
          ))}
        </Section>
      ) : null}
      {o.orderType === 'PF' ? (
        <Section title="Pratos">
          <Row style={{ flexWrap: 'wrap' }}>
            {(menu.data?.menu?.items ?? [])
              .filter((i) => i.section === 'MAIN_DISH')
              .map((d) => (
                <Chip
                  key={d.id}
                  label={`${d.name} · ${book.pfPrices.get(d.productId) ? formatBRL(book.pfPrices.get(d.productId)!) : '—'}`}
                  disabled={!d.isAvailable}
                  strike={!d.isAvailable}
                  onPress={() => update({ ...form, pfs: [...form.pfs, { key: newKey(), productId: d.productId, quantity: 1, notes: '', addons: [] }] })}
                />
              ))}
          </Row>
          {form.pfs.map((l) => (
            <Row key={l.key} style={{ justifyContent: 'space-between' }}>
              <Text style={{ flex: 1 }}>PF {book.products.get(l.productId)?.name}</Text>
              <QuantityStepper label="Quantidade" value={l.quantity} onChange={(q) => update({ ...form, pfs: q === 0 ? form.pfs.filter((x) => x.key !== l.key) : form.pfs.map((x) => (x.key === l.key ? { ...x, quantity: q } : x)) })} />
            </Row>
          ))}
        </Section>
      ) : null}
      <Section title="Bebidas">
        <BeverageGrid beverages={beverages} value={form.beverages} onChange={(b) => update({ ...form, beverages: b })} />
      </Section>
      {o.orderType !== 'KILO' && addons.length ? (
        <Section title="Adicionais em itens do pedido">
          <Row style={{ flexWrap: 'wrap' }}>
            {o.items
              .filter((i) => i.itemKind === 'MARMITA' || i.itemKind === 'PF')
              .map((i) => (
                <Chip key={i.id} label={`${i.quantity}× ${i.name}`} selected={addonFor === i.id} onPress={() => setAddonFor(addonFor === i.id ? null : i.id)} />
              ))}
          </Row>
          {addonFor ? <AddonPicker addons={addons} value={form.addons[addonFor] ?? []} onChange={(list) => update({ ...form, addons: { ...form.addons, [addonFor]: list } })} /> : null}
        </Section>
      ) : null}
      {add.error ? <Banner tone="danger" text={add.error} /> : null}
      {add.state === 'unknown' ? <Banner tone="warning" text="Não sabemos se os itens foram incluídos. Verificando…" /> : null}
      <Button title={`Incluir ${items.length} ${items.length === 1 ? 'item' : 'itens'}`} disabled={items.length === 0 || !online || add.state === 'unknown'} onPress={() => setConfirm(true)} />

      <MarmitaBuilderSheet
        key={builder ? 'open' : 'closed'}
        visible={builder}
        initial={null}
        sizes={sizes.data ?? []}
        menu={menu.data?.menu ?? null}
        addons={addons}
        onSave={(line) => update({ ...form, marmitas: [...form.marmitas, line] })}
        onClose={() => setBuilder(false)}
      />
      <ConfirmDialog
        visible={confirm}
        title={`Incluir no pedido #${o.orderNumber}?`}
        lines={lines}
        confirmLabel="Incluir itens"
        loading={add.busy}
        warning={add.error}
        onCancel={() => setConfirm(false)}
        onConfirm={async () => {
          await draft.flush(form);
          const outcome = await add.run({ items, version: o.version });
          if (outcome.kind === 'conflict') setConflict(outcome.current as Order);
          if (outcome.kind !== 'unknown') setConfirm(false);
        }}
      />
      <ConflictSheet visible={conflict !== null} summary={conflict ? summarizeOrder(conflict) : []} onClose={() => setConflict(null)} onRedo={() => setConflict(null)} />
    </Screen>
  );
}

/** Pesagens adicionais de uma comanda de quilo. */
export function WeighingsScreen() {
  const order = useLoadedOrder();
  const settings = useSettings();
  const online = useIsOnline();
  const [weighings, setWeighings] = useState<number[]>([]);
  const [grams, setGrams] = useState<number | null>(null);
  const [inputKey, setInputKey] = useState(0);
  const [confirm, setConfirm] = useState(false);
  const [conflict, setConflict] = useState<Order | null>(null);
  const canEdit = useCanWrite('orders.edit');
  const add = useAddWeighings(order.data, () => router.back());
  if (!order.data) return <Loading query={order} />;
  const o = order.data;
  const price = settings.data?.kiloPricePerKg ?? null;

  return (
    <Screen>
      <Stack.Screen options={{ title: `Pesagem · #${o.orderNumber}` }} />
      {!price ? <Banner tone="warning" text="Configure o preço do quilo." /> : <Text muted>Preço vigente: {formatBRL(price)}/kg</Text>}
      <WeightInput key={inputKey} label="Peso no prato" onChange={setGrams} autoFocus />
      <Button
        title="Adicionar"
        variant="secondary"
        disabled={grams === null}
        onPress={() => {
          setWeighings([...weighings, grams!]);
          setGrams(null);
          setInputKey((k) => k + 1);
        }}
      />
      {weighings.map((g, i) => (
        <Row key={`${g}-${i}`} style={{ justifyContent: 'space-between' }}>
          <Text>{gramsToKgLabel(g)}</Text>
          <Text>{price ? formatBRL(previewKiloAmountCents(g, price)) : '—'}</Text>
          <IconButton icon="trash-outline" label="Remover" onPress={() => setWeighings(weighings.filter((_, j) => j !== i))} />
        </Row>
      ))}
      {add.error ? <Banner tone="danger" text={add.error} /> : null}
      {add.state === 'unknown' ? <Banner tone="warning" text="Não sabemos se as pesagens foram registradas. Verificando…" /> : null}
      <Button title="Registrar pesagens" disabled={weighings.length === 0 || !online || !price || !canEdit || add.busy} onPress={() => setConfirm(true)} />
      <ConfirmDialog
        visible={confirm}
        title="Registrar pesagens?"
        lines={weighings.map((g, i) => ({ label: `Pesagem ${i + 1}: ${gramsToKgLabel(g)}`, value: price ? formatBRL(previewKiloAmountCents(g, price)) : '—' }))}
        confirmLabel="Registrar pesagens"
        loading={add.busy}
        onCancel={() => setConfirm(false)}
        onConfirm={async () => {
          const outcome = await add.run({ weighings: weighings.map((weightGrams) => ({ weightGrams })), version: o.version });
          if (outcome.kind === 'conflict') setConflict(outcome.current as Order);
          setConfirm(false);
        }}
      />
      <ConflictSheet visible={conflict !== null} summary={conflict ? summarizeOrder(conflict) : []} onClose={() => setConflict(null)} onRedo={() => setConflict(null)} />
    </Screen>
  );
}

const operationNames: Record<string, string> = {
  OrderCreated: 'Pedido criado',
  OrderDetailsUpdated: 'Dados alterados',
  OrderItemAdded: 'Item incluído',
  OrderItemChanged: 'Item alterado',
  OrderItemRemoved: 'Item removido',
  WeighingAdded: 'Pesagem incluída',
  WeighingRemoved: 'Pesagem removida',
  OrderStatusChanged: 'Status de produção',
  OrderCanceled: 'Pedido cancelado',
  PaymentRegistered: 'Pagamento',
  PaymentReversed: 'Estorno',
};

function describeChange(after: Record<string, unknown> | null): string | null {
  if (!after) return null;
  return Object.entries(after)
    .filter(([k]) => !k.endsWith('Id'))
    .map(([k, v]) => `${k}: ${typeof v === 'object' ? JSON.stringify(v) : String(v)}`)
    .join(' · ');
}

export function HistoryScreen() {
  const { id } = useLocalSearchParams<{ id: string }>();
  const timeZone = useSession((s) => s.me?.tenant.timeZone ?? 'America/Sao_Paulo');
  const history = useOrderHistory(id);
  const items = history.data?.pages.flatMap((p) => p.items) ?? [];
  return (
    <Screen>
      <Stack.Screen options={{ title: 'Histórico do pedido' }} />
      {history.isLoading ? <Skeleton /> : null}
      {history.isError ? <ErrorState message={errorMessage(history.error)} onRetry={() => void history.refetch()} /> : null}
      {items.map((h, i) => (
        <Card key={`${h.occurredAt}-${i}`}>
          <Row style={{ justifyContent: 'space-between' }}>
            <Text style={{ fontWeight: '700' }}>{operationNames[h.operationType] ?? h.operationType}</Text>
            <Text variant="small" muted>
              {formatDateTime(h.occurredAt, timeZone)}
            </Text>
          </Row>
          <Text variant="small" muted>
            {h.actor?.displayName ?? 'Sistema'}
          </Text>
          {describeChange(h.after) ? <Text variant="small">{describeChange(h.after)}</Text> : null}
        </Card>
      ))}
      {history.hasNextPage ? <Button title="Carregar mais" variant="secondary" onPress={() => void history.fetchNextPage()} loading={history.isFetchingNextPage} /> : null}
    </Screen>
  );
}

/** RF-008: mudanças não vistas por quem chama; "Ciente" vale só para essa pessoa (RN-008.7). */
export function AlertsScreen() {
  const { id } = useLocalSearchParams<{ id: string }>();
  const timeZone = useSession((s) => s.me?.tenant.timeZone ?? 'America/Sao_Paulo');
  const alerts = useOrderAlerts(id);
  const ack = useAcknowledgeAlerts(id);
  return (
    <Screen>
      <Stack.Screen options={{ title: 'Mudanças no pedido' }} />
      {alerts.isLoading ? <Skeleton /> : null}
      {alerts.data?.length === 0 ? <EmptyState icon="checkmark-done-outline" title="Nada novo" /> : null}
      {alerts.data?.map((a) => (
        <Card key={a.eventId}>
          <Text style={{ fontWeight: '700' }}>{a.summary}</Text>
          <Text variant="small" muted>
            {a.actor?.displayName ?? 'Sistema'} · {formatDateTime(a.occurredAt, timeZone)}
            {a.affectsProduction ? ' · afeta a cozinha' : ''}
          </Text>
        </Card>
      ))}
      {ack.error ? <Banner tone="danger" text={errorMessage(ack.error)} /> : null}
      {alerts.data?.length ? (
        <Button title="Ciente" icon="checkmark" loading={ack.isPending} onPress={() => ack.mutate(alerts.data!.map((a) => a.eventId), { onSuccess: () => router.back() })} />
      ) : null}
    </Screen>
  );
}

/** `PATCH /orders/{id}`: dados do pedido, não itens. Pago: só observação, mesa e comanda. */
export function EditOrderScreen() {
  const order = useLoadedOrder();
  if (!order.data) return <Loading query={order} />;
  return <EditOrder order={order.data} />;
}

function EditOrder({ order: o }: { order: Order }) {
  const canEditFee = usePermission('orders.delivery-fee.edit');
  const [name, setName] = useState(o.customer?.name ?? '');
  const [phone, setPhone] = useState(formatPhone(o.customer?.phoneNumber));
  const [fulfillment, setFulfillment] = useState(o.fulfillmentType);
  const [fee, setFee] = useState(o.deliveryFee);
  const [table, setTable] = useState(o.tableLabel ?? '');
  const [tab, setTab] = useState(o.tabNumber ?? '');
  const [notes, setNotes] = useState(o.notes ?? '');
  const [street, setStreet] = useState(o.deliveryAddress?.street ?? '');
  const [number, setNumber] = useState(o.deliveryAddress?.number ?? '');
  const [neighborhood, setNeighborhood] = useState(o.deliveryAddress?.neighborhood ?? '');
  const [confirm, setConfirm] = useState(false);
  const [conflict, setConflict] = useState<Order | null>(null);
  const update = useUpdateOrder(o.id);
  const marmita = o.orderType === 'MARMITA';

  const body = {
    ...(o.isPaid
      ? {}
      : marmita
        ? {
            customerName: name.trim() || null,
            customerPhone: phone ? toE164(phone) : null,
            fulfillmentType: fulfillment,
            deliveryAddress: fulfillment === 'DELIVERY' ? { street, number: number || null, complement: o.deliveryAddress?.complement ?? null, neighborhood: neighborhood || null, reference: o.deliveryAddress?.reference ?? null } : null,
            ...(canEditFee ? { deliveryFee: fulfillment === 'DELIVERY' ? fee : '0.00' } : {}),
          }
        : {}),
    tableLabel: table.trim() || null,
    tabNumber: tab.trim() || null,
    notes: notes.trim() || null,
  };

  return (
    <Screen>
      <Stack.Screen options={{ title: `Editar · #${o.orderNumber}` }} />
      {o.isPaid ? <Banner tone="info" text="Pedido quitado: só observação, mesa e comanda podem mudar." /> : null}
      {marmita && !o.isPaid ? (
        <>
          <TextField label="Cliente" value={name} onChangeText={setName} />
          <TextField label="Telefone" value={phone} onChangeText={(t) => setPhone(maskBR(t))} keyboardType="phone-pad" />
          <SegmentedControl
            value={fulfillment === 'DELIVERY' ? 'DELIVERY' : 'PICKUP'}
            onChange={(f) => setFulfillment(f)}
            options={[
              { value: 'PICKUP', label: 'Retirada' },
              { value: 'DELIVERY', label: 'Entrega' },
            ]}
          />
          {fulfillment === 'DELIVERY' ? (
            <>
              <TextField label="Rua" value={street} onChangeText={setStreet} />
              <Row>
                <View style={{ flex: 1 }}>
                  <TextField label="Número" value={number} onChangeText={setNumber} />
                </View>
                <View style={{ flex: 2 }}>
                  <TextField label="Bairro" value={neighborhood} onChangeText={setNeighborhood} />
                </View>
              </Row>
              {canEditFee ? <MoneyInput label="Taxa de entrega" value={fee} onChange={setFee} /> : null}
            </>
          ) : null}
        </>
      ) : null}
      {o.orderType !== 'MARMITA' ? (
        <Row>
          <View style={{ flex: 1 }}>
            <TextField label="Mesa" value={table} onChangeText={setTable} />
          </View>
          <View style={{ flex: 1 }}>
            <TextField label="Comanda" value={tab} onChangeText={setTab} />
          </View>
        </Row>
      ) : null}
      <TextField label="Observações" value={notes} onChangeText={setNotes} multiline />
      {update.error ? <Banner tone="danger" text={update.error} /> : null}
      <Button title="Salvar" onPress={() => setConfirm(true)} />
      <ConfirmDialog
        visible={confirm}
        title="Salvar alterações?"
        lines={[{ label: 'Pedido', value: `#${o.orderNumber}` }]}
        confirmLabel="Salvar alterações"
        loading={update.busy}
        onCancel={() => setConfirm(false)}
        onConfirm={async () => {
          const outcome = await update.run({ body, version: o.version });
          setConfirm(false);
          if (outcome.kind === 'confirmed') router.back();
          if (outcome.kind === 'conflict') setConflict(outcome.current as Order);
        }}
      />
      <ConflictSheet visible={conflict !== null} summary={conflict ? summarizeOrder(conflict) : []} onClose={() => setConflict(null)} onRedo={() => setConflict(null)} />
    </Screen>
  );
}
