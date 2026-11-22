/**
 * `menu/fixed-menu/beverage?id=` — tipo de bebida (ADR-0056 itens 9 a 12; 06 §6.5): tipo → tamanhos com preço
 * ("vende no site" em cada um) → sabores ou marcas com acréscimo opcional. O sabor é **do tamanho** (Q-305);
 * "Copiar sabores de outro tamanho" acrescenta os que faltam, sem redigitar.
 */
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useState } from 'react';
import { View } from 'react-native';

import { isApiError } from '@/api/errors';
import type { BeverageType, FixedMenu } from '@/api/types';
import { Banner, Button, Card, Chip, MoneyInput, Row, Screen, Section, Text, TextField, space } from '@/components/ui';
import { confirmAction } from '@/lib/confirm';
import { useCanWrite } from '@/stores/session';
import { toast } from '@/stores/toast';

import { SiteSaleField } from '../components';
import {
  beverageCreateBody,
  beverageFormErrors,
  beverageFormFrom,
  beverageUpdateBody,
  copyFlavors,
  emptyBeverageSize,
  emptyFlavor,
  fieldErrorOf,
  LIMITS,
  moveRow,
  nextSortOrder,
  type BeverageForm,
  type BeverageSizeRow,
  type FlavorRow,
} from '../fixedMenu';
import { FormFooter, NotFound, RowActions, SaveError, SortOrderField, WithFixedMenu } from '../formParts';
import { useSaveBeverageType } from '../hooks';

export default function BeverageFormScreen() {
  const { id } = useLocalSearchParams<{ id?: string }>();
  const save = useSaveBeverageType();
  const [reload, setReload] = useState(0);
  return (
    <WithFixedMenu title="Bebida">
      {(menu) => {
        const type = id ? menu.beverageTypes.find((t) => t.id === id) : null;
        if (id && !type) return <NotFound title="Bebida" />;
        return <BeverageFormView key={`${id ?? 'new'}-${reload}`} menu={menu} type={type ?? null} save={save} onConflict={() => setReload((r) => r + 1)} />;
      }}
    </WithFixedMenu>
  );
}

export function BeverageFormView({
  menu,
  type,
  save,
  onConflict,
}: {
  menu: FixedMenu;
  type: BeverageType | null;
  save: ReturnType<typeof useSaveBeverageType>;
  onConflict: () => void;
}) {
  const canManage = useCanWrite('catalog.manage');
  /**
   * Versão em que o formulário foi aberto: vai no `If-Match` mesmo que o cache recarregue no meio da edição (outro
   * aparelho alterou → `409`, nunca sobrescreve). As gravações desta tela (foto, reativar) avançam a base.
   */
  const [baseVersion, setBaseVersion] = useState(type?.version ?? 0);
  const [form, setForm] = useState<BeverageForm>(() => beverageFormFrom(type, { sortOrder: nextSortOrder(menu.beverageTypes) }));
  const [tried, setTried] = useState(false);
  /** Tamanho que está escolhendo de onde copiar os sabores. */
  const [copyTo, setCopyTo] = useState<string | null>(null);
  const local = beverageFormErrors(form);
  const err = (field: string) => fieldErrorOf(local, tried, save.error, field);
  const setSize = (i: number, patch: Partial<BeverageSizeRow>) => setForm((f) => ({ ...f, sizes: f.sizes.map((s, k) => (k === i ? { ...s, ...patch } : s)) }));
  const setFlavor = (i: number, j: number, patch: Partial<FlavorRow>) =>
    setForm((f) => ({ ...f, sizes: f.sizes.map((s, k) => (k === i ? { ...s, flavors: s.flavors.map((fl, m) => (m === j ? { ...fl, ...patch } : fl)) } : s)) }));

  const send = (body: Parameters<typeof save.mutate>[0], done: string, after: (t: BeverageType) => void) =>
    save.mutate(body, {
      onSuccess: (t) => {
        toast(done, 'success');
        after(t as BeverageType);
      },
      onError: (e) => {
        if (isApiError(e, 'VersionConflict')) onConflict();
      },
    });

  const submit = () => {
    setTried(true);
    if (Object.keys(local).length > 0) return;
    if (type) send({ id: type.id, version: baseVersion, body: beverageUpdateBody(form) }, 'Bebida salva', () => router.back());
    else send({ body: beverageCreateBody(form) }, 'Bebida criada', () => router.back());
  };

  /** Linha já salva sai com confirmação: ao salvar, fica inativa (a lista substitui a atual). */
  const confirmRemove = (saved: boolean, what: string, remove: () => void) => {
    if (!saved) {
      remove();
      return;
    }
    confirmAction({ title: `Remover ${what}?`, message: 'Ao salvar, fica inativo. Os pedidos já registrados não mudam.', confirmText: 'Remover', onConfirm: remove });
  };

  return (
    <Screen>
      <Stack.Screen options={{ title: type ? type.name : 'Nova bebida' }} />
      <SaveError error={save.error} />
      <TextField label="Tipo" placeholder="Ex.: Refrigerante, Suco" value={form.name} onChangeText={(name) => setForm((f) => ({ ...f, name }))} editable={canManage} maxLength={LIMITS.typeName} error={err('name')} />
      <SortOrderField value={form.sortOrder} onChange={(sortOrder) => setForm((f) => ({ ...f, sortOrder }))} editable={canManage} error={err('sortOrder')} />

      <Section title="Tamanhos">
        <Text variant="small" muted>
          Cada tamanho tem o seu preço e a sua lista de sabores. Sem sabores, é vendido sem escolha (ex.: Água 500 ml).
        </Text>
        {err('sizes') ? <Banner tone="danger" text={err('sizes')!} /> : null}
        {form.sizes.map((s, i) => {
          const sizeName = s.name || `tamanho ${i + 1}`;
          const sources = form.sizes.filter((o) => o.key !== s.key && o.flavors.some((f) => f.name.trim()));
          return (
            <Card key={s.key}>
              <TextField label={`Tamanho ${i + 1}`} placeholder="Ex.: Lata 350 ml, 2 L" value={s.name} onChangeText={(name) => setSize(i, { name })} editable={canManage} maxLength={LIMITS.sizeName} error={err(`sizes[${i}].name`)} />
              <MoneyInput label={`Preço: ${sizeName}`} value={s.price} editable={canManage} onChange={(price) => setSize(i, { price })} error={err(`sizes[${i}].price`)} />
              <SiteSaleField name={sizeName} value={s} counterPrice={s.price} editable={canManage} onChange={(v) => setSize(i, v)} error={err(`sizes[${i}].sitePrice`)} />

              <Text style={{ fontWeight: '600' }}>Sabores ou marcas</Text>
              {err(`sizes[${i}].flavors`) ? <Banner tone="danger" text={err(`sizes[${i}].flavors`)!} /> : null}
              {s.flavors.map((f, j) => (
                <View key={f.key} style={{ gap: space.xs, paddingLeft: space.sm }}>
                  <TextField
                    label={`Sabor ${j + 1} (${sizeName})`}
                    value={f.name}
                    onChangeText={(name) => setFlavor(i, j, { name })}
                    editable={canManage}
                    maxLength={LIMITS.flavorName}
                    error={err(`sizes[${i}].flavors[${j}].name`)}
                  />
                  <MoneyInput
                    label={`Acréscimo: ${f.name || `sabor ${j + 1}`} (${sizeName})`}
                    value={f.extraPrice}
                    editable={canManage}
                    onChange={(extraPrice) => setFlavor(i, j, { extraPrice })}
                    error={err(`sizes[${i}].flavors[${j}].extraPrice`)}
                  />
                  <RowActions
                    name={`${f.name || `sabor ${j + 1}`} (${sizeName})`}
                    index={j}
                    count={s.flavors.length}
                    editable={canManage}
                    onMove={(d) => setSize(i, { flavors: moveRow(s.flavors, j, d) })}
                    onRemove={() => confirmRemove(f.id !== null, `o sabor “${f.name}”`, () => setSize(i, { flavors: s.flavors.filter((_, m) => m !== j) }))}
                  />
                </View>
              ))}
              {canManage && s.flavors.length < LIMITS.flavors ? (
                <Button title={`Adicionar sabor (${sizeName})`} icon="add" variant="ghost" onPress={() => setSize(i, { flavors: [...s.flavors, emptyFlavor()] })} />
              ) : null}
              {canManage && sources.length > 0 ? (
                copyTo === s.key ? (
                  <View style={{ gap: space.xs }}>
                    <Text variant="small" muted>
                      Copiar os sabores de qual tamanho? Os que este tamanho já tem não se repetem.
                    </Text>
                    <Row style={{ flexWrap: 'wrap' }}>
                      {sources.map((o) => (
                        <Chip
                          key={o.key}
                          label={o.name || 'sem nome'}
                          onPress={() => {
                            setForm((f) => copyFlavors(f, o.key, s.key));
                            setCopyTo(null);
                          }}
                        />
                      ))}
                      <Chip label="Cancelar" onPress={() => setCopyTo(null)} />
                    </Row>
                  </View>
                ) : (
                  <Button title={`Copiar sabores de outro tamanho (${sizeName})`} icon="copy-outline" variant="secondary" onPress={() => setCopyTo(s.key)} />
                )
              ) : null}

              <RowActions
                name={sizeName}
                index={i}
                count={form.sizes.length}
                editable={canManage}
                onMove={(d) => setForm((f) => ({ ...f, sizes: moveRow(f.sizes, i, d) }))}
                onRemove={() => confirmRemove(s.id !== null, `o tamanho “${s.name}”`, () => setForm((f) => ({ ...f, sizes: f.sizes.filter((_, k) => k !== i) })))}
              />
            </Card>
          );
        })}
        {canManage && form.sizes.length < LIMITS.sizes ? (
          <Button title="Adicionar tamanho" icon="add" variant="secondary" onPress={() => setForm((f) => ({ ...f, sizes: [...f.sizes, emptyBeverageSize()] }))} />
        ) : null}
      </Section>

      <FormFooter
        canManage={canManage}
        saving={save.isPending}
        onSave={submit}
        existing={!!type}
        isActive={type?.isActive ?? true}
        itemName={type?.name ?? ''}
        deactivateText="Os tamanhos e sabores saem da venda, mas continuam no histórico dos pedidos. Se algum tamanho estiver em combo ativo, ajuste o combo antes."
        onDeactivate={() => type && send({ id: type.id, version: baseVersion, body: { isActive: false } }, 'Bebida inativada', () => router.back())}
        onReactivate={() => type && send({ id: type.id, version: baseVersion, body: { isActive: true } }, 'Bebida reativada', (t) => setBaseVersion(t.version))}
      />
    </Screen>
  );
}
