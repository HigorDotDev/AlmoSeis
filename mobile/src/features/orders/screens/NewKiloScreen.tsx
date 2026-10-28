import { router, Stack } from 'expo-router';
import { useEffect, useMemo, useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { KeyboardAvoidingView, Platform, View } from 'react-native';

import { Banner, Button, Card, ConfirmDialog, IconButton, Row, Screen, Section, Text, TextField, WeightInput } from '@/components/ui';
import { useProducts } from '@/features/catalog/hooks';
import { useDraft } from '@/features/drafts/useDraft';
import { useSettings } from '@/features/session/hooks';
import { formatBRL } from '@/lib/money';
import { gramsToKgLabel, previewKiloAmountCents } from '@/lib/weight';
import { useIsOnline } from '@/stores/connectivity';
import { usePermission, useSession } from '@/stores/session';

import { beverageLines, buildKiloBody, emptyKiloForm, previewKiloCents, priceBook, type KiloForm } from '../compose';
import { BeverageGrid, DraftOffer, SubmitFooter } from '../formParts';
import { useCreateOrder } from '../hooks';
import { firstIssue, kiloFormSchema } from '../schemas';

/** 08 §8.9 `orders/new/kilo`: mesa e comanda, pesagens em kg → gramas, prévia pelo preço/kg. */
export default function NewKiloScreen() {
  const timeZone = useSession((s) => s.me?.tenant.timeZone ?? 'America/Sao_Paulo');
  const online = useIsOnline();
  const canPricing = usePermission('settings.manage');
  const products = useProducts();
  const settings = useSettings();
  const draft = useDraft<KiloForm>('NEW_ORDER_KILO');
  const form = useForm<KiloForm>({ defaultValues: emptyKiloForm() });
  const v = useWatch({ control: form.control }) as KiloForm;
  const [grams, setGrams] = useState<number | null>(null);
  const [inputKey, setInputKey] = useState(0);
  const [confirming, setConfirming] = useState(false);
  const [issue, setIssue] = useState<string | null>(null);

  const pricePerKg = settings.data?.kiloPricePerKg ?? null;
  const book = useMemo(() => priceBook([], products.data), [products.data]);
  const preview = previewKiloCents(v, book, pricePerKg);
  const beverages = (products.data ?? []).filter((p) => p.kind === 'BEVERAGE' && p.isActive && p.isAvailable);
  const create = useCreateOrder({ draftId: draft.draftId, previewCents: preview, onCreated: (o) => router.replace(`/orders/${o.id}`) });

  const { save } = draft;
  useEffect(() => {
    if (form.formState.isDirty) save(v);
  }, [v, save, form.formState.isDirty]);

  const set = <K extends keyof KiloForm>(key: K, value: KiloForm[K]) => form.setValue(key, value as never, { shouldDirty: true });

  const addWeighing = () => {
    if (grams === null) return;
    set('weighings', [...v.weighings, grams]);
    setGrams(null);
    setInputKey((k) => k + 1);
  };

  const review = () => {
    const parsed = kiloFormSchema.safeParse(v);
    setIssue(firstIssue(parsed));
    if (parsed.success) setConfirming(true);
  };

  const submit = async () => {
    await draft.flush(v);
    const outcome = await create.run(buildKiloBody(v));
    if (outcome.kind !== 'unknown') setConfirming(false);
  };

  return (
    <KeyboardAvoidingView style={{ flex: 1 }} behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
      <Stack.Screen options={{ title: 'Nova comanda (quilo)' }} />
      <Screen>
        <DraftOffer offer={draft.offer} timeZone={timeZone} onResume={() => { const d = draft.resume(); if (d) form.reset(d); }} onDiscard={() => void draft.discardOffer()} />
        {!pricePerKg && settings.isSuccess ? (
          <Banner tone="warning" text="Configure o preço do quilo antes de registrar pesagens." action={canPricing ? { label: 'Configurar preços', onPress: () => router.push('/menu/pricing') } : undefined} />
        ) : null}
        <Row>
          <View style={{ flex: 1 }}>
            <TextField label="Mesa" value={v.tableLabel} onChangeText={(t) => set('tableLabel', t)} keyboardType="number-pad" />
          </View>
          <View style={{ flex: 1 }}>
            <TextField label="Comanda" value={v.tabNumber} onChangeText={(t) => set('tabNumber', t)} keyboardType="number-pad" />
          </View>
        </Row>

        <Section title={`Pesagens${pricePerKg ? ` · ${formatBRL(pricePerKg)}/kg` : ''}`}>
          <WeightInput key={inputKey} label="Peso no prato" onChange={setGrams} />
          <Button title="Adicionar pesagem" icon="add" variant="secondary" disabled={grams === null || !pricePerKg} onPress={addWeighing} />
          {v.weighings.map((g, i) => (
            <Card key={`${g}-${i}`}>
              <Row style={{ justifyContent: 'space-between' }}>
                <Text>{gramsToKgLabel(g)}</Text>
                <Text>{pricePerKg ? formatBRL(previewKiloAmountCents(g, pricePerKg)) : '—'}</Text>
                <IconButton icon="trash-outline" label="Remover pesagem" onPress={() => set('weighings', v.weighings.filter((_, j) => j !== i))} />
              </Row>
            </Card>
          ))}
        </Section>

        <Section title="Bebidas (opcional)">
          <BeverageGrid beverages={beverages} value={v.beverages} onChange={(b) => set('beverages', b)} />
        </Section>
        {issue ? <Banner tone="danger" text={issue} /> : null}
      </Screen>

      <SubmitFooter previewCents={preview} label="Registrar comanda" online={online} loading={create.busy} unknown={create.state === 'unknown'} error={create.error} onPress={review} />

      <ConfirmDialog
        visible={confirming}
        title="Registrar comanda?"
        lines={[
          { label: 'Mesa / comanda', value: [v.tableLabel && `Mesa ${v.tableLabel}`, v.tabNumber && `Comanda ${v.tabNumber}`].filter(Boolean).join(' · ') },
          ...v.weighings.map((g, i) => ({ label: `Pesagem ${i + 1}: ${gramsToKgLabel(g)}`, value: pricePerKg ? formatBRL(previewKiloAmountCents(g, pricePerKg)) : '—' })),
          ...beverageLines(book, v.beverages),
          { label: 'Prévia do total', value: formatBRL(preview) },
        ]}
        warning={create.state === 'unknown' ? 'Não sabemos se a comanda foi registrada. Verificando…' : create.error}
        confirmLabel={`Registrar ${formatBRL(preview)}`}
        loading={create.busy}
        onCancel={() => setConfirming(false)}
        onConfirm={() => void submit()}
      />
    </KeyboardAvoidingView>
  );
}
