import { router, Stack } from 'expo-router';
import { useEffect, useMemo, useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { KeyboardAvoidingView, Platform, View } from 'react-native';

import type { MenuItem } from '@/api/types';
import { BottomSheet, Banner, Button, Card, ConfirmDialog, IconButton, QuantityStepper, Row, Screen, Section, Text, TextField, space } from '@/components/ui';
import { useProducts } from '@/features/catalog/hooks';
import { useDraft } from '@/features/drafts/useDraft';
import { useCurrentMenu } from '@/features/menu/hooks';
import { formatBRL } from '@/lib/money';
import { newKey } from '@/lib/ids';
import { useIsOnline } from '@/stores/connectivity';
import { useSession } from '@/stores/session';

import { MenuMissingCard } from '../components';
import { beverageLines, buildPfBody, emptyPfForm, pfLineCents, previewPfCents, priceBook, type PfForm, type PfLine } from '../compose';
import { AddonPicker, BeverageGrid, DraftOffer, SubmitFooter } from '../formParts';
import { useCreateOrder } from '../hooks';
import { firstIssue, pfFormSchema } from '../schemas';

/** 08 §8.9 `orders/new/pf`: mesa, pratos do cardápio PF com preço, bebidas, "Abrir mesa". */
export default function NewPfScreen() {
  const timeZone = useSession((s) => s.me?.tenant.timeZone ?? 'America/Sao_Paulo');
  const online = useIsOnline();
  const products = useProducts();
  const menu = useCurrentMenu('PF');
  const draft = useDraft<PfForm>('NEW_ORDER_PF');
  const form = useForm<PfForm>({ defaultValues: emptyPfForm() });
  const v = useWatch({ control: form.control }) as PfForm;
  const [editing, setEditing] = useState<PfLine | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [issue, setIssue] = useState<string | null>(null);

  const book = useMemo(() => priceBook([], products.data, menu.data?.menu), [products.data, menu.data]);
  const preview = previewPfCents(v, book);
  const dishes = (menu.data?.menu?.items ?? []).filter((i) => i.section === 'MAIN_DISH');
  const beverages = (products.data ?? []).filter((p) => p.kind === 'BEVERAGE' && p.isActive && p.isAvailable);
  const addons = (products.data ?? []).filter((p) => p.kind === 'ADDON' && p.isActive && p.isAvailable);
  const create = useCreateOrder({ draftId: draft.draftId, previewCents: preview, onCreated: (o) => router.replace(`/orders/${o.id}`) });

  const { save } = draft;
  useEffect(() => {
    if (form.formState.isDirty) save(v);
  }, [v, save, form.formState.isDirty]);

  const set = <K extends keyof PfForm>(key: K, value: PfForm[K]) => form.setValue(key, value as never, { shouldDirty: true });
  const qtyOf = (dish: MenuItem) => v.lines.filter((l) => l.productId === dish.productId).reduce((s, l) => s + l.quantity, 0);
  const addDish = (dish: MenuItem) => set('lines', [...v.lines, { key: newKey(), productId: dish.productId, quantity: 1, notes: '', addons: [] }]);
  const saveLine = (line: PfLine) => set('lines', v.lines.map((l) => (l.key === line.key ? line : l)));

  const review = () => {
    const parsed = pfFormSchema.safeParse(v);
    setIssue(firstIssue(parsed));
    if (parsed.success) setConfirming(true);
  };

  const submit = async () => {
    await draft.flush(v);
    const outcome = await create.run(buildPfBody(v));
    if (outcome.kind !== 'unknown') setConfirming(false);
  };

  const name = (productId: string) => book.products.get(productId)?.name ?? '?';

  return (
    <KeyboardAvoidingView style={{ flex: 1 }} behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
      <Stack.Screen options={{ title: 'Nova mesa (PF)' }} />
      <Screen>
        <DraftOffer offer={draft.offer} timeZone={timeZone} onResume={() => { const d = draft.resume(); if (d) form.reset(d); }} onDiscard={() => void draft.discardOffer()} />
        {menu.data?.missing ? <MenuMissingCard menuType="PF" sources={menu.data.missing.sources} businessDate={menu.data.missing.businessDate} /> : null}
        <Row>
          <View style={{ flex: 1 }}>
            <TextField label="Mesa" value={v.tableLabel} onChangeText={(t) => set('tableLabel', t)} keyboardType="number-pad" />
          </View>
          <View style={{ flex: 1 }}>
            <TextField label="Comanda (opcional)" value={v.tabNumber} onChangeText={(t) => set('tabNumber', t)} keyboardType="number-pad" />
          </View>
        </Row>

        <Section title="Pratos">
          <View style={{ flexDirection: 'row', flexWrap: 'wrap', gap: space.sm }}>
            {dishes.map((d) => (
              <Card key={d.id} onPress={d.isAvailable ? () => addDish(d) : undefined} accessibilityLabel={`Adicionar ${d.name}`} style={{ width: '48%', opacity: d.isAvailable ? 1 : 0.45 }}>
                <Text style={{ fontWeight: '700', textDecorationLine: d.isAvailable ? 'none' : 'line-through' }}>{d.name}</Text>
                <Row style={{ justifyContent: 'space-between' }}>
                  <Text muted>{book.pfPrices.get(d.productId) ? formatBRL(book.pfPrices.get(d.productId)!) : '—'}</Text>
                  {qtyOf(d) > 0 ? <Text style={{ fontWeight: '700' }}>{qtyOf(d)}×</Text> : null}
                </Row>
              </Card>
            ))}
          </View>
          {v.lines.map((l) => (
            <Card key={l.key}>
              <Row style={{ justifyContent: 'space-between' }}>
                <View style={{ flex: 1 }}>
                  <Text style={{ fontWeight: '700' }}>PF {name(l.productId)}</Text>
                  {l.notes ? <Text variant="small">Obs.: {l.notes}</Text> : null}
                  {l.addons.length ? <Text variant="small" muted>+ {l.addons.map((a) => `${a.quantity}× ${name(a.productId)}`).join(', ')}</Text> : null}
                  <Text variant="small" muted>{formatBRL(pfLineCents(book, l))}</Text>
                </View>
                <QuantityStepper label={`PF ${name(l.productId)}`} min={1} value={l.quantity} onChange={(q) => saveLine({ ...l, quantity: q })} />
              </Row>
              <Row>
                <Button title="Obs. e adicionais" variant="ghost" onPress={() => setEditing(l)} />
                <IconButton icon="trash-outline" label="Remover prato" onPress={() => set('lines', v.lines.filter((x) => x.key !== l.key))} />
              </Row>
            </Card>
          ))}
        </Section>

        <Section title="Bebidas">
          <BeverageGrid beverages={beverages} value={v.beverages} onChange={(b) => set('beverages', b)} />
        </Section>
        <TextField label="Observações" value={v.notes} onChangeText={(n) => set('notes', n)} multiline />
        {issue ? <Banner tone="danger" text={issue} /> : null}
      </Screen>

      <SubmitFooter previewCents={preview} label="Abrir mesa" online={online} loading={create.busy} unknown={create.state === 'unknown'} error={create.error} onPress={review} />

      <BottomSheet visible={editing !== null} title={editing ? `PF ${name(editing.productId)}` : ''} onClose={() => setEditing(null)} footer={<Button title="Salvar" onPress={() => { if (editing) saveLine(editing); setEditing(null); }} />}>
        {editing ? (
          <>
            <TextField label="Observação" value={editing.notes} onChangeText={(notes) => setEditing({ ...editing, notes })} placeholder="Ex.: sem salada" />
            <AddonPicker addons={addons} value={editing.addons} onChange={(a) => setEditing({ ...editing, addons: a })} />
          </>
        ) : null}
      </BottomSheet>

      <ConfirmDialog
        visible={confirming}
        title="Abrir mesa?"
        lines={[
          { label: 'Mesa / comanda', value: [v.tableLabel && `Mesa ${v.tableLabel}`, v.tabNumber && `Comanda ${v.tabNumber}`].filter(Boolean).join(' · ') },
          ...v.lines.map((l) => ({ label: `${l.quantity}× PF ${name(l.productId)}`, value: formatBRL(pfLineCents(book, l)) })),
          ...beverageLines(book, v.beverages),
          { label: 'Prévia do total', value: formatBRL(preview) },
        ]}
        warning={create.state === 'unknown' ? 'Não sabemos se o pedido foi registrado. Verificando…' : create.error}
        confirmLabel={`Abrir mesa · ${formatBRL(preview)}`}
        loading={create.busy}
        onCancel={() => setConfirming(false)}
        onConfirm={() => void submit()}
      />
    </KeyboardAvoidingView>
  );
}
