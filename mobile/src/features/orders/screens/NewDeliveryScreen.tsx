import { router, Stack } from 'expo-router';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { KeyboardAvoidingView, Platform, View } from 'react-native';

import { errorMessage } from '@/api/errors';
import { Banner, Button, Card, ConfirmDialog, ErrorState, Screen, Section, SegmentedControl, Skeleton, Text, TextField, space, useColors } from '@/components/ui';
import { useCustomerByPhone } from '@/features/customers/hooks';
import { useDraft } from '@/features/drafts/useDraft';
import { EMPTY_MENU } from '@/features/fixedMenu/fixedMenu';
import { useFixedMenu } from '@/features/fixedMenu/hooks';
import { useSettings } from '@/features/session/hooks';
import { formatBRL, fromCents, toCents } from '@/lib/money';
import { formatPhone, maskBR, toE164 } from '@/lib/phone';
import { paymentMethodNames } from '@/lib/strings';
import { useIsOnline } from '@/stores/connectivity';
import { useSession } from '@/stores/session';

import { confirmLines, fixedMenuBook, type ALaCarteLine } from '../aLaCarte';
import { ALaCarteLineCard, ALaCarteLineSheet, FixedMenuPicker, lineEditable } from '../aLaCarteComponents';
import {
  addressText,
  buildDeliveryBody,
  changeToCarryText,
  deliveryFeeCents,
  deliveryFormIssues,
  emptyDeliveryForm,
  fieldMessage,
  firstDeliveryIssue,
  previewDeliveryCents,
  type DeliveryField,
  type DeliveryForm,
} from '../delivery';
import { AddressFields, DeliveryFeeField, ExpectedPaymentFields } from '../deliveryComponents';
import { DraftOffer, SubmitFooter } from '../formParts';
import { useCreateOrder } from '../hooks';

/**
 * 08 §8.9 `orders/new/delivery`: pedido por telefone da lanchonete (EP-003 fatia 4, ADR-0059). Telefone primeiro
 * (`POST /customers/lookup` preenche o nome), entrega ou retirada, endereço e taxa digitada na criação, forma
 * prevista e "troco para", e os itens com o mesmo montador da comanda à la carte. Entra aceito, com número e na
 * cozinha. A forma prevista não registra pagamento.
 */
export default function NewDeliveryScreen() {
  const c = useColors();
  const timeZone = useSession((s) => s.me?.tenant.timeZone ?? 'America/Sao_Paulo');
  const online = useIsOnline();
  const fixedMenu = useFixedMenu(false);
  const settings = useSettings();
  const draft = useDraft<DeliveryForm>('NEW_ORDER_DELIVERY');
  const form = useForm<DeliveryForm>({ defaultValues: emptyDeliveryForm() });
  const v = useWatch({ control: form.control }) as DeliveryForm;
  const [sheet, setSheet] = useState<{ line: ALaCarteLine; editing: boolean } | null>(null);
  const [confirming, setConfirming] = useState(false);
  // Depois do primeiro "Registrar", os problemas são recalculados a cada mudança.
  const [tried, setTried] = useState(false);

  const book = useMemo(() => fixedMenuBook(fixedMenu.data ?? EMPTY_MENU), [fixedMenu.data]);
  const defaultFee = settings.data?.defaultDeliveryFee ?? null;
  const methods = settings.data?.enabledPaymentMethods;
  const preview = previewDeliveryCents(v, book, defaultFee);
  const create = useCreateOrder({ draftId: draft.draftId, previewCents: preview, onCreated: (o) => router.replace(`/orders/${o.id}`) });

  const { save } = draft;
  useEffect(() => {
    if (form.formState.isDirty) save(v);
  }, [v, save, form.formState.isDirty]);

  const set = <K extends keyof DeliveryForm>(key: K, value: DeliveryForm[K]) => form.setValue(key, value as never, { shouldDirty: true });
  const putLine = (line: ALaCarteLine) => set('lines', v.lines.some((l) => l.key === line.key) ? v.lines.map((l) => (l.key === line.key ? line : l)) : [...v.lines, line]);

  // Telefone completo → cliente do telefone (ADR-0059 item 14). O nome é preenchido uma vez por cliente achado e só
  // com o campo vazio; o que a pessoa digitou vale no pedido, e o cadastro não muda (Q-314).
  const phoneE164 = toE164(v.phone);
  const lookup = useCustomerByPhone(phoneE164);
  const found = phoneE164 ? (lookup.data?.customer ?? null) : null;
  const lastAddress = phoneE164 ? (lookup.data?.lastDeliveryAddress ?? null) : null;
  const filledFor = useRef<string | null>(null);
  useEffect(() => {
    if (!found || filledFor.current === found.id) return;
    filledFor.current = found.id;
    if (form.getValues('name').trim() === '') form.setValue('name', found.name, { shouldDirty: true });
  }, [found, form]);

  const delivery = v.fulfillmentType === 'DELIVERY';
  const issues = tried ? deliveryFormIssues(v, book, preview, methods) : null;
  const server = create.fieldErrors;
  const err = (field: DeliveryField, serverKey: string) => issues?.[field] ?? fieldMessage(server, serverKey);

  const review = () => {
    setTried(true);
    if (!firstDeliveryIssue(deliveryFormIssues(v, book, preview, methods))) setConfirming(true);
  };

  const submit = async () => {
    await draft.flush(v);
    const outcome = await create.run(buildDeliveryBody(v, book));
    if (outcome.kind !== 'unknown' && outcome.kind !== 'ignored') setConfirming(false);
  };

  const feeCents = deliveryFeeCents(v, defaultFee);
  const changeLine = changeToCarryText({ expectedPaymentMethod: v.expectedPaymentMethod, changeFor: v.changeFor, balance: fromCents(preview), isPaid: false });
  const nameDiffers = found && v.name.trim() !== '' && v.name.trim() !== found.name;
  const issue = issues ? firstDeliveryIssue(issues) : null;

  return (
    <KeyboardAvoidingView style={{ flex: 1 }} behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
      <Stack.Screen options={{ title: 'Pedido por telefone' }} />
      <Screen>
        <DraftOffer
          offer={draft.offer}
          timeZone={timeZone}
          onResume={() => {
            const d = draft.resume();
            if (d) form.reset({ ...emptyDeliveryForm(), ...d });
          }}
          onDiscard={() => void draft.discardOffer()}
        />

        <Section title="Cliente">
          <TextField label="Telefone" value={v.phone} onChangeText={(t) => set('phone', maskBR(t))} keyboardType="phone-pad" error={err('phone', 'customerPhone')} placeholder="(34) 99999-1234" />
          {found ? (
            <Text variant="small" muted testID="phone-customer">
              Cliente cadastrado: {found.name}
              {toCents(found.pendingAmount) > 0 ? ` · Deve ${formatBRL(found.pendingAmount)}` : ''}
            </Text>
          ) : phoneE164 && lookup.data ? (
            <Text variant="small" muted>
              Telefone novo: o cliente é cadastrado ao registrar o pedido.
            </Text>
          ) : null}
          <TextField label="Nome" value={v.name} onChangeText={(t) => set('name', t)} autoCapitalize="words" error={err('name', 'customerName')} />
          {nameDiffers ? <Banner tone="info" text={`O pedido vai com o nome digitado. O cadastro continua "${found.name}" (muda só em Clientes).`} /> : null}
        </Section>

        <Section title="Atendimento">
          <SegmentedControl
            value={v.fulfillmentType}
            onChange={(f) => set('fulfillmentType', f)}
            options={[
              { value: 'DELIVERY', label: 'Entrega' },
              { value: 'PICKUP', label: 'Retirada' },
            ]}
          />
          {delivery ? (
            <View style={{ gap: space.sm }}>
              {lastAddress ? (
                <Card>
                  <Text variant="small" muted>
                    Endereço do último pedido
                  </Text>
                  <Text>{addressText(lastAddress)}</Text>
                  <Button title="Usar endereço do último pedido" icon="location-outline" variant="secondary" onPress={() => set('address', { ...lastAddress })} />
                </Card>
              ) : null}
              <AddressFields value={v.address} onChange={(a) => set('address', a)} streetError={err('street', 'deliveryAddress')} />
              <DeliveryFeeField value={v.deliveryFee} defaultFee={defaultFee} onChange={(fee) => set('deliveryFee', fee)} error={fieldMessage(server, 'deliveryFee')} />
            </View>
          ) : (
            <Text variant="small" muted>
              Retirada no balcão: sem endereço e sem taxa.
            </Text>
          )}
        </Section>

        {v.lines.length ? (
          <Section title="No pedido">
            {v.lines.map((l) => (
              <ALaCarteLineCard
                key={l.key}
                line={l}
                book={book}
                onChange={putLine}
                onEdit={lineEditable(l, book) ? () => setSheet({ line: l, editing: true }) : undefined}
                onRemove={() => set('lines', v.lines.filter((x) => x.key !== l.key))}
              />
            ))}
          </Section>
        ) : null}
        {issues?.lines ? <Text style={{ color: c.danger }}>{issues.lines}</Text> : null}

        {fixedMenu.isLoading ? <Skeleton lines={4} /> : null}
        {fixedMenu.isError ? <ErrorState message={errorMessage(fixedMenu.error)} onRetry={() => void fixedMenu.refetch()} /> : null}
        {fixedMenu.data ? <FixedMenuPicker book={book} onPick={(line) => setSheet({ line, editing: false })} onAddDirect={putLine} /> : null}

        <Section title="Pagamento">
          <ExpectedPaymentFields
            fulfillmentType={v.fulfillmentType}
            value={{ expectedPaymentMethod: v.expectedPaymentMethod, changeFor: v.changeFor }}
            onChange={(p) => {
              set('expectedPaymentMethod', p.expectedPaymentMethod);
              set('changeFor', p.changeFor);
            }}
            totalCents={preview}
            methods={methods}
            errors={{ method: err('expectedPaymentMethod', 'expectedPaymentMethod'), changeFor: err('changeFor', 'changeFor') }}
          />
        </Section>

        <View style={{ gap: space.sm }}>
          <TextField label="Observações do pedido" value={v.notes} onChangeText={(n) => set('notes', n)} multiline />
          {issue ? <Banner tone="danger" text={issue} /> : null}
        </View>
      </Screen>

      <SubmitFooter
        previewCents={preview}
        label={preview > 0 ? `Registrar ${formatBRL(preview)}` : 'Registrar pedido'}
        online={online}
        loading={create.busy}
        unknown={create.state === 'unknown'}
        error={create.error}
        onPress={review}
      />

      {sheet ? <ALaCarteLineSheet key={sheet.line.key} initial={sheet.line} editing={sheet.editing} book={book} onSave={putLine} onClose={() => setSheet(null)} /> : null}

      <ConfirmDialog
        visible={confirming}
        title="Registrar pedido por telefone?"
        lines={[
          { label: 'Cliente', value: `${v.name.trim()} · ${formatPhone(phoneE164)}` },
          { label: 'Atendimento', value: delivery ? 'Entrega' : 'Retirada' },
          ...(delivery ? [{ label: 'Endereço', value: addressText(v.address) }] : []),
          ...confirmLines(book, v.lines),
          ...(delivery ? [{ label: v.deliveryFee !== null ? 'Taxa de entrega (digitada)' : 'Taxa de entrega', value: formatBRL(feeCents) }] : []),
          ...(v.expectedPaymentMethod ? [{ label: 'Forma prevista', value: paymentMethodNames[v.expectedPaymentMethod] }] : []),
          ...(changeLine ? [{ label: 'Troco', value: changeLine }] : []),
          { label: 'Prévia do total', value: formatBRL(preview) },
        ]}
        warning={create.state === 'unknown' ? 'Não sabemos se o pedido foi registrado. Verificando antes de qualquer reenvio…' : create.error}
        confirmLabel={`Registrar ${formatBRL(preview)}`}
        loading={create.busy}
        onCancel={() => setConfirming(false)}
        onConfirm={() => void submit()}
      >
        <Text variant="small" muted>
          Preço de balcão. O total oficial vem do servidor. A forma prevista não registra pagamento.
        </Text>
      </ConfirmDialog>
    </KeyboardAvoidingView>
  );
}
