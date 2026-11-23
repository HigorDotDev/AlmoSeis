/**
 * EP-003 fatia 4 (ADR-0059, 06 §6.8 `POST /customers/lookup` e §6.9 `POST /orders`): delivery por telefone da lanchonete e
 * marmita por telefone. Telefone preenche o nome (sem sobrescrever o que foi digitado); "usar o endereço do último
 * pedido"; taxa digitada na criação; forma prevista e "troco para" (>= total); o corpo do contrato; aba Delivery com
 * os filtros (`fulfillmentType` repetível, `fulfillmentStatus`) e "Novos do site"; marmita na entrega exige telefone
 * e forma prevista; detalhe com o troco que o motoboy leva; taxa depois só com `orders.delivery-fee.edit`; pagar com
 * a forma prevista pré-selecionada, sem pagar sozinho.
 *
 * Ids e valores do exemplo "À la carte, entrega por telefone" do 06 §6.9: 1 combo (32,00) + açaí 500 ml com granola
 * (20,00) + taxa digitada 5,00 = 57,00, dinheiro com troco para 100,00 (o motoboy leva 43,00).
 */
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import { router } from 'expo-router';
import type { ReactElement } from 'react';
import { Text } from 'react-native';

import { configureApiClient, qs } from '@/api/client';
import type { Customer, FixedMenu, Me, Order, OrderSummary, Product } from '@/api/types';
import { useCustomerByPhone } from '@/features/customers/hooks';
import DeliveryTab, { deliveryFilters } from '@/features/delivery/screens/DeliveryTab';
import { fixedMenuBook, newComboLine, newDishLine } from '@/features/orders/aLaCarte';
import { buildMarmitaBody, emptyMarmitaForm } from '@/features/orders/compose';
import {
  buildDeliveryBody,
  changeForIssue,
  changeToCarryText,
  deliveryFormIssues,
  emptyDeliveryForm,
  expectedPaymentIssue,
  fieldMessage,
  firstDeliveryIssue,
  previewDeliveryCents,
} from '@/features/orders/delivery';
import { marmitaFormSchema } from '@/features/orders/schemas';
import NewDeliveryScreen from '@/features/orders/screens/NewDeliveryScreen';
import NewMarmitaScreen from '@/features/orders/screens/NewMarmitaScreen';
import OrderDetailScreen from '@/features/orders/screens/OrderDetailScreen';
import { EditOrderScreen } from '@/features/orders/screens/OrderSubScreens';
import PayScreen from '@/features/payments/screens/PayScreen';
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

const id = (suffix: string) => `0192d6a0-0000-7000-8000-000000000${suffix}`;
const T = '2026-11-22T10:00:00Z';
const ORDER_ID = id('501');
const PHONE = '+5534988887777';
const G_ACAI = id('e02');

const sizeBase = { sellsOnSite: false, sitePrice: null, isActive: true };
const dishBase = { version: 1, description: null, imageUrl: null, requiresPreparation: true, sellsOnSite: false, sitePrice: null, isActive: true, createdAt: T, updatedAt: T };

/** Recorte do `GET /fixed-menu` do 06 §6.5 com os ids do exemplo do 06 §6.9. */
const MENU: FixedMenu = {
  categories: [
    { id: id('a01'), version: 1, name: 'Lanches', sortOrder: 1, isActive: true, createdAt: T, updatedAt: T },
    { id: id('a02'), version: 1, name: 'Porções', sortOrder: 2, isActive: true, createdAt: T, updatedAt: T },
  ],
  dishes: [
    { ...dishBase, id: id('d01'), dishCategoryId: id('a01'), name: 'X-Burguer', hasSizes: false, price: '22.00', sortOrder: 1, isPaused: false, isSellable: true, sizes: [], addonGroupIds: [] },
    {
      ...dishBase,
      id: id('d03'),
      dishCategoryId: id('a02'),
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
      sellsOnSite: false,
      sitePrice: null,
      sortOrder: 1,
      isActive: true,
      isPaused: false,
      isSellable: true,
      components: [
        { id: id('c11'), kind: 'DISH', dishId: id('d01'), dishSizeId: null, beverageSizeId: null, name: 'X-Burguer', quantity: 1, sortOrder: 0, isSellable: true },
        { id: id('c13'), kind: 'BEVERAGE', dishId: null, dishSizeId: null, beverageSizeId: id('f11'), name: 'Refrigerante Lata 350 ml', quantity: 1, sortOrder: 1, isSellable: true },
      ],
      createdAt: T,
      updatedAt: T,
    },
  ],
  addonGroups: [
    {
      id: G_ACAI,
      version: 1,
      name: 'Complementos de açaí',
      sortOrder: 1,
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

const CARLA: Customer = {
  id: id('802'),
  version: 1,
  name: 'Carla Souza',
  phoneNumber: PHONE,
  notes: null,
  addresses: [],
  pendingAmount: '0.00',
  pendingOrderCount: 0,
  anonymizedAt: null,
  createdAt: '2026-11-22T19:10:00Z',
};
const LAST_ADDRESS = { street: 'Rua das Flores', number: '123', complement: null, neighborhood: 'Centro', reference: 'Portão verde' };

const SETTINGS = { version: 1, kiloPricePerKg: null, defaultDeliveryFee: '4.00', enabledPaymentMethods: ['CASH', 'PIX', 'CARD'], sharedDeviceIdleSeconds: 300 };

const bruno = { id: id('302'), displayName: 'Bruno' };

/** `201` do exemplo do 06 §6.9 (entrega por telefone). */
const ORDER: Order = {
  id: ORDER_ID,
  version: 1,
  orderNumber: 13,
  businessDayId: id('401'),
  businessDate: '2026-11-22',
  orderType: 'A_LA_CARTE',
  origin: 'APP',
  priceList: 'COUNTER',
  fulfillmentType: 'DELIVERY',
  customer: { id: CARLA.id, name: 'Carla Souza', phoneNumber: PHONE },
  deliveryAddress: LAST_ADDRESS,
  tableLabel: null,
  tabNumber: null,
  notes: null,
  expectedPaymentMethod: 'CASH',
  changeFor: '100.00',
  riskFlag: null,
  deliveryFee: '5.00',
  deliveryFeeSource: 'MANUAL',
  deliveryDistanceKm: null,
  itemsTotal: '52.00',
  weighingsTotal: '0.00',
  totalAmount: '57.00',
  paidAmount: '0.00',
  balance: '57.00',
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
  fulfillmentStatus: 'PENDING',
  fulfillmentStatusChangedAt: '2026-11-22T19:10:00Z',
  outForDeliveryAt: null,
  deliveredAt: null,
  pickedUpAt: null,
  isReadyForPickup: false,
  items: [],
  weighings: [],
  payments: [],
  pendingAlertCount: 0,
  createdAt: '2026-11-22T19:10:00Z',
  createdBy: bruno,
  updatedAt: '2026-11-22T19:10:00Z',
};

const summary = (over: Partial<OrderSummary>): OrderSummary => ({
  id: ORDER_ID,
  version: 1,
  orderNumber: 13,
  businessDate: '2026-11-22',
  orderType: 'A_LA_CARTE',
  origin: 'APP',
  fulfillmentType: 'DELIVERY',
  customerName: 'Carla Souza',
  tableLabel: null,
  tabNumber: null,
  totalAmount: '57.00',
  paidAmount: '0.00',
  balance: '57.00',
  isPaid: false,
  productionStatus: 'OPEN',
  cancellationStatus: 'ACTIVE',
  acceptanceStatus: 'ACCEPTED',
  acceptanceDeadline: null,
  fulfillmentStatus: 'PENDING',
  isReadyForPickup: false,
  expectedPaymentMethod: 'CASH',
  changeFor: '100.00',
  itemCount: 2,
  pendingAlertCount: 0,
  createdAt: '2026-11-22T19:10:00Z',
  ...over,
});

const COCA: Product = {
  id: 'coca',
  version: 1,
  kind: 'BEVERAGE',
  name: 'Coca-Cola 350ml',
  description: null,
  category: null,
  defaultPrice: '6.00',
  requiresPreparation: false,
  imageUrl: null,
  isActive: true,
  isAvailable: true,
  isVisibleOnPage: true,
  sitePrice: null,
  createdAt: T,
  updatedAt: T,
};

// ---------------------------------------------------------------- Infra (fetch simulado)

const json = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } });
const problem = (status: number, errorCode: string, ext: Record<string, unknown> = {}) =>
  new Response(JSON.stringify({ status, errorCode, title: errorCode, ...ext }), { status, headers: { 'Content-Type': 'application/problem+json' } });

type Handler = (method: string, path: string) => Response | undefined;
let writeHandler: Handler;
let orderResponse: Order;
let listItems: OrderSummary[];
let awaitingItems: OrderSummary[];
const writes: { method: string; path: string; headers: Record<string, string>; body: unknown }[] = [];
const reads: string[] = [];
const lookupHeaders: Record<string, string>[] = [];

function signIn(extra: string[] = []) {
  useSession.setState({
    status: 'signedIn',
    readOnly: false,
    me: {
      tenant: { id: 't1', name: 'Lanches do Beto', timeZone: 'America/Sao_Paulo', modules: ['A_LA_CARTE', 'DELIVERY', 'FIXED_MENU', 'MARMITA'] },
      employee: { id: 'e1', displayName: 'Ana', roles: ['ATTENDANT'], hasPin: false },
      permissions: ['orders.view', 'orders.create', 'orders.edit', 'orders.accept', 'orders.fulfillment.update', 'payments.register', 'production.view', ...extra],
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
  lookupHeaders.length = 0;
  writeHandler = () => undefined;
  orderResponse = ORDER;
  listItems = [];
  awaitingItems = [];
  (router.replace as jest.Mock).mockClear();
  (router.push as jest.Mock).mockClear();
  signIn();
  configureApiClient({ getAccessToken: () => 'tok', refresh: async () => true, onSessionExpired: () => {}, onUpgradeRequired: () => {}, onSubscriptionInactive: () => {} });
  global.fetch = jest.fn(async (url: string, init: RequestInit = {}) => {
    const path = url.replace(/^https?:\/\/[^/]+(\/api\/v1)?/, '');
    const method = init.method ?? 'GET';
    // `POST /customers/lookup` é leitura (telefone no corpo, sem `Idempotency-Key` nem `If-Match`): conta como leitura.
    if (method === 'POST' && path === '/customers/lookup') {
      const body = JSON.parse(init.body as string) as { phoneNumber: string };
      const headers = init.headers as Record<string, string>;
      reads.push(`lookup:${body.phoneNumber}`);
      lookupHeaders.push(headers);
      return body.phoneNumber === PHONE ? json(200, { customer: CARLA, lastDeliveryAddress: LAST_ADDRESS }) : json(200, { customer: null, lastDeliveryAddress: null });
    }
    if (method !== 'GET') {
      writes.push({ method, path, headers: init.headers as Record<string, string>, body: init.body ? JSON.parse(init.body as string) : undefined });
      return writeHandler(method, path) ?? json(500, { errorCode: 'Unexpected' });
    }
    reads.push(path);
    if (path.startsWith('/fixed-menu')) return json(200, MENU);
    if (path === '/tenant/settings') return json(200, SETTINGS);
    if (path.startsWith('/business-days/current')) return json(200, { id: id('401'), version: 1, businessDate: '2026-11-22', status: 'OPEN' });
    if (path.startsWith('/products')) return json(200, { items: [COCA] });
    if (path.startsWith('/marmita-sizes')) return json(200, { items: [] });
    if (path.startsWith('/orders?')) {
      return path.includes('acceptanceStatus=AWAITING_ACCEPTANCE') ? json(200, { items: awaitingItems, nextCursor: null }) : json(200, { items: listItems, nextCursor: null });
    }
    if (path === `/orders/${ORDER_ID}`) return json(200, orderResponse, { ETag: `"${orderResponse.version}"` });
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

async function confirm() {
  await act(async () => {
    await fireEvent.press(screen.getByTestId('confirm-button'));
  });
}

// ---------------------------------------------------------------- Regras puras

describe('forma prevista e troco (06 §6.9, Q-316)', () => {
  it('"troco para" de pelo menos o total; igual vale; só com dinheiro', () => {
    expect(changeForIssue({ expectedPaymentMethod: 'CASH', changeFor: '50.00' }, 5700)).toBe('O troco precisa ser para pelo menos R$ 57,00 (o total).');
    expect(changeForIssue({ expectedPaymentMethod: 'CASH', changeFor: '57.00' }, 5700)).toBeNull();
    expect(changeForIssue({ expectedPaymentMethod: 'CASH', changeFor: null }, 5700)).toBeNull();
    expect(changeForIssue({ expectedPaymentMethod: 'PIX', changeFor: '10.00' }, 5700)).toBeNull();
  });

  it('obrigatória na entrega, opcional na retirada, e dentro das habilitadas', () => {
    expect(expectedPaymentIssue('DELIVERY', { expectedPaymentMethod: null, changeFor: null })).toBe('Escolha a forma de pagamento prevista da entrega.');
    expect(expectedPaymentIssue('PICKUP', { expectedPaymentMethod: null, changeFor: null })).toBeNull();
    expect(expectedPaymentIssue('DELIVERY', { expectedPaymentMethod: 'CARD', changeFor: null }, ['CASH', 'PIX'])).toBe('Cartão está desabilitado. Escolha outra forma.');
  });

  it('o motoboy leva changeFor − balance; nada com o pedido quitado', () => {
    expect(changeToCarryText({ expectedPaymentMethod: 'CASH', changeFor: '100.00', balance: '57.00', isPaid: false })).toBe('Levar troco para R$ 100,00 → troco R$ 43,00');
    expect(changeToCarryText({ expectedPaymentMethod: 'CASH', changeFor: '100.00', balance: '0.00', isPaid: true })).toBeNull();
    expect(changeToCarryText({ expectedPaymentMethod: 'PIX', changeFor: null, balance: '57.00', isPaid: false })).toBeNull();
  });

  it('erro do servidor por campo, sem caixa e com filhos', () => {
    expect(fieldMessage({ 'deliveryAddress.street': ['Informe a rua.'] }, 'deliveryAddress')).toBe('Informe a rua.');
    expect(fieldMessage({ ChangeFor: ['Baixo.'] }, 'changeFor')).toBe('Baixo.');
    expect(fieldMessage({}, 'changeFor')).toBeNull();
  });
});

describe('corpo do delivery por telefone (06 §6.9 "À la carte, entrega por telefone")', () => {
  const combo = () => {
    const l = newComboLine('c', book.combos.get(id('c01'))!, book);
    return { ...l, parts: l.parts.map((p) => (p.comboComponentId === id('c13') ? { ...p, beverageFlavorId: id('f12') } : p)) };
  };
  const acai = () => ({ ...newDishLine('a', book.dishes.get(id('d03'))!), dishSizeId: id('d32'), addons: [{ addonOptionId: id('e21'), quantity: 1 }] });
  const form = () => ({
    ...emptyDeliveryForm(),
    phone: '(34) 98888-7777',
    name: ' Carla Souza ',
    address: { ...LAST_ADDRESS },
    deliveryFee: '5.00',
    expectedPaymentMethod: 'CASH' as const,
    changeFor: '100.00',
    lines: [combo(), acai()],
  });

  it('57,00 com a taxa digitada; sem customerId nem saveCustomer; endereço só no pedido', () => {
    const f = form();
    expect(previewDeliveryCents(f, book, '4.00')).toBe(5700);
    expect(buildDeliveryBody(f, book)).toEqual({
      orderType: 'A_LA_CARTE',
      fulfillmentType: 'DELIVERY',
      customerId: null,
      customerName: 'Carla Souza',
      customerPhone: PHONE,
      saveCustomer: false,
      customerAddressId: null,
      deliveryAddress: LAST_ADDRESS,
      deliveryFee: '5.00',
      tableLabel: null,
      tabNumber: null,
      notes: null,
      expectedPaymentMethod: 'CASH',
      changeFor: '100.00',
      items: [
        { itemKind: 'COMBO', comboId: id('c01'), quantity: 1, parts: [{ comboComponentId: id('c13'), beverageFlavorId: id('f12'), notes: null, groupAddons: null }] },
        { itemKind: 'FIXED_DISH', dishId: id('d03'), dishSizeId: id('d32'), quantity: 1, notes: null, groupAddons: [{ addonOptionId: id('e21'), quantity: 1 }] },
      ],
      weighings: [],
    });
  });

  it('retirada: sem endereço nem taxa; forma opcional; troco só com dinheiro', () => {
    const body = buildDeliveryBody({ ...form(), fulfillmentType: 'PICKUP', expectedPaymentMethod: 'PIX' }, book);
    expect(body).toMatchObject({ fulfillmentType: 'PICKUP', deliveryAddress: null, deliveryFee: null, expectedPaymentMethod: 'PIX', changeFor: null });
    expect(firstDeliveryIssue(deliveryFormIssues({ ...form(), fulfillmentType: 'PICKUP', expectedPaymentMethod: null, changeFor: null }, book, 5200))).toBeNull();
  });

  it('nome e telefone obrigatórios; entrega exige endereço e forma; troco abaixo do total recusado', () => {
    const issues = deliveryFormIssues({ ...emptyDeliveryForm(), changeFor: '10.00', expectedPaymentMethod: null }, book, 5700);
    expect(issues).toEqual({
      phone: 'Informe o telefone do cliente.',
      name: 'Informe o nome do cliente.',
      street: 'Informe o endereço de entrega.',
      expectedPaymentMethod: 'Escolha a forma de pagamento prevista da entrega.',
      lines: 'Inclua pelo menos um item do cardápio.',
    });
    expect(deliveryFormIssues({ ...form(), phone: '9999' }, book, 5700).phone).toBe('Telefone com DDD.');
    expect(deliveryFormIssues({ ...form(), changeFor: '50.00' }, book, 5700).changeFor).toBe('O troco precisa ser para pelo menos R$ 57,00 (o total).');
  });

  it('filtros da aba Delivery: fulfillmentType repetível e fulfillmentStatus', () => {
    expect(qs(deliveryFilters('ALL', 'ALL'))).toBe('?orderType=A_LA_CARTE&fulfillmentType=DELIVERY&fulfillmentType=PICKUP');
    expect(qs(deliveryFilters('DELIVERY', 'OUT_FOR_DELIVERY'))).toBe('?orderType=A_LA_CARTE&fulfillmentType=DELIVERY&fulfillmentStatus=OUT_FOR_DELIVERY');
  });
});

describe('marmita por telefone (Q-312)', () => {
  const base = { ...emptyMarmitaForm(), customer: { id: null, name: 'João Silva', phone: '', saveCustomer: false }, beverages: { coca: 1 } };

  it('entrega exige telefone e forma prevista; retirada (balcão) não', () => {
    const delivery = marmitaFormSchema.safeParse({ ...base, fulfillmentType: 'DELIVERY', address: { ...LAST_ADDRESS } });
    expect(delivery.success).toBe(false);
    expect(delivery.error?.issues.map((i) => i.message)).toEqual(['Informe o telefone da entrega.', 'Escolha a forma de pagamento prevista da entrega.']);
    expect(marmitaFormSchema.safeParse({ ...base, fulfillmentType: 'PICKUP' }).success).toBe(true);
  });

  it('o corpo leva a forma prevista e o troco; troco sem dinheiro não vai', () => {
    expect(buildMarmitaBody({ ...base, expectedPaymentMethod: 'CASH', changeFor: '50.00' }, null)).toMatchObject({ expectedPaymentMethod: 'CASH', changeFor: '50.00' });
    expect(buildMarmitaBody({ ...base, expectedPaymentMethod: 'PIX', changeFor: '50.00' }, null)).toMatchObject({ expectedPaymentMethod: 'PIX', changeFor: null });
  });
});

// ---------------------------------------------------------------- Pedido por telefone (tela)

describe('pedido por telefone (08 §8.9 orders/new/delivery)', () => {
  it('telefone preenche o nome, usa o último endereço, valida o troco e envia o corpo do contrato', async () => {
    writeHandler = (method, path) => (method === 'POST' && path === '/orders' ? json(201, ORDER, { ETag: '"1"' }) : undefined);
    await renderWith(<NewDeliveryScreen />);
    await screen.findByLabelText('Adicionar Açaí');

    await fireEvent.changeText(screen.getByLabelText('Telefone'), '34988887777');
    await waitFor(() => expect(screen.getByLabelText('Nome').props.value).toBe('Carla Souza'));
    expect(reads).toContain('lookup:+5534988887777');
    expect(reads.some((r) => r.includes('5534988887777') && !r.startsWith('lookup:'))).toBe(false);
    expect(lookupHeaders[0]!['Idempotency-Key']).toBeUndefined();
    expect(lookupHeaders[0]!['If-Match']).toBeUndefined();
    expect(screen.getByTestId('phone-customer')).toBeTruthy();

    await fireEvent.press(screen.getByLabelText('Usar endereço do último pedido'));
    expect(screen.getByLabelText('Rua').props.value).toBe('Rua das Flores');
    expect(screen.getByLabelText('Referência').props.value).toBe('Portão verde');

    // Taxa: começa com a padrão (R$ 4,00) e quem cria pode digitar (Q-315).
    expect(screen.getByLabelText('Taxa de entrega').props.value).toBe('R$ 4,00');
    await fireEvent.changeText(screen.getByLabelText('Taxa de entrega'), '500');

    await fireEvent.press(screen.getByLabelText('Adicionar Açaí'));
    await fireEvent.press(screen.getByLabelText('500 ml · R$ 18,00'));
    await fireEvent.press(screen.getByLabelText('Aumentar Granola'));
    await fireEvent.press(screen.getByLabelText('Adicionar · R$ 20,00'));
    await fireEvent.press(screen.getByLabelText('Adicionar X-Burguer + batata + refri'));
    await fireEvent.press(screen.getByLabelText('Coca-Cola'));
    await fireEvent.press(screen.getByLabelText('Adicionar · R$ 32,00'));

    await fireEvent.press(screen.getByLabelText('Dinheiro'));
    await fireEvent.changeText(screen.getByLabelText('Troco para (opcional)'), '5000');
    await fireEvent.press(screen.getByLabelText('Registrar R$ 57,00'));
    expect((await screen.findAllByText('O troco precisa ser para pelo menos R$ 57,00 (o total).')).length).toBeGreaterThan(0);
    expect(screen.queryByText('Registrar pedido por telefone?')).toBeNull();
    expect(writes).toHaveLength(0);

    await fireEvent.changeText(screen.getByLabelText('Troco para (opcional)'), '10000');
    expect(screen.getByTestId('change-preview')).toHaveTextContent('Levar troco: R$ 43,00');
    expect(screen.queryByText('O troco precisa ser para pelo menos R$ 57,00 (o total).')).toBeNull();
    await fireEvent.press(screen.getByLabelText('Registrar R$ 57,00'));
    expect(screen.getByText('Registrar pedido por telefone?')).toBeTruthy();
    expect(screen.getByText('Levar troco para R$ 100,00 → troco R$ 43,00')).toBeTruthy();
    await confirm();

    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]).toMatchObject({ method: 'POST', path: '/orders' });
    expect(writes[0]!.headers['Idempotency-Key']).toMatch(/[0-9a-f-]{36}/);
    expect(writes[0]!.body).toEqual({
      orderType: 'A_LA_CARTE',
      fulfillmentType: 'DELIVERY',
      customerId: null,
      customerName: 'Carla Souza',
      customerPhone: PHONE,
      saveCustomer: false,
      customerAddressId: null,
      deliveryAddress: LAST_ADDRESS,
      deliveryFee: '5.00',
      tableLabel: null,
      tabNumber: null,
      notes: null,
      expectedPaymentMethod: 'CASH',
      changeFor: '100.00',
      items: [
        { itemKind: 'FIXED_DISH', dishId: id('d03'), dishSizeId: id('d32'), quantity: 1, notes: null, groupAddons: [{ addonOptionId: id('e21'), quantity: 1 }] },
        { itemKind: 'COMBO', comboId: id('c01'), quantity: 1, parts: [{ comboComponentId: id('c13'), beverageFlavorId: id('f12'), notes: null, groupAddons: null }] },
      ],
      weighings: [],
    });
    await waitFor(() => expect(router.replace).toHaveBeenCalledWith(`/orders/${ORDER_ID}`));
    expect(usePendingOps.getState().ops).toHaveLength(0);
  });

  it('nome já digitado não é sobrescrito: o pedido vai com ele e o cadastro não muda (Q-314)', async () => {
    await renderWith(<NewDeliveryScreen />);
    await screen.findByLabelText('Adicionar Açaí');
    await fireEvent.changeText(screen.getByLabelText('Nome'), 'Carlinha');
    await fireEvent.changeText(screen.getByLabelText('Telefone'), '34988887777');
    await screen.findByTestId('phone-customer');
    expect(screen.getByLabelText('Nome').props.value).toBe('Carlinha');
    expect(screen.getByText('O pedido vai com o nome digitado. O cadastro continua "Carla Souza" (muda só em Clientes).')).toBeTruthy();
  });

  it('sem telefone não confirma; telefone novo avisa que o cliente será cadastrado', async () => {
    await renderWith(<NewDeliveryScreen />);
    await screen.findByLabelText('Adicionar Açaí');
    // Sem itens, a prévia já é a taxa padrão da entrega (R$ 4,00).
    await fireEvent.press(screen.getByLabelText('Registrar R$ 4,00'));
    expect((await screen.findAllByText('Informe o telefone do cliente.')).length).toBeGreaterThan(0);
    expect(screen.queryByText('Registrar pedido por telefone?')).toBeNull();
    await fireEvent.changeText(screen.getByLabelText('Telefone'), '34911112222');
    expect(await screen.findByText('Telefone novo: o cliente é cadastrado ao registrar o pedido.')).toBeTruthy();
    expect(screen.getByLabelText('Nome').props.value).toBe('');
    expect(writes).toHaveLength(0);
  });

  it('retirada sem forma prevista vai; 422 PaymentMethodDisabled recarrega as formas; erro de campo do servidor', async () => {
    writeHandler = () => problem(422, 'PaymentMethodDisabled');
    await renderWith(<NewDeliveryScreen />);
    await screen.findByLabelText('Adicionar Água 500 ml');
    await fireEvent.changeText(screen.getByLabelText('Telefone'), '34911112222');
    await fireEvent.changeText(screen.getByLabelText('Nome'), 'Pedro');
    await fireEvent.press(screen.getByRole('tab', { name: 'Retirada' }));
    expect(screen.queryByLabelText('Rua')).toBeNull();
    await fireEvent.press(screen.getByLabelText('Adicionar Água 500 ml'));
    await fireEvent.press(screen.getByLabelText('Pix'));
    await fireEvent.press(screen.getByLabelText('Registrar R$ 4,00'));
    const settingsReads = reads.filter((r) => r === '/tenant/settings').length;
    await confirm();
    expect((await screen.findAllByText('Essa forma de pagamento está desabilitada.')).length).toBeGreaterThan(0);
    expect(writes[0]!.body).toMatchObject({ fulfillmentType: 'PICKUP', deliveryAddress: null, deliveryFee: null, expectedPaymentMethod: 'PIX', changeFor: null, customerPhone: '+5534911112222' });
    await waitFor(() => expect(reads.filter((r) => r === '/tenant/settings').length).toBeGreaterThan(settingsReads));
    expect(usePendingOps.getState().ops).toHaveLength(0);

    writeHandler = () => problem(422, 'ValidationFailed', { errors: { customerPhone: ['Telefone fora do formato.'] } });
    await fireEvent.press(screen.getByLabelText('Registrar R$ 4,00'));
    await confirm();
    expect((await screen.findAllByText('Telefone fora do formato.')).length).toBeGreaterThan(0);
  });
});

describe('busca do cliente pelo telefone (06 §6.8 `POST /customers/lookup`)', () => {
  function Probe() {
    const q = useCustomerByPhone(PHONE);
    return <Text testID="lookup-status">{q.data?.customer?.name ?? q.fetchStatus}</Text>;
  }

  it('com orders.create: POST com o telefone no corpo, nunca na URL', async () => {
    await renderWith(<Probe />);
    await waitFor(() => expect(screen.getByTestId('lookup-status').props.children).toBe('Carla Souza'));
    expect(reads).toEqual(['lookup:+5534988887777']);
    expect(writes).toHaveLength(0);
  });

  it.each([
    ['KITCHEN', ['orders.view', 'production.view']],
    ['CASHIER', ['orders.view', 'payments.register', 'closing.view']],
  ])('sem orders.create (%s) não pergunta ao servidor', async (role, permissions) => {
    const me = useSession.getState().me!;
    useSession.setState({ me: { ...me, employee: { ...me.employee, roles: [role] }, permissions } as unknown as Me });
    await renderWith(<Probe />);
    await act(async () => {
      await new Promise((r) => setTimeout(r, 20));
    });
    expect(screen.getByTestId('lookup-status').props.children).toBe('idle');
    expect(reads.filter((r) => r.startsWith('lookup:'))).toHaveLength(0);
  });
});

// ---------------------------------------------------------------- Aba Delivery

describe('aba Delivery (RF-315)', () => {
  it('lista telefone e site com o troco do motoboy, "Novos do site" filtrada, e os filtros na query', async () => {
    listItems = [
      summary({}),
      summary({ id: id('503'), orderNumber: 14, origin: 'WEB', customerName: 'Rita', fulfillmentType: 'PICKUP', expectedPaymentMethod: 'PIX', changeFor: null, totalAmount: '20.00', balance: '20.00' }),
    ];
    awaitingItems = [summary({ id: id('504'), orderNumber: null, origin: 'WEB', customerName: 'Paulo', acceptanceStatus: 'AWAITING_ACCEPTANCE', acceptanceDeadline: new Date(Date.now() + 9 * 60_000).toISOString(), fulfillmentStatus: null, productionStatus: null })];
    await renderWith(<DeliveryTab />);
    expect(await screen.findByText('Carla Souza')).toBeTruthy();
    expect(screen.getByText('Rita')).toBeTruthy();
    expect(screen.getByText('Paga com dinheiro · Levar troco para R$ 100,00 → troco R$ 43,00')).toBeTruthy();
    expect(screen.getByText('Paga com pix')).toBeTruthy();
    // Selo "Site" só no pedido do site (o card da faixa também tem o seu).
    expect(await screen.findByText('Novos do site (1)')).toBeTruthy();
    expect(screen.getAllByText('Site')).toHaveLength(2);

    const list = reads.filter((r) => r.startsWith('/orders?') && !r.includes('AWAITING'));
    expect(list[0]).toBe('/orders?orderType=A_LA_CARTE&fulfillmentType=DELIVERY&fulfillmentType=PICKUP&limit=50');
    const awaiting = reads.find((r) => r.includes('acceptanceStatus=AWAITING_ACCEPTANCE'));
    expect(awaiting).toBe('/orders?orderType=A_LA_CARTE&fulfillmentType=DELIVERY&fulfillmentType=PICKUP&acceptanceStatus=AWAITING_ACCEPTANCE&limit=50');

    await fireEvent.press(screen.getByRole('tab', { name: 'Entrega' }));
    await fireEvent.press(screen.getByLabelText('Saiu para entrega'));
    await waitFor(() => expect(reads).toContain('/orders?orderType=A_LA_CARTE&fulfillmentType=DELIVERY&fulfillmentStatus=OUT_FOR_DELIVERY&limit=50'));
    await fireEvent.press(screen.getByRole('tab', { name: 'Retirada' }));
    // "Saiu para entrega" não existe na retirada: o filtro volta para "Todos".
    await waitFor(() => expect(reads).toContain('/orders?orderType=A_LA_CARTE&fulfillmentType=PICKUP&limit=50'));
    expect(screen.queryByLabelText('Saiu para entrega')).toBeNull();

    await fireEvent.press(screen.getByLabelText('Novo pedido'));
    expect(router.push).toHaveBeenCalledWith('/orders/new/delivery');
  });
});

// ---------------------------------------------------------------- Marmita (tela)

describe('nova marmita por telefone', () => {
  it('entrega exige telefone e forma prevista; envia telefone, Pix e a taxa digitada', async () => {
    writeHandler = (method, path) => (method === 'POST' && path === '/orders' ? json(201, { ...ORDER, orderType: 'MARMITA' }, { ETag: '"1"' }) : undefined);
    await renderWith(<NewMarmitaScreen />);
    await screen.findByLabelText('Aumentar Coca-Cola 350ml');
    await fireEvent.press(screen.getByLabelText('Aumentar Coca-Cola 350ml'));
    await fireEvent.changeText(screen.getByLabelText('Nome'), 'João Silva');
    await fireEvent.press(screen.getByRole('tab', { name: 'Entrega' }));
    await fireEvent.changeText(screen.getByLabelText('Rua'), 'Rua das Acácias');
    // Q-315: o atendente digita a taxa na criação, sem `orders.delivery-fee.edit`.
    await fireEvent.changeText(screen.getByLabelText('Taxa de entrega'), '600');
    await fireEvent.press(screen.getByLabelText('Registrar R$ 12,00'));
    expect((await screen.findAllByText('Informe o telefone da entrega.')).length).toBeGreaterThan(0);
    expect(screen.queryByText('Registrar pedido?')).toBeNull();

    await fireEvent.changeText(screen.getByLabelText('Telefone'), '34999991234');
    expect((await screen.findAllByText('Escolha a forma de pagamento prevista da entrega.')).length).toBeGreaterThan(0);
    await fireEvent.press(screen.getByLabelText('Pix'));
    await fireEvent.press(screen.getByLabelText('Registrar R$ 12,00'));
    expect(screen.getByText('Registrar pedido?')).toBeTruthy();
    await confirm();
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]!.body).toMatchObject({
      orderType: 'MARMITA',
      fulfillmentType: 'DELIVERY',
      customerId: null,
      customerName: 'João Silva',
      customerPhone: '+5534999991234',
      saveCustomer: false,
      deliveryAddress: { street: 'Rua das Acácias', number: null, complement: null, neighborhood: null, reference: null },
      deliveryFee: '6.00',
      expectedPaymentMethod: 'PIX',
      changeFor: null,
      items: [{ itemKind: 'BEVERAGE', productId: 'coca', quantity: 1, notes: null }],
    });
  });

  it('retirada no balcão: telefone e forma prevista continuam opcionais', async () => {
    writeHandler = (method, path) => (method === 'POST' && path === '/orders' ? json(201, { ...ORDER, orderType: 'MARMITA' }, { ETag: '"1"' }) : undefined);
    await renderWith(<NewMarmitaScreen />);
    await screen.findByLabelText('Aumentar Coca-Cola 350ml');
    await fireEvent.press(screen.getByLabelText('Aumentar Coca-Cola 350ml'));
    expect(screen.getByLabelText('Telefone (opcional)')).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Registrar R$ 6,00'));
    await confirm();
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]!.body).toMatchObject({ fulfillmentType: 'PICKUP', customerPhone: null, expectedPaymentMethod: null, changeFor: null, deliveryFee: null });
  });
});

// ---------------------------------------------------------------- Detalhe, edição e pagamento

describe('detalhe, taxa depois e pagamento', () => {
  it('detalhe: forma prevista, troco do motoboy e a taxa digitada', async () => {
    await renderWith(<OrderDetailScreen />);
    expect(await screen.findByTestId('change-to-carry')).toHaveTextContent('Levar troco para R$ 100,00 → troco R$ 43,00');
    expect(screen.getByText('Vai pagar em dinheiro, troco para R$ 100,00')).toBeTruthy();
    expect(screen.getByText('Entrega (taxa digitada)')).toBeTruthy();
    expect(screen.getByText('R$ 5,00')).toBeTruthy();
  });

  it('sem orders.delivery-fee.edit a taxa não se edita depois e não vai no PATCH', async () => {
    writeHandler = (method) => (method === 'PATCH' ? json(200, { ...ORDER, version: 2 }, { ETag: '"2"' }) : undefined);
    await renderWith(<EditOrderScreen />);
    expect(await screen.findByTestId('fee-locked')).toHaveTextContent('Taxa de entrega: R$ 5,00. Depois de registrado, só gerente ou dono muda a taxa.');
    expect(screen.queryByLabelText('Taxa de entrega')).toBeNull();
    // À la carte de entrega edita cliente e endereço, como a marmita.
    expect(screen.getByLabelText('Cliente').props.value).toBe('Carla Souza');
    await fireEvent.press(screen.getByLabelText('Salvar'));
    await confirm();
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]!.headers['If-Match']).toBe('"1"');
    expect(writes[0]!.body).not.toHaveProperty('deliveryFee');
  });

  it('troca de atendimento só com o módulo do novo atendimento; 403 ModuleNotEnabled mostra a mensagem', async () => {
    writeHandler = (method) => (method === 'PATCH' ? problem(403, 'ModuleNotEnabled', { module: 'DELIVERY' }) : undefined);
    await renderWith(<EditOrderScreen />);
    await screen.findByLabelText('Cliente');
    expect(screen.getByRole('tab', { name: 'Retirada' })).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Salvar'));
    await confirm();
    expect((await screen.findAllByText('Delivery não está habilitado neste estabelecimento.')).length).toBeGreaterThan(0);
  });

  it('sem o módulo DELIVERY, a edição não oferece trocar entrega e retirada', async () => {
    const me = useSession.getState().me!;
    useSession.setState({ me: { ...me, tenant: { ...me.tenant, modules: ['A_LA_CARTE', 'FIXED_MENU'] } } as unknown as Me });
    await renderWith(<EditOrderScreen />);
    expect(await screen.findByTestId('fulfillment-locked')).toHaveTextContent('Entrega: este estabelecimento não troca o atendimento deste pedido.');
    expect(screen.queryByRole('tab', { name: 'Retirada' })).toBeNull();
  });

  it('com a permissão, a taxa mudada vai no PATCH; 403 PermissionDenied mostra a mensagem', async () => {
    signIn(['orders.delivery-fee.edit']);
    writeHandler = () => problem(403, 'PermissionDenied', { permission: 'orders.delivery-fee.edit' });
    await renderWith(<EditOrderScreen />);
    await fireEvent.changeText(await screen.findByLabelText('Taxa de entrega'), '700');
    await fireEvent.press(screen.getByLabelText('Salvar'));
    expect(screen.getByText('R$ 5,00 → R$ 7,00')).toBeTruthy();
    await confirm();
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]!.body).toMatchObject({ deliveryFee: '7.00' });
    expect(await screen.findByText('Você não tem permissão para isso. Peça a um gerente.')).toBeTruthy();
  });

  it('Receber: a forma prevista vem pré-selecionada e nada é pago sem confirmar', async () => {
    orderResponse = { ...ORDER, expectedPaymentMethod: 'PIX', changeFor: null };
    await renderWith(<PayScreen />);
    expect(await screen.findByTestId('expected-payment-hint')).toHaveTextContent('Forma prevista: Pix. Confira o que chegou antes de confirmar.');
    await waitFor(() => expect(screen.getByRole('radio', { name: 'Pix' })).toHaveProp('accessibilityState', { selected: true }));
    expect(screen.getByRole('radio', { name: 'Dinheiro' })).toHaveProp('accessibilityState', { selected: false });
    expect(writes).toHaveLength(0);
  });
});

// ---------------------------------------------------------------- Fatia 5: pedido do site da lanchonete

describe('pedido do site da lanchonete (EP-003 fatia 5, 06 §6.19 "Pedido da lanchonete")', () => {
  const at = '2026-11-22T19:20:00Z';
  const line = (over: Partial<Order['items'][number]>): Order['items'][number] => ({
    id: id('641'),
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
    requiresPreparation: true,
    notes: null,
    addons: [],
    parts: [],
    createdAt: at,
    createdBy: null,
    ...over,
  });

  /** O `201` do exemplo (69,80 pelo preço de site), aguardando aceite: combo com bacon e Coca-Cola Zero, X-Burguer e lata. */
  const WEB_ORDER: Order = {
    ...ORDER,
    orderNumber: null,
    origin: 'WEB',
    priceList: 'SITE',
    fulfillmentType: 'PICKUP',
    customer: { id: null, name: 'Carla Souza', phoneNumber: PHONE },
    deliveryAddress: null,
    notes: 'Sem talher',
    expectedPaymentMethod: 'PIX',
    changeFor: null,
    deliveryFee: '0.00',
    deliveryFeeSource: null,
    itemsTotal: '69.80',
    totalAmount: '69.80',
    balance: '69.80',
    productionStatus: null,
    productionStatusChangedAt: null,
    acceptanceStatus: 'AWAITING_ACCEPTANCE',
    acceptanceDeadline: new Date(Date.now() + 9 * 60_000).toISOString(),
    acceptedAt: null,
    acceptedBy: null,
    fulfillmentStatus: null,
    fulfillmentStatusChangedAt: null,
    createdBy: null,
    items: [
      line({
        id: id('644'),
        itemKind: 'COMBO',
        comboId: id('c01'),
        name: 'X-Burguer + batata + refri',
        unitPrice: '34.90',
        lineTotal: '34.90',
        requiresPreparation: false,
        parts: [
          line({
            id: id('645'),
            itemKind: 'COMBO_PART',
            dishId: id('d01'),
            name: 'X-Burguer',
            notes: 'Sem cebola',
            addons: [
              {
                id: id('646'),
                itemKind: 'GROUP_ADDON',
                productId: null,
                addonOptionId: id('e11'),
                name: 'Bacon',
                quantity: 1,
                unitPrice: '4.00',
                lineTotal: '4.00',
                requiresPreparation: true,
                notes: null,
                createdAt: at,
                createdBy: null,
              } as Order['items'][number]['addons'][number],
            ],
          }),
          line({ id: id('647'), itemKind: 'COMBO_PART', beverageSizeId: id('f11'), beverageFlavorId: id('f13'), name: 'Refrigerante Lata 350 ml · Coca-Cola Zero', unitPrice: '0.50', lineTotal: '0.50', requiresPreparation: false }),
        ],
      }),
      line({ id: id('648'), dishId: id('d01'), name: 'X-Burguer', unitPrice: '23.90', lineTotal: '23.90' }),
      line({ id: id('649'), itemKind: 'FIXED_BEVERAGE', beverageSizeId: id('f11'), beverageFlavorId: id('f12'), name: 'Refrigerante Lata 350 ml · Coca-Cola', unitPrice: '6.50', lineTotal: '6.50', requiresPreparation: false }),
    ],
  } as Order;

  const awaitingSummary = () =>
    summary({
      orderNumber: null,
      origin: 'WEB',
      fulfillmentType: 'PICKUP',
      totalAmount: '69.80',
      balance: '69.80',
      expectedPaymentMethod: 'PIX',
      changeFor: null,
      acceptanceStatus: 'AWAITING_ACCEPTANCE',
      acceptanceDeadline: WEB_ORDER.acceptanceDeadline,
      fulfillmentStatus: null,
      productionStatus: null,
      itemCount: 6,
    });

  it('"Novos do site" na aba Delivery: a árvore do combo, o total do site, e o aceite com If-Match', async () => {
    orderResponse = WEB_ORDER;
    awaitingItems = [awaitingSummary()];
    writeHandler = (method, path) =>
      method === 'POST' && path === `/orders/${ORDER_ID}/accept`
        ? json(200, { ...WEB_ORDER, version: 2, orderNumber: 15, acceptanceStatus: 'ACCEPTED', acceptedAt: at, acceptedBy: bruno, productionStatus: 'OPEN', fulfillmentStatus: 'PENDING' }, { ETag: '"2"' })
        : undefined;
    await renderWith(<DeliveryTab />);
    expect(await screen.findByText('Novos do site (1)')).toBeTruthy();
    // O card lê o detalhe: o combo com as partes recuadas (sabor, adicional e observação por prato), o prato e a bebida.
    expect(await screen.findByText('1× X-Burguer + batata + refri')).toBeTruthy();
    expect(screen.getAllByTestId('web-combo-part')).toHaveLength(2);
    expect(screen.getAllByText('1× X-Burguer')).toHaveLength(2);
    expect(screen.getByText('+ 1× Bacon')).toBeTruthy();
    expect(screen.getByText('Obs.: Sem cebola')).toBeTruthy();
    expect(screen.getByText('1× Refrigerante Lata 350 ml · Coca-Cola Zero')).toBeTruthy();
    expect(screen.getByText('1× Refrigerante Lata 350 ml · Coca-Cola')).toBeTruthy();
    expect(screen.getAllByText('R$ 69,80').length).toBeGreaterThan(0);
    expect(screen.getByText('Vai pagar com Pix')).toBeTruthy();

    await fireEvent.press(screen.getByText('Aceitar'));
    expect(screen.getByText('Aceitar pedido do site?')).toBeTruthy();
    await act(async () => {
      await fireEvent.press(screen.getByText('Aceitar pedido'));
    });
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]).toMatchObject({ method: 'POST', path: `/orders/${ORDER_ID}/accept`, body: { estimatedReadyMinutes: null } });
    expect(writes[0]!.headers['If-Match']).toBe('"1"');
    expect(writes[0]!.headers['Idempotency-Key']).toMatch(/[0-9a-f-]{36}/);
  });

  it('sem o detalhe carregado, o card mostra o itemCount da lista', async () => {
    awaitingItems = [{ ...awaitingSummary(), id: id('599') }];
    await renderWith(<DeliveryTab />);
    expect(await screen.findByText('Novos do site (1)')).toBeTruthy();
    expect(await screen.findByText('6 itens')).toBeTruthy();
  });

  it('recusa: motivo obrigatório e o corpo do contrato', async () => {
    orderResponse = WEB_ORDER;
    awaitingItems = [awaitingSummary()];
    writeHandler = (method, path) =>
      method === 'POST' && path === `/orders/${ORDER_ID}/reject` ? json(200, { ...WEB_ORDER, version: 2, acceptanceStatus: 'REJECTED' }, { ETag: '"2"' }) : undefined;
    await renderWith(<DeliveryTab />);
    await screen.findByText('1× X-Burguer + batata + refri');
    await fireEvent.press(screen.getByText('Recusar'));
    await act(async () => {
      await fireEvent.press(screen.getByText('Recusar pedido'));
    });
    expect(screen.getByText('Escolha o motivo.')).toBeTruthy();
    expect(writes).toHaveLength(0);
  });

  it('detalhe: aguardando aceite, sem número, com a árvore do combo e "Preços do site"', async () => {
    orderResponse = WEB_ORDER;
    await renderWith(<OrderDetailScreen />);
    expect(await screen.findByText('Aguardando aceite')).toBeTruthy();
    expect(screen.getByText('Preços do site')).toBeTruthy();
    expect(screen.getByText('1× X-Burguer + batata + refri')).toBeTruthy();
    // A linha do combo mostra a árvore inteira: 34,90 + bacon 4,00 + Coca-Cola Zero 0,50 = 39,40 (valores da API).
    expect(screen.getByText('R$ 39,40')).toBeTruthy();
    expect(screen.getByText('+ 1× Bacon (R$ 4,00)')).toBeTruthy();
    expect(screen.getByText('Aceitar')).toBeTruthy();
    expect(screen.getByText('Recusar')).toBeTruthy();
  });
});
