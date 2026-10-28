import { router, Stack } from 'expo-router';
import { useEffect, useMemo, useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { KeyboardAvoidingView, Platform, View } from 'react-native';

import type { Customer } from '@/api/types';
import {
  Banner,
  Button,
  Card,
  Chip,
  ConfirmDialog,
  IconButton,
  MoneyInput,
  Row,
  Screen,
  Section,
  SegmentedControl,
  Text,
  TextField,
  space,
} from '@/components/ui';
import { useMarmitaSizes, useProducts } from '@/features/catalog/hooks';
import { CustomerPicker } from '@/features/customers/CustomerPicker';
import { useDraft } from '@/features/drafts/useDraft';
import { useCurrentMenu } from '@/features/menu/hooks';
import { useSettings } from '@/features/session/hooks';
import { formatBRL } from '@/lib/money';
import { newKey } from '@/lib/ids';
import { toE164 } from '@/lib/phone';
import { useIsOnline } from '@/stores/connectivity';
import { usePermission, useSession } from '@/stores/session';

import { MenuMissingCard } from '../components';
import { beverageLines, buildMarmitaBody, emptyMarmitaForm, marmitaLineCents, marmitaLineLabel, previewMarmitaCents, priceBook, type MarmitaForm, type MarmitaLine } from '../compose';
import { BeverageGrid, DraftOffer, SubmitFooter } from '../formParts';
import { useCreateOrder } from '../hooks';
import { MarmitaBuilderSheet } from '../MarmitaBuilderSheet';
import { firstIssue, marmitaFormSchema } from '../schemas';

/** 08 §8.9 `orders/new/marmita`: uma página, rascunho automático, prévia no rodapé. */
export default function NewMarmitaScreen() {
  const timeZone = useSession((s) => s.me?.tenant.timeZone ?? 'America/Sao_Paulo');
  const online = useIsOnline();
  const canEditFee = usePermission('orders.delivery-fee.edit');
  const sizes = useMarmitaSizes();
  const products = useProducts();
  const settings = useSettings();
  const menu = useCurrentMenu('MARMITA');
  const draft = useDraft<MarmitaForm>('NEW_ORDER_MARMITA');
  const form = useForm<MarmitaForm>({ defaultValues: emptyMarmitaForm() });
  const v = useWatch({ control: form.control }) as MarmitaForm;
  const [selected, setSelected] = useState<Customer | null>(null);
  const [builder, setBuilder] = useState<{ open: boolean; line: MarmitaLine | null }>({ open: false, line: null });
  const [confirming, setConfirming] = useState(false);
  const [issue, setIssue] = useState<string | null>(null);

  const book = useMemo(() => priceBook(sizes.data, products.data), [sizes.data, products.data]);
  const preview = previewMarmitaCents(v, book, settings.data?.defaultDeliveryFee ?? null);
  const beverages = (products.data ?? []).filter((p) => p.kind === 'BEVERAGE' && p.isActive && p.isAvailable);
  const addons = (products.data ?? []).filter((p) => p.kind === 'ADDON' && p.isActive && p.isAvailable);

  const create = useCreateOrder({ draftId: draft.draftId, previewCents: preview, onCreated: (o) => router.replace(`/orders/${o.id}`) });

  // Rascunho a cada mudança (RN-014.3).
  const { save } = draft;
  useEffect(() => {
    if (form.formState.isDirty) save(v);
  }, [v, save, form.formState.isDirty]);

  const set = <K extends keyof MarmitaForm>(key: K, value: MarmitaForm[K]) => form.setValue(key, value as never, { shouldDirty: true });

  const saveLine = (line: MarmitaLine) => {
    const exists = v.lines.some((l) => l.key === line.key);
    set('lines', exists ? v.lines.map((l) => (l.key === line.key ? line : l)) : [...v.lines, line]);
  };

  const review = () => {
    const parsed = marmitaFormSchema.safeParse(v);
    setIssue(firstIssue(parsed));
    if (parsed.success) setConfirming(true);
  };

  const submit = async () => {
    const body = buildMarmitaBody(v, v.customer.phone ? toE164(v.customer.phone) : null);
    await draft.flush(v);
    const outcome = await create.run(body);
    if (outcome.kind !== 'unknown') setConfirming(false);
  };

  const errors = form.formState.errors;
  const delivery = v.fulfillmentType === 'DELIVERY';

  return (
    <KeyboardAvoidingView style={{ flex: 1 }} behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
      <Stack.Screen options={{ title: 'Nova marmita' }} />
      <Screen>
        <DraftOffer
          offer={draft.offer}
          timeZone={timeZone}
          onResume={() => {
            const value = draft.resume();
            if (value) form.reset(value);
          }}
          onDiscard={() => void draft.discardOffer()}
        />
        {menu.data?.missing ? <MenuMissingCard menuType="MARMITA" sources={menu.data.missing.sources} businessDate={menu.data.missing.businessDate} /> : null}

        <Section title="Cliente">
          <CustomerPicker
            value={v.customer}
            onChange={(c) => set('customer', c)}
            onSelectCustomer={(c) => {
              setSelected(c);
              const def = c?.addresses.find((a) => a.isDefault) ?? c?.addresses[0];
              set('addressId', def?.id ?? null);
            }}
            errors={{ name: errors.customer?.name?.message, phone: errors.customer?.phone?.message }}
          />
        </Section>

        <Section title="Atendimento">
          <SegmentedControl
            value={v.fulfillmentType}
            onChange={(f) => set('fulfillmentType', f)}
            options={[
              { value: 'PICKUP', label: 'Retirada' },
              { value: 'DELIVERY', label: 'Entrega' },
            ]}
          />
          {delivery ? (
            <View style={{ gap: space.sm }}>
              {selected?.addresses.length ? (
                <Row style={{ flexWrap: 'wrap' }}>
                  {selected.addresses.map((a) => (
                    <Chip key={a.id} label={`${a.label ?? 'Endereço'}: ${a.street}, ${a.number ?? 's/n'}`} selected={v.addressId === a.id} onPress={() => set('addressId', a.id)} />
                  ))}
                  <Chip label="Outro endereço" selected={v.addressId === null} onPress={() => set('addressId', null)} />
                </Row>
              ) : null}
              {v.addressId === null ? (
                <>
                  <TextField label="Rua" value={v.address?.street ?? ''} onChangeText={(street) => set('address', { number: null, complement: null, neighborhood: null, reference: null, ...v.address, street })} />
                  <Row>
                    <View style={{ flex: 1 }}>
                      <TextField label="Número" value={v.address?.number ?? ''} onChangeText={(number) => set('address', { street: '', complement: null, neighborhood: null, reference: null, ...v.address, number })} />
                    </View>
                    <View style={{ flex: 2 }}>
                      <TextField label="Bairro" value={v.address?.neighborhood ?? ''} onChangeText={(neighborhood) => set('address', { street: '', number: null, complement: null, reference: null, ...v.address, neighborhood })} />
                    </View>
                  </Row>
                  <TextField label="Complemento" value={v.address?.complement ?? ''} onChangeText={(complement) => set('address', { street: '', number: null, neighborhood: null, reference: null, ...v.address, complement })} />
                  <TextField label="Referência" value={v.address?.reference ?? ''} onChangeText={(reference) => set('address', { street: '', number: null, neighborhood: null, complement: null, ...v.address, reference })} />
                </>
              ) : null}
              {canEditFee ? (
                <MoneyInput label="Taxa de entrega" value={v.deliveryFee ?? settings.data?.defaultDeliveryFee ?? '0.00'} onChange={(fee) => set('deliveryFee', fee === settings.data?.defaultDeliveryFee ? null : fee)} />
              ) : (
                <Text muted>Taxa de entrega: {formatBRL(settings.data?.defaultDeliveryFee ?? '0.00')}</Text>
              )}
            </View>
          ) : null}
        </Section>

        <Section title="Marmitas" right={<Button title="Adicionar" icon="add" variant="secondary" onPress={() => setBuilder({ open: true, line: null })} disabled={!menu.data?.menu} />}>
          {v.lines.length === 0 ? <Text muted>Nenhuma marmita ainda.</Text> : null}
          {v.lines.map((l) => (
            <Card key={l.key} onPress={() => setBuilder({ open: true, line: l })} accessibilityLabel={`Editar ${marmitaLineLabel(l, book)}`}>
              <Row style={{ justifyContent: 'space-between', alignItems: 'flex-start' }}>
                <View style={{ flex: 1 }}>
                  <Text style={{ fontWeight: '700' }}>
                    {l.quantity}× {marmitaLineLabel(l, book)}
                  </Text>
                  {l.addons.length ? (
                    <Text variant="small" muted>
                      + {l.addons.map((a) => `${a.quantity}× ${book.products.get(a.productId)?.name ?? '?'}`).join(', ')}
                    </Text>
                  ) : null}
                  {l.notes ? <Text variant="small">Obs.: {l.notes}</Text> : null}
                  <Text variant="small" muted>
                    {formatBRL(marmitaLineCents(book, l))}
                  </Text>
                </View>
                <IconButton icon="copy-outline" label="Mais uma igual" onPress={() => set('lines', [...v.lines, { ...l, key: newKey() }])} />
                <IconButton icon="trash-outline" label="Remover marmita" onPress={() => set('lines', v.lines.filter((x) => x.key !== l.key))} />
              </Row>
            </Card>
          ))}
        </Section>

        <Section title="Bebidas">
          <BeverageGrid beverages={beverages} value={v.beverages} onChange={(b) => set('beverages', b)} />
        </Section>

        <TextField label="Observações do pedido" value={v.notes} onChangeText={(n) => set('notes', n)} multiline />
        {issue ? <Banner tone="danger" text={issue} /> : null}
      </Screen>

      <SubmitFooter
        previewCents={preview}
        label="Registrar pedido"
        online={online}
        loading={create.busy}
        unknown={create.state === 'unknown'}
        error={create.error}
        onPress={review}
      />

      <MarmitaBuilderSheet
        key={builder.open ? (builder.line?.key ?? 'new') : 'closed'}
        visible={builder.open}
        initial={builder.line}
        sizes={sizes.data ?? []}
        menu={menu.data?.menu ?? null}
        addons={addons}
        onSave={saveLine}
        onClose={() => setBuilder({ open: false, line: null })}
      />

      <ConfirmDialog
        visible={confirming}
        title="Registrar pedido?"
        lines={[
          { label: 'Cliente', value: v.customer.name || 'Sem nome' },
          { label: 'Atendimento', value: delivery ? 'Entrega' : 'Retirada' },
          ...v.lines.map((l) => ({ label: `${l.quantity}× ${marmitaLineLabel(l, book)}`, value: formatBRL(marmitaLineCents(book, l)) })),
          ...beverageLines(book, v.beverages),
          { label: 'Prévia do total', value: formatBRL(preview) },
        ]}
        warning={create.state === 'unknown' ? 'Não sabemos se o pedido foi registrado. Verificando antes de qualquer reenvio…' : create.error}
        confirmLabel={`Registrar ${formatBRL(preview)}`}
        loading={create.busy}
        onCancel={() => setConfirming(false)}
        onConfirm={() => void submit()}
      />
    </KeyboardAvoidingView>
  );
}
