/**
 * `menu/fixed-menu/combo?id=` — combo fixo (ADR-0056 itens 13 a 16; 06 §6.5): preço fechado, "vende no site" pela
 * chave do próprio combo, e de 1 a 10 componentes: prato com tamanho fixo e quantidade, ou bebida de tipo + tamanho
 * (o sabor é escolhido no pedido). Item pausado pode entrar: o combo só fica fora de venda durante a pausa.
 */
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useState } from 'react';
import { View } from 'react-native';

import { isApiError } from '@/api/errors';
import type { Combo, ComboComponentKind, FixedMenu } from '@/api/types';
import { Banner, Button, Card, Chip, MoneyInput, QuantityStepper, Row, Screen, Section, Text, TextField, space } from '@/components/ui';
import { useCanWrite } from '@/stores/session';
import { toast } from '@/stores/toast';

import { PhotoCard, SiteSaleField } from '../components';
import {
  comboCreateBody,
  comboFormErrors,
  comboFormFrom,
  comboUpdateBody,
  componentLabel,
  emptyComponent,
  fieldErrorOf,
  LIMITS,
  moveRow,
  nextSortOrder,
  type ComboForm,
  type ComponentRow,
} from '../fixedMenu';
import { FieldError, FormFooter, NotFound, RowActions, SaveError, SortOrderField, WithFixedMenu } from '../formParts';
import { useSaveCombo } from '../hooks';

export default function ComboFormScreen() {
  const { id } = useLocalSearchParams<{ id?: string }>();
  const save = useSaveCombo();
  const [reload, setReload] = useState(0);
  return (
    <WithFixedMenu title="Combo">
      {(menu) => {
        const combo = id ? menu.combos.find((c) => c.id === id) : null;
        if (id && !combo) return <NotFound title="Combo" />;
        return <ComboFormView key={`${id ?? 'new'}-${reload}`} menu={menu} combo={combo ?? null} save={save} onConflict={() => setReload((r) => r + 1)} />;
      }}
    </WithFixedMenu>
  );
}

function ComboFormView({ menu, combo, save, onConflict }: { menu: FixedMenu; combo: Combo | null; save: ReturnType<typeof useSaveCombo>; onConflict: () => void }) {
  const canManage = useCanWrite('catalog.manage');
  /**
   * Versão em que o formulário foi aberto: vai no `If-Match` mesmo que o cache recarregue no meio da edição (outro
   * aparelho alterou → `409`, nunca sobrescreve). As gravações desta tela (foto, reativar) avançam a base.
   */
  const [baseVersion, setBaseVersion] = useState(combo?.version ?? 0);
  const [form, setForm] = useState<ComboForm>(() => comboFormFrom(combo, menu, { sortOrder: nextSortOrder(menu.combos) }));
  const [tried, setTried] = useState(false);
  const local = comboFormErrors(form, menu);
  const err = (field: string) => fieldErrorOf(local, tried, save.error, field);
  const set = <K extends keyof ComboForm>(key: K, value: ComboForm[K]) => setForm((f) => ({ ...f, [key]: value }));
  const setRow = (i: number, patch: Partial<ComponentRow>) => setForm((f) => ({ ...f, components: f.components.map((c, k) => (k === i ? { ...c, ...patch } : c)) }));
  const add = (kind: ComboComponentKind) => setForm((f) => ({ ...f, components: [...f.components, emptyComponent(kind)] }));

  const send = (body: Parameters<typeof save.mutate>[0], done: string, after: (c: Combo) => void) =>
    save.mutate(body, {
      onSuccess: (c) => {
        toast(done, 'success');
        after(c as Combo);
      },
      onError: (e) => {
        if (isApiError(e, 'VersionConflict')) onConflict();
      },
    });

  const submit = () => {
    setTried(true);
    if (Object.keys(local).length > 0) return;
    if (combo) send({ id: combo.id, version: baseVersion, body: comboUpdateBody(form) }, 'Combo salvo', () => router.back());
    else send({ body: comboCreateBody(form) }, 'Combo criado', (c) => router.replace({ pathname: '/menu/fixed-menu/combo', params: { id: c.id } }));
  };

  const dishes = menu.dishes.filter((d) => d.isActive);
  const types = menu.beverageTypes.filter((t) => t.isActive && t.sizes.some((s) => s.isActive));

  return (
    <Screen>
      <Stack.Screen options={{ title: combo ? combo.name : 'Novo combo' }} />
      <SaveError error={save.error} />
      <TextField label="Nome" placeholder="Ex.: X-Burguer + batata + refri" value={form.name} onChangeText={(t) => set('name', t)} editable={canManage} maxLength={LIMITS.comboName} error={err('name')} />
      <TextField label="Descrição (opcional)" value={form.description} onChangeText={(t) => set('description', t)} editable={canManage} maxLength={LIMITS.description} multiline error={err('description')} />
      <MoneyInput label="Preço do combo" value={form.price} editable={canManage} onChange={(price) => set('price', price)} error={err('price')} />
      <Text variant="small" muted>
        Preço fechado. No pedido, adicionais dos pratos e o acréscimo do sabor da bebida são cobrados à parte.
      </Text>
      <SiteSaleField value={form} counterPrice={form.price} editable={canManage} onChange={(v) => setForm((f) => ({ ...f, ...v }))} error={err('sitePrice')} />
      <Text variant="small" muted>
        Os itens do combo não precisam vender no site sozinhos; basta estarem disponíveis.
      </Text>
      <SortOrderField value={form.sortOrder} onChange={(v) => set('sortOrder', v)} editable={canManage} error={err('sortOrder')} />

      <Section title="Itens do combo">
        {err('components') ? <Banner tone="danger" text={err('components')!} /> : null}
        {form.components.map((row, i) => {
          const dish = row.kind === 'DISH' ? menu.dishes.find((d) => d.id === row.dishId) : undefined;
          const type = row.kind === 'BEVERAGE' ? menu.beverageTypes.find((t) => t.id === row.beverageTypeId) : undefined;
          const label = componentLabel(row, menu);
          return (
            <Card key={row.key}>
              <Text style={{ fontWeight: '700' }}>
                {row.kind === 'DISH' ? 'Prato' : 'Bebida'}: {label}
                {row.kind === 'BEVERAGE' && row.beverageSizeId ? ' (sabor no pedido)' : ''}
              </Text>
              {row.kind === 'DISH' ? (
                <>
                  <Row style={{ flexWrap: 'wrap' }}>
                    {dishes.map((d) => (
                      <Chip key={d.id} label={d.name} selected={row.dishId === d.id} disabled={!canManage} onPress={() => setRow(i, { dishId: d.id, dishSizeId: null })} />
                    ))}
                  </Row>
                  <FieldError text={err(`components[${i}].dishId`)} />
                  {dish?.hasSizes ? (
                    <View style={{ gap: space.xs }}>
                      <Text variant="small" muted>
                        Tamanho de {dish.name}
                      </Text>
                      <Row style={{ flexWrap: 'wrap' }}>
                        {dish.sizes
                          .filter((s) => s.isActive)
                          .map((s) => (
                            <Chip key={s.id} label={s.name} selected={row.dishSizeId === s.id} disabled={!canManage} onPress={() => setRow(i, { dishSizeId: s.id })} />
                          ))}
                      </Row>
                      <FieldError text={err(`components[${i}].dishSizeId`)} />
                    </View>
                  ) : null}
                </>
              ) : (
                <>
                  <Row style={{ flexWrap: 'wrap' }}>
                    {types.map((t) => (
                      <Chip key={t.id} label={t.name} selected={row.beverageTypeId === t.id} disabled={!canManage} onPress={() => setRow(i, { beverageTypeId: t.id, beverageSizeId: null })} />
                    ))}
                  </Row>
                  {type ? (
                    <Row style={{ flexWrap: 'wrap' }}>
                      {type.sizes
                        .filter((s) => s.isActive)
                        .map((s) => (
                          <Chip key={s.id} label={s.name} selected={row.beverageSizeId === s.id} disabled={!canManage} onPress={() => setRow(i, { beverageSizeId: s.id })} />
                        ))}
                    </Row>
                  ) : null}
                  <FieldError text={err(`components[${i}].beverageSizeId`)} />
                </>
              )}
              <Row style={{ justifyContent: 'space-between' }}>
                <Text>Quantidade</Text>
                <QuantityStepper label={`quantidade de ${label}`} value={row.quantity} min={1} max={LIMITS.componentQuantity} onChange={(quantity) => canManage && setRow(i, { quantity })} />
              </Row>
              <RowActions
                name={label}
                index={i}
                count={form.components.length}
                editable={canManage}
                onMove={(d) => setForm((f) => ({ ...f, components: moveRow(f.components, i, d) }))}
                onRemove={() => setForm((f) => ({ ...f, components: f.components.filter((_, k) => k !== i) }))}
              />
            </Card>
          );
        })}
        {canManage && form.components.length < LIMITS.components ? (
          <Row>
            <Button title="Adicionar prato" icon="add" variant="secondary" style={{ flex: 1 }} disabled={dishes.length === 0} onPress={() => add('DISH')} />
            <Button title="Adicionar bebida" icon="add" variant="secondary" style={{ flex: 1 }} disabled={types.length === 0} onPress={() => add('BEVERAGE')} />
          </Row>
        ) : null}
      </Section>

      {combo ? (
        <PhotoCard
          kind="combo"
          id={combo.id}
          version={baseVersion}
          onUploaded={setBaseVersion}
          onConflict={onConflict}
          imageUrl={combo.imageUrl}
          canManage={canManage}
          onRemove={() => send({ id: combo.id, version: baseVersion, body: { imageStorageKey: null } }, 'Foto removida', (c) => setBaseVersion(c.version))}
        />
      ) : (
        <Text variant="small" muted>
          A foto pode ser incluída depois de salvar.
        </Text>
      )}

      <FormFooter
        canManage={canManage}
        saving={save.isPending}
        onSave={submit}
        existing={!!combo}
        isActive={combo?.isActive ?? true}
        itemName={combo?.name ?? ''}
        deactivateText="O combo sai da venda, mas continua no histórico dos pedidos."
        onDeactivate={() => combo && send({ id: combo.id, version: baseVersion, body: { isActive: false } }, 'Combo inativado', () => router.back())}
        onReactivate={() => combo && send({ id: combo.id, version: baseVersion, body: { isActive: true } }, 'Combo reativado', (c) => setBaseVersion(c.version))}
      />
    </Screen>
  );
}
