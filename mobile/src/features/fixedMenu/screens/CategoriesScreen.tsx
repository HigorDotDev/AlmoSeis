/**
 * `menu/fixed-menu/categories` — categorias dos pratos (ADR-0056 item 5, RN-304.3): criar, renomear, ordenar
 * (subir/descer renumera e manda um `PATCH` por categoria que mudou) e inativar (com prato ativo → `422`).
 */
import { Stack } from 'expo-router';
import { useState } from 'react';
import { View } from 'react-native';

import type { DishCategory } from '@/api/types';
import { Banner, Button, Card, IconButton, Row, Screen, Text, TextField } from '@/components/ui';
import { confirmAction } from '@/lib/confirm';
import { useCanWrite } from '@/stores/session';
import { toast } from '@/stores/toast';

import { fixedMenuErrorText } from '../components';
import { fieldErrorOf, LIMITS, nextSortOrder, reorderCategories } from '../fixedMenu';
import { WithFixedMenu } from '../formParts';
import { useReorderCategories, useSaveCategory } from '../hooks';

export default function CategoriesScreen() {
  return <WithFixedMenu title="Categorias">{(menu) => <CategoriesView categories={menu.categories} dishCount={(id) => menu.dishes.filter((d) => d.isActive && d.dishCategoryId === id).length} />}</WithFixedMenu>;
}

function CategoriesView({ categories, dishCount }: { categories: DishCategory[]; dishCount: (id: string) => number }) {
  const canManage = useCanWrite('catalog.manage');
  const save = useSaveCategory();
  const reorder = useReorderCategories();
  const [newName, setNewName] = useState('');
  const [editing, setEditing] = useState<{ id: string; name: string } | null>(null);
  const active = categories.filter((c) => c.isActive);
  const inactive = categories.filter((c) => !c.isActive);
  const error = save.error ?? reorder.error;

  const create = () =>
    save.mutate(
      { body: { name: newName.trim(), sortOrder: nextSortOrder(categories) } },
      {
        onSuccess: () => {
          setNewName('');
          toast('Categoria criada', 'success');
        },
      },
    );

  const rename = (c: DishCategory) =>
    editing &&
    save.mutate({ id: c.id, version: c.version, body: { name: editing.name.trim() } }, { onSuccess: () => setEditing(null) });

  return (
    <Screen>
      <Stack.Screen options={{ title: 'Categorias' }} />
      <Text variant="small" muted>
        As categorias organizam os pratos no app e na página (Lanches, Pastéis, Porções, Açaí).
      </Text>
      {error ? <Banner tone="danger" text={fixedMenuErrorText(error)} /> : null}
      {active.map((c, i) => (
        <Card key={c.id}>
          {editing?.id === c.id ? (
            <View style={{ gap: 8 }}>
              <TextField label="Nome da categoria" value={editing.name} maxLength={LIMITS.categoryName} onChangeText={(name) => setEditing({ id: c.id, name })} error={fieldErrorOf({}, false, save.error, 'name')} />
              <Row>
                <Button title="Salvar nome" style={{ flex: 1 }} disabled={!editing.name.trim()} loading={save.isPending} onPress={() => rename(c)} />
                <Button title="Cancelar" variant="ghost" onPress={() => setEditing(null)} />
              </Row>
            </View>
          ) : (
            <Row style={{ justifyContent: 'space-between' }}>
              <View style={{ flex: 1 }}>
                <Text style={{ fontWeight: '700' }}>{c.name}</Text>
                <Text variant="small" muted>
                  {dishCount(c.id) === 1 ? '1 prato' : `${dishCount(c.id)} pratos`}
                </Text>
              </View>
              {canManage ? (
                <Row gap={0}>
                  <IconButton icon="arrow-up" label={`Subir ${c.name}`} disabled={i === 0 || reorder.isPending} onPress={() => reorder.mutate(reorderCategories(active, i, -1))} />
                  <IconButton icon="arrow-down" label={`Descer ${c.name}`} disabled={i === active.length - 1 || reorder.isPending} onPress={() => reorder.mutate(reorderCategories(active, i, 1))} />
                  <IconButton icon="create-outline" label={`Renomear ${c.name}`} onPress={() => setEditing({ id: c.id, name: c.name })} />
                  <IconButton
                    icon="trash-outline"
                    label={`Inativar ${c.name}`}
                    onPress={() =>
                      confirmAction({
                        title: `Inativar “${c.name}”?`,
                        message: 'Só é possível sem pratos ativos nela. Inative ou mude os pratos de categoria antes.',
                        confirmText: 'Inativar',
                        onConfirm: () => save.mutate({ id: c.id, version: c.version, body: { isActive: false } }),
                      })
                    }
                  />
                </Row>
              ) : null}
            </Row>
          )}
        </Card>
      ))}
      {canManage ? (
        <Card>
          <TextField label="Nova categoria" placeholder="Ex.: Lanches" value={newName} maxLength={LIMITS.categoryName} onChangeText={setNewName} />
          <Button title="Criar categoria" icon="add" disabled={!newName.trim()} loading={save.isPending} onPress={create} />
        </Card>
      ) : null}
      {inactive.length ? (
        <>
          <Text variant="subtitle">Inativas</Text>
          {inactive.map((c) => (
            <Card key={c.id} style={{ opacity: 0.7 }}>
              <Row style={{ justifyContent: 'space-between' }}>
                <Text style={{ flex: 1 }}>{c.name}</Text>
                {canManage ? <Button title="Reativar" variant="secondary" onPress={() => save.mutate({ id: c.id, version: c.version, body: { isActive: true } })} /> : null}
              </Row>
            </Card>
          ))}
        </>
      ) : null}
    </Screen>
  );
}
