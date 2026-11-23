/**
 * EP-003 fatia 1 (ADR-0054, ADR-0055, 06 §6.3/§6.4/§6.12/§6.14): abas e menu pelos módulos, aba lembrada que
 * deixou de existir, rótulos no cadastro e em "Dados do estabelecimento", recarga do `me` no
 * `TenantModulesChanged` e na entrada `TENANT` do feed, e a mensagem do `403 ModuleNotEnabled`.
 */
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { configure, fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import type { ReactElement } from 'react';

import { apiRequest, configureApiClient } from '@/api/client';
import { ApiError, errorMessage } from '@/api/errors';
import { keysForEntity } from '@/api/queryKeys';
import type { Me, RealtimeEnvelope, Tenant, TenantModule } from '@/api/types';
import SignupScreen, { signupLabelsError } from '@/features/auth/screens/SignupScreen';
import { TenantScreen } from '@/features/settings/screens/SettingsScreens';
import { menuGroups } from '@/features/shell/AppShell';
import { useMeSync } from '@/features/session/hooks';
import { pullChanges, resetSyncThrottle } from '@/features/sync/syncEngine';
import { useAppRuntime } from '@/features/sync/useAppRuntime';
import SalaoTab from '@/features/orders/screens/SalaoTab';
import {
  LABELS,
  initialTab,
  keepLabelsOf,
  labelsError,
  labelsFor,
  salaoKinds,
  toggleLabel,
  visibleTabs,
} from '@/features/tenant/establishment';
import { useConnectivity } from '@/stores/connectivity';
import { useSession } from '@/stores/session';
import { useToast } from '@/stores/toast';
import { memory } from '@/test-utils/fakeDb';

// A primeira renderização das telas com o React Query passa de 1 s em máquina lenta.
configure({ asyncUtilTimeout: 8000 });
jest.setTimeout(60_000);

jest.mock('expo-router', () => ({
  router: { push: jest.fn(), back: jest.fn(), replace: jest.fn() },
  Stack: { Screen: () => null },
}));
jest.mock('@/db/metaRepo', () => ({ metaRepo: jest.requireActual('@/test-utils/fakeDb').metaRepo }));
jest.mock('@/db/pendingOpsRepo', () => ({ pendingOpsRepo: jest.requireActual('@/test-utils/fakeDb').pendingOpsRepo }));
jest.mock('@/db/draftsRepo', () => ({ draftsRepo: jest.requireActual('@/test-utils/fakeDb').draftsRepo }));
jest.mock('expo-network', () => ({
  getNetworkStateAsync: async () => ({ isConnected: true, isInternetReachable: true }),
  addNetworkStateListener: () => ({ remove: () => {} }),
}));
jest.mock('@/features/sync/appLifecycle', () => ({ subscribeAppLifecycle: () => () => {} }));

/** Captura os handlers que o `useAppRuntime` passa para a conexão do SignalR. */
const mockRealtime: { onEvent?: (e: RealtimeEnvelope) => void } = {};
jest.mock('@/features/realtime/realtime', () => {
  const actual = jest.requireActual('@/features/realtime/realtime');
  class FakeConnection {
    constructor(handlers: { onEvent: (e: RealtimeEnvelope) => void }) {
      mockRealtime.onEvent = handlers.onEvent;
    }
    start = async () => {};
    stop = async () => {};
  }
  return { ...actual, RealtimeConnection: FakeConnection };
});

const mockSignup = { mutate: jest.fn(), error: null as unknown, isPending: false, reset: jest.fn() };
jest.mock('@/features/auth/hooks', () => ({ ...jest.requireActual('@/features/auth/hooks'), useSignup: () => mockSignup }));

const RESTAURANT: TenantModule[] = ['MARMITA', 'PF', 'KILO', 'DAILY_MENU'];
const SNACK_BAR: TenantModule[] = ['A_LA_CARTE', 'DELIVERY', 'FIXED_MENU'];
const ALL_PERMISSIONS: Me['permissions'] = ['orders.view', 'orders.create', 'production.view', 'closing.view', 'menu.manage', 'catalog.manage', 'settings.manage', 'cash.open'];

/** `Me` do exemplo do 06 §6.3, com os módulos e as permissões do caso. */
function makeMe(modules: TenantModule[] | undefined, permissions: Me['permissions'] = ALL_PERMISSIONS): Me {
  const snack = modules?.includes('A_LA_CARTE') ?? false;
  const resto = modules === undefined || modules.includes('MARMITA');
  return {
    user: { id: 'u1', fullName: 'Ana Souza', email: null, phoneNumber: null },
    tenant: {
      id: 't1',
      name: snack && !resto ? 'Lanches do Beto' : 'Marmitaria da Ana',
      timeZone: 'America/Sao_Paulo',
      behaviors: [...(resto ? (['RESTAURANT'] as const) : []), ...(snack ? (['SNACK_BAR'] as const) : [])],
      labels: snack && !resto ? ['BURGER_JOINT', 'ACAI_SHOP'] : ['MARMITARIA'],
      ...(modules ? { modules } : {}),
    } as Me['tenant'],
    employee: { id: 'e1', displayName: 'Ana', roles: ['OWNER'], hasPin: true },
    permissions,
    memberships: [],
    subscription: { status: 'TRIALING', trialEndsAt: null, daysLeft: 7, canWrite: true },
    sharedDevice: null,
    serverTime: '2026-11-22T12:00:00Z',
  };
}

const json = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } });

const clients: QueryClient[] = [];
afterEach(() => {
  for (const c of clients.splice(0)) c.clear();
});

function newClient() {
  // `gcTime: 0` também nas mutações: o padrão (5 min) deixa um timer vivo, e o Jest esperava ~5 min para sair.
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false, gcTime: 0 } } });
  clients.push(client);
  return client;
}

async function renderWith(ui: ReactElement, client = newClient()) {
  return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
}

let routes: Record<string, (init: RequestInit) => Response>;
const requests: { method: string; path: string; headers: Record<string, string>; body: unknown }[] = [];

beforeEach(() => {
  memory.reset();
  resetSyncThrottle();
  requests.length = 0;
  routes = {};
  mockSignup.mutate.mockReset();
  mockSignup.error = null;
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

const titles = (me: Me) => visibleTabs(me).map((t) => t.title);

describe('abas pelos módulos e permissões (ADR-0055)', () => {
  it('restaurante: as abas de hoje', () => {
    expect(titles(makeMe(RESTAURANT))).toEqual(['Marmitas', 'Salão', 'Pedidos', 'Fechamento']);
  });

  it('lanchonete: Salão · Delivery · Cozinha · Fechamento', () => {
    expect(visibleTabs(makeMe(SNACK_BAR))).toEqual([
      { name: 'salao', title: 'Salão' },
      { name: 'delivery', title: 'Delivery' },
      { name: 'pedidos', title: 'Cozinha' },
      { name: 'fechamento', title: 'Fechamento' },
    ]);
  });

  it('os dois comportamentos: Marmitas · Salão · Delivery · Cozinha · Fechamento (Q-304)', () => {
    expect(titles(makeMe([...RESTAURANT, ...SNACK_BAR]))).toEqual(['Marmitas', 'Salão', 'Delivery', 'Cozinha', 'Fechamento']);
  });

  it('funcionário só com production.view vê só a Cozinha (ou Pedidos no restaurante)', () => {
    expect(titles(makeMe(SNACK_BAR, ['production.view']))).toEqual(['Cozinha']);
    expect(titles(makeMe(RESTAURANT, ['production.view']))).toEqual(['Pedidos']);
  });

  it('`me` guardado por um app anterior (sem `modules`) é tratado como restaurante', () => {
    expect(titles(makeMe(undefined))).toEqual(['Marmitas', 'Salão', 'Pedidos', 'Fechamento']);
  });

  it('sub-abas do Salão: PF e Quilo; só comandas; PF, Quilo e Comandas', () => {
    expect(salaoKinds(makeMe(RESTAURANT))).toEqual(['PF', 'KILO']);
    expect(salaoKinds(makeMe(SNACK_BAR))).toEqual(['A_LA_CARTE']);
    expect(salaoKinds(makeMe([...RESTAURANT, ...SNACK_BAR]))).toEqual(['PF', 'KILO', 'A_LA_CARTE']);
  });

  it('Salão da lanchonete (fatia 3): as comandas à la carte, sem sub-abas, com "Abrir comanda"', async () => {
    useSession.setState({ status: 'signedIn', me: makeMe(SNACK_BAR) });
    routes['GET /business-days/current'] = () => json(200, { id: 'bd1', version: 1, businessDate: '2026-11-22', status: 'OPEN' });
    routes['GET /orders'] = () => json(200, { items: [], nextCursor: null });
    await renderWith(<SalaoTab />);
    expect(await screen.findByText('Abrir comanda')).toBeTruthy();
    expect(screen.queryByText(/Disponível em breve/)).toBeNull();
    expect(screen.queryByText('PF')).toBeNull();
    expect(screen.queryByText('Nova comanda')).toBeNull();
    expect(screen.queryByText('Nova mesa')).toBeNull();
    // Só a modalidade da sub-aba: nunca mistura PF ou quilo com o cardápio fixo (ADR-0058 item 3).
    await waitFor(() => expect(requests.some((r) => r.path.startsWith('/orders?') && r.path.includes('orderType=A_LA_CARTE'))).toBe(true));
  });
});

describe('última aba lembrada (ADR-0055 item 5)', () => {
  it('mantém a aba que ainda existe', () => {
    expect(initialTab('pedidos', visibleTabs(makeMe(SNACK_BAR)))).toBe('pedidos');
    expect(initialTab('delivery', visibleTabs(makeMe(SNACK_BAR)))).toBe('delivery');
  });

  it('ignora a aba que não existe mais e cai na primeira visível', () => {
    expect(initialTab('marmitas', visibleTabs(makeMe(SNACK_BAR)))).toBe('salao');
    expect(initialTab('delivery', visibleTabs(makeMe(RESTAURANT)))).toBe('marmitas');
    expect(initialTab('marmitas', visibleTabs(makeMe(SNACK_BAR, ['production.view'])))).toBe('pedidos');
  });

  it('valor desconhecido ou vazio vai para a primeira; sem aba nenhuma, null', () => {
    expect(initialTab('comandas', visibleTabs(makeMe(RESTAURANT)))).toBe('marmitas');
    expect(initialTab(null, visibleTabs(makeMe(SNACK_BAR)))).toBe('salao');
    expect(initialTab('pedidos', [])).toBeNull();
  });
});

describe('menu lateral pelos módulos (ADR-0055 item 6)', () => {
  const labels = (me: Me) => menuGroups(me).flatMap((g) => g.entries.map((e) => e.label));

  it('lanchonete: sem cadastros do restaurante, com "Cardápio"', () => {
    const l = labels(makeMe(SNACK_BAR));
    for (const hidden of ['Cardápio do dia', 'Cardápio semanal', 'Histórico de cardápios', 'Tamanhos de marmita', 'Configuração de preços', 'Pratos e bebidas', 'Página de pedidos']) {
      expect(l).not.toContain(hidden);
    }
    expect(l).toContain('Cardápio');
    expect(l).toContain('Clientes');
    expect(l).toContain('Dados do estabelecimento');
  });

  it('restaurante: igual a hoje, sem o "Cardápio" fixo', () => {
    const l = labels(makeMe(RESTAURANT));
    for (const shown of ['Cardápio do dia', 'Cardápio semanal', 'Histórico de cardápios', 'Tamanhos de marmita', 'Configuração de preços', 'Pratos e bebidas', 'Página de pedidos']) {
      expect(l).toContain(shown);
    }
    expect(l).not.toContain('Cardápio');
  });
});

describe('rótulos (ADR-0054 itens 8 a 10)', () => {
  it('lista fixa com 3 do restaurante e 12 da lanchonete', () => {
    expect(LABELS).toHaveLength(15);
    expect(labelsFor(['RESTAURANT']).map((l) => l.text)).toEqual(['Restaurante', 'Marmitaria', 'Self-service/quilo']);
    expect(labelsFor(['SNACK_BAR'])).toHaveLength(12);
  });

  it('pelo menos um, todos do comportamento', () => {
    expect(labelsError([], ['SNACK_BAR'])).toBe('Marque pelo menos um rótulo.');
    expect(labelsError(['MARMITARIA'], ['SNACK_BAR'])).toBe('Marque só rótulos do tipo de estabelecimento escolhido.');
    expect(labelsError(['BURGER_JOINT', 'ACAI_SHOP'], ['SNACK_BAR'])).toBeNull();
    expect(labelsError(['MARMITARIA', 'BURGER_JOINT'], ['RESTAURANT', 'SNACK_BAR'])).toBeNull();
    expect(signupLabelsError('RESTAURANT', ['BURGER_JOINT'])).toMatch(/só rótulos/);
    expect(signupLabelsError('', [])).toBeNull();
  });

  it('marcar e desmarcar mantém a ordem da lista; trocar de tipo limpa', () => {
    expect(toggleLabel(['ACAI_SHOP'], 'BURGER_JOINT')).toEqual(['BURGER_JOINT', 'ACAI_SHOP']);
    expect(toggleLabel(['BURGER_JOINT', 'ACAI_SHOP'], 'BURGER_JOINT')).toEqual(['ACAI_SHOP']);
    expect(keepLabelsOf(['MARMITARIA'], ['SNACK_BAR'])).toEqual([]);
  });
});

describe('cadastro com tipo e rótulos (06 §6.3 `POST /auth/signup`)', () => {
  async function fillUntilEstablishment() {
    await renderWith(<SignupScreen />);
    await fireEvent.changeText(screen.getByLabelText('Seu nome'), 'Beto Lima');
    await fireEvent.changeText(screen.getByLabelText('Celular com DDD'), '34999990002');
    await fireEvent.changeText(screen.getByLabelText('Senha'), 'S3nha-forte!');
    await fireEvent.press(screen.getByText('Continuar'));
    await screen.findByLabelText('Nome do estabelecimento');
    await fireEvent.changeText(screen.getByLabelText('Nome do estabelecimento'), 'Lanches do Beto');
    await fireEvent.changeText(screen.getByLabelText('CPF ou CNPJ'), '11222333000181');
    await fireEvent.changeText(screen.getByLabelText('Cidade'), 'Uberlândia');
    await fireEvent.changeText(screen.getByLabelText('UF'), 'MG');
  }

  it('exige o tipo (sem padrão) e pelo menos um rótulo, e só mostra os rótulos do tipo escolhido', async () => {
    await fillUntilEstablishment();
    expect(screen.getByLabelText('Restaurante').props.accessibilityState.checked).toBe(false);
    expect(screen.getByLabelText('Lanchonete').props.accessibilityState.checked).toBe(false);
    expect(screen.queryByText('Hamburgueria')).toBeNull();

    await fireEvent.press(screen.getByText('Continuar'));
    expect(await screen.findByText('Escolha o tipo de estabelecimento.')).toBeTruthy();

    await fireEvent.press(screen.getByLabelText('Restaurante'));
    expect(await screen.findByText('Marmitaria')).toBeTruthy();
    expect(screen.queryByText('Hamburgueria')).toBeNull();
    await fireEvent.press(screen.getByText('Marmitaria'));

    // Trocar para lanchonete limpa os rótulos do restaurante.
    await fireEvent.press(screen.getByLabelText('Lanchonete'));
    expect(await screen.findByText('Hamburgueria')).toBeTruthy();
    expect(screen.queryByText('Marmitaria')).toBeNull();
    await fireEvent.press(screen.getByText('Continuar'));
    expect(await screen.findByText('Marque pelo menos um rótulo.')).toBeTruthy();
    expect(screen.queryByText('Li e aceito os termos')).toBeNull();
  });

  it('envia `tenant.behavior` e `tenant.labels` no corpo', async () => {
    await fillUntilEstablishment();
    await fireEvent.press(screen.getByLabelText('Lanchonete'));
    await fireEvent.press(await screen.findByText('Açaiteria'));
    await fireEvent.press(screen.getByText('Hamburgueria'));
    await fireEvent.press(screen.getByText('Continuar'));
    await fireEvent(await screen.findByLabelText('Aceito os termos'), 'valueChange', true);
    await fireEvent.press(screen.getByText('Criar estabelecimento'));
    await waitFor(() => expect(mockSignup.mutate).toHaveBeenCalled());
    const body = mockSignup.mutate.mock.calls[0][0];
    expect(body.tenant).toMatchObject({ name: 'Lanches do Beto', documentType: 'CNPJ', behavior: 'SNACK_BAR', labels: ['BURGER_JOINT', 'ACAI_SHOP'] });
  });
});

describe('"Dados do estabelecimento": tipo só leitura e troca de rótulos (06 §6.4)', () => {
  const tenant: Tenant = {
    id: 't1',
    version: 3,
    name: 'Lanches do Beto',
    documentType: 'CNPJ',
    documentNumber: '11222333000181',
    city: 'Uberlândia',
    state: 'MG',
    timeZone: 'America/Sao_Paulo',
    behaviors: ['SNACK_BAR'],
    labels: ['BURGER_JOINT', 'ACAI_SHOP'],
    createdAt: '2026-10-27T09:30:00Z',
  };

  beforeEach(() => {
    routes['GET /tenant'] = () => json(200, tenant, { ETag: '"3"' });
  });

  it('proprietário troca os rótulos com If-Match; o tipo não tem botão de troca', async () => {
    useSession.setState({ status: 'signedIn', readOnly: false, me: makeMe(SNACK_BAR) });
    routes['PATCH /tenant'] = (init) => json(200, { ...tenant, version: 4, labels: JSON.parse(init.body as string).labels ?? tenant.labels }, { ETag: '"4"' });
    await renderWith(<TenantScreen />);

    expect(await screen.findByLabelText('Tipo de estabelecimento: Lanchonete')).toBeTruthy();
    expect(screen.queryByText('Marmitaria')).toBeNull();
    expect(screen.queryByText(/trocar o tipo|contratar/i)).toBeNull();

    await fireEvent.press(screen.getByText('Açaiteria'));
    await fireEvent.press(screen.getByText('Pastelaria'));
    await fireEvent.press(screen.getByText('Salvar'));
    await waitFor(() => expect(requests.some((r) => r.method === 'PATCH')).toBe(true));
    const patch = requests.find((r) => r.method === 'PATCH')!;
    expect(patch.path).toBe('/tenant');
    expect(patch.headers['If-Match']).toBe('"3"');
    expect(patch.body).toMatchObject({ labels: ['BURGER_JOINT', 'PASTEL_SHOP'] });
  });

  it('sem rótulo marcado não salva', async () => {
    useSession.setState({ status: 'signedIn', readOnly: false, me: makeMe(SNACK_BAR) });
    await renderWith(<TenantScreen />);
    await fireEvent.press(await screen.findByText('Açaiteria'));
    await fireEvent.press(screen.getByText('Hamburgueria'));
    expect(screen.getByText('Marque pelo menos um rótulo.')).toBeTruthy();
    await fireEvent.press(screen.getByText('Salvar'));
    expect(requests.some((r) => r.method === 'PATCH')).toBe(false);
  });

  it('sem settings.manage: rótulos só para ler, sem "Salvar"', async () => {
    useSession.setState({ status: 'signedIn', readOnly: false, me: makeMe(SNACK_BAR, ['orders.view']) });
    await renderWith(<TenantScreen />);
    expect(await screen.findByText('Hamburgueria, Açaiteria')).toBeTruthy();
    expect(screen.queryByText('Salvar')).toBeNull();
  });

  it('409 VersionConflict: avisa e não sobrescreve', async () => {
    useSession.setState({ status: 'signedIn', readOnly: false, me: makeMe(SNACK_BAR) });
    routes['PATCH /tenant'] = () =>
      new Response(JSON.stringify({ status: 409, errorCode: 'VersionConflict', title: 'Conflito', current: { ...tenant, version: 4 } }), {
        status: 409,
        headers: { 'Content-Type': 'application/problem+json' },
      });
    await renderWith(<TenantScreen />);
    await fireEvent.press(await screen.findByText('Pastelaria'));
    await fireEvent.press(screen.getByText('Salvar'));
    expect(await screen.findByText(/Outra pessoa alterou os dados do estabelecimento/)).toBeTruthy();
    expect(requests.filter((r) => r.method === 'PATCH')).toHaveLength(1);
  });
});

describe('mudança de módulos: recarrega o `me` e as abas (06 §6.12, §6.14)', () => {
  function Runtime() {
    useAppRuntime();
    useMeSync();
    return null;
  }

  it('TenantModulesChanged → GET /auth/me → abas da lanchonete somadas', async () => {
    let me = makeMe(RESTAURANT);
    routes['GET /auth/me'] = () => json(200, me);
    routes['GET /sync/changes'] = () => json(200, { changes: [], lastSeq: 0, hasMore: false });
    useSession.setState({ status: 'signedIn', readOnly: false, me });
    await renderWith(<Runtime />);
    await waitFor(() => expect(mockRealtime.onEvent).toBeDefined());
    await waitFor(() => expect(requests.some((r) => r.path === '/auth/me')).toBe(true));
    expect(titles(useSession.getState().me!)).toEqual(['Marmitas', 'Salão', 'Pedidos', 'Fechamento']);

    me = makeMe([...RESTAURANT, ...SNACK_BAR]);
    mockRealtime.onEvent!({
      eventId: 'ev-1',
      eventType: 'TenantModulesChanged',
      seq: null,
      occurredAt: '2026-11-22T12:00:00Z',
      entityType: 'TENANT',
      entityId: 't1',
      entityVersion: 5,
      actorEmployeeId: null,
      data: { behaviors: ['RESTAURANT', 'SNACK_BAR'], modules: me.tenant.modules },
    });
    await waitFor(() => expect(titles(useSession.getState().me!)).toEqual(['Marmitas', 'Salão', 'Delivery', 'Cozinha', 'Fechamento']));
  });

  it('entrada TENANT no feed invalida o `me` e o tenant', async () => {
    expect(keysForEntity('TENANT', 't1')).toEqual([['me'], ['tenant']]);
    memory.meta.set('lastSeq:t1', '10');
    routes['GET /sync/changes'] = () => json(200, { changes: [{ seq: 11, entityType: 'TENANT', entityId: 't1', entityVersion: 5, changeKind: 'UPSERT' }], lastSeq: 11, hasMore: false });
    const client = newClient();
    const invalidate = jest.spyOn(client, 'invalidateQueries');
    await pullChanges({ queryClient: client, tenantId: 't1', ownEmployeeId: null });
    const keys = invalidate.mock.calls.map((c) => c[0]?.queryKey);
    expect(keys).toContainEqual(['me']);
    expect(keys).toContainEqual(['tenant']);
  });
});

describe('403 ModuleNotEnabled', () => {
  it('mensagem pt-BR pelo módulo da extensão', () => {
    const e = (module?: string) => new ApiError(403, { status: 403, errorCode: 'ModuleNotEnabled', title: 'Este recurso não está habilitado no estabelecimento.', ...(module ? { module } : {}) });
    expect(errorMessage(e('PF'))).toBe('PF não está habilitado neste estabelecimento.');
    expect(errorMessage(e('DAILY_MENU'))).toBe('Cardápio do dia não está habilitado neste estabelecimento.');
    expect(errorMessage(e())).toBe('Este recurso não está habilitado no estabelecimento.');
  });

  it('o cliente avisa para recarregar o `me`', async () => {
    const onModuleNotEnabled = jest.fn();
    configureApiClient({ getAccessToken: () => 'tok', refresh: async () => true, onSessionExpired: () => {}, onUpgradeRequired: () => {}, onSubscriptionInactive: () => {}, onModuleNotEnabled });
    routes['POST /orders'] = () =>
      new Response(JSON.stringify({ status: 403, errorCode: 'ModuleNotEnabled', module: 'PF' }), { status: 403, headers: { 'Content-Type': 'application/problem+json' } });
    await expect(apiRequest('POST', '/orders', { body: {} })).rejects.toBeInstanceOf(ApiError);
    expect(onModuleNotEnabled).toHaveBeenCalledTimes(1);
  });
});
