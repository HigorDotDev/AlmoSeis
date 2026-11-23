/**
 * EP-003 fatia 3 (ADR-0058, 06 §6.9): comanda à la carte com o cardápio fixo. Montar prato com tamanho e adicionais,
 * bebida com sabor e combo com partes; o corpo enviado (`POST /orders` [I]); a árvore no detalhe; adicional de grupo
 * num prato já registrado; remover o combo inteiro; item indisponível desabilitado; `422 ProductUnavailable`
 * recarregando o cardápio; a cozinha e o fechamento.
 *
 * Os ids seguem o exemplo literal do `Order` à la carte do 06 §6.9 (d01 X-Burguer, d02/d21 Batata frita P,
 * d03/d32 Açaí 500 ml, f11 Lata 350 ml, f12 Coca-Cola, f13 Coca-Cola Zero, c01 o combo, e11 Bacon, e21 Granola),
 * com os preços do exemplo do `GET /fixed-menu` do 06 §6.5.
 */
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import { router } from 'expo-router';
import type { ReactElement } from 'react';

import { configureApiClient } from '@/api/client';
import type { ClosingSummary, Combo, FixedMenu, Me, Order, OrderItem, ProductionOrder, TenantModule } from '@/api/types';
import { ClosingSummaryView } from '@/features/closing/components';
import {
  addItemsBody,
  addonTargets,
  aLaCarteFormIssue,
  buildALaCarteBody,
  comboUnavailableReason,
  emptyALaCarteForm,
  fixedMenuBook,
  itemWithChildrenCents,
  lineCents,
  lineItem,
  newComboLine,
  newDishLine,
  type ALaCarteLine,
} from '@/features/orders/aLaCarte';
import OrderDetailScreen from '@/features/orders/screens/OrderDetailScreen';
import NewALaCarteScreen from '@/features/orders/screens/NewALaCarteScreen';
import { AddItemsScreen } from '@/features/orders/screens/OrderSubScreens';
import { ProductionCard } from '@/features/production/screens/ProductionTab';
import { useConnectivity } from '@/stores/connectivity';
import { usePendingOps } from '@/stores/pendingOps';
import { useSession } from '@/stores/session';
import { useToast } from '@/stores/toast';
import { memory } from '@/test-utils/fakeDb';

jest.setTimeout(60_000);

jest.mock('expo-router', () => ({
  router: { push: jest.fn(), back: jest.fn(), replace: jest.fn() },
  Stack: { Screen: () => null },
  useLocalSearchParams: () => ({ id: '0192d6a0-0000-7000-8000-000000000501' }),
}));
jest.mock('@/db/pendingOpsRepo', () => ({ pendingOpsRepo: jest.requireActual('@/test-utils/fakeDb').pendingOpsRepo }));
jest.mock('@/db/draftsRepo', () => ({ draftsRepo: jest.requireActual('@/test-utils/fakeDb').draftsRepo }));
jest.mock('@/db/metaRepo', () => ({ metaRepo: jest.requireActual('@/test-utils/fakeDb').metaRepo }));
// O painel da cozinha toca som e mantém a tela acesa; aqui só interessa o card.
jest.mock('@/features/production/orderSound', () => ({ useOrderSound: () => ({ play: () => {} }) }));
jest.mock('@/lib/keepAwake', () => ({ useKeepAwake: () => {} }));

const id = (suffix: string) => `0192d6a0-0000-7000-8000-000000000${suffix}`;
const T = '2026-11-22T10:00:00Z';
const ORDER_ID = id('501');

const CAT_LANCHES = id('a01');
const CAT_PORCOES = id('a02');
const G_LANCHE = id('e01');
const G_ACAI = id('e02');

const sizeBase = { sellsOnSite: false, sitePrice: null, isActive: true };
const dishBase = { version: 1, description: null, imageUrl: null, requiresPreparation: true, sellsOnSite: false, sitePrice: null, isActive: true, createdAt: T, updatedAt: T };

/** Batata P à venda, Batata G pausada, X-Salada pausado, Guaraná pausado, Cheddar pausado. */
const MENU: FixedMenu = {
  categories: [
    { id: CAT_LANCHES, version: 1, name: 'Lanches', sortOrder: 1, isActive: true, createdAt: T, updatedAt: T },
    { id: CAT_PORCOES, version: 1, name: 'Porções', sortOrder: 2, isActive: true, createdAt: T, updatedAt: T },
  ],
  dishes: [
    { ...dishBase, id: id('d01'), dishCategoryId: CAT_LANCHES, name: 'X-Burguer', hasSizes: false, price: '22.00', sortOrder: 1, isPaused: false, isSellable: true, sizes: [], addonGroupIds: [G_LANCHE] },
    { ...dishBase, id: id('d04'), dishCategoryId: CAT_LANCHES, name: 'X-Salada', hasSizes: false, price: '24.00', sortOrder: 2, isPaused: true, isSellable: false, sizes: [], addonGroupIds: [G_LANCHE] },
    {
      ...dishBase,
      id: id('d02'),
      dishCategoryId: CAT_PORCOES,
      name: 'Batata frita',
      hasSizes: true,
      price: null,
      sortOrder: 1,
      isPaused: false,
      isSellable: true,
      sizes: [
        { ...sizeBase, id: id('d21'), name: 'P', price: '12.00', sortOrder: 1, isPaused: false, isSellable: true },
        { ...sizeBase, id: id('d22'), name: 'G', price: '18.00', sortOrder: 2, isPaused: true, isSellable: false },
      ],
      addonGroupIds: [],
    },
    {
      ...dishBase,
      id: id('d03'),
      dishCategoryId: CAT_PORCOES,
      name: 'Açaí',
      hasSizes: true,
      price: null,
      sortOrder: 2,
      isPaused: false,
      isSellable: true,
      sizes: [
        { ...sizeBase, id: id('d31'), name: '300 ml', price: '14.00', sortOrder: 1, isPaused: false, isSellable: true },
        { ...sizeBase, id: id('d32'), name: '500 ml', price: '18.00', sortOrder: 2, isPaused: false, isSellable: true },
      ],
      addonGroupIds: [G_ACAI],
    },
  ],
  beverageTypes: [
    {
      id: id('b01'),
      version: 1,
      name: 'Refrigerante',
      sortOrder: 1,
      isActive: true,
      sizes: [
        {
          ...sizeBase,
          id: id('f11'),
          name: 'Lata 350 ml',
          price: '6.00',
          sortOrder: 1,
          isPaused: false,
          isSellable: true,
          flavors: [
            { id: id('f12'), name: 'Coca-Cola', extraPrice: '0.00', sortOrder: 1, isActive: true, isPaused: false },
            { id: id('f13'), name: 'Coca-Cola Zero', extraPrice: '0.50', sortOrder: 2, isActive: true, isPaused: false },
            { id: id('f14'), name: 'Guaraná', extraPrice: '0.00', sortOrder: 3, isActive: true, isPaused: true },
          ],
        },
      ],
      createdAt: T,
      updatedAt: T,
    },
    {
      id: id('b02'),
      version: 1,
      name: 'Água',
      sortOrder: 2,
      isActive: true,
      sizes: [{ ...sizeBase, id: id('f21'), name: '500 ml', price: '4.00', sortOrder: 1, isPaused: false, isSellable: true, flavors: [] }],
      createdAt: T,
      updatedAt: T,
    },
  ],
  combos: [
    {
      id: id('c01'),
      version: 1,
      name: 'X-Burguer + batata + refri',
      description: null,
      imageUrl: null,
      price: '32.00',
      sellsOnSite: true,
      sitePrice: '34.90',
      sortOrder: 1,
      isActive: true,
      isPaused: false,
      isSellable: true,
      components: [
        { id: id('c11'), kind: 'DISH', dishId: id('d01'), dishSizeId: null, beverageSizeId: null, name: 'X-Burguer', quantity: 1, sortOrder: 0, isSellable: true },
        { id: id('c12'), kind: 'DISH', dishId: id('d02'), dishSizeId: id('d21'), beverageSizeId: null, name: 'Batata frita P', quantity: 1, sortOrder: 1, isSellable: true },
        { id: id('c13'), kind: 'BEVERAGE', dishId: null, dishSizeId: null, beverageSizeId: id('f11'), name: 'Refrigerante Lata 350 ml', quantity: 1, sortOrder: 2, isSellable: true },
      ],
      createdAt: T,
      updatedAt: T,
    },
    {
      id: id('c02'),
      version: 1,
      name: 'Combo família',
      description: null,
      imageUrl: null,
      price: '59.00',
      sellsOnSite: false,
      sitePrice: null,
      sortOrder: 2,
      isActive: true,
      isPaused: false,
      isSellable: false,
      components: [
        { id: id('c21'), kind: 'DISH', dishId: id('d01'), dishSizeId: null, beverageSizeId: null, name: 'X-Burguer', quantity: 2, sortOrder: 0, isSellable: true },
        { id: id('c22'), kind: 'DISH', dishId: id('d02'), dishSizeId: id('d22'), beverageSizeId: null, name: 'Batata frita G', quantity: 1, sortOrder: 1, isSellable: false },
      ],
      createdAt: T,
      updatedAt: T,
    },
  ],
  addonGroups: [
    {
      id: G_LANCHE,
      version: 1,
      name: 'Adicionais de lanche',
      sortOrder: 1,
      isActive: true,
      options: [
        { id: id('e11'), name: 'Bacon', price: '4.00', sortOrder: 1, isActive: true, isPaused: false },
        { id: id('e12'), name: 'Cheddar', price: '3.00', sortOrder: 2, isActive: true, isPaused: true },
      ],
      dishCategoryIds: [CAT_LANCHES],
      dishIds: [],
      createdAt: T,
      updatedAt: T,
    },
    {
      id: G_ACAI,
      version: 1,
      name: 'Complementos de açaí',
      sortOrder: 2,
      isActive: true,
      options: [{ id: id('e21'), name: 'Granola', price: '2.00', sortOrder: 1, isActive: true, isPaused: false }],
      dishCategoryIds: [],
      dishIds: [id('d03')],
      createdAt: T,
      updatedAt: T,
    },
  ],
};

const book = fixedMenuBook(MENU);
const bruno = { id: id('302'), displayName: 'Bruno' };

const item = (over: Partial<OrderItem>): OrderItem => ({
  id: id('600'),
  itemKind: 'FIXED_DISH',
  marmitaSizeId: null,
  productId: null,
  dishId: null,
  dishSizeId: null,
  beverageSizeId: null,
  beverageFlavorId: null,
  comboId: null,
  name: '',
  sizeName: null,
  flavorName: null,
  composition: null,
  components: [],
  quantity: 1,
  unitPrice: '0.00',
  lineTotal: '0.00',
  requiresPreparation: false,
  notes: null,
  addons: [],
  parts: [],
  createdAt: '2026-11-22T19:10:00Z',
  createdBy: bruno,
  ...over,
});

const groupAddon = (suffix: string, optionSuffix: string, name: string, unitPrice: string, lineTotal: string) => ({
  id: id(suffix),
  itemKind: 'GROUP_ADDON' as const,
  productId: null,
  addonOptionId: id(optionSuffix),
  name,
  quantity: 1,
  unitPrice,
  lineTotal,
  requiresPreparation: true,
  notes: null,
  createdAt: '2026-11-22T19:10:00Z',
  createdBy: bruno,
});

/** Exemplo literal do `Order` à la carte do 06 §6.9 (R$ 99,00), sobre os campos comuns do exemplo de marmita. */
const ORDER: Order = {
  id: ORDER_ID,
  version: 4,
  orderNumber: 12,
  businessDayId: id('401'),
  businessDate: '2026-11-22',
  orderType: 'A_LA_CARTE',
  origin: 'APP',
  priceList: 'COUNTER',
  fulfillmentType: 'DINE_IN',
  customer: null,
  deliveryAddress: null,
  tableLabel: '5',
  tabNumber: null,
  notes: null,
  expectedPaymentMethod: null,
  changeFor: null,
  riskFlag: null,
  deliveryFee: '0.00',
  deliveryFeeSource: null,
  deliveryDistanceKm: null,
  itemsTotal: '99.00',
  weighingsTotal: '0.00',
  totalAmount: '99.00',
  paidAmount: '0.00',
  balance: '99.00',
  isPaid: false,
  productionStatus: 'OPEN',
  productionStatusChangedAt: '2026-11-22T19:10:00Z',
  cancellationStatus: 'ACTIVE',
  cancellation: null,
  acceptanceStatus: 'ACCEPTED',
  acceptanceDeadline: null,
  acceptedAt: '2026-11-22T19:10:00Z',
  acceptedBy: bruno,
  estimatedReadyMinutes: null,
  rejection: null,
  expiredAt: null,
  withdrawnAt: null,
  fulfillmentStatus: null,
  fulfillmentStatusChangedAt: null,
  outForDeliveryAt: null,
  deliveredAt: null,
  pickedUpAt: null,
  isReadyForPickup: false,
  items: [
    item({
      id: id('641'),
      itemKind: 'FIXED_DISH',
      dishId: id('d03'),
      dishSizeId: id('d32'),
      name: 'Açaí 500 ml',
      sizeName: '500 ml',
      unitPrice: '18.00',
      lineTotal: '18.00',
      requiresPreparation: true,
      notes: 'Pouco leite condensado',
      addons: [groupAddon('642', 'e21', 'Granola', '2.00', '2.00')],
    }),
    item({
      id: id('643'),
      itemKind: 'FIXED_BEVERAGE',
      beverageSizeId: id('f11'),
      beverageFlavorId: id('f12'),
      name: 'Refrigerante Lata 350 ml · Coca-Cola',
      sizeName: 'Lata 350 ml',
      flavorName: 'Coca-Cola',
      unitPrice: '6.00',
      lineTotal: '6.00',
    }),
    item({
      id: id('644'),
      itemKind: 'COMBO',
      comboId: id('c01'),
      name: 'X-Burguer + batata + refri',
      quantity: 2,
      unitPrice: '32.00',
      lineTotal: '64.00',
      parts: [
        item({ id: id('645'), itemKind: 'COMBO_PART', dishId: id('d01'), name: 'X-Burguer', requiresPreparation: true, notes: 'Sem cebola', addons: [groupAddon('646', 'e11', 'Bacon', '4.00', '8.00')] }),
        item({ id: id('647'), itemKind: 'COMBO_PART', dishId: id('d02'), dishSizeId: id('d21'), name: 'Batata frita P', sizeName: 'P', requiresPreparation: true }),
        item({
          id: id('648'),
          itemKind: 'COMBO_PART',
          beverageSizeId: id('f11'),
          beverageFlavorId: id('f13'),
          name: 'Refrigerante Lata 350 ml · Coca-Cola Zero',
          sizeName: 'Lata 350 ml',
          flavorName: 'Coca-Cola Zero',
          unitPrice: '0.50',
          lineTotal: '1.00',
        }),
      ],
    }),
  ],
  weighings: [],
  payments: [],
  pendingAlertCount: 0,
  createdAt: '2026-11-22T19:10:00Z',
  createdBy: bruno,
  updatedAt: '2026-11-22T19:10:00Z',
};

// ---------------------------------------------------------------- Infra de teste (fetch simulado)

const json = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } });
const problem = (status: number, errorCode: string, ext: Record<string, unknown> = {}) =>
  new Response(JSON.stringify({ status, errorCode, title: errorCode, ...ext }), { status, headers: { 'Content-Type': 'application/problem+json' } });

type Handler = (method: string, path: string) => Response | undefined;
let writeHandler: Handler;
let menuResponse: () => FixedMenu;
const writes: { method: string; path: string; headers: Record<string, string>; body: unknown }[] = [];
const reads: string[] = [];

function signIn(modules: TenantModule[] = ['A_LA_CARTE', 'DELIVERY', 'FIXED_MENU']) {
  useSession.setState({
    status: 'signedIn',
    readOnly: false,
    me: {
      tenant: { id: 't1', name: 'Lanches do Beto', timeZone: 'America/Sao_Paulo', modules },
      employee: { id: 'e1', displayName: 'Ana', roles: ['OWNER'], hasPin: false },
      permissions: ['orders.view', 'orders.create', 'orders.edit', 'payments.register', 'orders.cancel', 'production.view', 'production.update', 'closing.view'],
      subscription: { canWrite: true },
    } as unknown as Me,
  });
}

beforeEach(() => {
  memory.reset();
  usePendingOps.getState().clear();
  useToast.setState({ toasts: [] });
  useConnectivity.setState({ online: true });
  writes.length = 0;
  reads.length = 0;
  writeHandler = () => undefined;
  menuResponse = () => MENU;
  (router.replace as jest.Mock).mockClear();
  (router.back as jest.Mock).mockClear();
  signIn();
  configureApiClient({ getAccessToken: () => 'tok', refresh: async () => true, onSessionExpired: () => {}, onUpgradeRequired: () => {}, onSubscriptionInactive: () => {} });
  global.fetch = jest.fn(async (url: string, init: RequestInit = {}) => {
    const path = url.replace(/^https?:\/\/[^/]+(\/api\/v1)?/, '');
    const method = init.method ?? 'GET';
    if (method !== 'GET') {
      writes.push({ method, path, headers: init.headers as Record<string, string>, body: init.body ? JSON.parse(init.body as string) : undefined });
      const r = writeHandler(method, path);
      if (r) return r;
      return json(500, { errorCode: 'Unexpected' });
    }
    reads.push(path);
    if (path.startsWith('/fixed-menu')) return json(200, menuResponse());
    if (path === `/orders/${ORDER_ID}`) return json(200, ORDER, { ETag: '"4"' });
    if (path.startsWith('/business-days/current')) return json(200, { id: id('401'), version: 1, businessDate: '2026-11-22', status: 'OPEN' });
    return json(404, { errorCode: 'NotFound' });
  }) as unknown as typeof fetch;
});

const clients: QueryClient[] = [];
afterEach(() => {
  for (const c of clients.splice(0)) c.clear();
});

async function renderWith(ui: ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false, gcTime: 0 } } });
  clients.push(client);
  return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
}

// ---------------------------------------------------------------- Regras puras

describe('montagem e corpo (06 §6.9 "Regras do corpo A_LA_CARTE")', () => {
  it('prato com tamanho e adicional: Açaí 500 ml + Granola = R$ 20,00, no formato do contrato', () => {
    const line: ALaCarteLine = { ...newDishLine('k1', book.dishes.get(id('d03'))!), dishSizeId: id('d32'), notes: 'Pouco leite condensado', addons: [{ addonOptionId: id('e21'), quantity: 1 }] };
    expect(lineCents(book, line)).toBe(2000);
    expect(lineItem(book, line)).toEqual({
      itemKind: 'FIXED_DISH',
      dishId: id('d03'),
      dishSizeId: id('d32'),
      quantity: 1,
      notes: 'Pouco leite condensado',
      groupAddons: [{ addonOptionId: id('e21'), quantity: 1 }],
    });
  });

  it('prato com tamanho não vai sem o tamanho; prato sem tamanho manda dishSizeId null', () => {
    const acai = newDishLine('k1', book.dishes.get(id('d03'))!);
    expect(acai.dishSizeId).toBeNull();
    expect(aLaCarteFormIssue({ ...emptyALaCarteForm(), tableLabel: '5', lines: [acai] }, book)).toBe('Açaí: Escolha o tamanho.');
    // Batata: só a P está à venda, então já vem escolhida.
    expect(newDishLine('k2', book.dishes.get(id('d02'))!).dishSizeId).toBe(id('d21'));
    expect(lineItem(book, newDishLine('k3', book.dishes.get(id('d01'))!))).toMatchObject({ itemKind: 'FIXED_DISH', dishSizeId: null, groupAddons: [] });
  });

  it('bebida: tamanho + acréscimo do sabor; com sabores, o sabor é obrigatório', () => {
    const coke: ALaCarteLine = { key: 'b', kind: 'BEVERAGE', beverageSizeId: id('f11'), beverageFlavorId: id('f13'), quantity: 2 };
    expect(lineCents(book, coke)).toBe(1300);
    expect(lineItem(book, coke)).toEqual({ itemKind: 'FIXED_BEVERAGE', beverageSizeId: id('f11'), beverageFlavorId: id('f13'), quantity: 2, notes: null });
    expect(aLaCarteFormIssue({ ...emptyALaCarteForm(), tabNumber: '47', lines: [{ ...coke, beverageFlavorId: null }] }, book)).toBe('Refrigerante Lata 350 ml: Escolha o sabor.');
  });

  it('combo 2× com bacon e Coca Zero = R$ 73,00 (ADR-0058 item 9); só as partes com escolha vão em parts', () => {
    const line = newComboLine('c', book.combos.get(id('c01'))!, book);
    const combo: ALaCarteLine = {
      ...line,
      quantity: 2,
      parts: line.parts.map((p) =>
        p.comboComponentId === id('c11') ? { ...p, notes: 'Sem cebola', addons: [{ addonOptionId: id('e11'), quantity: 1 }] } : p.comboComponentId === id('c13') ? { ...p, beverageFlavorId: id('f13') } : p,
      ),
    };
    expect(lineCents(book, combo)).toBe(7300);
    expect(lineItem(book, combo)).toEqual({
      itemKind: 'COMBO',
      comboId: id('c01'),
      quantity: 2,
      parts: [
        { comboComponentId: id('c11'), beverageFlavorId: null, notes: 'Sem cebola', groupAddons: [{ addonOptionId: id('e11'), quantity: 1 }] },
        { comboComponentId: id('c13'), beverageFlavorId: id('f13'), notes: null, groupAddons: null },
      ],
    });
    expect(aLaCarteFormIssue({ ...emptyALaCarteForm(), tableLabel: '5', lines: [line] }, book)).toBe('X-Burguer + batata + refri: Escolha o sabor de Refrigerante Lata 350 ml.');
  });

  it('comanda: A_LA_CARTE, DINE_IN, mesa e/ou comanda, sem pesagem; sem mesa nem comanda não vai', () => {
    const body = buildALaCarteBody({ ...emptyALaCarteForm(), tableLabel: ' 5 ', lines: [newDishLine('k', book.dishes.get(id('d01'))!)] }, book);
    expect(body).toMatchObject({ orderType: 'A_LA_CARTE', fulfillmentType: 'DINE_IN', tableLabel: '5', tabNumber: null, customerName: null, deliveryFee: null, weighings: [] });
    expect(aLaCarteFormIssue({ ...emptyALaCarteForm(), lines: [newDishLine('k', book.dishes.get(id('d01'))!)] }, book)).toBe('Informe a mesa ou a comanda.');
  });

  it('motivo do combo fora de venda vem dos componentes (pausa derivada)', () => {
    expect(comboUnavailableReason(book.combos.get(id('c02'))!)).toBe('Indisponível: Batata frita G pausado');
    expect(comboUnavailableReason({ ...book.combos.get(id('c01'))!, isPaused: true, isSellable: false } as Combo)).toBe('Pausado');
  });

  it('adicional de grupo depois: só em prato avulso e parte de prato, pago pela quantidade dos antepassados', () => {
    const targets = addonTargets(ORDER);
    expect(targets.map((t) => [t.item.id, t.multiplier])).toEqual([
      [id('641'), 1],
      [id('645'), 2],
      [id('647'), 2],
    ]);
    expect(addItemsBody({ lines: [], addons: { [id('645')]: [{ addonOptionId: id('e11'), quantity: 1 }] } }, book)).toEqual([
      { itemKind: 'GROUP_ADDON', parentOrderItemId: id('645'), addonOptionId: id('e11'), quantity: 1, notes: null },
    ]);
    expect(itemWithChildrenCents(ORDER.items[2]!)).toBe(7300);
  });
});

// ---------------------------------------------------------------- Abrir comanda

describe('abrir comanda (08 §8.9 orders/new/a-la-carte)', () => {
  it('monta prato com tamanho e adicional, bebida com sabor e combo com partes, e envia o corpo do contrato', async () => {
    writeHandler = (method, path) => (method === 'POST' && path === '/orders' ? json(201, ORDER, { ETag: '"1"' }) : undefined);
    await renderWith(<NewALaCarteScreen />);
    await screen.findByLabelText('Adicionar Açaí');
    await fireEvent.changeText(screen.getByLabelText('Mesa'), '5');

    // Prato → tamanho → adicionais → observação.
    await fireEvent.press(screen.getByLabelText('Adicionar Açaí'));
    expect(screen.getByText('Escolha o tamanho.')).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('500 ml · R$ 18,00'));
    await fireEvent.press(screen.getByLabelText('Aumentar Granola'));
    await fireEvent.changeText(screen.getByLabelText('Observação'), 'Pouco leite condensado');
    await fireEvent.press(screen.getByLabelText('Adicionar · R$ 20,00'));

    // Bebida → sabor (o pausado aparece desabilitado).
    await fireEvent.press(screen.getByLabelText('Adicionar Refrigerante Lata 350 ml'));
    expect(screen.getByLabelText('Guaraná · pausado')).toBeDisabled();
    await fireEvent.press(screen.getByLabelText('Coca-Cola'));
    await fireEvent.press(screen.getByLabelText('Adicionar · R$ 6,00'));

    // Combo → adicionais e observação no prato, sabor na bebida.
    await fireEvent.press(screen.getByLabelText('Adicionar X-Burguer + batata + refri'));
    expect(screen.getByText('Escolha o sabor de Refrigerante Lata 350 ml.')).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Aumentar Bacon'));
    expect(screen.queryByLabelText('Aumentar Cheddar')).toBeNull();
    await fireEvent.changeText(screen.getByLabelText('Observação · X-Burguer'), 'Sem cebola');
    await fireEvent.press(screen.getByLabelText('Coca-Cola Zero + R$ 0,50'));
    await fireEvent.press(screen.getByLabelText('Aumentar Quantidade de X-Burguer + batata + refri'));
    await fireEvent.press(screen.getByLabelText('Adicionar · R$ 73,00'));

    expect(screen.getByText('R$ 99,00')).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Abrir comanda'));
    expect(screen.getByText('Abrir comanda?')).toBeTruthy();
    await act(async () => {
      await fireEvent.press(screen.getByTestId('confirm-button'));
    });
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]!.headers['Idempotency-Key']).toMatch(/[0-9a-f-]{36}/);
    expect(writes[0]!.headers['If-Match']).toBeUndefined();
    expect(writes[0]).toMatchObject({ method: 'POST', path: '/orders' });
    expect(writes[0]!.body).toEqual({
      orderType: 'A_LA_CARTE',
      fulfillmentType: 'DINE_IN',
      customerId: null,
      customerName: null,
      customerPhone: null,
      saveCustomer: false,
      customerAddressId: null,
      deliveryAddress: null,
      deliveryFee: null,
      tableLabel: '5',
      tabNumber: null,
      notes: null,
      items: [
        { itemKind: 'FIXED_DISH', dishId: id('d03'), dishSizeId: id('d32'), quantity: 1, notes: 'Pouco leite condensado', groupAddons: [{ addonOptionId: id('e21'), quantity: 1 }] },
        { itemKind: 'FIXED_BEVERAGE', beverageSizeId: id('f11'), beverageFlavorId: id('f12'), quantity: 1, notes: null },
        {
          itemKind: 'COMBO',
          comboId: id('c01'),
          quantity: 2,
          parts: [
            { comboComponentId: id('c11'), beverageFlavorId: null, notes: 'Sem cebola', groupAddons: [{ addonOptionId: id('e11'), quantity: 1 }] },
            { comboComponentId: id('c13'), beverageFlavorId: id('f13'), notes: null, groupAddons: null },
          ],
        },
      ],
      weighings: [],
    });
    await waitFor(() => expect(router.replace).toHaveBeenCalledWith(`/orders/${ORDER_ID}`));
  });

  it('indisponíveis aparecem desabilitados com o motivo; tamanho pausado não se escolhe', async () => {
    await renderWith(<NewALaCarteScreen />);
    expect(await screen.findByLabelText('Combo família, Indisponível: Batata frita G pausado')).toBeTruthy();
    expect(screen.getByText('Indisponível: Batata frita G pausado')).toBeTruthy();
    expect(screen.getByLabelText('X-Salada, Pausado')).toBeTruthy();
    expect(screen.queryByLabelText('Adicionar X-Salada')).toBeNull();
    expect(screen.queryByLabelText('Adicionar Combo família')).toBeNull();
    await fireEvent.press(screen.getByLabelText('Adicionar Batata frita'));
    expect(screen.getByLabelText('G · R$ 18,00 · pausado')).toBeDisabled();
  });

  it('422 ProductUnavailable: mostra o detalhe do servidor e recarrega o cardápio; nada fica pendente', async () => {
    writeHandler = () => problem(422, 'ProductUnavailable', { detail: 'X-Burguer + batata + refri está indisponível. Pausado: Batata frita P.' });
    await renderWith(<NewALaCarteScreen />);
    await screen.findByLabelText('Adicionar X-Burguer');
    await fireEvent.changeText(screen.getByLabelText('Comanda'), '47');
    await fireEvent.press(screen.getByLabelText('Adicionar X-Burguer'));
    await fireEvent.press(screen.getByLabelText('Adicionar · R$ 22,00'));
    await fireEvent.press(screen.getByLabelText('Abrir comanda'));
    const before = reads.filter((r) => r.startsWith('/fixed-menu')).length;
    await act(async () => {
      await fireEvent.press(screen.getByTestId('confirm-button'));
    });
    expect((await screen.findAllByText('X-Burguer + batata + refri está indisponível. Pausado: Batata frita P.')).length).toBeGreaterThan(0);
    await waitFor(() => expect(reads.filter((r) => r.startsWith('/fixed-menu')).length).toBeGreaterThan(before));
    expect(usePendingOps.getState().ops).toHaveLength(0);
  });

  it('403 ModuleNotEnabled: a mensagem do módulo', async () => {
    writeHandler = () => problem(403, 'ModuleNotEnabled', { module: 'A_LA_CARTE' });
    await renderWith(<NewALaCarteScreen />);
    await screen.findByLabelText('Adicionar X-Burguer');
    await fireEvent.changeText(screen.getByLabelText('Mesa'), '3');
    await fireEvent.press(screen.getByLabelText('Adicionar X-Burguer'));
    await fireEvent.press(screen.getByLabelText('Adicionar · R$ 22,00'));
    await fireEvent.press(screen.getByLabelText('Abrir comanda'));
    await act(async () => {
      await fireEvent.press(screen.getByTestId('confirm-button'));
    });
    expect((await screen.findAllByText('Comandas à la carte não está habilitado neste estabelecimento.')).length).toBeGreaterThan(0);
  });
});

// ---------------------------------------------------------------- Detalhe e inclusão

describe('detalhe da comanda: a árvore (06 §6.9)', () => {
  it('combo com as partes e os adicionais; o valor da linha é a árvore inteira', async () => {
    await renderWith(<OrderDetailScreen />);
    expect(await screen.findByText('2× X-Burguer + batata + refri')).toBeTruthy();
    expect(screen.getByText('R$ 73,00')).toBeTruthy();
    expect(screen.getByText('1× Açaí 500 ml')).toBeTruthy();
    expect(screen.getByText('+ 1× Granola (R$ 2,00)')).toBeTruthy();
    expect(screen.getByText('1× Batata frita P')).toBeTruthy();
    expect(screen.getByText('+ 1× Bacon (R$ 8,00)')).toBeTruthy();
    expect(screen.getByText('Obs.: Sem cebola')).toBeTruthy();
    expect(screen.getByText('1× Refrigerante Lata 350 ml · Coca-Cola Zero')).toBeTruthy();
    expect(screen.getByText('+ R$ 1,00')).toBeTruthy();
    // Parte de prato: só a observação; parte de bebida: nada (trocar o sabor = remover e incluir, Q-322).
    expect(screen.getByLabelText('Observação de X-Burguer')).toBeTruthy();
    expect(screen.queryByLabelText('Observação de Refrigerante Lata 350 ml · Coca-Cola Zero')).toBeNull();
  });

  it('remover o combo leva a árvore: DELETE do COMBO com If-Match', async () => {
    writeHandler = (method, path) => (method === 'DELETE' ? json(200, { ...ORDER, version: 5, items: ORDER.items.slice(0, 2) }) : undefined);
    await renderWith(<OrderDetailScreen />);
    await fireEvent.press(await screen.findByLabelText('Alterar X-Burguer + batata + refri'));
    expect(screen.queryByLabelText('Observação')).toBeNull();
    expect(screen.getByText(/Para trocar o sabor de uma bebida, remova o combo e inclua de novo/)).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Remover combo'));
    expect(screen.getByText('Remover combo?')).toBeTruthy();
    expect(screen.getByText('O combo sai inteiro: as partes e os adicionais delas também.')).toBeTruthy();
    await act(async () => {
      await fireEvent.press(screen.getByTestId('confirm-button'));
    });
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]).toMatchObject({ method: 'DELETE', path: `/orders/${ORDER_ID}/items/${id('644')}` });
    expect(writes[0]!.headers['If-Match']).toBe('"4"');
    expect(writes[0]!.headers['Idempotency-Key']).toBeUndefined();
  });

  it('parte de prato: altera só a observação (sem quantidade nem remover)', async () => {
    await renderWith(<OrderDetailScreen />);
    await fireEvent.press(await screen.findByLabelText('Observação de X-Burguer'));
    expect(screen.queryByLabelText('Aumentar Quantidade')).toBeNull();
    expect(screen.queryByLabelText('Remover item')).toBeNull();
    expect(screen.getByLabelText('Observação')).toBeTruthy();
  });
});

describe('adicionar itens à comanda (POST /orders/{id}/items [I][M])', () => {
  it('adicional de grupo numa parte de prato do combo, com If-Match e Idempotency-Key', async () => {
    writeHandler = (method) => (method === 'POST' ? json(200, { ...ORDER, version: 5 }) : undefined);
    await renderWith(<AddItemsScreen />);
    await fireEvent.press(await screen.findByLabelText('2× X-Burguer (X-Burguer + batata + refri)'));
    // Batata não tem grupo de adicionais: não aparece como alvo.
    expect(screen.queryByLabelText('2× Batata frita P (X-Burguer + batata + refri)')).toBeNull();
    await fireEvent.press(screen.getByLabelText('Aumentar Bacon'));
    await fireEvent.press(screen.getByLabelText('Incluir 1 item · R$ 8,00'));
    expect(screen.getByText('+1× Bacon em X-Burguer')).toBeTruthy();
    await act(async () => {
      await fireEvent.press(screen.getByTestId('confirm-button'));
    });
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]).toMatchObject({
      method: 'POST',
      path: `/orders/${ORDER_ID}/items`,
      body: { items: [{ itemKind: 'GROUP_ADDON', parentOrderItemId: id('645'), addonOptionId: id('e11'), quantity: 1, notes: null }] },
    });
    expect(writes[0]!.headers['If-Match']).toBe('"4"');
    expect(writes[0]!.headers['Idempotency-Key']).toMatch(/[0-9a-f-]{36}/);
    await waitFor(() => expect(router.back).toHaveBeenCalled());
  });

  it('409 VersionConflict: mostra o que mudou e não reenvia', async () => {
    writeHandler = () => problem(409, 'VersionConflict', { current: { ...ORDER, version: 6, isPaid: true } });
    await renderWith(<AddItemsScreen />);
    await fireEvent.press(await screen.findByLabelText('Adicionar Água 500 ml'));
    await fireEvent.press(screen.getByLabelText('Incluir 1 item · R$ 4,00'));
    await act(async () => {
      await fireEvent.press(screen.getByTestId('confirm-button'));
    });
    expect(await screen.findByText(/Pedido quitado/)).toBeTruthy();
    expect(writes).toHaveLength(1);
    expect(writes[0]!.body).toEqual({ items: [{ itemKind: 'FIXED_BEVERAGE', beverageSizeId: id('f21'), beverageFlavorId: null, quantity: 1, notes: null }] });
  });
});

// ---------------------------------------------------------------- Cozinha e fechamento

describe('cozinha e fechamento', () => {
  it('cozinha: os pratos e as partes como vêm do servidor, com tamanho, adicionais e observação, sem combo nem bebida', async () => {
    // Exemplo literal do 06 §6.9 "Produção", à la carte.
    const board: ProductionOrder = {
      id: ORDER_ID,
      version: 4,
      orderNumber: 12,
      orderType: 'A_LA_CARTE',
      origin: 'APP',
      fulfillmentType: 'DINE_IN',
      fulfillmentStatus: null,
      label: 'Mesa 5',
      notes: null,
      isPaid: false,
      createdAt: '2026-11-22T19:10:00Z',
      acceptedAt: '2026-11-22T19:10:00Z',
      pendingAlertCount: 0,
      items: [
        { id: id('641'), name: 'Açaí 500 ml', composition: null, quantity: 1, notes: 'Pouco leite condensado', requiresPreparation: true, addons: [{ id: id('642'), name: 'Granola', quantity: 1, notes: null }] },
        { id: id('645'), name: 'X-Burguer', composition: null, quantity: 2, notes: 'Sem cebola', requiresPreparation: true, addons: [{ id: id('646'), name: 'Bacon', quantity: 1, notes: null }] },
        { id: id('647'), name: 'Batata frita P', composition: null, quantity: 2, notes: null, requiresPreparation: true, addons: [] },
      ],
    };
    await renderWith(<ProductionCard order={board} status="OPEN" flash={false} onFlashed={() => {}} />);
    expect(screen.getByText('Mesa 5')).toBeTruthy();
    expect(screen.getByText('1× Açaí 500 ml')).toBeTruthy();
    expect(screen.getByText('+ 1× Granola')).toBeTruthy();
    expect(screen.getByText('⚠ Pouco leite condensado')).toBeTruthy();
    expect(screen.getByText('2× X-Burguer')).toBeTruthy();
    expect(screen.getByText('2× Batata frita P')).toBeTruthy();
    expect(screen.queryByText(/combo|Refrigerante|R\$/i)).toBeNull();
  });

  const summary: ClosingSummary = {
    businessDay: { id: id('401'), version: 1, businessDate: '2026-11-22', status: 'OPEN' },
    isFinal: false,
    dailyClosingId: null,
    computedAt: '2026-11-22T21:55:00Z',
    orderCount: 3,
    totalSold: '230.00',
    deliveryFeesTotal: '0.00',
    siteSoldTotal: '47.80',
    totalReceived: '130.00',
    receivedFromPreviousDays: '0.00',
    totalPending: '100.00',
    canceled: { count: 0, total: '0.00' },
    reversed: { count: 0, total: '0.00' },
    webOrders: { awaitingAcceptanceCount: 0, autoRejectedCount: 0, notAcceptedCount: 0 },
    unmarkedDeliveryCount: 0,
    byOrderType: [
      { orderType: 'MARMITA', count: 0, sold: '0.00', received: '0.00', pending: '0.00', reversed: '0.00' },
      { orderType: 'PF', count: 0, sold: '0.00', received: '0.00', pending: '0.00', reversed: '0.00' },
      { orderType: 'KILO', count: 0, sold: '0.00', received: '0.00', pending: '0.00', reversed: '0.00' },
      { orderType: 'A_LA_CARTE', count: 3, sold: '230.00', received: '130.00', pending: '100.00', reversed: '0.00' },
    ],
    byPaymentMethod: [
      { paymentMethod: 'CASH', count: 1, received: '30.00', reversed: '0.00' },
      { paymentMethod: 'PIX', count: 2, received: '100.00', reversed: '0.00' },
      { paymentMethod: 'CARD', count: 0, received: '0.00', reversed: '0.00' },
    ],
    cash: { openingBalance: '100.00', cashReceived: '30.00', cashReversed: '0.00', supplies: '0.00', withdrawals: '0.00', expectedCash: '130.00', countedCash: null, cashDifference: null, justification: null },
    adjustments: [],
    closedAt: null,
    closedBy: null,
  };

  it('fechamento da lanchonete: linha "À la carte" e "Vendido pelo site", sem as modalidades do restaurante', async () => {
    await renderWith(<ClosingSummaryView summary={summary} />);
    expect(screen.getByText('À la carte (3)')).toBeTruthy();
    expect(screen.queryByText(/^Marmita \(/)).toBeNull();
    expect(screen.queryByText(/^PF \(/)).toBeNull();
    expect(screen.getByText('Vendido pelo site (já no total)')).toBeTruthy();
    expect(screen.getByText('R$ 47,80')).toBeTruthy();
  });

  it('restaurante: a linha À la carte zerada não aparece', async () => {
    signIn(['MARMITA', 'PF', 'KILO', 'DAILY_MENU']);
    const zero = { ...summary, byOrderType: summary.byOrderType.map((r) => (r.orderType === 'A_LA_CARTE' ? { ...r, count: 0, sold: '0.00', received: '0.00', pending: '0.00' } : r)) };
    await renderWith(<ClosingSummaryView summary={zero} />);
    expect(screen.getByText('Marmita (0)')).toBeTruthy();
    expect(screen.queryByText(/À la carte/)).toBeNull();
  });
});
