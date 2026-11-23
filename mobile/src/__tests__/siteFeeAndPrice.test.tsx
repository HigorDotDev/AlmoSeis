/**
 * Decisões do usuário de 2026-11-23 (ADR-0057 complementado, 06 §6.5 e §6.11):
 * - "Vendido pelo site" sem a taxa e a nova linha "Taxa de entrega do site" no resumo, no fechamento e no mensal (M1);
 * - bebida do restaurante com preço de site `0,00` (I6);
 * - o formulário de tamanho de marmita sempre envia `sellsOnSite`.
 *
 * Valores do exemplo do ADR-0057 (M1): retirada do site R$ 40,00 + entrega do site R$ 45,00 (R$ 5,00 de taxa) + PF do
 * app R$ 48,00 → vendido R$ 133,00; "Vendido pelo site" R$ 80,00; "Taxa de entrega do site" R$ 5,00.
 */
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import type { ReactElement } from 'react';

import { configureApiClient } from '@/api/client';
import type { ClosingSummary, MarmitaSize, Me, MonthlyClosing, Product } from '@/api/types';
import { ProductFormScreen, SizesScreen } from '@/features/catalog/screens/CatalogScreens';
import { ClosingSummaryView } from '@/features/closing/components';
import { MonthlyScreen } from '@/features/closing/screens/ClosingScreens';
import { useConnectivity } from '@/stores/connectivity';
import { useSession } from '@/stores/session';
import { useToast } from '@/stores/toast';

let mockParams: Record<string, string> = {};
jest.mock('expo-router', () => ({
  router: { push: jest.fn(), back: jest.fn(), replace: jest.fn() },
  Stack: { Screen: () => null },
  useLocalSearchParams: () => mockParams,
}));

const T = '2026-11-23T09:00:00Z';

const water: Product = {
  id: 'p1',
  version: 2,
  kind: 'BEVERAGE',
  name: 'Água da casa',
  description: null,
  category: null,
  defaultPrice: '3.00',
  requiresPreparation: false,
  imageUrl: null,
  isActive: true,
  isAvailable: true,
  isVisibleOnPage: true,
  sitePrice: '3.00',
  createdAt: T,
  updatedAt: T,
};

const summary: ClosingSummary = {
  businessDay: { id: 'bd1', version: 1, businessDate: '2026-11-23', status: 'OPEN' },
  isFinal: false,
  dailyClosingId: null,
  computedAt: T,
  orderCount: 3,
  totalSold: '133.00',
  deliveryFeesTotal: '5.00',
  siteSoldTotal: '80.00',
  siteDeliveryFeeTotal: '5.00',
  totalReceived: '133.00',
  receivedFromPreviousDays: '0.00',
  totalPending: '0.00',
  canceled: { count: 0, total: '0.00' },
  reversed: { count: 0, total: '0.00' },
  webOrders: { awaitingAcceptanceCount: 0, autoRejectedCount: 0, notAcceptedCount: 0 },
  unmarkedDeliveryCount: 0,
  byOrderType: [
    { orderType: 'MARMITA', count: 2, sold: '85.00', received: '85.00', pending: '0.00', reversed: '0.00' },
    { orderType: 'PF', count: 1, sold: '48.00', received: '48.00', pending: '0.00', reversed: '0.00' },
    { orderType: 'KILO', count: 0, sold: '0.00', received: '0.00', pending: '0.00', reversed: '0.00' },
    { orderType: 'A_LA_CARTE', count: 0, sold: '0.00', received: '0.00', pending: '0.00', reversed: '0.00' },
  ],
  byPaymentMethod: [{ paymentMethod: 'PIX', count: 3, received: '133.00', reversed: '0.00' }],
  cash: { openingBalance: '100.00', cashReceived: '0.00', cashReversed: '0.00', supplies: '0.00', withdrawals: '0.00', expectedCash: '100.00', countedCash: null, cashDifference: null, justification: null },
  adjustments: [],
  closedAt: null,
  closedBy: null,
};

/** Exemplo literal do 06 §6.11 `GET /closing/monthly`. */
const monthly: MonthlyClosing = {
  month: '2026-10',
  closingCount: 26,
  totalSold: '91250.00',
  totalReceived: '88940.00',
  totalPendingAtClosings: '4120.00',
  cashDifferenceTotal: '-37.50',
  siteSoldTotal: '1840.00',
  siteDeliveryFeeTotal: '215.00',
  byOrderType: [
    { orderType: 'MARMITA', sold: '31250.00', received: '30100.00' },
    { orderType: 'PF', sold: '21750.00', received: '21050.00' },
    { orderType: 'KILO', sold: '38250.00', received: '37790.00' },
    { orderType: 'A_LA_CARTE', sold: '0.00', received: '0.00' },
  ],
  byPaymentMethod: [
    { paymentMethod: 'CASH', received: '21250.00' },
    { paymentMethod: 'PIX', received: '45390.00' },
    { paymentMethod: 'CARD', received: '22300.00' },
  ],
  days: [{ businessDate: '2026-10-27', dailyClosingIds: ['0192d6a0-0000-7000-8000-000000000a01'], totalSold: '3650.00', totalReceived: '3430.00' }],
};

const json = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } });

let writeHandler: (body: unknown) => Response;
let sizes: MarmitaSize[];
const writes: { method: string; path: string; headers: Record<string, string>; body: Record<string, unknown> }[] = [];
const reads: string[] = [];

beforeEach(() => {
  mockParams = {};
  writes.length = 0;
  reads.length = 0;
  sizes = [];
  writeHandler = () => json(200, water);
  useToast.setState({ toasts: [] });
  useConnectivity.setState({ online: true });
  useSession.setState({
    status: 'signedIn',
    readOnly: false,
    me: { tenant: { id: 't1', name: 'Marmitaria', timeZone: 'America/Sao_Paulo' }, employee: { id: 'e1', displayName: 'Ana', roles: ['OWNER'] }, permissions: ['orders.view', 'catalog.manage', 'closing.view'] } as unknown as Me,
  });
  configureApiClient({ getAccessToken: () => 'tok', refresh: async () => true, onSessionExpired: () => {}, onUpgradeRequired: () => {}, onSubscriptionInactive: () => {} });
  global.fetch = jest.fn(async (url: string, init: RequestInit = {}) => {
    const path = url.replace(/^https?:\/\/[^/]+(\/api\/v1)?/, '');
    const method = init.method ?? 'GET';
    if (method !== 'GET') {
      const body = init.body ? JSON.parse(init.body as string) : undefined;
      writes.push({ method, path, headers: init.headers as Record<string, string>, body });
      return writeHandler(body);
    }
    reads.push(path);
    if (path === '/products/p1') return json(200, water, { ETag: '"2"' });
    if (path.startsWith('/marmita-sizes')) return json(200, { items: sizes });
    if (path.startsWith('/closing/monthly')) return json(200, monthly);
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

async function save() {
  await act(async () => {
    await fireEvent.press(screen.getByText('Salvar'));
  });
}

describe('"Vendido pelo site" sem a taxa e "Taxa de entrega do site" (M1)', () => {
  it('resumo e fechamento (ClosingSummaryView): as duas linhas, já no total', async () => {
    await renderWith(<ClosingSummaryView summary={summary} />);
    expect(screen.getByText('Vendido pelo site (já no total)')).toBeTruthy();
    expect(screen.getByText('R$ 80,00')).toBeTruthy();
    expect(screen.getByText('Taxa de entrega do site (já no total)')).toBeTruthy();
    expect(screen.getByText('Inclui R$ 5,00 de taxas de entrega.')).toBeTruthy();
  });

  it('resposta guardada por um app anterior, sem os campos: nenhuma das linhas', async () => {
    const { siteSoldTotal: _a, siteDeliveryFeeTotal: _b, ...old } = summary;
    await renderWith(<ClosingSummaryView summary={old} />);
    expect(screen.queryByText(/pelo site|do site/)).toBeNull();
  });

  it('mensal: as somas do mês', async () => {
    await renderWith(<MonthlyScreen />);
    expect(await screen.findByText('Vendido pelo site (já no total)')).toBeTruthy();
    expect(screen.getByText('R$ 1.840,00')).toBeTruthy();
    expect(screen.getByText('Taxa de entrega do site (já no total)')).toBeTruthy();
    expect(screen.getByText('R$ 215,00')).toBeTruthy();
  });
});

describe('bebida do restaurante com preço de site 0,00 (ADR-0057 item 2 complementado, I6)', () => {
  it('edição: preço de site 0,00 vai no PATCH como "0.00"', async () => {
    mockParams = { id: 'p1' };
    writeHandler = () => json(200, { ...water, version: 3, sitePrice: '0.00' });
    await renderWith(<ProductFormScreen />);
    await fireEvent.changeText(await screen.findByLabelText('Preço no site'), '0');
    expect(screen.getByText('Pode ser maior, menor ou igual ao do balcão, ou R$ 0,00 (cortesia no site).')).toBeTruthy();
    await save();
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]).toMatchObject({ method: 'PATCH', path: '/products/p1', body: { sitePrice: '0.00' } });
    expect(writes[0]!.headers['If-Match']).toBe('"2"');
  });

  it('edição sem mexer no preço de site: o campo não vai', async () => {
    mockParams = { id: 'p1' };
    await renderWith(<ProductFormScreen />);
    await fireEvent.changeText(await screen.findByLabelText('Nome'), 'Água da casa 500 ml');
    await save();
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]!.body).not.toHaveProperty('sitePrice');
  });

  it('esconder e mostrar de novo mantém o 0,00 (não volta para o de balcão)', async () => {
    mockParams = { id: 'p1' };
    await renderWith(<ProductFormScreen />);
    await fireEvent.changeText(await screen.findByLabelText('Preço no site'), '0');
    await fireEvent(screen.getByLabelText('Vende no site'), 'valueChange', false);
    await fireEvent(screen.getByLabelText('Vende no site'), 'valueChange', true);
    expect(screen.getByLabelText('Preço no site').props.value).toBe('R$ 0,00');
  });

  it('cadastro: o campo começa com o preço do balcão (sem mexer, não vai); 0,00 vai como "0.00"', async () => {
    mockParams = { id: 'new', kind: 'BEVERAGE' };
    writeHandler = () => json(201, { ...water, id: 'p9' });
    await renderWith(<ProductFormScreen />);
    await fireEvent.changeText(screen.getByLabelText('Nome'), 'Cafezinho');
    await fireEvent.changeText(screen.getByLabelText('Preço'), '300');
    expect(screen.getByLabelText('Preço no site').props.value).toBe('R$ 3,00');
    await save();
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]).toMatchObject({ method: 'POST', path: '/products', body: { kind: 'BEVERAGE', defaultPrice: '3.00' } });
    expect(writes[0]!.body).not.toHaveProperty('sitePrice');
  });

  it('cadastro com preço de site 0,00', async () => {
    mockParams = { id: 'new', kind: 'BEVERAGE' };
    writeHandler = () => json(201, { ...water, id: 'p9', sitePrice: '0.00' });
    await renderWith(<ProductFormScreen />);
    await fireEvent.changeText(screen.getByLabelText('Nome'), 'Cafezinho');
    await fireEvent.changeText(screen.getByLabelText('Preço'), '300');
    await fireEvent.changeText(screen.getByLabelText('Preço no site'), '0');
    await save();
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]!.body).toMatchObject({ kind: 'BEVERAGE', defaultPrice: '3.00', sitePrice: '0.00' });
  });
});

describe('tamanho de marmita: o formulário sempre envia sellsOnSite (ADR-0057 item 2 complementado)', () => {
  it('novo tamanho sem ligar a chave: POST com sellsOnSite false', async () => {
    writeHandler = () => json(201, { id: 's9', version: 1, name: 'G', price: '25.00', sortOrder: 1, isActive: true, sellsOnSite: false, sitePrice: null });
    await renderWith(<SizesScreen />);
    await fireEvent.press(await screen.findByText('Novo tamanho'));
    await fireEvent.changeText(screen.getByLabelText('Nome (P, M, G…)'), 'G');
    await fireEvent.changeText(screen.getByLabelText('Preço'), '2500');
    await act(async () => {
      await fireEvent.press(screen.getByText('Salvar tamanho'));
    });
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]).toMatchObject({ method: 'POST', path: '/marmita-sizes', body: { name: 'G', price: '25.00', sellsOnSite: false, sitePrice: null } });
    expect(writes[0]!.body).toHaveProperty('sellsOnSite', false);
  });

  it('ligando a chave: sellsOnSite true e o preço de site', async () => {
    writeHandler = () => json(201, { id: 's9', version: 1, name: 'G', price: '25.00', sortOrder: 1, isActive: true, sellsOnSite: true, sitePrice: '27.00' });
    await renderWith(<SizesScreen />);
    await fireEvent.press(await screen.findByText('Novo tamanho'));
    await fireEvent.changeText(screen.getByLabelText('Nome (P, M, G…)'), 'G');
    await fireEvent.changeText(screen.getByLabelText('Preço'), '2500');
    await fireEvent(screen.getByLabelText('Vende no site'), 'valueChange', true);
    await fireEvent.changeText(screen.getByLabelText('Preço no site'), '2700');
    await act(async () => {
      await fireEvent.press(screen.getByText('Salvar tamanho'));
    });
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]!.body).toMatchObject({ sellsOnSite: true, sitePrice: '27.00' });
  });
});
