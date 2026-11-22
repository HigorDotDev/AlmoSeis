/**
 * 08 §8.10 `menu/fixed-menu` — "Cardápio" da lanchonete (EP-003 fatia 2; ADR-0056, ADR-0057; 06 §6.5).
 * Três abas: Pratos (por categoria, com os grupos de adicionais), Bebidas e Combos.
 * - Ver: `orders.view`. Cadastrar: `catalog.manage`. Pausar/reativar: `catalog.availability` (atendente e cozinha
 *   também, Q-306): quem só pode pausar vê a mesma lista, com as chaves de pausa e sem os botões de cadastro.
 * - A disponibilidade (`isSellable`) é a do servidor; a tela só explica ("Indisponível: Batata frita P pausado").
 */
import { router, Stack } from 'expo-router';
import { useState } from 'react';
import { Switch, View } from 'react-native';

import { errorMessage } from '@/api/errors';
import type { AddonGroup, BeverageType, Combo, Dish, FixedMenu } from '@/api/types';
import { Banner, Button, Card, Divider, EmptyState, ErrorState, Row, Screen, Section, SegmentedControl, Skeleton, Text, space } from '@/components/ui';
import { formatBRL, toCents } from '@/lib/money';
import { useCanWrite, usePermission } from '@/stores/session';

import { PauseSwitch, StatusText, usePauseAction } from '../components';
import {
  addonGroupLinksText,
  addonGroupsFor,
  beverageSizeStatus,
  comboStatus,
  dishPriceSummary,
  dishStatus,
  EMPTY_MENU,
  groupDishes,
  siteSummary,
} from '../fixedMenu';
import { useFixedMenu } from '../hooks';

type Tab = 'dishes' | 'beverages' | 'combos';

const TABS: { value: Tab; label: string }[] = [
  { value: 'dishes', label: 'Pratos' },
  { value: 'beverages', label: 'Bebidas' },
  { value: 'combos', label: 'Combos' },
];

export default function FixedMenuScreen() {
  const [tab, setTab] = useState<Tab>('dishes');
  const [showInactive, setShowInactive] = useState(false);
  const menu = useFixedMenu(true);
  const canManage = useCanWrite('catalog.manage');
  const canPause = useCanWrite('catalog.availability');
  const hasManage = usePermission('catalog.manage');
  const hasPause = usePermission('catalog.availability');
  const data = menu.data ?? EMPTY_MENU;

  return (
    <Screen>
      <Stack.Screen options={{ title: 'Cardápio' }} />
      <SegmentedControl value={tab} onChange={setTab} options={TABS} />
      {!hasManage && hasPause ? (
        <Banner tone="info" text="Você pode pausar e reativar o que acabou. Para mudar preços e cadastro, peça ao proprietário ou ao gerente." />
      ) : null}
      {!hasManage && !hasPause ? <Banner tone="info" text="Só o proprietário ou o gerente alteram o cardápio." /> : null}
      {hasManage ? (
        <Row style={{ justifyContent: 'space-between' }}>
          <Text>Mostrar inativos</Text>
          <Switch value={showInactive} onValueChange={setShowInactive} accessibilityLabel="Mostrar inativos" />
        </Row>
      ) : null}
      {menu.isLoading ? <Skeleton lines={4} /> : null}
      {menu.isError && !menu.data ? <ErrorState message={errorMessage(menu.error)} onRetry={() => void menu.refetch()} /> : null}
      {menu.data ? (
        tab === 'dishes' ? (
          <DishesTab menu={data} showInactive={showInactive} canManage={canManage} canPause={canPause} />
        ) : tab === 'beverages' ? (
          <BeveragesTab menu={data} showInactive={showInactive} canManage={canManage} canPause={canPause} />
        ) : (
          <CombosTab menu={data} showInactive={showInactive} canManage={canManage} canPause={canPause} />
        )
      ) : null}
    </Screen>
  );
}

interface TabProps {
  menu: FixedMenu;
  showInactive: boolean;
  canManage: boolean;
  canPause: boolean;
}

const inactiveStyle = { opacity: 0.55 };

// ---------------------------------------------------------------- Pratos

function DishesTab({ menu, showInactive, canManage, canPause }: TabProps) {
  const groups = groupDishes(menu, showInactive);
  const addonGroups = menu.addonGroups.filter((g) => showInactive || g.isActive);
  const activeCategories = menu.categories.filter((c) => c.isActive);
  return (
    <>
      {canManage ? (
        <Row>
          <Button
            title="Novo prato"
            icon="add"
            style={{ flex: 1 }}
            disabled={activeCategories.length === 0}
            onPress={() => router.push({ pathname: '/menu/fixed-menu/dish', params: {} })}
          />
          <Button title="Categorias" icon="list-outline" variant="secondary" style={{ flex: 1 }} onPress={() => router.push('/menu/fixed-menu/categories')} />
        </Row>
      ) : null}
      {canManage && activeCategories.length === 0 ? (
        <Text variant="small" muted>
          Crie primeiro uma categoria (Lanches, Porções, Açaí…) em “Categorias”.
        </Text>
      ) : null}
      {groups.every((g) => g.dishes.length === 0) ? <EmptyState icon="fast-food-outline" title="Nenhum prato cadastrado" /> : null}
      {groups.map((g) =>
        g.dishes.length === 0 && !canManage ? null : (
          <Section key={g.category?.id ?? 'other'} title={g.category ? `${g.category.name}${g.category.isActive ? '' : ' (inativa)'}` : 'Outros'}>
            {g.dishes.length === 0 ? (
              <Text variant="small" muted>
                Nenhum prato nesta categoria.
              </Text>
            ) : null}
            {g.dishes.map((d) => (
              <DishCard key={d.id} dish={d} menu={menu} canManage={canManage} canPause={canPause} />
            ))}
          </Section>
        ),
      )}

      <Divider />
      <Section
        title="Grupos de adicionais"
        right={canManage ? <Button title="Novo grupo" icon="add" variant="ghost" onPress={() => router.push({ pathname: '/menu/fixed-menu/addon-group', params: {} })} /> : null}
      >
        <Text variant="small" muted>
          Ligados a uma categoria ou a um prato. O mesmo preço em qualquer tamanho e no site.
        </Text>
        {addonGroups.length === 0 ? (
          <Text variant="small" muted>
            Nenhum grupo cadastrado.
          </Text>
        ) : null}
        {addonGroups.map((g) => (
          <AddonGroupCard key={g.id} group={g} menu={menu} canManage={canManage} canPause={canPause} showInactive={showInactive} />
        ))}
      </Section>
    </>
  );
}

function DishCard({ dish, menu, canManage, canPause }: { dish: Dish; menu: FixedMenu; canManage: boolean; canPause: boolean }) {
  const { run, busyId } = usePauseAction();
  const busy = busyId === dish.id;
  const sizes = dish.sizes.filter((s) => s.isActive);
  const groups = addonGroupsFor(menu, dish.dishCategoryId, dish.id);
  return (
    <Card
      style={!dish.isActive ? inactiveStyle : undefined}
      onPress={canManage ? () => router.push({ pathname: '/menu/fixed-menu/dish', params: { id: dish.id } }) : undefined}
      accessibilityLabel={canManage ? `Editar ${dish.name}` : undefined}
    >
      <Row style={{ justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <View style={{ flex: 1, gap: 2 }}>
          <Text style={{ fontWeight: '700' }}>{dish.name}</Text>
          <Text variant="small" muted>
            {dishPriceSummary(dish)}
          </Text>
          {!dish.hasSizes ? (
            <Text variant="small" muted>
              {siteSummary(dish.sellsOnSite, dish.sitePrice, dish.price)}
            </Text>
          ) : null}
          {groups.length ? (
            <Text variant="small" muted>
              Adicionais: {groups.map((g) => g.name).join(', ')}
            </Text>
          ) : null}
          <StatusText status={dishStatus(dish)} />
        </View>
        {dish.isActive ? (
          <PauseSwitch
            name={dish.name}
            paused={dish.isPaused}
            canPause={canPause}
            busy={busy}
            onChange={(paused) => run({ kind: 'dish', id: dish.id, version: dish.version, body: { isPaused: paused }, label: dish.name, paused })}
          />
        ) : null}
      </Row>
      {dish.hasSizes && dish.isActive
        ? sizes.map((s) => (
            <Row key={s.id} style={{ justifyContent: 'space-between', paddingLeft: space.md }}>
              <View style={{ flex: 1 }}>
                <Text>
                  {s.name} · {formatBRL(s.price)}
                </Text>
                <Text variant="small" muted>
                  {siteSummary(s.sellsOnSite, s.sitePrice, s.price)}
                </Text>
              </View>
              <PauseSwitch
                name={`${dish.name} ${s.name}`}
                paused={s.isPaused}
                canPause={canPause}
                busy={busy}
                onChange={(paused) =>
                  run({ kind: 'dish', id: dish.id, version: dish.version, body: { sizes: [{ id: s.id, isPaused: paused }] }, label: `${dish.name} ${s.name}`, paused })
                }
              />
            </Row>
          ))
        : null}
    </Card>
  );
}

function AddonGroupCard({ group, menu, canManage, canPause, showInactive }: { group: AddonGroup; menu: FixedMenu; canManage: boolean; canPause: boolean; showInactive: boolean }) {
  const { run, busyId } = usePauseAction();
  const options = group.options.filter((o) => showInactive || o.isActive);
  return (
    <Card
      style={!group.isActive ? inactiveStyle : undefined}
      onPress={canManage ? () => router.push({ pathname: '/menu/fixed-menu/addon-group', params: { id: group.id } }) : undefined}
      accessibilityLabel={canManage ? `Editar ${group.name}` : undefined}
    >
      <Text style={{ fontWeight: '700' }}>
        {group.name}
        {group.isActive ? '' : ' (inativo)'}
      </Text>
      <Text variant="small" muted>
        {addonGroupLinksText(menu, group)}
      </Text>
      {options.map((o) => (
        <Row key={o.id} style={[{ justifyContent: 'space-between', paddingLeft: space.md }, !o.isActive ? inactiveStyle : null]}>
          <Text style={{ flex: 1 }}>
            {o.name} · {toCents(o.price) === 0 ? 'grátis' : `+ ${formatBRL(o.price)}`}
          </Text>
          {group.isActive && o.isActive ? (
            <PauseSwitch
              name={o.name}
              paused={o.isPaused}
              canPause={canPause}
              busy={busyId === group.id}
              onChange={(paused) => run({ kind: 'addon', id: group.id, version: group.version, body: { options: [{ id: o.id, isPaused: paused }] }, label: o.name, paused })}
            />
          ) : null}
        </Row>
      ))}
    </Card>
  );
}

// ---------------------------------------------------------------- Bebidas

function BeveragesTab({ menu, showInactive, canManage, canPause }: TabProps) {
  const types = menu.beverageTypes.filter((t) => showInactive || t.isActive);
  return (
    <>
      {canManage ? <Button title="Novo tipo de bebida" icon="add" onPress={() => router.push({ pathname: '/menu/fixed-menu/beverage', params: {} })} /> : null}
      {types.length === 0 ? <EmptyState icon="wine-outline" title="Nenhuma bebida cadastrada" text="Tipo (Refrigerante, Suco) → tamanho com preço → sabores." /> : null}
      {types.map((t) => (
        <BeverageTypeCard key={t.id} type={t} canManage={canManage} canPause={canPause} showInactive={showInactive} />
      ))}
    </>
  );
}

function BeverageTypeCard({ type, canManage, canPause, showInactive }: { type: BeverageType; canManage: boolean; canPause: boolean; showInactive: boolean }) {
  const { run, busyId } = usePauseAction();
  const busy = busyId === type.id;
  const sizes = type.sizes.filter((s) => showInactive || s.isActive);
  return (
    <Card
      style={!type.isActive ? inactiveStyle : undefined}
      onPress={canManage ? () => router.push({ pathname: '/menu/fixed-menu/beverage', params: { id: type.id } }) : undefined}
      accessibilityLabel={canManage ? `Editar ${type.name}` : undefined}
    >
      <Text variant="subtitle">
        {type.name}
        {type.isActive ? '' : ' (inativo)'}
      </Text>
      {sizes.length === 0 ? (
        <Text variant="small" muted>
          Nenhum tamanho.
        </Text>
      ) : null}
      {sizes.map((s) => {
        const label = `${type.name} ${s.name}`;
        const flavors = s.flavors.filter((f) => showInactive || f.isActive);
        return (
          <View key={s.id} style={[{ gap: 4 }, !s.isActive ? inactiveStyle : null]}>
            <Row style={{ justifyContent: 'space-between', alignItems: 'flex-start' }}>
              <View style={{ flex: 1 }}>
                <Text style={{ fontWeight: '600' }}>
                  {s.name} · {formatBRL(s.price)}
                </Text>
                <Text variant="small" muted>
                  {siteSummary(s.sellsOnSite, s.sitePrice, s.price)}
                  {flavors.length === 0 ? ' · sem escolha de sabor' : ''}
                </Text>
                <StatusText status={beverageSizeStatus(s)} />
              </View>
              {type.isActive && s.isActive ? (
                <PauseSwitch
                  name={label}
                  paused={s.isPaused}
                  canPause={canPause}
                  busy={busy}
                  onChange={(paused) => run({ kind: 'beverage', id: type.id, version: type.version, body: { sizes: [{ id: s.id, isPaused: paused }] }, label, paused })}
                />
              ) : null}
            </Row>
            {flavors.map((f) => (
              <Row key={f.id} style={[{ justifyContent: 'space-between', paddingLeft: space.lg }, !f.isActive ? inactiveStyle : null]}>
                <Text style={{ flex: 1 }}>
                  {f.name}
                  {toCents(f.extraPrice) > 0 ? ` · + ${formatBRL(f.extraPrice)}` : ''}
                </Text>
                {type.isActive && s.isActive && f.isActive ? (
                  <PauseSwitch
                    name={`${f.name} ${s.name}`}
                    paused={f.isPaused}
                    canPause={canPause}
                    busy={busy}
                    onChange={(paused) =>
                      run({ kind: 'beverage', id: type.id, version: type.version, body: { flavors: [{ id: f.id, isPaused: paused }] }, label: `${f.name} ${s.name}`, paused })
                    }
                  />
                ) : null}
              </Row>
            ))}
          </View>
        );
      })}
    </Card>
  );
}

// ---------------------------------------------------------------- Combos

function CombosTab({ menu, showInactive, canManage, canPause }: TabProps) {
  const combos = menu.combos.filter((c) => showInactive || c.isActive);
  return (
    <>
      {canManage ? <Button title="Novo combo" icon="add" onPress={() => router.push({ pathname: '/menu/fixed-menu/combo', params: {} })} /> : null}
      {combos.length === 0 ? <EmptyState icon="pricetags-outline" title="Nenhum combo cadastrado" text="Preço fechado, com pratos e bebidas do cardápio." /> : null}
      {combos.map((c) => (
        <ComboCard key={c.id} combo={c} canManage={canManage} canPause={canPause} />
      ))}
    </>
  );
}

function ComboCard({ combo, canManage, canPause }: { combo: Combo; canManage: boolean; canPause: boolean }) {
  const { run, busyId } = usePauseAction();
  return (
    <Card
      style={!combo.isActive ? inactiveStyle : undefined}
      onPress={canManage ? () => router.push({ pathname: '/menu/fixed-menu/combo', params: { id: combo.id } }) : undefined}
      accessibilityLabel={canManage ? `Editar ${combo.name}` : undefined}
    >
      <Row style={{ justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <View style={{ flex: 1, gap: 2 }}>
          <Text style={{ fontWeight: '700' }}>{combo.name}</Text>
          <Text variant="small" muted>
            {formatBRL(combo.price)} · {siteSummary(combo.sellsOnSite, combo.sitePrice, combo.price)}
          </Text>
          {[...combo.components]
            .sort((a, b) => a.sortOrder - b.sortOrder)
            .map((p) => (
              <Text key={p.id} variant="small" muted={p.isSellable}>
                {p.quantity}× {p.name}
                {p.kind === 'BEVERAGE' ? ' (sabor no pedido)' : ''}
              </Text>
            ))}
          <StatusText status={comboStatus(combo)} />
        </View>
        {combo.isActive ? (
          <PauseSwitch
            name={combo.name}
            paused={combo.isPaused}
            canPause={canPause}
            busy={busyId === combo.id}
            onChange={(paused) => run({ kind: 'combo', id: combo.id, version: combo.version, body: { isPaused: paused }, label: combo.name, paused })}
          />
        ) : null}
      </Row>
    </Card>
  );
}
