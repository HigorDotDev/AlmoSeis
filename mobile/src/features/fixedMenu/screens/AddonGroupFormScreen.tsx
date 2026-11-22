/**
 * `menu/fixed-menu/addon-group?id=` — grupo de adicionais (ADR-0056 itens 17 a 20; 06 §6.5): opções com preço
 * (pode ser grátis) e vínculo com categorias e/ou pratos. Sem limite mínimo/máximo nesta fase (item 19).
 */
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useState } from 'react';

import { isApiError } from '@/api/errors';
import type { AddonGroup, FixedMenu } from '@/api/types';
import { Banner, Button, Card, Chip, MoneyInput, Row, Screen, Section, Text, TextField } from '@/components/ui';
import { confirmAction } from '@/lib/confirm';
import { useCanWrite } from '@/stores/session';
import { toast } from '@/stores/toast';

import {
  addonGroupCreateBody,
  addonGroupFormErrors,
  addonGroupFormFrom,
  addonGroupUpdateBody,
  emptyOption,
  fieldErrorOf,
  LIMITS,
  moveRow,
  nextSortOrder,
  toggleId,
  type AddonGroupForm,
  type OptionRow,
} from '../fixedMenu';
import { FormFooter, NotFound, RowActions, SaveError, SortOrderField, WithFixedMenu } from '../formParts';
import { useSaveAddonGroup } from '../hooks';

export default function AddonGroupFormScreen() {
  const { id } = useLocalSearchParams<{ id?: string }>();
  const save = useSaveAddonGroup();
  const [reload, setReload] = useState(0);
  return (
    <WithFixedMenu title="Grupo de adicionais">
      {(menu) => {
        const group = id ? menu.addonGroups.find((g) => g.id === id) : null;
        if (id && !group) return <NotFound title="Grupo de adicionais" />;
        return <AddonGroupFormView key={`${id ?? 'new'}-${reload}`} menu={menu} group={group ?? null} save={save} onConflict={() => setReload((r) => r + 1)} />;
      }}
    </WithFixedMenu>
  );
}

function AddonGroupFormView({ menu, group, save, onConflict }: { menu: FixedMenu; group: AddonGroup | null; save: ReturnType<typeof useSaveAddonGroup>; onConflict: () => void }) {
  const canManage = useCanWrite('catalog.manage');
  /**
   * Versão em que o formulário foi aberto: vai no `If-Match` mesmo que o cache recarregue no meio da edição (outro
   * aparelho alterou → `409`, nunca sobrescreve). As gravações desta tela (foto, reativar) avançam a base.
   */
  const [baseVersion, setBaseVersion] = useState(group?.version ?? 0);
  const [form, setForm] = useState<AddonGroupForm>(() => addonGroupFormFrom(group, { sortOrder: nextSortOrder(menu.addonGroups) }));
  const [tried, setTried] = useState(false);
  const local = addonGroupFormErrors(form);
  const err = (field: string) => fieldErrorOf(local, tried, save.error, field);
  const setOption = (i: number, patch: Partial<OptionRow>) => setForm((f) => ({ ...f, options: f.options.map((o, k) => (k === i ? { ...o, ...patch } : o)) }));
  const categories = menu.categories.filter((c) => c.isActive || form.dishCategoryIds.includes(c.id));
  const dishes = menu.dishes.filter((d) => d.isActive || form.dishIds.includes(d.id));

  const send = (body: Parameters<typeof save.mutate>[0], done: string, after: (g: AddonGroup) => void) =>
    save.mutate(body, {
      onSuccess: (g) => {
        toast(done, 'success');
        after(g as AddonGroup);
      },
      onError: (e) => {
        if (isApiError(e, 'VersionConflict')) onConflict();
      },
    });

  const submit = () => {
    setTried(true);
    if (Object.keys(local).length > 0) return;
    if (group) send({ id: group.id, version: baseVersion, body: addonGroupUpdateBody(form) }, 'Grupo salvo', () => router.back());
    else send({ body: addonGroupCreateBody(form) }, 'Grupo criado', () => router.back());
  };

  const removeOption = (i: number) => {
    const o = form.options[i]!;
    const remove = () => setForm((f) => ({ ...f, options: f.options.filter((_, k) => k !== i) }));
    if (o.id === null) remove();
    else confirmAction({ title: `Remover “${o.name}”?`, message: 'Ao salvar, a opção fica inativa. Os pedidos já registrados não mudam.', confirmText: 'Remover', onConfirm: remove });
  };

  return (
    <Screen>
      <Stack.Screen options={{ title: group ? group.name : 'Novo grupo de adicionais' }} />
      <SaveError error={save.error} />
      <TextField label="Nome do grupo" placeholder="Ex.: Adicionais de lanche" value={form.name} onChangeText={(name) => setForm((f) => ({ ...f, name }))} editable={canManage} maxLength={LIMITS.groupName} error={err('name')} />
      <SortOrderField value={form.sortOrder} onChange={(sortOrder) => setForm((f) => ({ ...f, sortOrder }))} editable={canManage} error={err('sortOrder')} />

      <Section title="Opções">
        {err('options') ? <Banner tone="danger" text={err('options')!} /> : null}
        {form.options.map((o, i) => (
          <Card key={o.key}>
            <TextField label={`Opção ${i + 1}`} placeholder="Ex.: Bacon" value={o.name} onChangeText={(name) => setOption(i, { name })} editable={canManage} maxLength={LIMITS.optionName} error={err(`options[${i}].name`)} />
            <MoneyInput label={`Preço: ${o.name || `opção ${i + 1}`}`} value={o.price} editable={canManage} onChange={(price) => setOption(i, { price })} error={err(`options[${i}].price`)} />
            <RowActions
              name={o.name || `opção ${i + 1}`}
              index={i}
              count={form.options.length}
              editable={canManage}
              onMove={(d) => setForm((f) => ({ ...f, options: moveRow(f.options, i, d) }))}
              onRemove={() => removeOption(i)}
            />
          </Card>
        ))}
        {canManage && form.options.length < LIMITS.options ? (
          <Button title="Adicionar opção" icon="add" variant="secondary" onPress={() => setForm((f) => ({ ...f, options: [...f.options, emptyOption()] }))} />
        ) : null}
      </Section>

      <Section title="Vale para">
        <Text variant="small" muted>
          Ligado a uma categoria, aparece em todos os pratos dela. Ligado a um prato, só nele.
        </Text>
        <Text style={{ fontWeight: '600' }}>Categorias</Text>
        <Row style={{ flexWrap: 'wrap' }}>
          {categories.map((c) => (
            <Chip key={c.id} label={c.name} selected={form.dishCategoryIds.includes(c.id)} disabled={!canManage} onPress={() => setForm((f) => ({ ...f, dishCategoryIds: toggleId(f.dishCategoryIds, c.id) }))} />
          ))}
        </Row>
        <Text style={{ fontWeight: '600' }}>Pratos</Text>
        <Row style={{ flexWrap: 'wrap' }}>
          {dishes.map((d) => (
            <Chip key={d.id} label={d.name} selected={form.dishIds.includes(d.id)} disabled={!canManage} onPress={() => setForm((f) => ({ ...f, dishIds: toggleId(f.dishIds, d.id) }))} />
          ))}
        </Row>
        {err('dishCategoryIds') || err('dishIds') ? <Banner tone="danger" text={(err('dishCategoryIds') ?? err('dishIds'))!} /> : null}
      </Section>

      <FormFooter
        canManage={canManage}
        saving={save.isPending}
        onSave={submit}
        existing={!!group}
        isActive={group?.isActive ?? true}
        itemName={group?.name ?? ''}
        deactivateText="As opções deixam de aparecer nos pratos. Os pedidos já registrados não mudam."
        onDeactivate={() => group && send({ id: group.id, version: baseVersion, body: { isActive: false } }, 'Grupo inativado', () => router.back())}
        onReactivate={() => group && send({ id: group.id, version: baseVersion, body: { isActive: true } }, 'Grupo reativado', (g) => setBaseVersion(g.version))}
      />
    </Screen>
  );
}
