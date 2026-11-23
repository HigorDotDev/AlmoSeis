/**
 * Fatia 1 do EP-002 (ADR-0046): faixa "Novos do site", aceite [I][M], recusa [M], conflito "Já aceito por …",
 * expiração, resultado desconhecido e ações de atendimento. Não há página pública ainda: os pedidos são dados de teste.
 */
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import type { ReactElement } from 'react';

import { configureApiClient } from '@/api/client';
import type { ClosingSummary, Me, Order, OrderSummary } from '@/api/types';
import { ClosingSummaryView } from '@/features/closing/components';
import { OrderCard } from '@/features/orders/components';
import { FulfillmentActions, NewWebOrdersSection } from '@/features/orders/webOrderComponents';
import { useConnectivity } from '@/stores/connectivity';
import { usePendingOps } from '@/stores/pendingOps';
import { useSession } from '@/stores/session';
import { useToast } from '@/stores/toast';

import { memory } from '@/test-utils/fakeDb';

jest.mock('expo-router', () => ({ router: { push: jest.fn() } }));
jest.mock('@/db/pendingOpsRepo', () => ({ pendingOpsRepo: jest.requireActual('@/test-utils/fakeDb').pendingOpsRepo }));
jest.mock('@/db/draftsRepo', () => ({ draftsRepo: jest.requireActual('@/test-utils/fakeDb').draftsRepo }));
jest.mock('@/db/metaRepo', () => ({ metaRepo: jest.requireActual('@/test-utils/fakeDb').metaRepo }));

const ID = '0192d6a0-0000-7000-8000-000000000504';

const summary: OrderSummary = {
  id: ID,
  version: 1,
  orderNumber: null,
  businessDate: '2026-10-27',
  orderType: 'MARMITA',
  origin: 'WEB',
  fulfillmentType: 'DELIVERY',
  customerName: 'Carla Souza',
  tableLabel: null,
  tabNumber: null,
  totalAmount: '45.00',
  paidAmount: '0.00',
  balance: '45.00',
  isPaid: false,
  productionStatus: null,
  cancellationStatus: 'ACTIVE',
  acceptanceStatus: 'AWAITING_ACCEPTANCE',
  // Longe no futuro: o card não fica "esgotado" durante o teste.
  acceptanceDeadline: new Date(Date.now() + 9 * 60_000).toISOString(),
  fulfillmentStatus: null,
  isReadyForPickup: false,
  itemCount: 1,
  pendingAlertCount: 0,
  createdAt: '2026-10-27T12:00:00Z',
};

/** Exemplo literal do 06 §6.9 "Pedido do site". */
const webOrder: Order = {
  id: ID,
  version: 1,
  orderNumber: null,
  businessDayId: '0192d6a0-0000-7000-8000-000000000401',
  businessDate: '2026-10-27',
  orderType: 'MARMITA',
  origin: 'WEB',
  fulfillmentType: 'DELIVERY',
  customer: { id: null, name: 'Carla Souza', phoneNumber: '+5534988887777' },
  deliveryAddress: { street: 'Rua das Palmeiras', number: '45', complement: 'Casa 2', neighborhood: 'Centro', reference: null },
  tableLabel: null,
  tabNumber: null,
  notes: null,
  expectedPaymentMethod: 'CASH',
  changeFor: '50.00',
  riskFlag: null,
  deliveryFee: '5.00',
  deliveryFeeSource: 'FIXED_DEFAULT',
  deliveryDistanceKm: null,
  itemsTotal: '40.00',
  weighingsTotal: '0.00',
  totalAmount: '45.00',
  paidAmount: '0.00',
  balance: '45.00',
  isPaid: false,
  productionStatus: null,
  productionStatusChangedAt: null,
  cancellationStatus: 'ACTIVE',
  cancellation: null,
  acceptanceStatus: 'AWAITING_ACCEPTANCE',
  acceptanceDeadline: summary.acceptanceDeadline,
  acceptedAt: null,
  acceptedBy: null,
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
    {
      id: '0192d6a0-0000-7000-8000-000000000641',
      itemKind: 'MARMITA',
      marmitaSizeId: '0192d6a0-0000-7000-8000-000000000202',
      productId: null,
      name: 'Marmita M',
      composition: 'Frango grelhado · Arroz, Feijão',
      components: [],
      quantity: 2,
      unitPrice: '20.00',
      lineTotal: '40.00',
      requiresPreparation: true,
      notes: null,
      addons: [],
      createdAt: '2026-10-27T12:00:00Z',
      createdBy: null,
    },
  ],
  weighings: [],
  payments: [],
  pendingAlertCount: 0,
  createdAt: '2026-10-27T12:00:00Z',
  createdBy: null,
  updatedAt: '2026-10-27T12:00:00Z',
};

const accepted: Order = {
  ...webOrder,
  version: 2,
  orderNumber: 15,
  customer: { id: '0192d6a0-0000-7000-8000-000000000802', name: 'Carla Souza', phoneNumber: '+5534988887777' },
  productionStatus: 'OPEN',
  acceptanceStatus: 'ACCEPTED',
  acceptedAt: '2026-10-27T12:03:00Z',
  acceptedBy: { id: '0192d6a0-0000-7000-8000-000000000302', displayName: 'Bruno' },
  estimatedReadyMinutes: 30,
  fulfillmentStatus: 'PENDING',
};

const json = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } });
const problem = (status: number, errorCode: string, ext: Record<string, unknown> = {}) =>
  new Response(JSON.stringify({ status, errorCode, title: errorCode, ...ext }), { status, headers: { 'Content-Type': 'application/problem+json' } });

type Handler = (method: string, path: string, init: RequestInit) => Response | Promise<Response> | undefined;
let writeHandler: Handler;
const writes: { method: string; path: string; headers: Record<string, string>; body: unknown }[] = [];

function signIn(permissions: Me['permissions']) {
  useSession.setState({
    status: 'signedIn',
    readOnly: false,
    me: {
      tenant: { id: 't1', name: 'Marmitaria', timeZone: 'America/Sao_Paulo' },
      employee: { id: 'e1', displayName: 'Ana', roles: ['OWNER'], hasPin: false },
      permissions,
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
  writeHandler = () => undefined;
  configureApiClient({
    getAccessToken: () => 'tok',
    refresh: async () => true,
    onSessionExpired: () => {},
    onUpgradeRequired: () => {},
    onSubscriptionInactive: () => {},
  });
  global.fetch = jest.fn(async (url: string, init: RequestInit) => {
    const path = url.replace(/^https?:\/\/[^/]+(\/api\/v1)?/, '');
    const method = init.method ?? 'GET';
    if (method !== 'GET') {
      writes.push({ method, path, headers: init.headers as Record<string, string>, body: init.body ? JSON.parse(init.body as string) : undefined });
      const r = await writeHandler(method, path, init);
      if (r) return r;
    }
    if (path.startsWith('/business-days/current')) return json(200, { id: 'bd1', businessDate: '2026-10-27', status: 'OPEN', version: 1 });
    if (path.startsWith('/orders?') && path.includes('acceptanceStatus=AWAITING_ACCEPTANCE')) return json(200, { items: [summary], nextCursor: null });
    if (path === `/orders/${ID}`) return json(200, webOrder, { ETag: '"1"' });
    return json(404, { errorCode: 'NotFound' });
  }) as unknown as typeof fetch;
});

const clients: QueryClient[] = [];

afterEach(() => {
  // Sem timers vivos depois do teste (refetchInterval da faixa).
  for (const c of clients.splice(0)) c.clear();
});

async function renderWith(ui: ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false, gcTime: 0 } } });
  clients.push(client);
  return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
}

const toasts = () => useToast.getState().toasts.map((t) => t.message);

describe('faixa "Novos do site" (ADR-0046 item 21)', () => {
  it('mostra a contagem, o prazo, os itens, o endereço e o telefone não verificado', async () => {
    signIn(['orders.view', 'orders.accept']);
    await renderWith(<NewWebOrdersSection />);
    expect(await screen.findByText('Novos do site (1)')).toBeTruthy();
    expect(screen.getByText('Carla Souza')).toBeTruthy();
    expect(screen.getByText('R$ 45,00')).toBeTruthy();
    expect(screen.getByText('Site')).toBeTruthy();
    expect(screen.getByText(/^Aceite em \d:\d\d$/)).toBeTruthy();
    expect(await screen.findByText('2× Marmita M')).toBeTruthy();
    expect(screen.getByText(/Rua das Palmeiras, 45, Casa 2, Centro/)).toBeTruthy();
    expect(screen.getByText(/telefone não verificado/)).toBeTruthy();
    expect(screen.getByText('Vai pagar em dinheiro, troco para R$ 50,00')).toBeTruthy();
    expect(screen.queryByText('Verificação anti-robô indisponível no envio')).toBeNull();
    expect(screen.getByText('Aceitar')).toBeTruthy();
    expect(screen.getByText('Recusar')).toBeTruthy();
  });

  it('sem orders.accept (cozinha) vê o pedido, mas não os botões', async () => {
    signIn(['orders.view', 'production.view']);
    await renderWith(<NewWebOrdersSection />);
    expect(await screen.findByText('Novos do site (1)')).toBeTruthy();
    expect(screen.queryByText('Aceitar')).toBeNull();
    expect(screen.getByText('Aguardando o balcão aceitar.')).toBeTruthy();
  });

  it('recolhida na cozinha: só a contagem até tocar em "Ver"', async () => {
    signIn(['orders.view']);
    await renderWith(<NewWebOrdersSection collapsible />);
    expect(await screen.findByText('Novos do site (1)')).toBeTruthy();
    expect(screen.queryByText('Carla Souza')).toBeNull();
    await fireEvent.press(screen.getByText('Ver'));
    expect(screen.getByText('Carla Souza')).toBeTruthy();
  });
});

describe('aceite [I][M] (ADR-0046 itens 6–9)', () => {
  async function openAccept() {
    signIn(['orders.view', 'orders.accept']);
    await renderWith(<NewWebOrdersSection />);
    await screen.findByText('2× Marmita M');
    await fireEvent.press(screen.getByText('Aceitar'));
    expect(screen.getByText('Aceitar pedido do site?')).toBeTruthy();
  }

  it('envia If-Match, Idempotency-Key e o tempo estimado escolhido', async () => {
    writeHandler = () => json(200, accepted, { ETag: '"2"' });
    await openAccept();
    await fireEvent.press(screen.getByText('30 min'));
    await act(async () => {
      await fireEvent.press(screen.getByText('Aceitar pedido'));
    });
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]).toMatchObject({ method: 'POST', path: `/orders/${ID}/accept`, body: { estimatedReadyMinutes: 30 } });
    expect(writes[0]!.headers['If-Match']).toBe('"1"');
    expect(writes[0]!.headers['Idempotency-Key']).toMatch(/[0-9a-f-]{36}/);
    await waitFor(() => expect(toasts()).toContain('Pedido do site aceito: #15'));
    expect(memory.ops.size).toBe(0);
  });

  it('409 VersionConflict: "Já aceito por Bruno", sem sobrescrever', async () => {
    writeHandler = () => problem(409, 'VersionConflict', { current: accepted });
    await openAccept();
    await act(async () => {
      await fireEvent.press(screen.getByText('Aceitar pedido'));
    });
    await waitFor(() => expect(toasts()).toContain('Já aceito por Bruno.'));
    expect(writes).toHaveLength(1);
    expect(screen.queryByText('Aceitar pedido do site?')).toBeNull();
  });

  it('409 WebOrderExpired: "O prazo acabou, o pedido expirou"', async () => {
    writeHandler = () => problem(409, 'WebOrderExpired');
    await openAccept();
    await act(async () => {
      await fireEvent.press(screen.getByText('Aceitar pedido'));
    });
    await waitFor(() => expect(toasts()).toContain('O prazo acabou, o pedido expirou.'));
    expect(memory.ops.size).toBe(0);
  });

  it('sem resposta: resultado desconhecido, pendência guardada e nenhum reenvio ao confirmar de novo', async () => {
    writeHandler = () => {
      throw new TypeError('Network request failed');
    };
    await openAccept();
    await act(async () => {
      await fireEvent.press(screen.getByText('Aceitar pedido'));
    });
    // Ao ficar desconhecido, o hook consulta GET /operations/{key} (aqui sem resposta útil): nunca reenvia.
    await waitFor(() => expect(screen.getByText(/Verificando se o aceite entrou/)).toBeTruthy());
    expect([...memory.ops.values()][0]).toMatchObject({ status: 'UNKNOWN', path: `/orders/${ID}/accept` });
    await act(async () => {
      await fireEvent.press(screen.getByText('Aceitar pedido'));
    });
    expect(writes.filter((w) => w.path.endsWith('/accept'))).toHaveLength(1);
  });
});

describe('recusa [M] (ADR-0046 item 12)', () => {
  it('motivo da lista, texto obrigatório em "Outro", aviso ao cliente e If-Match sem Idempotency-Key', async () => {
    writeHandler = () => json(200, { ...webOrder, version: 2, acceptanceStatus: 'REJECTED' }, { ETag: '"2"' });
    signIn(['orders.view', 'orders.accept']);
    await renderWith(<NewWebOrdersSection />);
    await screen.findByText('2× Marmita M');
    await fireEvent.press(screen.getByText('Recusar'));
    expect(screen.getByText('O cliente verá esta mensagem.')).toBeTruthy();

    // Sem motivo: não envia.
    await fireEvent.press(screen.getByText('Recusar pedido'));
    expect(screen.getByText('Escolha o motivo.')).toBeTruthy();
    expect(writes).toHaveLength(0);

    // "Outro" sem texto: não envia.
    await fireEvent.press(screen.getByText('Outro motivo'));
    await fireEvent.press(screen.getByText('Recusar pedido'));
    expect(screen.getByText('Escreva o motivo para o cliente.')).toBeTruthy();
    expect(writes).toHaveLength(0);

    await fireEvent.changeText(screen.getByLabelText('Mensagem para o cliente (obrigatória)'), 'Fechamos mais cedo hoje.');
    expect(screen.getByText('O cliente vai ler: “Fechamos mais cedo hoje.”')).toBeTruthy();
    await act(async () => {
      await fireEvent.press(screen.getByText('Recusar pedido'));
    });
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]).toMatchObject({ method: 'POST', path: `/orders/${ID}/reject`, body: { reasonCode: 'OTHER', message: 'Fechamos mais cedo hoje.' } });
    expect(writes[0]!.headers['If-Match']).toBe('"1"');
    expect(writes[0]!.headers['Idempotency-Key']).toBeUndefined();
  });
});

describe('atendimento [M] (ADR-0046 item 18)', () => {
  const delivery = { id: 'o1', version: 4, orderNumber: 12, fulfillmentType: 'DELIVERY' as const, fulfillmentStatus: 'PENDING' as const, acceptanceStatus: 'ACCEPTED' as const };

  it('produção aberta: "Concluir e marcar saída" manda completeProduction', async () => {
    signIn(['orders.view', 'orders.fulfillment.update', 'production.update']);
    writeHandler = () => json(200, { ...accepted, id: 'o1', version: 5, productionStatus: 'COMPLETED', fulfillmentStatus: 'OUT_FOR_DELIVERY' });
    await renderWith(<FulfillmentActions order={{ ...delivery, productionStatus: 'IN_PROGRESS' }} />);
    await fireEvent.press(screen.getByText('Concluir e marcar saída'));
    expect(screen.getByText('Em produção → Concluído')).toBeTruthy();
    await act(async () => {
      await fireEvent.press(screen.getByTestId('confirm-button'));
    });
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]).toMatchObject({ method: 'PATCH', path: '/orders/o1/fulfillment', body: { fulfillmentStatus: 'OUT_FOR_DELIVERY', completeProduction: true } });
    expect(writes[0]!.headers['If-Match']).toBe('"4"');
  });

  it('ProductionNotCompleted: explica e não fecha a confirmação', async () => {
    signIn(['orders.view', 'orders.fulfillment.update']);
    writeHandler = () => problem(409, 'ProductionNotCompleted');
    await renderWith(<FulfillmentActions order={{ ...delivery, productionStatus: 'COMPLETED' }} />);
    await fireEvent.press(screen.getByText('Saiu para entrega'));
    await act(async () => {
      await fireEvent.press(screen.getByTestId('confirm-button'));
    });
    expect(await screen.findByText(/A cozinha ainda não concluiu este pedido/)).toBeTruthy();
    expect(writes[0]!.body).toEqual({ fulfillmentStatus: 'OUT_FOR_DELIVERY', completeProduction: false });
  });

  it('sem production.update e com a produção aberta: espera a cozinha', async () => {
    signIn(['orders.view', 'orders.fulfillment.update']);
    await renderWith(<FulfillmentActions order={{ ...delivery, productionStatus: 'OPEN' }} />);
    expect(screen.getByText(/fica disponível quando a cozinha concluir/)).toBeTruthy();
    expect(screen.queryByText('Saiu para entrega')).toBeNull();
  });

  it('no detalhe aparecem as voltas; no card só o avanço', async () => {
    signIn(['orders.view', 'orders.fulfillment.update']);
    const out = { ...delivery, fulfillmentStatus: 'OUT_FOR_DELIVERY' as const, productionStatus: 'COMPLETED' as const };
    const view = await renderWith(<FulfillmentActions order={out} />);
    expect(screen.getByText('Entregue')).toBeTruthy();
    expect(screen.getByText('Voltou sem entregar')).toBeTruthy();
    await view.unmount();
    await renderWith(<FulfillmentActions order={out} compact />);
    expect(screen.getByText('Entregue')).toBeTruthy();
    expect(screen.queryByText('Voltou sem entregar')).toBeNull();
  });
});

describe('listas e fechamento', () => {
  it('OrderCard: selo "Site", selo de atendimento e o próximo passo', async () => {
    signIn(['orders.view', 'orders.fulfillment.update']);
    const card: OrderSummary = { ...summary, orderNumber: 15, acceptanceStatus: 'ACCEPTED', acceptanceDeadline: null, productionStatus: 'COMPLETED', fulfillmentStatus: 'OUT_FOR_DELIVERY' };
    await renderWith(<OrderCard order={card} />);
    expect(screen.getByText(/^#15 · /)).toBeTruthy();
    expect(screen.getByTestId('site-badge')).toBeTruthy();
    expect(screen.getByTestId('fulfillment-badge')).toBeTruthy();
    expect(screen.getByText('Saiu para entrega')).toBeTruthy();
    expect(screen.getByText('Entregue')).toBeTruthy();
  });

  it('retirada pronta: "Pronto para retirar" é derivado de isReadyForPickup', async () => {
    signIn(['orders.view']);
    const card: OrderSummary = { ...summary, origin: 'APP', orderNumber: 3, fulfillmentType: 'PICKUP', acceptanceStatus: 'ACCEPTED', acceptanceDeadline: null, productionStatus: 'COMPLETED', fulfillmentStatus: 'PENDING', isReadyForPickup: true };
    await renderWith(<OrderCard order={card} />);
    expect(screen.getByText('Pronto para retirar')).toBeTruthy();
    expect(screen.queryByTestId('site-badge')).toBeNull();
  });

  it('fechamento mostra os pedidos do site e as entregas sem marcação', async () => {
    signIn(['closing.view']);
    const s = {
      businessDay: { id: 'bd1', version: 3, businessDate: '2026-10-27', status: 'OPEN' },
      isFinal: false,
      dailyClosingId: null,
      computedAt: '2026-10-27T15:00:00Z',
      orderCount: 0,
      totalSold: '0.00',
      deliveryFeesTotal: '0.00',
      totalReceived: '0.00',
      receivedFromPreviousDays: '0.00',
      totalPending: '0.00',
      canceled: { count: 0, total: '0.00' },
      reversed: { count: 0, total: '0.00' },
      webOrders: { awaitingAcceptanceCount: 2, autoRejectedCount: 0, notAcceptedCount: 1 },
      unmarkedDeliveryCount: 4,
      byOrderType: [],
      byPaymentMethod: [],
      cash: { openingBalance: '0.00', cashReceived: '0.00', cashReversed: '0.00', supplies: '0.00', withdrawals: '0.00', expectedCash: '0.00', countedCash: null, cashDifference: null, justification: null },
      adjustments: [],
      closedAt: null,
      closedBy: null,
    } as ClosingSummary;
    await renderWith(<ClosingSummaryView summary={s} />);
    expect(screen.getByText('Pedidos do site e entregas')).toBeTruthy();
    expect(screen.getByText('Aguardando aceite')).toBeTruthy();
    expect(screen.getByText('4 entregas sem marcação de entregue.')).toBeTruthy();
  });
});
