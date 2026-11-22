/**
 * `menu/fixed-menu/dish?id=` — prato do cardápio fixo (ADR-0056 itens 5 a 8, ADR-0057; 06 §6.5).
 * "Tamanho?" ligado: lista de tamanhos com preço (e "vende no site" em cada um); desligado: preço único.
 * `catalog.manage`; sem ela a tela não abre a edição (a pausa fica na lista).
 */
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useState } from 'react';

import { isApiError } from '@/api/errors';
import type { Dish, FixedMenu } from '@/api/types';
import { Banner, Button, Card, Chip, MoneyInput, Row, Screen, Section, Text, TextField } from '@/components/ui';
import { confirmAction } from '@/lib/confirm';
import { useCanWrite } from '@/stores/session';
import { toast } from '@/stores/toast';

import { PhotoCard, SiteSaleField, ToggleRow } from '../components';
import {
  addonGroupsFor,
  dishCreateBody,
  dishFormErrors,
  dishFormFrom,
  dishUpdateBody,
  emptyDishSize,
  fieldErrorOf,
  LIMITS,
  moveRow,
  nextSortOrder,
  type DishForm,
  type DishSizeRow,
} from '../fixedMenu';
import { FieldError, FormFooter, NotFound, RowActions, SaveError, SortOrderField, WithFixedMenu } from '../formParts';
import { useSaveDish } from '../hooks';

export default function DishFormScreen() {
  const { id, categoryId } = useLocalSearchParams<{ id?: string; categoryId?: string }>();
  // A mutação fica fora do formulário, que remonta com a versão atual depois de um 409 (o aviso sobrevive).
  const save = useSaveDish();
  const [reload, setReload] = useState(0);
  return (
    <WithFixedMenu title="Prato">
      {(menu) => {
        const dish = id ? menu.dishes.find((d) => d.id === id) : null;
        if (id && !dish) return <NotFound title="Prato" />;
        return <DishFormView key={`${id ?? 'new'}-${reload}`} menu={menu} dish={dish ?? null} defaultCategoryId={categoryId ?? null} save={save} onConflict={() => setReload((r) => r + 1)} />;
      }}
    </WithFixedMenu>
  );
}

function DishFormView({
  menu,
  dish,
  defaultCategoryId,
  save,
  onConflict,
}: {
  menu: FixedMenu;
  dish: Dish | null;
  defaultCategoryId: string | null;
  save: ReturnType<typeof useSaveDish>;
  onConflict: () => void;
}) {
  const canManage = useCanWrite('catalog.manage');
  /**
   * Versão em que o formulário foi aberto: vai no `If-Match` mesmo que o cache recarregue no meio da edição (outro
   * aparelho alterou → `409`, nunca sobrescreve). As gravações desta tela (foto, reativar) avançam a base.
   */
  const [baseVersion, setBaseVersion] = useState(dish?.version ?? 0);
  const categories = menu.categories.filter((c) => c.isActive || c.id === dish?.dishCategoryId);
  const firstCategory = defaultCategoryId ?? categories[0]?.id ?? null;
  const [form, setForm] = useState<DishForm>(() =>
    dishFormFrom(dish, { dishCategoryId: firstCategory, sortOrder: nextSortOrder(menu.dishes.filter((d) => d.dishCategoryId === firstCategory)) }),
  );
  const [tried, setTried] = useState(false);
  const local = dishFormErrors(form);
  const err = (field: string) => fieldErrorOf(local, tried, save.error, field);
  const set = <K extends keyof DishForm>(key: K, value: DishForm[K]) => setForm((f) => ({ ...f, [key]: value }));
  const setSize = (index: number, patch: Partial<DishSizeRow>) => setForm((f) => ({ ...f, sizes: f.sizes.map((s, i) => (i === index ? { ...s, ...patch } : s)) }));
  const groups = addonGroupsFor(menu, form.dishCategoryId, dish?.id ?? null);

  const send = (body: Parameters<typeof save.mutate>[0], done: string, after: (d: Dish) => void) =>
    save.mutate(body, {
      onSuccess: (d) => {
        toast(done, 'success');
        after(d as Dish);
      },
      onError: (e) => {
        if (isApiError(e, 'VersionConflict')) onConflict();
      },
    });

  const submit = () => {
    setTried(true);
    if (Object.keys(local).length > 0) return;
    if (dish) {
      send({ id: dish.id, version: baseVersion, body: dishUpdateBody(form) }, 'Prato salvo', () => router.back());
    } else {
      // Novo: volta na edição, onde a foto pode ser incluída (a chave da imagem leva o id).
      send({ body: dishCreateBody(form) }, 'Prato criado', (d) => router.replace({ pathname: '/menu/fixed-menu/dish', params: { id: d.id } }));
    }
  };

  const onToggleSizes = (on: boolean) => {
    if (on) {
      setForm((f) => ({ ...f, hasSizes: true, sizes: f.sizes.length ? f.sizes : [emptyDishSize()] }));
      return;
    }
    const saved = form.sizes.filter((s) => s.id !== null).length;
    if (dish?.hasSizes && saved > 0) {
      confirmAction({
        title: 'Desligar os tamanhos?',
        message: 'Os tamanhos deste prato ficam inativos (os pedidos já registrados não mudam) e o prato passa a ter preço único.',
        confirmText: 'Desligar',
        onConfirm: () => set('hasSizes', false),
      });
    } else {
      set('hasSizes', false);
    }
  };

  const removeSize = (index: number) => {
    const size = form.sizes[index]!;
    const remove = () => setForm((f) => ({ ...f, sizes: f.sizes.filter((_, i) => i !== index) }));
    if (size.id === null) {
      remove();
      return;
    }
    confirmAction({
      title: `Remover o tamanho “${size.name}”?`,
      message: 'Ao salvar, ele fica inativo. Os pedidos já registrados não mudam.',
      confirmText: 'Remover',
      onConfirm: remove,
    });
  };

  return (
    <Screen>
      <Stack.Screen options={{ title: dish ? dish.name : 'Novo prato' }} />
      <SaveError error={save.error} />
      {categories.length === 0 ? <Banner tone="warning" text="Crie uma categoria antes de cadastrar pratos." /> : null}

      <Text variant="small" muted>
        Categoria
      </Text>
      <Row style={{ flexWrap: 'wrap' }}>
        {categories.map((c) => (
          <Chip key={c.id} label={c.name} selected={form.dishCategoryId === c.id} disabled={!canManage} onPress={() => set('dishCategoryId', c.id)} />
        ))}
      </Row>
      <FieldError text={err('dishCategoryId')} />

      <TextField label="Nome" value={form.name} onChangeText={(t) => set('name', t)} editable={canManage} maxLength={LIMITS.dishName} error={err('name')} />
      <TextField
        label="Descrição (opcional)"
        value={form.description}
        onChangeText={(t) => set('description', t)}
        editable={canManage}
        maxLength={LIMITS.description}
        multiline
        error={err('description')}
      />
      <ToggleRow label="Vai para a cozinha?" value={form.requiresPreparation} disabled={!canManage} onChange={(v) => set('requiresPreparation', v)} />
      <ToggleRow
        label="Tamanho?"
        hint={form.hasSizes ? 'Cada tamanho tem o seu preço (300 ml, 500 ml…).' : 'Desligado: o prato tem um preço só.'}
        value={form.hasSizes}
        disabled={!canManage}
        onChange={onToggleSizes}
      />

      {form.hasSizes ? (
        <Section title="Tamanhos">
          {err('sizes') ? <Banner tone="danger" text={err('sizes')!} /> : null}
          {form.sizes.map((s, i) => (
            <Card key={s.key}>
              <TextField
                label={`Tamanho ${i + 1}`}
                value={s.name}
                placeholder="Ex.: 300 ml, P, Família"
                onChangeText={(t) => setSize(i, { name: t })}
                editable={canManage}
                maxLength={LIMITS.sizeName}
                error={err(`sizes[${i}].name`)}
              />
              <MoneyInput label={`Preço: ${s.name || `tamanho ${i + 1}`}`} value={s.price} editable={canManage} onChange={(price) => setSize(i, { price })} error={err(`sizes[${i}].price`)} />
              <SiteSaleField
                name={s.name || `tamanho ${i + 1}`}
                value={s}
                counterPrice={s.price}
                editable={canManage}
                onChange={(v) => setSize(i, v)}
                error={err(`sizes[${i}].sitePrice`)}
              />
              <RowActions
                name={s.name || `tamanho ${i + 1}`}
                index={i}
                count={form.sizes.length}
                editable={canManage}
                onMove={(d) => setForm((f) => ({ ...f, sizes: moveRow(f.sizes, i, d) }))}
                onRemove={() => removeSize(i)}
              />
            </Card>
          ))}
          {canManage && form.sizes.length < LIMITS.sizes ? (
            <Button title="Adicionar tamanho" icon="add" variant="secondary" onPress={() => setForm((f) => ({ ...f, sizes: [...f.sizes, emptyDishSize()] }))} />
          ) : null}
        </Section>
      ) : (
        <>
          <MoneyInput label="Preço" value={form.price} editable={canManage} onChange={(price) => set('price', price)} error={err('price')} />
          <SiteSaleField value={form} counterPrice={form.price} editable={canManage} onChange={(v) => setForm((f) => ({ ...f, ...v }))} error={err('sitePrice') ?? err('sellsOnSite')} />
        </>
      )}

      <SortOrderField label="Ordem na categoria" value={form.sortOrder} onChange={(v) => set('sortOrder', v)} editable={canManage} error={err('sortOrder')} />

      <Section title="Adicionais">
        <Text variant="small" muted>
          {groups.length
            ? groups.map((g) => g.name).join(', ')
            : 'Nenhum grupo de adicionais. Ligue um grupo a esta categoria ou a este prato em “Grupos de adicionais”.'}
        </Text>
      </Section>

      {dish ? (
        <PhotoCard
          kind="dish"
          id={dish.id}
          version={baseVersion}
          onUploaded={setBaseVersion}
          onConflict={onConflict}
          imageUrl={dish.imageUrl}
          canManage={canManage}
          onRemove={() => send({ id: dish.id, version: baseVersion, body: { imageStorageKey: null } }, 'Foto removida', (d) => setBaseVersion(d.version))}
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
        existing={!!dish}
        isActive={dish?.isActive ?? true}
        itemName={dish?.name ?? ''}
        deactivateText="Ele sai da venda e do cardápio, mas continua no histórico dos pedidos. Se estiver em algum combo ativo, ajuste o combo antes."
        onDeactivate={() => dish && send({ id: dish.id, version: baseVersion, body: { isActive: false } }, 'Prato inativado', () => router.back())}
        onReactivate={() => dish && send({ id: dish.id, version: baseVersion, body: { isActive: true } }, 'Prato reativado', (d) => setBaseVersion(d.version))}
      />
    </Screen>
  );
}
