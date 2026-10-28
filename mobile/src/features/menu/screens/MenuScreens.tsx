/** 08 §8.10: cardápio do dia, semanal, histórico e editor (`menu/menus/*`). */
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useEffect, useMemo, useRef, useState } from 'react';
import { Switch, View } from 'react-native';

import { errorMessage, isApiError } from '@/api/errors';
import type { MenuKind, MenuType, Product } from '@/api/types';
import {
  Banner,
  BottomSheet,
  Button,
  Card,
  Chip,
  EmptyState,
  ErrorState,
  IconButton,
  MoneyInput,
  Row,
  Screen,
  Section,
  SegmentedControl,
  Skeleton,
  Text,
  TextField,
  space,
} from '@/components/ui';
import { useProducts } from '@/features/catalog/hooks';
import { useCurrentBusinessDay } from '@/features/businessDay/hooks';
import { addDays, formatBusinessDate, isoDayOfWeek, todayIn, WEEKDAYS } from '@/lib/dates';
import { formatBRL } from '@/lib/money';
import { productKindNames } from '@/lib/strings';
import { useCanWrite, useSession } from '@/stores/session';

import { toItemInputs, useCloneMenu, useCreateMenu, useCurrentMenu, useMenu, useMenus, useReplaceMenuItems, useUpdateMenuItem } from '../hooks';

const TYPES: { value: MenuType; label: string }[] = [
  { value: 'MARMITA', label: 'Marmita' },
  { value: 'PF', label: 'PF' },
];

function slotLabel(m: { kind: MenuKind; date: string | null; dayOfWeek: number | null }) {
  return m.kind === 'DAILY' ? formatBusinessDate(m.date!, true) : `Modelo de ${WEEKDAYS[m.dayOfWeek!]?.toLowerCase()}`;
}

/** Cria o cardápio do slot (vazio) e abre o editor. 409 MenuAlreadyExists abre o existente. */
function CreateSlotButton({ menuType, kind, date, dayOfWeek, label }: { menuType: MenuType; kind: MenuKind; date: string | null; dayOfWeek: number | null; label: string }) {
  const create = useCreateMenu();
  return (
    <>
      <Button
        title={label}
        icon="add"
        loading={create.isPending}
        onPress={() =>
          create.mutate(
            { menuType, kind, date, dayOfWeek, items: [] },
            {
              onSuccess: (m) => router.push(`/menu/menus/${m.id}`),
              onError: (e) => {
                if (isApiError(e, 'MenuAlreadyExists') && typeof e.extensions.existingMenuId === 'string') router.push(`/menu/menus/${e.extensions.existingMenuId}`);
              },
            },
          )
        }
      />
      {create.error && !isApiError(create.error, 'MenuAlreadyExists') ? <Banner tone="danger" text={errorMessage(create.error)} /> : null}
    </>
  );
}

export function TodayMenuScreen() {
  const params = useLocalSearchParams<{ menuType?: MenuType }>();
  const [menuType, setMenuType] = useState<MenuType>(params.menuType ?? 'MARMITA');
  const day = useCurrentBusinessDay();
  const timeZone = useSession((s) => s.me?.tenant.timeZone ?? 'America/Sao_Paulo');
  const current = useCurrentMenu(menuType, !!day.data);
  const date = day.data?.businessDate ?? todayIn(timeZone);
  // Sem expediente aberto, o "cardápio de hoje" é o DAILY da data de hoje.
  const byDate = useMenus({ menuType, kind: 'DAILY', from: date, to: date });
  const menuId = day.data ? current.data?.menu?.id : byDate.data?.[0]?.id;

  return (
    <Screen>
      <Stack.Screen options={{ title: 'Cardápio do dia' }} />
      <SegmentedControl value={menuType} onChange={setMenuType} options={TYPES} />
      <Text muted>{formatBusinessDate(date, true)}</Text>
      {menuId ? (
        <MenuEditor menuId={menuId} embedded />
      ) : current.isLoading || byDate.isLoading ? (
        <Skeleton />
      ) : (
        <>
          <EmptyState title="Ainda não há cardápio" text="Monte agora ou reaproveite um anterior pelo histórico." />
          <CreateSlotButton menuType={menuType} kind="DAILY" date={date} dayOfWeek={null} label="Montar agora" />
          <Button title="Reaproveitar do histórico" variant="secondary" onPress={() => router.push('/menu/menus/history')} />
        </>
      )}
    </Screen>
  );
}

export function WeeklyMenuScreen() {
  const [menuType, setMenuType] = useState<MenuType>('MARMITA');
  const templates = useMenus({ menuType, kind: 'WEEKLY_TEMPLATE' });
  return (
    <Screen>
      <Stack.Screen options={{ title: 'Cardápio semanal' }} />
      <SegmentedControl value={menuType} onChange={setMenuType} options={TYPES} />
      <Text muted>O modelo do dia vira o cardápio do dia ao abrir o expediente (ADR-0038).</Text>
      {templates.isLoading ? <Skeleton /> : null}
      {[1, 2, 3, 4, 5, 6, 7].map((dow) => {
        const t = templates.data?.find((m) => m.dayOfWeek === dow);
        return (
          <Card key={dow} onPress={t ? () => router.push(`/menu/menus/${t.id}`) : undefined}>
            <Row style={{ justifyContent: 'space-between' }}>
              <Text style={{ fontWeight: '700' }}>{WEEKDAYS[dow]}</Text>
              {t ? <Text muted>{t.itemCount} itens</Text> : <CreateSlotButton menuType={menuType} kind="WEEKLY_TEMPLATE" date={null} dayOfWeek={dow} label="Criar" />}
            </Row>
          </Card>
        );
      })}
    </Screen>
  );
}

export function MenuHistoryScreen() {
  const [menuType, setMenuType] = useState<MenuType>('MARMITA');
  const timeZone = useSession((s) => s.me?.tenant.timeZone ?? 'America/Sao_Paulo');
  const today = todayIn(timeZone);
  const menus = useMenus({ menuType, kind: 'DAILY', from: addDays(today, -30), to: addDays(today, 7) });
  const clone = useCloneMenu();
  const day = useCurrentBusinessDay();
  const target = day.data?.businessDate ?? today;

  const doClone = (id: string, body: { kind: MenuKind; date: string | null; dayOfWeek: number | null }) =>
    clone.mutate(
      { id, body },
      {
        onSuccess: (m) => router.push(`/menu/menus/${m.id}`),
        onError: (e) => {
          if (isApiError(e, 'MenuAlreadyExists') && typeof e.extensions.existingMenuId === 'string') router.push(`/menu/menus/${e.extensions.existingMenuId}`);
        },
      },
    );

  return (
    <Screen>
      <Stack.Screen options={{ title: 'Histórico de cardápios' }} />
      <SegmentedControl value={menuType} onChange={setMenuType} options={TYPES} />
      {clone.error && !isApiError(clone.error, 'MenuAlreadyExists') ? <Banner tone="danger" text={errorMessage(clone.error)} /> : null}
      {menus.isLoading ? <Skeleton /> : null}
      {menus.data?.length === 0 ? <EmptyState title="Nenhum cardápio nos últimos 30 dias" /> : null}
      {menus.data?.map((m) => (
        <Card key={m.id} onPress={() => router.push(`/menu/menus/${m.id}`)}>
          <Row style={{ justifyContent: 'space-between' }}>
            <Text style={{ fontWeight: '700' }}>{slotLabel(m)}</Text>
            <Text muted>{m.itemCount} itens</Text>
          </Row>
          <Row style={{ flexWrap: 'wrap' }}>
            {m.date !== target ? <Button title={`Usar em ${formatBusinessDate(target)}`} variant="secondary" onPress={() => doClone(m.id, { kind: 'DAILY', date: target, dayOfWeek: null })} /> : null}
            <Button title="Usar amanhã" variant="secondary" onPress={() => doClone(m.id, { kind: 'DAILY', date: addDays(target, 1), dayOfWeek: null })} />
            <Button
              title={`Salvar como modelo de ${WEEKDAYS[isoDayOfWeek(m.date!)]?.toLowerCase()}`}
              variant="ghost"
              onPress={() => doClone(m.id, { kind: 'WEEKLY_TEMPLATE', date: null, dayOfWeek: isoDayOfWeek(m.date!) })}
            />
          </Row>
        </Card>
      ))}
    </Screen>
  );
}

export function MenuDetailScreen() {
  const { id } = useLocalSearchParams<{ id: string }>();
  return (
    <Screen>
      <MenuEditor menuId={id} />
    </Screen>
  );
}

/**
 * Itens em seções Pratos e Acompanhamentos; "+ Adicionar do catálogo"; "+ Novo prato" (volta com o prato
 * incluído, RN-002.4/3.7); toggle "acabou"; preço de PF editável.
 */
function MenuEditor({ menuId, embedded }: { menuId: string; embedded?: boolean }) {
  const params = useLocalSearchParams<{ addProductId?: string }>();
  const menu = useMenu(menuId);
  const products = useProducts();
  const canManage = useCanWrite('menu.manage');
  const replace = useReplaceMenuItems();
  const updateItem = useUpdateMenuItem();
  const [adding, setAdding] = useState(false);
  const [priceFor, setPriceFor] = useState<{ itemId: string; price: string } | null>(null);
  const handled = useRef<string | null>(null);

  const m = menu.data;
  const error = replace.error ?? updateItem.error;

  const addProducts = (ids: string[]) => {
    if (!m) return;
    const existing = new Set(m.items.map((i) => i.productId));
    const next = [...toItemInputs(m)];
    let order = Math.max(0, ...m.items.map((i) => i.sortOrder));
    for (const productId of ids) {
      if (!existing.has(productId)) next.push({ productId, pfPrice: null, isAvailable: true, sortOrder: ++order });
    }
    replace.mutate({ id: m.id, items: next, version: m.version });
  };

  // Voltou do "+ Novo prato" com o produto criado.
  useEffect(() => {
    if (m && params.addProductId && handled.current !== params.addProductId) {
      handled.current = params.addProductId;
      addProducts([params.addProductId]);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [m?.id, params.addProductId]);

  const candidates = useMemo(() => {
    const inMenu = new Set(m?.items.map((i) => i.productId));
    return (products.data ?? []).filter((p) => (p.kind === 'MAIN_DISH' || p.kind === 'SIDE_DISH') && p.isActive && !inMenu.has(p.id));
  }, [products.data, m]);

  if (menu.isLoading) return <Skeleton />;
  if (!m) return <ErrorState message={errorMessage(menu.error)} onRetry={() => void menu.refetch()} />;

  const section = (s: 'MAIN_DISH' | 'SIDE_DISH') => m.items.filter((i) => i.section === s).sort((a, b) => a.sortOrder - b.sortOrder);

  return (
    <View style={{ gap: space.md }}>
      {!embedded ? <Stack.Screen options={{ title: `${m.menuType === 'PF' ? 'PF' : 'Marmita'} · ${slotLabel(m)}` }} /> : null}
      {error ? <Banner tone="danger" text={errorMessage(error)} /> : null}
      {(['MAIN_DISH', 'SIDE_DISH'] as const).map((s) => (
        <Section key={s} title={s === 'MAIN_DISH' ? 'Pratos' : 'Acompanhamentos'}>
          {section(s).length === 0 ? <Text muted>Nenhum.</Text> : null}
          {section(s).map((i) => (
            <Card key={i.id}>
              <Row>
                <View style={{ flex: 1 }}>
                  <Text style={{ fontWeight: '700', textDecorationLine: i.isAvailable ? 'none' : 'line-through' }}>{i.name}</Text>
                  {m.menuType === 'PF' && s === 'MAIN_DISH' ? (
                    <Text variant="small" muted onPress={canManage ? () => setPriceFor({ itemId: i.id, price: i.pfPrice ?? '0.00' }) : undefined}>
                      PF: {i.pfPrice ? formatBRL(i.pfPrice) : 'preço padrão'} {canManage ? '· alterar' : ''}
                    </Text>
                  ) : null}
                  {!i.isAvailable ? <Text variant="small" muted>Acabou</Text> : null}
                </View>
                {canManage ? (
                  <>
                    <Switch
                      value={i.isAvailable}
                      accessibilityLabel={`${i.name} disponível`}
                      onValueChange={(isAvailable) => updateItem.mutate({ id: m.id, itemId: i.id, body: { isAvailable }, version: m.version })}
                    />
                    <IconButton
                      icon="trash-outline"
                      label={`Tirar ${i.name} do cardápio`}
                      onPress={() => replace.mutate({ id: m.id, version: m.version, items: toItemInputs(m).filter((x) => x.productId !== i.productId) })}
                    />
                  </>
                ) : null}
              </Row>
            </Card>
          ))}
        </Section>
      ))}
      {canManage ? (
        <Row>
          <Button title="Adicionar do catálogo" icon="add" variant="secondary" onPress={() => setAdding(true)} style={{ flex: 1 }} />
          <Button
            title="Novo prato"
            icon="add-circle-outline"
            variant="secondary"
            onPress={() => router.push({ pathname: '/menu/products/new', params: { kind: 'MAIN_DISH', returnTo: `/menu/menus/${m.id}` } })}
            style={{ flex: 1 }}
          />
        </Row>
      ) : null}

      <CatalogPicker visible={adding} candidates={candidates} onClose={() => setAdding(false)} onAdd={(ids) => { addProducts(ids); setAdding(false); }} />

      <BottomSheet
        visible={priceFor !== null}
        title="Preço do PF"
        onClose={() => setPriceFor(null)}
        footer={
          <Button
            title="Salvar preço"
            loading={updateItem.isPending}
            onPress={() => priceFor && updateItem.mutate({ id: m.id, itemId: priceFor.itemId, body: { pfPrice: priceFor.price }, version: m.version }, { onSuccess: () => setPriceFor(null) })}
          />
        }
      >
        {priceFor ? <MoneyInput label="Preço deste prato no PF" value={priceFor.price} onChange={(price) => setPriceFor({ ...priceFor, price })} /> : null}
      </BottomSheet>
    </View>
  );
}

function CatalogPicker({ visible, candidates, onClose, onAdd }: { visible: boolean; candidates: Product[]; onClose: () => void; onAdd: (ids: string[]) => void }) {
  const [selected, setSelected] = useState<string[]>([]);
  const [search, setSearch] = useState('');
  const close = () => {
    setSelected([]);
    onClose();
  };
  const list = candidates.filter((p) => p.name.toLowerCase().includes(search.trim().toLowerCase()));
  return (
    <BottomSheet
      visible={visible}
      title="Adicionar do catálogo"
      onClose={close}
      footer={
        <Button
          title={`Adicionar ${selected.length}`}
          disabled={selected.length === 0}
          onPress={() => {
            onAdd(selected);
            setSelected([]);
          }}
        />
      }
    >
      <TextField label="Buscar" value={search} onChangeText={setSearch} />
      {(['MAIN_DISH', 'SIDE_DISH'] as const).map((k) => (
        <View key={k} style={{ gap: space.sm }}>
          <Text variant="subtitle">{productKindNames[k]}</Text>
          <Row style={{ flexWrap: 'wrap' }}>
            {list
              .filter((p) => p.kind === k)
              .map((p) => (
                <Chip key={p.id} label={p.name} selected={selected.includes(p.id)} onPress={() => setSelected((s) => (s.includes(p.id) ? s.filter((x) => x !== p.id) : [...s, p.id]))} />
              ))}
          </Row>
        </View>
      ))}
      {candidates.length === 0 ? <Text muted>Todos os pratos e acompanhamentos ativos já estão no cardápio.</Text> : null}
    </BottomSheet>
  );
}

