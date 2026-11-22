/**
 * EP-003 fatia 2 (ADR-0056, ADR-0057; 06 §6.5 "Cardápio fixo", com o exemplo literal do `GET /fixed-menu`):
 * prato com tamanhos e "vende no site", bebida com sabores e "copiar sabores", combo, pausa derivada exibida,
 * permissão só de pausa, `409 UsedInActiveCombos`, `409 VersionConflict` sem reenvio, tamanhos de marmita com
 * preço de site e o feed/evento do cardápio fixo.
 */
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { configure, fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import type { ReactElement } from 'react';

import { configureApiClient } from '@/api/client';
import { ApiError, errorMessage } from '@/api/errors';
import { keysForEntity } from '@/api/queryKeys';
import type { FixedMenu, MarmitaSize, Me, Permission } from '@/api/types';
import { SizesScreen } from '@/features/catalog/screens/CatalogScreens';
import {
  beverageCreateBody,
  beverageFormFrom,
  comboStatus,
  copyFlavors,
  dishCreateBody,
  dishFormErrors,
  dishFormFrom,
  dishUpdateBody,
  reorderCategories,
} from '@/features/fixedMenu/fixedMenu';
import BeverageFormScreen from '@/features/fixedMenu/screens/BeverageFormScreen';
import ComboFormScreen from '@/features/fixedMenu/screens/ComboFormScreen';
import DishFormScreen from '@/features/fixedMenu/screens/DishFormScreen';
import FixedMenuScreen from '@/features/fixedMenu/screens/FixedMenuScreen';
import { siteBodyPrice, toggleSiteSale } from '@/lib/sitePrice';
import { useConnectivity } from '@/stores/connectivity';
import { useSession } from '@/stores/session';
import { useToast } from '@/stores/toast';

configure({ asyncUtilTimeout: 8000 });
jest.setTimeout(60_000);

const mockParams: { current: Record<string, string> } = { current: {} };
jest.mock('expo-router', () => ({
  router: { push: jest.fn(), back: jest.fn(), replace: jest.fn() },
  Stack: { Screen: () => null },
  useLocalSearchParams: () => mockParams.current,
}));
jest.mock('@/lib/confirm', () => ({ confirmAction: jest.fn((o: { onConfirm: () => void }) => o.onConfirm()) }));
jest.mock('expo-image', () => ({ Image: () => null }));
jest.mock('expo-image-picker', () => ({ launchCameraAsync: jest.fn(), launchImageLibraryAsync: jest.fn() }));

// ------------------------------------------------------------------ exemplo literal do 06 §6.5 (`GET /fixed-menu`)

const LANCHES = '0192d6a0-0000-7000-8000-000000001001';
const PORCOES = '0192d6a0-0000-7000-8000-000000001002';
const XBURGUER = '0192d6a0-0000-7000-8000-000000001101';
const BATATA = '0192d6a0-0000-7000-8000-000000001102';
const BATATA_P = '0192d6a0-0000-7000-8000-000000001201';
const REFRI = '0192d6a0-0000-7000-8000-000000001301';
const LATA = '0192d6a0-0000-7000-8000-000000001311';
const COMBO = '0192d6a0-0000-7000-8000-000000001401';

function example(): FixedMenu {
  return {
    categories: [
      { id: LANCHES, version: 1, name: 'Lanches', sortOrder: 1, isActive: true, createdAt: '2026-11-22T10:00:00Z', updatedAt: '2026-11-22T10:00:00Z' },
      { id: PORCOES, version: 1, name: 'Porções', sortOrder: 2, isActive: true, createdAt: '2026-11-22T10:00:00Z', updatedAt: '2026-11-22T10:00:00Z' },
    ],
    dishes: [
      {
        id: XBURGUER,
        version: 1,
        dishCategoryId: LANCHES,
        name: 'X-Burguer',
        description: 'Pão, hambúrguer 150 g, queijo',
        imageUrl: null,
        requiresPreparation: true,
        hasSizes: false,
        price: '22.00',
        sellsOnSite: true,
        sitePrice: '23.90',
        sortOrder: 1,
        isActive: true,
        isPaused: false,
        isSellable: true,
        sizes: [],
        addonGroupIds: ['0192d6a0-0000-7000-8000-000000001501'],
        createdAt: '2026-11-22T10:01:00Z',
        updatedAt: '2026-11-22T10:01:00Z',
      },
      {
        id: BATATA,
        version: 3,
        dishCategoryId: PORCOES,
        name: 'Batata frita',
        description: null,
        imageUrl: null,
        requiresPreparation: true,
        hasSizes: true,
        price: null,
        sellsOnSite: false,
        sitePrice: null,
        sortOrder: 1,
        isActive: true,
        isPaused: false,
        isSellable: true,
        sizes: [
          { id: BATATA_P, name: 'P', price: '12.00', sellsOnSite: true, sitePrice: '13.00', sortOrder: 1, isActive: true, isPaused: true, isSellable: false },
          { id: '0192d6a0-0000-7000-8000-000000001202', name: 'G', price: '18.00', sellsOnSite: true, sitePrice: '19.50', sortOrder: 2, isActive: true, isPaused: false, isSellable: true },
        ],
        addonGroupIds: [],
        createdAt: '2026-11-22T10:02:00Z',
        updatedAt: '2026-11-22T11:30:00Z',
      },
    ],
    beverageTypes: [
      {
        id: REFRI,
        version: 2,
        name: 'Refrigerante',
        sortOrder: 1,
        isActive: true,
        sizes: [
          {
            id: LATA,
            name: 'Lata 350 ml',
            price: '6.00',
            sellsOnSite: true,
            sitePrice: '6.50',
            sortOrder: 1,
            isActive: true,
            isPaused: false,
            isSellable: true,
            flavors: [
              { id: '0192d6a0-0000-7000-8000-000000001321', name: 'Coca-Cola', extraPrice: '0.00', sortOrder: 1, isActive: true, isPaused: true },
              { id: '0192d6a0-0000-7000-8000-000000001322', name: 'Coca-Cola Zero', extraPrice: '0.50', sortOrder: 2, isActive: true, isPaused: false },
            ],
          },
        ],
        createdAt: '2026-11-22T10:03:00Z',
        updatedAt: '2026-11-22T11:00:00Z',
      },
    ],
    combos: [
      {
        id: COMBO,
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
        isSellable: false,
        components: [
          { id: '0192d6a0-0000-7000-8000-000000001411', kind: 'DISH', dishId: XBURGUER, dishSizeId: null, beverageSizeId: null, name: 'X-Burguer', quantity: 1, sortOrder: 0, isSellable: true },
          { id: '0192d6a0-0000-7000-8000-000000001412', kind: 'DISH', dishId: BATATA, dishSizeId: BATATA_P, beverageSizeId: null, name: 'Batata frita P', quantity: 1, sortOrder: 1, isSellable: false },
          { id: '0192d6a0-0000-7000-8000-000000001413', kind: 'BEVERAGE', dishId: null, dishSizeId: null, beverageSizeId: LATA, name: 'Refrigerante Lata 350 ml', quantity: 1, sortOrder: 2, isSellable: true },
        ],
        createdAt: '2026-11-22T10:05:00Z',
        updatedAt: '2026-11-22T10:05:00Z',
      },
    ],
    addonGroups: [
      {
        id: '0192d6a0-0000-7000-8000-000000001501',
        version: 1,
        name: 'Adicionais de lanche',
        sortOrder: 1,
        isActive: true,
        options: [
          { id: '0192d6a0-0000-7000-8000-000000001511', name: 'Bacon', price: '4.00', sortOrder: 1, isActive: true, isPaused: false },
          { id: '0192d6a0-0000-7000-8000-000000001512', name: 'Sem cebola', price: '0.00', sortOrder: 2, isActive: true, isPaused: false },
        ],
        dishCategoryIds: [LANCHES],
        dishIds: [],
        createdAt: '2026-11-22T10:06:00Z',
        updatedAt: '2026-11-22T10:06:00Z',
      },
    ],
  };
}

// ------------------------------------------------------------------ infraestrutura

const OWNER: Permission[] = ['orders.view', 'catalog.manage', 'catalog.availability'];
const ATTENDANT: Permission[] = ['orders.view', 'orders.create', 'catalog.availability'];
const CASHIER: Permission[] = ['orders.view', 'payments.register'];

function signIn(permissions: Permission[]) {
  const me = {
    user: { id: 'u1', fullName: 'Beto', email: null, phoneNumber: null },
    tenant: { id: 't1', name: 'Lanches do Beto', timeZone: 'America/Sao_Paulo', behaviors: ['SNACK_BAR'], labels: ['BURGER_JOINT'], modules: ['A_LA_CARTE', 'DELIVERY', 'FIXED_MENU'] },
    employee: { id: 'e1', displayName: 'Beto', roles: ['OWNER'], hasPin: true },
    permissions,
    memberships: [],
    subscription: { status: 'ACTIVE', trialEndsAt: null, daysLeft: null, canWrite: true },
    sharedDevice: null,
    serverTime: '2026-11-22T12:00:00Z',
  } as Me;
  useSession.setState({ status: 'signedIn', readOnly: false, me });
}

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const clients: QueryClient[] = [];
afterEach(() => {
  for (const c of clients.splice(0)) c.clear();
});

async function renderWith(ui: ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false, gcTime: 0 } } });
  clients.push(client);
  return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
}

type Route = (init: RequestInit) => Response;
let routes: Record<string, Route>;
const requests: { method: string; path: string; headers: Record<string, string>; body: unknown }[] = [];
const sent = (method: string, path: string) => requests.filter((r) => r.method === method && r.path.split('?')[0] === path);

beforeEach(() => {
  requests.length = 0;
  mockParams.current = {};
  routes = { 'GET /fixed-menu': () => json(200, example()) };
  useToast.setState({ toasts: [] });
  useConnectivity.setState({ online: true });
  configureApiClient({ getAccessToken: () => 'tok', refresh: async () => true, onSessionExpired: () => {}, onUpgradeRequired: () => {}, onSubscriptionInactive: () => {} });
  global.fetch = jest.fn(async (url: string, init: RequestInit = {}) => {
    const path = url.replace(/^https?:\/\/[^/]+(\/api\/v1)?/, '');
    const method = init.method ?? 'GET';
    requests.push({ method, path, headers: (init.headers ?? {}) as Record<string, string>, body: init.body ? JSON.parse(init.body as string) : undefined });
    const route = routes[`${method} ${path.split('?')[0]}`];
    return route ? route(init) : json(404, { errorCode: 'NotFound' });
  }) as unknown as typeof fetch;
});

const ifMatch = (r: { headers: Record<string, string> }) => r.headers['If-Match'] ?? r.headers['if-match'];

// ------------------------------------------------------------------ regras puras

describe('formulário → corpo da API (06 §6.5)', () => {
  it('açaí com tamanhos: preço em cada tamanho, site vazio vai null, a ordem é a da lista', () => {
    const form = {
      ...dishFormFrom(null, { dishCategoryId: PORCOES, sortOrder: 1 }),
      name: 'Açaí',
      hasSizes: true,
      sizes: [
        { key: 'a', id: null, name: '300 ml', price: '14.00', sellsOnSite: true, sitePrice: '0.00' },
        { key: 'b', id: null, name: '500 ml', price: '18.00', sellsOnSite: true, sitePrice: '19.00' },
        { key: 'c', id: null, name: '700 ml', price: '23.00', sellsOnSite: false, sitePrice: '0.00' },
      ],
    };
    expect(dishFormErrors(form)).toEqual({});
    expect(dishCreateBody(form)).toEqual({
      dishCategoryId: PORCOES,
      name: 'Açaí',
      description: null,
      requiresPreparation: true,
      hasSizes: true,
      price: null,
      sellsOnSite: false,
      sitePrice: null,
      sortOrder: 1,
      sizes: [
        { id: null, name: '300 ml', price: '14.00', sellsOnSite: true, sitePrice: null, sortOrder: 1 },
        { id: null, name: '500 ml', price: '18.00', sellsOnSite: true, sitePrice: '19.00', sortOrder: 2 },
        { id: null, name: '700 ml', price: '23.00', sellsOnSite: false, sitePrice: null, sortOrder: 3 },
      ],
    });
  });

  it('"Tamanho?" ligado sem tamanho e desligado sem preço não salvam', () => {
    const base = dishFormFrom(null, { dishCategoryId: LANCHES, sortOrder: 1 });
    expect(dishFormErrors({ ...base, name: 'Açaí', hasSizes: true })).toHaveProperty('sizes');
    expect(dishFormErrors({ ...base, name: 'X-Burguer' })).toHaveProperty('price');
  });

  it('PATCH: com tamanho manda só `sizes`; sem tamanho manda o preço e não manda `sizes`', () => {
    const batata = example().dishes[1]!;
    const withSizes = dishUpdateBody(dishFormFrom(batata, { dishCategoryId: null, sortOrder: 1 }));
    expect(withSizes).not.toHaveProperty('price');
    expect(withSizes).not.toHaveProperty('sellsOnSite');
    expect(withSizes.sizes?.map((s) => s.id)).toEqual([BATATA_P, '0192d6a0-0000-7000-8000-000000001202']);
    const single = dishUpdateBody({ ...dishFormFrom(batata, { dishCategoryId: null, sortOrder: 1 }), hasSizes: false, price: '15.00' });
    expect(single).not.toHaveProperty('sizes');
    expect(single).toMatchObject({ hasSizes: false, price: '15.00', sellsOnSite: false, sitePrice: null });
  });

  it('ligar "vende no site" preenche com o preço de balcão; desligar guarda o preço', () => {
    expect(toggleSiteSale({ sellsOnSite: false, sitePrice: '0.00' }, true, '22.00')).toEqual({ sellsOnSite: true, sitePrice: '22.00' });
    expect(toggleSiteSale({ sellsOnSite: false, sitePrice: '23.90' }, true, '22.00')).toEqual({ sellsOnSite: true, sitePrice: '23.90' });
    expect(siteBodyPrice({ sellsOnSite: false, sitePrice: '23.90' })).toBe('23.90');
    expect(siteBodyPrice({ sellsOnSite: true, sitePrice: '0.00' })).toBeNull();
  });

  it('copiar sabores acrescenta só os que faltam, como sabores novos', () => {
    const form = beverageFormFrom(example().beverageTypes[0]!, { sortOrder: 1 });
    const withTwoL = { ...form, sizes: [...form.sizes, { key: 'x', id: null, name: '2 L', price: '14.00', sellsOnSite: false, sitePrice: '0.00', flavors: [{ key: 'y', id: null, name: 'coca-cola', extraPrice: '0.00' }] }] };
    const copied = copyFlavors(withTwoL, LATA, 'x');
    expect(copied.sizes[1]!.flavors.map((f) => [f.id, f.name, f.extraPrice])).toEqual([
      [null, 'coca-cola', '0.00'],
      [null, 'Coca-Cola Zero', '0.50'],
    ]);
    expect(copied.sizes[0]).toBe(withTwoL.sizes[0]);
    expect(beverageCreateBody(copied).sizes[1]!.flavors).toEqual([
      { id: null, name: 'coca-cola', extraPrice: '0.00', sortOrder: 1 },
      { id: null, name: 'Coca-Cola Zero', extraPrice: '0.50', sortOrder: 2 },
    ]);
  });

  it('combo fora de venda por derivação mostra o componente pausado', () => {
    const combo = example().combos[0]!;
    expect(comboStatus(combo)).toEqual({ tone: 'danger', text: 'Indisponível: Batata frita P pausado' });
    expect(comboStatus({ ...combo, isSellable: true })).toBeNull();
    expect(comboStatus({ ...combo, isPaused: true })?.text).toBe('Pausado');
  });

  it('reordenar categorias manda só quem mudou de lugar', () => {
    expect(reorderCategories(example().categories, 1, -1)).toEqual([
      { id: PORCOES, version: 1, sortOrder: 1 },
      { id: LANCHES, version: 1, sortOrder: 2 },
    ]);
    expect(reorderCategories(example().categories, 0, -1)).toEqual([]);
  });

  it('feed/evento das raízes do cardápio fixo recarrega o GET /fixed-menu inteiro', () => {
    for (const t of ['DISH_CATEGORY', 'DISH', 'BEVERAGE_TYPE', 'COMBO', 'ADDON_GROUP'] as const) {
      expect(keysForEntity(t, 'x')).toEqual([['fixedMenu']]);
    }
  });

  it('mensagem do 409 UsedInActiveCombos lista os combos', () => {
    const e = new ApiError(409, {
      errorCode: 'UsedInActiveCombos',
      combos: [
        { id: '0192d6a0-0000-7000-8000-000000001402', name: 'Batata + refri' },
        { id: COMBO, name: 'X-Burguer + batata + refri' },
      ],
    });
    expect(errorMessage(e)).toBe('Este item é usado nos combos ativos “Batata + refri” e “X-Burguer + batata + refri”. Ajuste ou inative esses combos antes.');
  });
});

// ------------------------------------------------------------------ telas

describe('tela Cardápio', () => {
  it('lista pratos por categoria, adicionais e o combo indisponível pela batata pausada', async () => {
    signIn(OWNER);
    await renderWith(<FixedMenuScreen />);
    expect(await screen.findByText('X-Burguer')).toBeTruthy();
    expect(screen.getAllByText('Lanches')).toHaveLength(2); // seção e "vale para" do grupo
    expect(screen.getByText('Porções')).toBeTruthy();
    expect(screen.getByText('P R$ 12,00 · G R$ 18,00')).toBeTruthy();
    expect(screen.getByText('Adicionais: Adicionais de lanche')).toBeTruthy();
    expect(screen.getByText('Site: R$ 23,90')).toBeTruthy();
    expect(screen.getByLabelText('Novo prato')).toBeTruthy();

    await fireEvent.press(screen.getByRole('tab', { name: 'Combos' }));
    expect(await screen.findByText('Indisponível: Batata frita P pausado')).toBeTruthy();
    expect(screen.getByText('R$ 32,00 · Site: R$ 34,90')).toBeTruthy();

    await fireEvent.press(screen.getByRole('tab', { name: 'Bebidas' }));
    expect(await screen.findByText('Coca-Cola Zero · + R$ 0,50')).toBeTruthy();
  });

  it('atendente só pausa: sem botões de cadastro; reativar a batata P manda a pausa da raiz com If-Match', async () => {
    signIn(ATTENDANT);
    const after = example();
    after.dishes[1]!.version = 4;
    after.dishes[1]!.sizes[0]!.isPaused = false;
    after.dishes[1]!.sizes[0]!.isSellable = true;
    after.combos[0]!.isSellable = true;
    after.combos[0]!.components[1]!.isSellable = true;
    routes[`PATCH /fixed-menu/dishes/${BATATA}/availability`] = () => {
      routes['GET /fixed-menu'] = () => json(200, after);
      return json(200, after.dishes[1]);
    };
    await renderWith(<FixedMenuScreen />);
    expect(await screen.findByText('Batata frita')).toBeTruthy();
    expect(screen.getByText(/Você pode pausar e reativar/)).toBeTruthy();
    expect(screen.queryByLabelText('Novo prato')).toBeNull();
    expect(screen.queryByLabelText('Categorias')).toBeNull();
    expect(screen.queryByLabelText('Novo grupo')).toBeNull();

    await fireEvent(await screen.findByLabelText('Batata frita P disponível'), 'valueChange', true);
    await waitFor(() => expect(sent('PATCH', `/fixed-menu/dishes/${BATATA}/availability`)).toHaveLength(1));
    const req = sent('PATCH', `/fixed-menu/dishes/${BATATA}/availability`)[0]!;
    expect(req.body).toEqual({ sizes: [{ id: BATATA_P, isPaused: false }] });
    expect(ifMatch(req)).toBe('"3"');
    // O cardápio inteiro é recarregado: o combo volta à venda sem ter sido tocado.
    await waitFor(() => expect(sent('GET', '/fixed-menu').length).toBeGreaterThanOrEqual(2));
    await fireEvent.press(screen.getByRole('tab', { name: 'Combos' }));
    await waitFor(() => expect(screen.queryByText('Indisponível: Batata frita P pausado')).toBeNull());
    expect(useToast.getState().toasts.map((t) => t.message)).toContain('Batata frita P reativado');
  });

  it('pausar um sabor e um combo usa a rota do tipo e a do combo', async () => {
    signIn(ATTENDANT);
    routes[`PATCH /fixed-menu/beverage-types/${REFRI}/availability`] = () => json(200, example().beverageTypes[0]);
    routes[`PATCH /fixed-menu/combos/${COMBO}/availability`] = () => json(200, example().combos[0]);
    await renderWith(<FixedMenuScreen />);
    await fireEvent.press(await screen.findByRole('tab', { name: 'Bebidas' }));
    await fireEvent(await screen.findByLabelText('Coca-Cola Zero Lata 350 ml disponível'), 'valueChange', false);
    await waitFor(() => expect(sent('PATCH', `/fixed-menu/beverage-types/${REFRI}/availability`)).toHaveLength(1));
    expect(sent('PATCH', `/fixed-menu/beverage-types/${REFRI}/availability`)[0]!.body).toEqual({ flavors: [{ id: '0192d6a0-0000-7000-8000-000000001322', isPaused: true }] });

    await fireEvent.press(screen.getByRole('tab', { name: 'Combos' }));
    await fireEvent(await screen.findByLabelText('X-Burguer + batata + refri disponível'), 'valueChange', false);
    await waitFor(() => expect(sent('PATCH', `/fixed-menu/combos/${COMBO}/availability`)).toHaveLength(1));
    expect(sent('PATCH', `/fixed-menu/combos/${COMBO}/availability`)[0]!.body).toEqual({ isPaused: true });
  });

  it('sem catalog.availability: só leitura, sem chaves de pausa', async () => {
    signIn(CASHIER);
    await renderWith(<FixedMenuScreen />);
    expect(await screen.findByText('X-Burguer')).toBeTruthy();
    expect(screen.queryByLabelText('X-Burguer disponível')).toBeNull();
    expect(screen.getAllByText('Pausado').length).toBeGreaterThan(0);
    expect(screen.getByText(/Só o proprietário ou o gerente/)).toBeTruthy();
  });
});

describe('cadastro', () => {
  it('cria prato com "Tamanho?" e vende no site num tamanho, preenchido com o balcão', async () => {
    signIn(OWNER);
    mockParams.current = { categoryId: PORCOES };
    routes['POST /fixed-menu/dishes'] = () => json(201, { ...example().dishes[1], id: 'novo', name: 'Açaí' });
    await renderWith(<DishFormScreen />);
    await fireEvent.changeText(await screen.findByLabelText('Nome'), 'Açaí');
    await fireEvent(screen.getByLabelText('Tamanho?'), 'valueChange', true);
    await fireEvent.changeText(screen.getByLabelText('Tamanho 1'), '300 ml');
    await fireEvent.changeText(screen.getByLabelText('Preço: 300 ml'), '1400');
    await fireEvent(screen.getByLabelText('Vende no site: 300 ml'), 'valueChange', true);
    expect(screen.getByLabelText('Preço no site: 300 ml').props.value).toBe('R$ 14,00');
    await fireEvent.press(screen.getByLabelText('Adicionar tamanho'));
    await fireEvent.changeText(screen.getByLabelText('Tamanho 2'), '500 ml');
    await fireEvent.changeText(screen.getByLabelText('Preço: 500 ml'), '1800');
    await fireEvent.press(screen.getByLabelText('Salvar'));

    await waitFor(() => expect(sent('POST', '/fixed-menu/dishes')).toHaveLength(1));
    expect(sent('POST', '/fixed-menu/dishes')[0]!.body).toEqual({
      dishCategoryId: PORCOES,
      name: 'Açaí',
      description: null,
      requiresPreparation: true,
      hasSizes: true,
      price: null,
      sellsOnSite: false,
      sitePrice: null,
      sortOrder: 2,
      sizes: [
        { id: null, name: '300 ml', price: '14.00', sellsOnSite: true, sitePrice: '14.00', sortOrder: 1 },
        { id: null, name: '500 ml', price: '18.00', sellsOnSite: false, sitePrice: null, sortOrder: 2 },
      ],
    });
  });

  it('"Tamanho?" ligado sem tamanho não envia', async () => {
    signIn(OWNER);
    await renderWith(<DishFormScreen />);
    await fireEvent.changeText(await screen.findByLabelText('Nome'), 'Açaí');
    await fireEvent(screen.getByLabelText('Tamanho?'), 'valueChange', true);
    await fireEvent.press(screen.getByLabelText('Remover tamanho 1'));
    await fireEvent.press(screen.getByLabelText('Salvar'));
    expect(await screen.findByText(/cadastre pelo menos um tamanho/)).toBeTruthy();
    expect(sent('POST', '/fixed-menu/dishes')).toHaveLength(0);
  });

  it('bebida: dois tamanhos, sabores com acréscimo e "copiar sabores de outro tamanho"', async () => {
    signIn(OWNER);
    routes['POST /fixed-menu/beverage-types'] = () => json(201, example().beverageTypes[0]);
    await renderWith(<BeverageFormScreen />);
    await fireEvent.changeText(await screen.findByLabelText('Tipo'), 'Suco');
    await fireEvent.changeText(screen.getByLabelText('Tamanho 1'), '500 ml');
    await fireEvent.changeText(screen.getByLabelText('Preço: 500 ml'), '900');
    await fireEvent.press(screen.getByLabelText('Adicionar sabor (500 ml)'));
    await fireEvent.changeText(screen.getByLabelText('Sabor 1 (500 ml)'), 'Laranja');
    await fireEvent.press(screen.getByLabelText('Adicionar sabor (500 ml)'));
    await fireEvent.changeText(screen.getByLabelText('Sabor 2 (500 ml)'), 'Laranja com acerola');
    await fireEvent.changeText(screen.getByLabelText('Acréscimo: Laranja com acerola (500 ml)'), '100');
    await fireEvent.press(screen.getByLabelText('Adicionar tamanho'));
    await fireEvent.changeText(screen.getByLabelText('Tamanho 2'), '1 L');
    await fireEvent.changeText(screen.getByLabelText('Preço: 1 L'), '1600');
    await fireEvent.press(screen.getByLabelText('Copiar sabores de outro tamanho (1 L)'));
    await fireEvent.press(screen.getByLabelText('500 ml'));
    expect(screen.getByLabelText('Sabor 2 (1 L)').props.value).toBe('Laranja com acerola');
    await fireEvent.press(screen.getByLabelText('Salvar'));

    await waitFor(() => expect(sent('POST', '/fixed-menu/beverage-types')).toHaveLength(1));
    const flavors = [
      { id: null, name: 'Laranja', extraPrice: '0.00', sortOrder: 1 },
      { id: null, name: 'Laranja com acerola', extraPrice: '1.00', sortOrder: 2 },
    ];
    expect(sent('POST', '/fixed-menu/beverage-types')[0]!.body).toEqual({
      name: 'Suco',
      sortOrder: 2,
      sizes: [
        { id: null, name: '500 ml', price: '9.00', sellsOnSite: false, sitePrice: null, sortOrder: 1, flavors },
        { id: null, name: '1 L', price: '16.00', sellsOnSite: false, sitePrice: null, sortOrder: 2, flavors },
      ],
    });
  });

  it('combo com prato de tamanho fixo e bebida de tipo + tamanho, vende no site', async () => {
    signIn(OWNER);
    routes['POST /fixed-menu/combos'] = () => json(201, { ...example().combos[0], id: 'novo-combo' });
    await renderWith(<ComboFormScreen />);
    await fireEvent.changeText(await screen.findByLabelText('Nome'), 'Batata + refri');
    await fireEvent.changeText(screen.getByLabelText('Preço do combo'), '2000');
    await fireEvent(screen.getByLabelText('Vende no site'), 'valueChange', true);
    await fireEvent.changeText(screen.getByLabelText('Preço no site'), '2190');
    await fireEvent.press(screen.getByLabelText('Adicionar prato'));
    await fireEvent.press(screen.getByLabelText('Batata frita'));
    await fireEvent.press(screen.getByLabelText('G'));
    await fireEvent.press(screen.getByLabelText('Adicionar bebida'));
    await fireEvent.press(screen.getByLabelText('Refrigerante'));
    await fireEvent.press(screen.getByLabelText('Lata 350 ml'));
    await fireEvent.press(screen.getByLabelText('Aumentar quantidade de Refrigerante Lata 350 ml'));
    await fireEvent.press(screen.getByLabelText('Salvar'));

    await waitFor(() => expect(sent('POST', '/fixed-menu/combos')).toHaveLength(1));
    expect(sent('POST', '/fixed-menu/combos')[0]!.body).toEqual({
      name: 'Batata + refri',
      description: null,
      price: '20.00',
      sellsOnSite: true,
      sitePrice: '21.90',
      sortOrder: 2,
      components: [
        { kind: 'DISH', dishId: BATATA, dishSizeId: '0192d6a0-0000-7000-8000-000000001202', beverageSizeId: null, quantity: 1 },
        { kind: 'BEVERAGE', dishId: null, dishSizeId: null, beverageSizeId: LATA, quantity: 2 },
      ],
    });
  });

  it('combo: prato com tamanho sem o tamanho escolhido não envia', async () => {
    signIn(OWNER);
    await renderWith(<ComboFormScreen />);
    await fireEvent.changeText(await screen.findByLabelText('Nome'), 'Só batata');
    await fireEvent.changeText(screen.getByLabelText('Preço do combo'), '1500');
    await fireEvent.press(screen.getByLabelText('Adicionar prato'));
    await fireEvent.press(screen.getByLabelText('Batata frita'));
    await fireEvent.press(screen.getByLabelText('Salvar'));
    expect(await screen.findByText('Escolha o tamanho do prato.')).toBeTruthy();
    expect(sent('POST', '/fixed-menu/combos')).toHaveLength(0);
  });

  it('inativar prato usado em combo ativo mostra os combos (409 UsedInActiveCombos)', async () => {
    signIn(OWNER);
    mockParams.current = { id: BATATA };
    routes[`PATCH /fixed-menu/dishes/${BATATA}`] = () =>
      json(409, {
        type: 'urn:almoseis:error:used-in-active-combos',
        title: 'O item é usado em combos ativos.',
        status: 409,
        errorCode: 'UsedInActiveCombos',
        combos: [
          { id: '0192d6a0-0000-7000-8000-000000001402', name: 'Batata + refri' },
          { id: COMBO, name: 'X-Burguer + batata + refri' },
        ],
      });
    await renderWith(<DishFormScreen />);
    await fireEvent.press(await screen.findByLabelText('Excluir (inativar)'));
    expect(await screen.findByText(/usado nos combos ativos “Batata \+ refri” e “X-Burguer \+ batata \+ refri”/)).toBeTruthy();
    const req = sent('PATCH', `/fixed-menu/dishes/${BATATA}`)[0]!;
    expect(req.body).toEqual({ isActive: false });
    expect(ifMatch(req)).toBe('"3"');
  });

  it('409 VersionConflict: recarrega o cardápio, remonta o formulário com o salvo e não reenvia', async () => {
    signIn(OWNER);
    mockParams.current = { id: XBURGUER };
    const current = { ...example().dishes[0]!, version: 2, price: '24.00' };
    routes[`PATCH /fixed-menu/dishes/${XBURGUER}`] = () => {
      const fresh = example();
      fresh.dishes[0] = current;
      routes['GET /fixed-menu'] = () => json(200, fresh);
      return json(409, { errorCode: 'VersionConflict', title: 'Conflito', status: 409, current });
    };
    await renderWith(<DishFormScreen />);
    await fireEvent.changeText(await screen.findByLabelText('Preço'), '2300');
    await fireEvent.press(screen.getByLabelText('Salvar'));
    expect(await screen.findByText(/Outra pessoa alterou este item/)).toBeTruthy();
    await waitFor(() => expect(screen.getByLabelText('Preço').props.value).toBe('R$ 24,00'));
    expect(sent('PATCH', `/fixed-menu/dishes/${XBURGUER}`)).toHaveLength(1);
    expect(sent('GET', '/fixed-menu').length).toBeGreaterThanOrEqual(2);
  });

  it('cardápio recarregado no meio da edição: o If-Match continua sendo a versão em que o formulário abriu', async () => {
    signIn(OWNER);
    mockParams.current = { id: XBURGUER };
    routes[`PATCH /fixed-menu/dishes/${XBURGUER}`] = () => json(200, { ...example().dishes[0], version: 3 });
    await renderWith(<DishFormScreen />);
    await fireEvent.changeText(await screen.findByLabelText('Preço'), '2300');
    // Outro aparelho alterou o prato; o evento recarrega o cardápio enquanto a pessoa edita.
    const fresh = example();
    fresh.dishes[0] = { ...fresh.dishes[0]!, version: 2, name: 'X-Burguer da casa' };
    routes['GET /fixed-menu'] = () => json(200, fresh);
    await clients[clients.length - 1]!.invalidateQueries({ queryKey: ['fixedMenu'] });
    await waitFor(() => expect(sent('GET', '/fixed-menu')).toHaveLength(2));
    await fireEvent.press(screen.getByLabelText('Salvar'));
    await waitFor(() => expect(sent('PATCH', `/fixed-menu/dishes/${XBURGUER}`)).toHaveLength(1));
    expect(ifMatch(sent('PATCH', `/fixed-menu/dishes/${XBURGUER}`)[0]!)).toBe('"1"');
  });

  it('422 por campo aparece no campo do tamanho', async () => {
    signIn(OWNER);
    mockParams.current = { id: BATATA };
    routes[`PATCH /fixed-menu/dishes/${BATATA}`] = () =>
      json(422, { errorCode: 'ValidationFailed', title: 'Dados inválidos', status: 422, errors: { 'sizes[1].name': ['Já existe um tamanho ativo com esse nome.'] } });
    await renderWith(<DishFormScreen />);
    await fireEvent.press(await screen.findByLabelText('Salvar'));
    expect(await screen.findAllByText('Já existe um tamanho ativo com esse nome.')).toHaveLength(2);
  });

  it('403 ModuleNotEnabled vira a mensagem do módulo', async () => {
    signIn(OWNER);
    routes['POST /fixed-menu/combos'] = () => json(403, { errorCode: 'ModuleNotEnabled', module: 'FIXED_MENU', status: 403, title: 'Módulo' });
    await renderWith(<ComboFormScreen />);
    await fireEvent.changeText(await screen.findByLabelText('Nome'), 'Combo');
    await fireEvent.changeText(screen.getByLabelText('Preço do combo'), '1000');
    await fireEvent.press(screen.getByLabelText('Adicionar prato'));
    await fireEvent.press(screen.getByLabelText('X-Burguer'));
    await fireEvent.press(screen.getByLabelText('Salvar'));
    expect(await screen.findByText('Cardápio fixo não está habilitado neste estabelecimento.')).toBeTruthy();
  });
});

describe('tamanhos de marmita com preço de site (restaurante)', () => {
  const size = (over: Partial<MarmitaSize>): MarmitaSize => ({ id: 's1', version: 1, name: 'M', price: '20.00', sortOrder: 2, isActive: true, sellsOnSite: false, sitePrice: null, ...over });

  it('sem tamanho que vende no site, avisa da página; ligar preenche o preço e salva com If-Match', async () => {
    signIn(OWNER);
    routes['GET /marmita-sizes'] = () => json(200, { items: [size({})] });
    routes['PATCH /marmita-sizes/s1'] = () => json(200, size({ version: 2, sellsOnSite: true, sitePrice: '21.90' }));
    await renderWith(<SizesScreen />);
    expect(await screen.findByText(/Nenhum tamanho de marmita vende no site/)).toBeTruthy();
    expect(screen.getByText('Não vende no site')).toBeTruthy();
    await fireEvent.press(screen.getByText('M'));
    await fireEvent(screen.getByLabelText('Vende no site'), 'valueChange', true);
    expect(screen.getByLabelText('Preço no site').props.value).toBe('R$ 20,00');
    await fireEvent.changeText(screen.getByLabelText('Preço no site'), '2190');
    await fireEvent.press(screen.getByLabelText('Salvar tamanho'));
    await waitFor(() => expect(sent('PATCH', '/marmita-sizes/s1')).toHaveLength(1));
    const req = sent('PATCH', '/marmita-sizes/s1')[0]!;
    expect(req.body).toEqual({ name: 'M', price: '20.00', sortOrder: 2, sellsOnSite: true, sitePrice: '21.90', isActive: true });
    expect(ifMatch(req)).toBe('"1"');
  });

  it('com tamanho no site, mostra o preço de site e não avisa', async () => {
    signIn(OWNER);
    routes['GET /marmita-sizes'] = () => json(200, { items: [size({ sellsOnSite: true, sitePrice: '21.90' })] });
    await renderWith(<SizesScreen />);
    expect(await screen.findByText('Site: R$ 21,90')).toBeTruthy();
    expect(screen.queryByText(/Nenhum tamanho de marmita vende no site/)).toBeNull();
  });
});

