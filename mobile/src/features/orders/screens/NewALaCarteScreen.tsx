import { router, Stack } from 'expo-router';
import { useEffect, useMemo, useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { KeyboardAvoidingView, Platform, View } from 'react-native';

import { errorMessage } from '@/api/errors';
import { Banner, ConfirmDialog, ErrorState, Row, Screen, Section, Skeleton, Text, TextField, space } from '@/components/ui';
import { useDraft } from '@/features/drafts/useDraft';
import { EMPTY_MENU } from '@/features/fixedMenu/fixedMenu';
import { useFixedMenu } from '@/features/fixedMenu/hooks';
import { formatBRL } from '@/lib/money';
import { useIsOnline } from '@/stores/connectivity';
import { useSession } from '@/stores/session';

import { aLaCarteFormIssue, buildALaCarteBody, confirmLines, emptyALaCarteForm, fixedMenuBook, previewALaCarteCents, type ALaCarteForm, type ALaCarteLine } from '../aLaCarte';
import { ALaCarteLineCard, ALaCarteLineSheet, FixedMenuPicker, lineEditable } from '../aLaCarteComponents';
import { DraftOffer, SubmitFooter } from '../formParts';
import { useCreateOrder } from '../hooks';

/**
 * 08 §8.9 `orders/new/a-la-carte`: abrir comanda à la carte no salão (ADR-0058 item 5), no fluxo do PF: mesa e/ou
 * comanda, itens só do cardápio fixo (preço de balcão), "Abrir comanda" → Confirma → `POST /orders` [I].
 */
export default function NewALaCarteScreen() {
  const timeZone = useSession((s) => s.me?.tenant.timeZone ?? 'America/Sao_Paulo');
  const online = useIsOnline();
  // Leitura de venda: sem os inativos; pausados vêm com `isSellable: false` e aparecem desabilitados.
  const fixedMenu = useFixedMenu(false);
  const draft = useDraft<ALaCarteForm>('NEW_ORDER_A_LA_CARTE');
  const form = useForm<ALaCarteForm>({ defaultValues: emptyALaCarteForm() });
  const v = useWatch({ control: form.control }) as ALaCarteForm;
  const [sheet, setSheet] = useState<{ line: ALaCarteLine; editing: boolean } | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [issue, setIssue] = useState<string | null>(null);

  const book = useMemo(() => fixedMenuBook(fixedMenu.data ?? EMPTY_MENU), [fixedMenu.data]);
  const preview = previewALaCarteCents(v, book);
  const create = useCreateOrder({ draftId: draft.draftId, previewCents: preview, onCreated: (o) => router.replace(`/orders/${o.id}`) });

  const { save } = draft;
  useEffect(() => {
    if (form.formState.isDirty) save(v);
  }, [v, save, form.formState.isDirty]);

  const set = <K extends keyof ALaCarteForm>(key: K, value: ALaCarteForm[K]) => form.setValue(key, value as never, { shouldDirty: true });
  const putLine = (line: ALaCarteLine) => set('lines', v.lines.some((l) => l.key === line.key) ? v.lines.map((l) => (l.key === line.key ? line : l)) : [...v.lines, line]);

  const review = () => {
    const problem = aLaCarteFormIssue(v, book);
    setIssue(problem);
    if (!problem) setConfirming(true);
  };

  const submit = async () => {
    await draft.flush(v);
    const outcome = await create.run(buildALaCarteBody(v, book));
    if (outcome.kind !== 'unknown' && outcome.kind !== 'ignored') setConfirming(false);
  };

  return (
    <KeyboardAvoidingView style={{ flex: 1 }} behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
      <Stack.Screen options={{ title: 'Abrir comanda' }} />
      <Screen>
        <DraftOffer offer={draft.offer} timeZone={timeZone} onResume={() => { const d = draft.resume(); if (d) form.reset(d); }} onDiscard={() => void draft.discardOffer()} />
        <Row>
          <View style={{ flex: 1 }}>
            <TextField label="Mesa" value={v.tableLabel} onChangeText={(t) => set('tableLabel', t)} keyboardType="number-pad" />
          </View>
          <View style={{ flex: 1 }}>
            <TextField label="Comanda" value={v.tabNumber} onChangeText={(t) => set('tabNumber', t)} keyboardType="number-pad" />
          </View>
        </Row>
        <TextField label="Nome do cliente (opcional)" value={v.customerName} onChangeText={(t) => set('customerName', t)} />

        {v.lines.length ? (
          <Section title="Na comanda">
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

        {fixedMenu.isLoading ? <Skeleton lines={4} /> : null}
        {fixedMenu.isError ? <ErrorState message={errorMessage(fixedMenu.error)} onRetry={() => void fixedMenu.refetch()} /> : null}
        {fixedMenu.data ? <FixedMenuPicker book={book} onPick={(line) => setSheet({ line, editing: false })} onAddDirect={putLine} /> : null}

        <View style={{ gap: space.sm }}>
          <TextField label="Observações da comanda" value={v.notes} onChangeText={(n) => set('notes', n)} multiline />
          {issue ? <Banner tone="danger" text={issue} /> : null}
        </View>
      </Screen>

      <SubmitFooter previewCents={preview} label="Abrir comanda" online={online} loading={create.busy} unknown={create.state === 'unknown'} error={create.error} onPress={review} />

      {sheet ? <ALaCarteLineSheet key={sheet.line.key} initial={sheet.line} editing={sheet.editing} book={book} onSave={putLine} onClose={() => setSheet(null)} /> : null}

      <ConfirmDialog
        visible={confirming}
        title="Abrir comanda?"
        lines={[
          { label: 'Mesa / comanda', value: [v.tableLabel && `Mesa ${v.tableLabel}`, v.tabNumber && `Comanda ${v.tabNumber}`].filter(Boolean).join(' · ') },
          ...(v.customerName.trim() ? [{ label: 'Cliente', value: v.customerName.trim() }] : []),
          ...confirmLines(book, v.lines),
          { label: 'Prévia do total', value: formatBRL(preview) },
        ]}
        warning={create.state === 'unknown' ? 'Não sabemos se o pedido foi registrado. Verificando…' : create.error}
        confirmLabel={`Abrir comanda · ${formatBRL(preview)}`}
        loading={create.busy}
        onCancel={() => setConfirming(false)}
        onConfirm={() => void submit()}
      >
        <Text variant="small" muted>
          Preço de balcão. O total oficial vem do servidor.
        </Text>
      </ConfirmDialog>
    </KeyboardAvoidingView>
  );
}
