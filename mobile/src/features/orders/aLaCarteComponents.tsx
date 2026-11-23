/**
 * Peças do pedido à la carte (EP-003 fatia 3, 08 §8.9): o cardápio fixo para escolher, a folha de montagem de um
 * item (prato → tamanho → adicionais → observação; bebida → sabor; combo → cada parte) e o card da linha.
 * O que não está à venda aparece desabilitado, com o motivo que o servidor derivou (`isSellable`, ADR-0056 item 23).
 */
import { useState } from 'react';
import { View } from 'react-native';

import type { AddonGroup, BeverageSize, Combo, Dish } from '@/api/types';
import { Button, BottomSheet, Card, Chip, IconButton, QuantityStepper, Row, Section, Text, TextField, space, useColors } from '@/components/ui';
import { dishPriceSummary, groupDishes } from '@/features/fixedMenu/fixedMenu';
import { formatBRL, toCents } from '@/lib/money';
import { newKey } from '@/lib/ids';

import {
  activeDishSizes,
  activeFlavors,
  beverageSizeUnavailableReason,
  comboUnavailableReason,
  dishAddonGroups,
  dishUnavailableReason,
  flavorSellable,
  lineCents,
  lineDetails,
  lineLabel,
  lineMissing,
  MAX_GROUP_ADDON_QTY,
  MAX_LINE_QTY,
  needsFlavor,
  newBeverageLine,
  newComboLine,
  newDishLine,
  optionSellable,
  setAddonQty,
  type ALaCarteLine,
  type ComboLine,
  type ComboPartChoice,
  type FixedMenuBook,
  type GroupAddonLine,
} from './aLaCarte';

const extraText = (price: string) => (toCents(price) > 0 ? ` + ${formatBRL(price)}` : '');

/** Um card do cardápio: nome, preço e, fora de venda, o motivo (riscado e sem toque). */
function MenuTile({ title, price, reason, onPress }: { title: string; price: string; reason: string | null; onPress: () => void }) {
  const c = useColors();
  const off = reason !== null;
  const body = (
    <>
      <Text style={{ fontWeight: '700', textDecorationLine: off ? 'line-through' : 'none' }}>{title}</Text>
      <Text variant="small" muted>
        {price}
      </Text>
      {off ? (
        <Text variant="small" style={{ color: c.danger }}>
          {reason}
        </Text>
      ) : null}
    </>
  );
  if (!off) {
    return (
      <Card onPress={onPress} accessibilityLabel={`Adicionar ${title}`} style={{ width: '48%' }}>
        {body}
      </Card>
    );
  }
  // Fora de venda: sem toque, e o leitor de tela diz o motivo.
  return (
    <View accessible accessibilityLabel={`${title}, ${reason}`} accessibilityState={{ disabled: true }} style={{ width: '48%', opacity: 0.5 }}>
      <Card>{body}</Card>
    </View>
  );
}

const grid = { flexDirection: 'row', flexWrap: 'wrap', gap: space.sm } as const;

/**
 * O cardápio fixo para montar o pedido, nas três abas do cadastro (Pratos, Bebidas, Combos; ADR-0056 item 3).
 * Bebida sem sabor entra direto (o stepper fica na linha); o resto abre a folha de montagem.
 */
export function FixedMenuPicker({ book, onPick, onAddDirect }: { book: FixedMenuBook; onPick: (line: ALaCarteLine) => void; onAddDirect: (line: ALaCarteLine) => void }) {
  const groups = groupDishes(book.menu, false).filter((g) => g.dishes.length > 0);
  const beverageTypes = book.menu.beverageTypes.filter((t) => t.isActive && t.sizes.some((s) => s.isActive));
  const combos = book.menu.combos.filter((c) => c.isActive);

  const pickDish = (dish: Dish) => onPick(newDishLine(newKey(), dish));
  const pickBeverage = (size: BeverageSize) => {
    const line = newBeverageLine(newKey(), size);
    if (needsFlavor(size)) onPick(line);
    else onAddDirect(line);
  };
  const pickCombo = (combo: Combo) => onPick(newComboLine(newKey(), combo, book));

  if (groups.length === 0 && beverageTypes.length === 0 && combos.length === 0) {
    return <Text muted>O cardápio da lanchonete está vazio. Cadastre pratos, bebidas ou combos no menu “Cardápio”.</Text>;
  }

  return (
    <View style={{ gap: space.md }}>
      {groups.length ? (
        <Section title="Pratos">
          {groups.map((g) => (
            <View key={g.category?.id ?? 'other'} style={{ gap: space.sm }}>
              {g.category ? <Text variant="small" muted style={{ fontWeight: '700' }}>{g.category.name}</Text> : null}
              <View style={grid}>
                {g.dishes.map((d) => (
                  <MenuTile key={d.id} title={d.name} price={dishPriceSummary(d)} reason={dishUnavailableReason(d)} onPress={() => pickDish(d)} />
                ))}
              </View>
            </View>
          ))}
        </Section>
      ) : null}
      {beverageTypes.length ? (
        <Section title="Bebidas">
          {beverageTypes.map((t) => (
            <View key={t.id} style={{ gap: space.sm }}>
              <Text variant="small" muted style={{ fontWeight: '700' }}>{t.name}</Text>
              <View style={grid}>
                {t.sizes
                  .filter((s) => s.isActive)
                  .map((s) => (
                    <MenuTile key={s.id} title={`${t.name} ${s.name}`} price={formatBRL(s.price)} reason={beverageSizeUnavailableReason(s)} onPress={() => pickBeverage(s)} />
                  ))}
              </View>
            </View>
          ))}
        </Section>
      ) : null}
      {combos.length ? (
        <Section title="Combos">
          <View style={grid}>
            {combos.map((c) => (
              <MenuTile key={c.id} title={c.name} price={formatBRL(c.price)} reason={comboUnavailableReason(c)} onPress={() => pickCombo(c)} />
            ))}
          </View>
        </Section>
      ) : null}
    </View>
  );
}

/** Opções dos grupos de adicionais do prato, com quantidade de 0 a 10; pausada aparece desabilitada. */
export function GroupAddonPicker({ groups, value, onChange, label }: { groups: AddonGroup[]; value: readonly GroupAddonLine[]; onChange: (next: GroupAddonLine[]) => void; label?: string }) {
  const c = useColors();
  if (groups.length === 0) return null;
  const qty = (id: string) => value.find((a) => a.addonOptionId === id)?.quantity ?? 0;
  return (
    <View style={{ gap: space.sm }}>
      {groups.map((g) => (
        <View key={g.id} style={{ gap: space.xs }}>
          <Text variant="small" muted style={{ fontWeight: '700' }}>
            {g.name}
            {label ? ` · ${label}` : ''}
          </Text>
          {g.options.map((o) => {
            const ok = optionSellable(o);
            return (
              <Row key={o.id} style={{ justifyContent: 'space-between' }}>
                <View style={{ flex: 1 }}>
                  <Text style={!ok ? { textDecorationLine: 'line-through', color: c.textMuted } : undefined}>
                    {o.name} · {toCents(o.price) > 0 ? `+ ${formatBRL(o.price)}` : 'grátis'}
                  </Text>
                  {!ok ? (
                    <Text variant="small" style={{ color: c.danger }}>
                      Pausado
                    </Text>
                  ) : null}
                </View>
                {ok ? <QuantityStepper label={o.name} value={qty(o.id)} max={MAX_GROUP_ADDON_QTY} onChange={(q) => onChange(setAddonQty(value, o.id, q))} /> : null}
              </Row>
            );
          })}
        </View>
      ))}
    </View>
  );
}

function FlavorChips({ size, value, onChange }: { size: BeverageSize | undefined; value: string | null; onChange: (id: string) => void }) {
  const flavors = activeFlavors(size);
  if (flavors.length === 0) return null;
  return (
    <Row style={{ flexWrap: 'wrap' }}>
      {flavors.map((f) => {
        const ok = flavorSellable(f);
        return <Chip key={f.id} label={`${f.name}${extraText(f.extraPrice)}${ok ? '' : ' · pausado'}`} selected={value === f.id} disabled={!ok} strike={!ok} onPress={() => onChange(f.id)} />;
      })}
    </Row>
  );
}

/**
 * Montagem de um item (08 §8.9): prato → tamanho (se houver) → adicionais do grupo → observação; bebida → sabor;
 * combo → em cada prato, adicionais e observação próprios, e em cada bebida, o sabor. O estado nasce de `initial`:
 * quem usa remonta pelo `key` a cada abertura.
 */
export function ALaCarteLineSheet({
  initial,
  editing,
  book,
  onSave,
  onClose,
}: {
  initial: ALaCarteLine | null;
  /** `true` quando a linha já está no formulário (o botão diz "Salvar"). */
  editing: boolean;
  book: FixedMenuBook;
  onSave: (line: ALaCarteLine) => void;
  onClose: () => void;
}) {
  const [line, setLine] = useState<ALaCarteLine | null>(initial);
  if (!line) return null;
  const missing = lineMissing(line, book);
  const title = line.kind === 'DISH' ? (book.dishes.get(line.dishId)?.name ?? 'Prato') : lineLabel(book, line);

  return (
    <BottomSheet
      visible
      title={title}
      onClose={onClose}
      footer={
        <View style={{ gap: 4 }}>
          {missing ? (
            <Text variant="small" muted style={{ textAlign: 'center' }}>
              {missing}
            </Text>
          ) : null}
          <Button
            title={`${editing ? 'Salvar' : 'Adicionar'} · ${formatBRL(lineCents(book, line))}`}
            size="large"
            disabled={!!missing}
            onPress={() => {
              onSave(line);
              onClose();
            }}
          />
        </View>
      }
    >
      {line.kind === 'DISH' ? <DishFields line={line} book={book} onChange={setLine} /> : null}
      {line.kind === 'BEVERAGE' ? (
        <>
          <Text variant="subtitle">Sabor</Text>
          <FlavorChips size={book.beverageSizes.get(line.beverageSizeId)?.size} value={line.beverageFlavorId} onChange={(id) => setLine({ ...line, beverageFlavorId: id })} />
        </>
      ) : null}
      {line.kind === 'COMBO' ? <ComboFields line={line} book={book} onChange={setLine} /> : null}
      <Row style={{ justifyContent: 'space-between' }}>
        <Text variant="subtitle">Quantidade</Text>
        <QuantityStepper label={`Quantidade de ${title}`} min={1} max={MAX_LINE_QTY} value={line.quantity} onChange={(quantity) => setLine({ ...line, quantity })} />
      </Row>
    </BottomSheet>
  );
}

function DishFields({ line, book, onChange }: { line: Extract<ALaCarteLine, { kind: 'DISH' }>; book: FixedMenuBook; onChange: (l: ALaCarteLine) => void }) {
  const dish = book.dishes.get(line.dishId);
  const sizes = dish ? activeDishSizes(dish) : [];
  return (
    <>
      {sizes.length ? (
        <>
          <Text variant="subtitle">Tamanho</Text>
          <Row style={{ flexWrap: 'wrap' }}>
            {sizes.map((s) => (
              <Chip
                key={s.id}
                label={`${s.name} · ${formatBRL(s.price)}${s.isSellable ? '' : ' · pausado'}`}
                selected={line.dishSizeId === s.id}
                disabled={!s.isSellable}
                strike={!s.isSellable}
                onPress={() => onChange({ ...line, dishSizeId: s.id })}
              />
            ))}
          </Row>
        </>
      ) : null}
      <GroupAddonPicker groups={dishAddonGroups(book, line.dishId)} value={line.addons} onChange={(addons) => onChange({ ...line, addons })} />
      <TextField label="Observação" value={line.notes} onChangeText={(notes) => onChange({ ...line, notes })} placeholder="Ex.: sem cebola" />
    </>
  );
}

function ComboFields({ line, book, onChange }: { line: ComboLine; book: FixedMenuBook; onChange: (l: ALaCarteLine) => void }) {
  const combo = book.combos.get(line.comboId);
  const setPart = (part: ComboPartChoice) => onChange({ ...line, parts: line.parts.map((p) => (p.comboComponentId === part.comboComponentId ? part : p)) });
  return (
    <>
      {(combo?.components ?? []).map((c) => {
        const part = line.parts.find((p) => p.comboComponentId === c.id) ?? { comboComponentId: c.id, beverageFlavorId: null, notes: '', addons: [] };
        if (c.kind === 'BEVERAGE') {
          const size = c.beverageSizeId ? book.beverageSizes.get(c.beverageSizeId)?.size : undefined;
          return (
            <View key={c.id} style={{ gap: space.xs }}>
              <Text variant="subtitle">
                {c.quantity}× {c.name}
              </Text>
              {needsFlavor(size) ? <FlavorChips size={size} value={part.beverageFlavorId} onChange={(id) => setPart({ ...part, beverageFlavorId: id })} /> : <Text muted>Sem escolha de sabor.</Text>}
            </View>
          );
        }
        return (
          <View key={c.id} style={{ gap: space.xs }}>
            <Text variant="subtitle">
              {c.quantity}× {c.name}
            </Text>
            <GroupAddonPicker groups={dishAddonGroups(book, c.dishId)} value={part.addons} onChange={(addons) => setPart({ ...part, addons })} />
            <TextField label={`Observação · ${c.name}`} value={part.notes} onChangeText={(notes) => setPart({ ...part, notes })} placeholder="Ex.: sem cebola" />
          </View>
        );
      })}
      <Text variant="small" muted>
        Os adicionais são cobrados à parte, como o acréscimo do sabor.
      </Text>
    </>
  );
}

/** Linha do formulário: nome, detalhes (adicionais, observação, partes), prévia, quantidade, editar e remover. */
export function ALaCarteLineCard({ line, book, onChange, onEdit, onRemove }: { line: ALaCarteLine; book: FixedMenuBook; onChange: (l: ALaCarteLine) => void; onEdit?: () => void; onRemove: () => void }) {
  const label = lineLabel(book, line);
  const missing = lineMissing(line, book);
  const c = useColors();
  return (
    <Card>
      <Row style={{ justifyContent: 'space-between' }}>
        <View style={{ flex: 1 }}>
          <Text style={{ fontWeight: '700' }}>{label}</Text>
          {lineDetails(book, line).map((d) => (
            <Text key={d} variant="small" muted>
              {d}
            </Text>
          ))}
          {missing ? (
            <Text variant="small" style={{ color: c.danger }}>
              {missing}
            </Text>
          ) : null}
          <Text variant="small" muted>
            {formatBRL(lineCents(book, line))}
          </Text>
        </View>
        <QuantityStepper label={label} min={1} max={MAX_LINE_QTY} value={line.quantity} onChange={(quantity) => onChange({ ...line, quantity })} />
      </Row>
      <Row>
        {onEdit ? <Button title={line.kind === 'BEVERAGE' ? 'Sabor' : 'Editar'} variant="ghost" onPress={onEdit} /> : null}
        <IconButton icon="trash-outline" label={`Remover ${label}`} onPress={onRemove} />
      </Row>
    </Card>
  );
}

/** Pode abrir a folha de edição? (bebida sem sabor não tem o que editar além da quantidade.) */
export function lineEditable(line: ALaCarteLine, book: FixedMenuBook): boolean {
  if (line.kind !== 'BEVERAGE') return true;
  return needsFlavor(book.beverageSizes.get(line.beverageSizeId)?.size);
}
