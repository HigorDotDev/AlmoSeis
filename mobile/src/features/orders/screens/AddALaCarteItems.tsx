/**
 * `orders/[id]/add-items` de uma comanda à la carte (08 §8.9, 06 §6.9 `POST /orders/{id}/items` [I][M]): pratos,
 * bebidas e combos do cardápio fixo e o adicional de grupo num prato já registrado (prato avulso ou parte **de prato**
 * de um combo, ADR-0058 item 10), tudo numa só confirmação (RN-012.3). Não há parte nova num combo registrado, e
 * trocar o sabor de uma parte é remover e incluir o combo (Q-322).
 */
import { router, Stack } from 'expo-router';
import { useMemo, useState } from 'react';

import { errorMessage } from '@/api/errors';
import type { Order } from '@/api/types';
import { Banner, Button, Chip, ConfirmDialog, ConflictSheet, ErrorState, Row, Screen, Section, Skeleton, Text } from '@/components/ui';
import { useDraft } from '@/features/drafts/useDraft';
import { EMPTY_MENU } from '@/features/fixedMenu/fixedMenu';
import { useFixedMenu } from '@/features/fixedMenu/hooks';
import { formatBRL } from '@/lib/money';
import { useIsOnline } from '@/stores/connectivity';
import { useSession } from '@/stores/session';

import {
  addItemsBody,
  addItemsConfirmLines,
  addonTargets,
  dishAddonGroups,
  emptyALaCarteAddItems,
  fixedMenuBook,
  lineLabel,
  lineMissing,
  previewAddItemsCents,
  type ALaCarteAddItemsForm,
  type ALaCarteLine,
} from '../aLaCarte';
import { ALaCarteLineCard, ALaCarteLineSheet, FixedMenuPicker, GroupAddonPicker, lineEditable } from '../aLaCarteComponents';
import { DraftOffer } from '../formParts';
import { isDispatched } from '../fulfillment';
import { summarizeOrder, useAddItems } from '../hooks';

export function AddALaCarteItems({ order: o }: { order: Order }) {
  const online = useIsOnline();
  const timeZone = useSession((s) => s.me?.tenant.timeZone ?? 'America/Sao_Paulo');
  const fixedMenu = useFixedMenu(false);
  const book = useMemo(() => fixedMenuBook(fixedMenu.data ?? EMPTY_MENU), [fixedMenu.data]);
  const draft = useDraft<ALaCarteAddItemsForm>('ADD_ITEMS', o.id);
  const [form, setForm] = useState<ALaCarteAddItemsForm>(emptyALaCarteAddItems());
  const [sheet, setSheet] = useState<{ line: ALaCarteLine; editing: boolean } | null>(null);
  const [addonFor, setAddonFor] = useState<string | null>(null);
  const [confirm, setConfirm] = useState(false);
  const [issue, setIssue] = useState<string | null>(null);
  const [conflict, setConflict] = useState<Order | null>(null);
  const add = useAddItems(o, draft.draftId, () => router.back());
  const dispatched = isDispatched(o.fulfillmentStatus);

  const update = (next: ALaCarteAddItemsForm) => {
    setForm(next);
    draft.save(next);
  };
  const putLine = (line: ALaCarteLine) =>
    update({ ...form, lines: form.lines.some((l) => l.key === line.key) ? form.lines.map((l) => (l.key === line.key ? line : l)) : [...form.lines, line] });

  const targets = addonTargets(o);
  const target = targets.find((t) => t.item.id === addonFor) ?? null;
  const items = addItemsBody(form, book);
  const preview = previewAddItemsCents(form, book, o);

  const review = () => {
    const problem = form.lines.map((l) => [l, lineMissing(l, book)] as const).find(([, m]) => m);
    setIssue(problem ? `${lineLabel(book, problem[0])}: ${problem[1]}` : null);
    if (!problem) setConfirm(true);
  };

  return (
    <Screen>
      <Stack.Screen options={{ title: `Adicionar itens · #${o.orderNumber}` }} />
      <DraftOffer offer={draft.offer} timeZone={timeZone} onResume={() => { const d = draft.resume(); if (d) setForm(d); }} onDiscard={() => void draft.discardOffer()} />
      {form.lines.length ? (
        <Section title="Para incluir">
          {form.lines.map((l) => (
            <ALaCarteLineCard
              key={l.key}
              line={l}
              book={book}
              onChange={putLine}
              onEdit={lineEditable(l, book) ? () => setSheet({ line: l, editing: true }) : undefined}
              onRemove={() => update({ ...form, lines: form.lines.filter((x) => x.key !== l.key) })}
            />
          ))}
        </Section>
      ) : null}

      {fixedMenu.isLoading ? <Skeleton lines={4} /> : null}
      {fixedMenu.isError ? <ErrorState message={errorMessage(fixedMenu.error)} onRetry={() => void fixedMenu.refetch()} /> : null}
      {fixedMenu.data ? <FixedMenuPicker book={book} onPick={(line) => setSheet({ line, editing: false })} onAddDirect={putLine} /> : null}

      {targets.length && fixedMenu.data ? (
        <Section title="Adicionais em pratos do pedido">
          <Row style={{ flexWrap: 'wrap' }}>
            {targets
              .filter((t) => dishAddonGroups(book, t.item.dishId ?? null).length > 0)
              .map((t) => (
                <Chip key={t.item.id} label={t.label} selected={addonFor === t.item.id} onPress={() => setAddonFor(addonFor === t.item.id ? null : t.item.id)} />
              ))}
          </Row>
          {target ? (
            <GroupAddonPicker
              groups={dishAddonGroups(book, target.item.dishId ?? null)}
              value={form.addons[target.item.id] ?? []}
              label={target.item.name}
              onChange={(list) => update({ ...form, addons: { ...form.addons, [target.item.id]: list } })}
            />
          ) : (
            <Text variant="small" muted>
              Toque num prato para incluir adicionais nele. Cobrados pela quantidade do prato.
            </Text>
          )}
        </Section>
      ) : null}

      {dispatched ? <Banner tone="warning" text="Este pedido já saiu (ou foi retirado). Para incluir itens, crie outro pedido." /> : null}
      {issue ? <Banner tone="danger" text={issue} /> : null}
      {add.error ? <Banner tone="danger" text={add.error} /> : null}
      {add.state === 'unknown' ? <Banner tone="warning" text="Não sabemos se os itens foram incluídos. Verificando…" /> : null}
      <Button
        title={`Incluir ${items.length} ${items.length === 1 ? 'item' : 'itens'} · ${formatBRL(preview)}`}
        disabled={items.length === 0 || !online || add.state === 'unknown' || dispatched}
        onPress={review}
      />

      {sheet ? <ALaCarteLineSheet key={sheet.line.key} initial={sheet.line} editing={sheet.editing} book={book} onSave={putLine} onClose={() => setSheet(null)} /> : null}

      <ConfirmDialog
        visible={confirm}
        title={`Incluir no pedido #${o.orderNumber}?`}
        lines={[...addItemsConfirmLines(form, book, o), { label: 'Prévia', value: formatBRL(preview) }]}
        confirmLabel="Incluir itens"
        loading={add.busy}
        warning={add.state === 'unknown' ? 'Não sabemos se os itens foram incluídos. Verificando…' : add.error}
        onCancel={() => setConfirm(false)}
        onConfirm={async () => {
          await draft.flush(form);
          const outcome = await add.run({ items, version: o.version });
          if (outcome.kind === 'conflict') setConflict(outcome.current as Order);
          if (outcome.kind !== 'unknown' && outcome.kind !== 'ignored') setConfirm(false);
        }}
      />
      <ConflictSheet visible={conflict !== null} summary={conflict ? summarizeOrder(conflict) : []} onClose={() => setConflict(null)} onRedo={() => setConflict(null)} />
    </Screen>
  );
}
