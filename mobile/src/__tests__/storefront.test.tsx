/**
 * EP-002 fatia 2, F-210 (06 §6.18): tela "Página de pedidos" — chaves rápidas com o objeto inteiro e `If-Match`,
 * 422 com o motivo do servidor, 409 sem sobrescrever, modo leitura, compartilhar e QR. Também a chave
 * `isVisibleOnPage` do produto e a forma de pagamento esperada do pedido do site.
 */
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import type { ReactElement } from 'react';
import { Linking } from 'react-native';

import { configureApiClient } from '@/api/client';
import type { Me, Product, Storefront } from '@/api/types';
import { ProductFormScreen } from '@/features/catalog/screens/CatalogScreens';
import { distanceFeeText, expectedPaymentText } from '@/features/orders/webOrders';
import { WebOrderPaymentInfo } from '@/features/orders/webOrderComponents';
import { qrMatrix } from '@/features/storefront/qrMatrix';
import { CONFLICT_TEXT, StorefrontScreen } from '@/features/storefront/screens/StorefrontScreen';
import {
  formErrors,
  formFromStorefront,
  mapsUrl,
  normalizeSlugInput,
  pageDeliveryText,
  parseCoordinate,
  parseKm,
  slugProblem,
  storeStatusInfo,
  toUpdateBody,
} from '@/features/storefront/storefront';
import { shareLink as shareLinkWeb } from '@/lib/share.web';
import { useConnectivity } from '@/stores/connectivity';
import { useSession } from '@/stores/session';
import { useToast } from '@/stores/toast';

jest.mock('expo-router', () => ({
  router: { push: jest.fn(), back: jest.fn(), replace: jest.fn() },
  Stack: { Screen: () => null },
  useLocalSearchParams: () => ({ id: 'p1' }),
}));
jest.mock('@/lib/confirm', () => ({ confirmAction: jest.fn((o: { onConfirm: () => void }) => o.onConfirm()) }));
jest.mock('@/lib/share', () => ({ shareLink: jest.fn(async () => 'shared') }));

/** Exemplo literal do 06 §6.18 (`GET /storefront`), mas inativa e sem endereço, como nasce no autocadastro. */
const base: Storefront = {
  version: 3,
  slug: 'marmitaria-da-ana',
  previousSlugs: ['marmitaria-ana'],
  publicName: 'Marmitaria da Ana',
  isPageActive: false,
  isAcceptingOrders: true,
  isDeliveryEnabled: true,
  offersDelivery: true,
  offersPickup: true,
  contactPhone: '+5534999990000',
  hoursText: 'Seg a sáb, 10h30 às 14h',
  address: { street: null, number: null, complement: null, neighborhood: null, city: null, state: null, zipCode: null },
  location: null,
  delivery: { feeMode: 'DISTANCE', pricePerKm: '0.80', minimumFee: '3.00', maxDistanceKm: '6.0' },
  pageDelivery: { available: false, missing: ['CONFIRMED_LOCATION'] },
  status: 'UNAVAILABLE',
  updatedAt: '2026-10-27T09:30:00Z',
};

const product: Product = {
  id: 'p1',
  version: 2,
  kind: 'BEVERAGE',
  name: 'Coca-Cola 350ml',
  description: null,
  category: 'Refrigerantes',
  defaultPrice: '6.00',
  requiresPreparation: false,
  imageUrl: null,
  isActive: true,
  isAvailable: true,
  isVisibleOnPage: true,
  createdAt: '2026-10-27T09:40:00Z',
  updatedAt: '2026-10-27T09:40:00Z',
};

const json = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } });
const problem = (status: number, errorCode: string, ext: Record<string, unknown> = {}) =>
  new Response(JSON.stringify({ status, errorCode, title: errorCode, ...ext }), { status, headers: { 'Content-Type': 'application/problem+json' } });

let current: Storefront;
let writeHandler: (body: unknown) => Response;
const writes: { method: string; path: string; headers: Record<string, string>; body: unknown }[] = [];

function signIn(permissions: Me['permissions']) {
  useSession.setState({
    status: 'signedIn',
    readOnly: false,
    me: { tenant: { id: 't1', name: 'Marmitaria', timeZone: 'America/Sao_Paulo' }, employee: { id: 'e1', displayName: 'Ana', roles: ['OWNER'] }, permissions } as unknown as Me,
  });
}

beforeEach(() => {
  current = base;
  writes.length = 0;
  writeHandler = () => json(200, current);
  useToast.setState({ toasts: [] });
  useConnectivity.setState({ online: true });
  configureApiClient({ getAccessToken: () => 'tok', refresh: async () => true, onSessionExpired: () => {}, onUpgradeRequired: () => {}, onSubscriptionInactive: () => {} });
  global.fetch = jest.fn(async (url: string, init: RequestInit) => {
    const path = url.replace(/^https?:\/\/[^/]+(\/api\/v1)?/, '');
    const method = init.method ?? 'GET';
    if (method !== 'GET') {
      const body = init.body ? JSON.parse(init.body as string) : undefined;
      writes.push({ method, path, headers: init.headers as Record<string, string>, body });
      return writeHandler(body);
    }
    if (path === '/storefront') return json(200, current, { ETag: `"${current.version}"` });
    if (path === '/products/p1') return json(200, product, { ETag: '"2"' });
    if (path === '/tenant/settings') return json(200, { version: 1, defaultDeliveryFee: '5.00', kiloPricePerKg: null, enabledPaymentMethods: ['CASH'], sharedDeviceIdleSeconds: 300 });
    return json(404, { errorCode: 'NotFound' });
  }) as unknown as typeof fetch;
});

const clients: QueryClient[] = [];
afterEach(() => {
  for (const c of clients.splice(0)) c.clear();
});

async function renderWith(ui: ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } } });
  clients.push(client);
  return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
}

const toasts = () => useToast.getState().toasts.map((t) => t.message);

describe('regras da tela (06 §6.18)', () => {
  it('slug: normaliza a digitação e aponta o formato inválido', () => {
    expect(normalizeSlugInput('Marmitaria  da Ána!')).toBe('marmitaria-da-ana');
    expect(slugProblem('ab')).toMatch(/3 a 40/);
    expect(slugProblem('-ana')).toMatch(/hífen/);
    expect(slugProblem('12345')).toMatch(/letra/);
    expect(slugProblem('marmitaria-da-ana')).toBeNull();
  });

  it('chave rápida manda a configuração inteira, trocando só a chave', () => {
    expect(toUpdateBody(base, null, { isAcceptingOrders: false })).toEqual({
      slug: 'marmitaria-da-ana',
      publicName: 'Marmitaria da Ana',
      isPageActive: false,
      isAcceptingOrders: false,
      isDeliveryEnabled: true,
      offersDelivery: true,
      offersPickup: true,
      contactPhone: '+5534999990000',
      hoursText: 'Seg a sáb, 10h30 às 14h',
      address: base.address,
      delivery: base.delivery,
    });
  });

  it('formulário: telefone em E.164, UF maiúscula, vazios viram null; a regra da taxa vai como está', () => {
    const form = { ...formFromStorefront(base), contactPhone: '(34) 98888-7777', street: ' Rua das Flores ', number: '123', state: 'mg', zipCode: '38400-000', hoursText: '  ' };
    const body = toUpdateBody(base, form);
    expect(body.contactPhone).toBe('+5534988887777');
    expect(body.hoursText).toBeNull();
    expect(body.address).toEqual({ street: 'Rua das Flores', number: '123', complement: null, neighborhood: null, city: null, state: 'MG', zipCode: '38400-000' });
    expect(body.delivery).toEqual(base.delivery);
    expect(formErrors({ ...form, contactPhone: '3499', offersPickup: false, offersDelivery: false })).toMatchObject({
      contactPhone: expect.any(String),
      offersPickup: expect.any(String),
    });
  });

  it('estado da página em linguagem de balcão; indisponível com a página ativa = assinatura', () => {
    expect(storeStatusInfo({ status: 'OPEN', isPageActive: true }).title).toBe('Aberta');
    expect(storeStatusInfo({ status: 'PAUSED', isPageActive: true }).title).toBe('Pausada');
    expect(storeStatusInfo({ status: 'CLOSED', isPageActive: true }).title).toBe('Fechada agora');
    expect(storeStatusInfo({ status: 'UNAVAILABLE', isPageActive: false }).title).toBe('Desativada');
    expect(storeStatusInfo({ status: 'UNAVAILABLE', isPageActive: true }).text).toMatch(/assinatura/);
  });

  it('QR: matriz quadrada com os três padrões de posição', () => {
    const m = qrMatrix('http://localhost:3000/marmitaria-da-ana');
    expect(m.rows).toHaveLength(m.size);
    // Canto superior esquerdo: 7 módulos escuros na primeira linha; o mesmo no canto direito.
    expect(m.rows[0]![0]).toEqual([0, 7]);
    expect(m.rows[0]!.at(-1)).toEqual([m.size - 7, 7]);
    expect(m.rows[m.size - 1]![0]).toEqual([0, 7]);
  });
});

describe('tela "Página de pedidos"', () => {
  it('mostra o estado, o link e o QR; compartilha o link', async () => {
    signIn(['orders.view', 'storefront.manage']);
    await renderWith(<StorefrontScreen />);
    expect(await screen.findByText(/Sua página está: Desativada/)).toBeTruthy();
    expect(screen.getByTestId('storefront-url').props.children).toBe('http://localhost:3000/marmitaria-da-ana');
    expect(screen.getByText(/Links antigos que ainda levam para cá: marmitaria-ana/)).toBeTruthy();
    expect(screen.queryByText(/Disponível em breve/)).toBeNull();
    await fireEvent.press(screen.getByText('Mostrar QR code'));
    expect(screen.getByTestId('storefront-qr')).toBeTruthy();
    await act(async () => {
      await fireEvent.press(screen.getByText('Compartilhar link'));
    });
    const { shareLink } = jest.requireMock('@/lib/share') as { shareLink: jest.Mock };
    expect(shareLink).toHaveBeenCalledWith({
      title: 'Marmitaria da Ana',
      url: 'http://localhost:3000/marmitaria-da-ana',
      message: 'Faça seu pedido na Marmitaria da Ana pelo site: http://localhost:3000/marmitaria-da-ana',
    });
  });

  it('ativar sem endereço: PUT com If-Match e o objeto inteiro; o motivo do 422 aparece', async () => {
    writeHandler = () =>
      problem(422, 'ValidationFailed', { errors: { isPageActive: ['Para ativar a página, informe o endereço do restaurante (rua, número, bairro, cidade e UF).'] } });
    signIn(['orders.view', 'storefront.manage']);
    await renderWith(<StorefrontScreen />);
    await screen.findByText(/Sua página está/);
    await act(async () => {
      await fireEvent(screen.getByLabelText('Página ativa'), 'valueChange', true);
    });
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]).toMatchObject({ method: 'PUT', path: '/storefront', body: { ...toUpdateBody(base), isPageActive: true } });
    expect(writes[0]!.headers['If-Match']).toBe('"3"');
    expect(await screen.findByText(/Para ativar a página, informe o endereço do restaurante/)).toBeTruthy();
  });

  it('pausar: manda isAcceptingOrders false e mostra a página pausada', async () => {
    current = { ...base, isPageActive: true, status: 'OPEN' };
    writeHandler = (body) => json(200, { ...current, ...(body as object), version: 4, status: 'PAUSED' }, { ETag: '"4"' });
    signIn(['orders.view', 'storefront.manage']);
    await renderWith(<StorefrontScreen />);
    await screen.findByText(/Sua página está: Aberta/);
    await act(async () => {
      await fireEvent(screen.getByLabelText('Pausar pedidos pelo site'), 'valueChange', true);
    });
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]!.body).toMatchObject({ isPageActive: true, isAcceptingOrders: false });
    expect(await screen.findByText(/Sua página está: Pausada/)).toBeTruthy();
    expect(toasts()).toContain('Pedidos pelo site pausados');
  });

  it('409 VersionConflict ao salvar: avisa, recarrega e não sobrescreve', async () => {
    const changed: Storefront = { ...base, version: 4, publicName: 'Marmitaria Ana & Filhos' };
    writeHandler = () => problem(409, 'VersionConflict', { current: changed });
    signIn(['orders.view', 'storefront.manage']);
    await renderWith(<StorefrontScreen />);
    await screen.findByText(/Sua página está/);
    await fireEvent.changeText(screen.getByLabelText('Nome na página'), 'Marmitaria da Ana Maria');
    await act(async () => {
      await fireEvent.press(screen.getByText('Salvar'));
    });
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]!.body).toMatchObject({ publicName: 'Marmitaria da Ana Maria' });
    expect(await screen.findByText(CONFLICT_TEXT)).toBeTruthy();
    // O formulário voltou com o que está no servidor; nada foi reenviado.
    expect(screen.getByLabelText('Nome na página').props.value).toBe('Marmitaria Ana & Filhos');
    expect(writes).toHaveLength(1);
  });

  it('com edição pendente, as chaves rápidas ficam travadas', async () => {
    signIn(['orders.view', 'storefront.manage']);
    await renderWith(<StorefrontScreen />);
    await screen.findByText(/Sua página está/);
    await fireEvent.changeText(screen.getByLabelText('Rua'), 'Rua das Flores');
    expect(screen.getByLabelText('Página ativa').props.disabled).toBe(true);
    expect(screen.getByText('Salve ou desfaça as alterações abaixo antes de ativar ou pausar.')).toBeTruthy();
  });

  it('só orders.view: vê o link e compartilha, sem chaves nem "Salvar"', async () => {
    signIn(['orders.view']);
    await renderWith(<StorefrontScreen />);
    await screen.findByText(/Sua página está/);
    expect(screen.getByText('Compartilhar link')).toBeTruthy();
    expect(screen.queryByLabelText('Página ativa')).toBeNull();
    expect(screen.queryByText('Salvar')).toBeNull();
    expect(screen.getByLabelText('Nome na página').props.editable).toBe(false);
  });
});

describe('compartilhar na web (share.web.ts)', () => {
  const original = global.navigator;
  afterEach(() => Object.defineProperty(global, 'navigator', { value: original, configurable: true }));

  it('sem navigator.share, copia o link', async () => {
    const writeText = jest.fn(async () => {});
    Object.defineProperty(global, 'navigator', { value: { clipboard: { writeText } }, configurable: true });
    await expect(shareLinkWeb({ title: 'A', message: 'm', url: 'http://x/a' })).resolves.toBe('copied');
    expect(writeText).toHaveBeenCalledWith('http://x/a');
  });

  it('com navigator.share, usa a folha do navegador; fechar não copia', async () => {
    const writeText = jest.fn(async () => {});
    const abort = Object.assign(new Error('x'), { name: 'AbortError' });
    const share = jest.fn(async () => {
      throw abort;
    });
    Object.defineProperty(global, 'navigator', { value: { share, clipboard: { writeText } }, configurable: true });
    await expect(shareLinkWeb({ title: 'A', message: 'm', url: 'http://x/a' })).resolves.toBe('dismissed');
    expect(share).toHaveBeenCalledWith({ title: 'A', text: 'm', url: 'http://x/a' });
    expect(writeText).not.toHaveBeenCalled();
  });
});

describe('produto: "Aparece na página" (ADR-0044 item 5)', () => {
  it('bebida: desligar manda isVisibleOnPage false no PATCH', async () => {
    writeHandler = () => json(200, { ...product, version: 3, isVisibleOnPage: false });
    signIn(['orders.view', 'catalog.manage']);
    await renderWith(<ProductFormScreen />);
    const toggle = await screen.findByLabelText('Aparece na página');
    await fireEvent(toggle, 'valueChange', false);
    await act(async () => {
      await fireEvent.press(screen.getByText('Salvar'));
    });
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]).toMatchObject({ method: 'PATCH', path: '/products/p1', body: { isVisibleOnPage: false } });
    expect(writes[0]!.headers['If-Match']).toBe('"2"');
  });
});

describe('pedido do site: forma de pagamento esperada e riskFlag', () => {
  it('textos por forma', () => {
    expect(expectedPaymentText({ expectedPaymentMethod: 'CASH', changeFor: '50.00' })).toBe('Vai pagar em dinheiro, troco para R$ 50,00');
    expect(expectedPaymentText({ expectedPaymentMethod: 'CASH', changeFor: null })).toBe('Vai pagar em dinheiro');
    expect(expectedPaymentText({ expectedPaymentMethod: 'PIX', changeFor: null })).toBe('Vai pagar com Pix');
    expect(expectedPaymentText({ expectedPaymentMethod: 'CARD', changeFor: null })).toBe('Vai pagar com cartão');
    expect(expectedPaymentText({ expectedPaymentMethod: null, changeFor: null })).toBeNull();
  });

  it('aviso discreto quando o anti-robô não respondeu', async () => {
    await render(<WebOrderPaymentInfo order={{ expectedPaymentMethod: 'PIX', changeFor: null, riskFlag: 'CAPTCHA_UNAVAILABLE' }} />);
    expect(screen.getByText('Vai pagar com Pix')).toBeTruthy();
    expect(screen.getByText('Verificação anti-robô indisponível no envio')).toBeTruthy();
  });

  it('pedido do app (tudo null): não mostra nada', async () => {
    await render(<WebOrderPaymentInfo order={{ expectedPaymentMethod: null, changeFor: null, riskFlag: null }} />);
    expect(screen.queryByTestId('expected-payment')).toBeNull();
    expect(screen.queryByTestId('risk-flag')).toBeNull();
  });
});

describe('fatia 4: entrega por distância e ponto do restaurante (06 §6.18, ADR-0048)', () => {
  const unconfirmed: Storefront = {
    ...base,
    isPageActive: true,
    status: 'OPEN',
    address: { street: 'Rua das Flores', number: '123', complement: null, neighborhood: 'Centro', city: 'Uberlândia', state: 'MG', zipCode: '38400000' },
    location: { latitude: -18.918512, longitude: -48.277003, isConfirmed: false },
    pageDelivery: { available: false, missing: ['CONFIRMED_LOCATION'] },
  };

  it('o que falta em linguagem simples; MAPS_PROVIDER à parte', () => {
    expect(pageDeliveryText({ available: false, missing: ['DELIVERY_OFF', 'PRICE_PER_KM', 'CONFIRMED_LOCATION'] }).steps).toBe(
      'Para entregar pela página: ligue a entrega, informe o preço por km e confirme o ponto do restaurante.',
    );
    expect(pageDeliveryText({ available: false, missing: ['CONFIRMED_LOCATION'] }).steps).toBe('Para entregar pela página: confirme o ponto do restaurante.');
    expect(pageDeliveryText({ available: false, missing: ['MAPS_PROVIDER'] })).toEqual({ steps: null, provider: 'Cálculo de distância não configurado no servidor.' });
    expect(pageDeliveryText({ available: true, missing: [] })).toEqual({ steps: null, provider: null });
  });

  it('raio e coordenadas digitados', () => {
    expect(parseKm('6')).toBe('6.0');
    expect(parseKm('6,5')).toBe('6.5');
    expect(parseKm('')).toBeNull();
    expect(parseKm('0')).toBeUndefined();
    expect(parseKm('6,55')).toBeUndefined();
    expect(parseCoordinate('-18,9185123', 90)).toBe(-18.918512);
    expect(parseCoordinate('91', 90)).toBeNull();
    expect(mapsUrl({ latitude: -18.918512, longitude: -48.277003 })).toBe('https://www.google.com/maps/search/?api=1&query=-18.918512,-48.277003');
  });

  it('bug: depois de ativar, o texto da chave diz que a página está ativa', async () => {
    writeHandler = (body) => json(200, { ...current, ...(body as object), version: 4, status: 'OPEN' }, { ETag: '"4"' });
    signIn(['orders.view', 'storefront.manage']);
    await renderWith(<StorefrontScreen />);
    expect(await screen.findByText('Desativada: o link mostra “indisponível”.')).toBeTruthy();
    await act(async () => {
      await fireEvent(screen.getByLabelText('Página ativa'), 'valueChange', true);
    });
    expect(await screen.findByText('Ativa: quem abre o link vê o cardápio do dia e pode pedir.')).toBeTruthy();
    expect(screen.queryByText('Desativada: o link mostra “indisponível”.')).toBeNull();
    expect(screen.getByText(/Sua página está: Aberta/)).toBeTruthy();
  });

  it('ponto não confirmado: coordenadas, "Ver no mapa" e "Confirmar este ponto" com o objeto inteiro', async () => {
    current = unconfirmed;
    const open = jest.spyOn(Linking, 'openURL').mockResolvedValue(undefined);
    writeHandler = (body) => json(200, { ...current, version: 4, location: { ...(body as Storefront).location!, isConfirmed: true }, pageDelivery: { available: true, missing: [] } }, { ETag: '"4"' });
    signIn(['orders.view', 'storefront.manage']);
    await renderWith(<StorefrontScreen />);
    expect(await screen.findByText('-18.918512, -48.277003')).toBeTruthy();
    expect(screen.getByText('Para entregar pela página: confirme o ponto do restaurante.')).toBeTruthy();
    await fireEvent.press(screen.getByText('Ver no mapa'));
    expect(open).toHaveBeenCalledWith('https://www.google.com/maps/search/?api=1&query=-18.918512,-48.277003');
    await act(async () => {
      await fireEvent.press(screen.getByText('Confirmar este ponto'));
    });
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]!.body).toEqual({ ...toUpdateBody(unconfirmed), location: { latitude: -18.918512, longitude: -48.277003, isConfirmed: true } });
    expect(writes[0]!.headers['If-Match']).toBe('"3"');
    expect(await screen.findByText('A página oferece entrega agora.')).toBeTruthy();
    expect(screen.queryByText('Confirmar este ponto')).toBeNull();
    open.mockRestore();
  });

  it('correção à mão (avançado): manda o ponto digitado, confirmado', async () => {
    current = unconfirmed;
    signIn(['orders.view', 'storefront.manage']);
    await renderWith(<StorefrontScreen />);
    await screen.findByText('-18.918512, -48.277003');
    await fireEvent.press(screen.getByText('Corrigir o ponto à mão (avançado)'));
    await fireEvent.changeText(screen.getByLabelText('Latitude'), '-18,9190001');
    await fireEvent.changeText(screen.getByLabelText('Longitude'), '-48.2775');
    await act(async () => {
      await fireEvent.press(screen.getByText('Usar este ponto'));
    });
    await waitFor(() => expect(writes).toHaveLength(1));
    expect((writes[0]!.body as { location: unknown }).location).toEqual({ latitude: -18.919, longitude: -48.2775, isConfirmed: true });
  });

  it('salvar a regra da taxa: preço, mínima e raio; o formulário não manda o ponto', async () => {
    current = unconfirmed;
    signIn(['orders.view', 'storefront.manage']);
    await renderWith(<StorefrontScreen />);
    await screen.findByText('-18.918512, -48.277003');
    await fireEvent.changeText(screen.getByTestId('price-per-km'), '120');
    await fireEvent.changeText(screen.getByLabelText('Raio máximo em km (opcional)'), '8,5');
    // Com edição pendente, o ponto não pode ser confirmado (o PUT levaria a edição pela metade).
    expect(screen.getByText('Salve ou desfaça as alterações antes de mexer no ponto.')).toBeTruthy();
    await act(async () => {
      await fireEvent.press(screen.getByText('Salvar'));
    });
    await waitFor(() => expect(writes).toHaveLength(1));
    const body = writes[0]!.body as Record<string, unknown>;
    expect(body.delivery).toEqual({ feeMode: 'DISTANCE', pricePerKm: '1.20', minimumFee: '3.00', maxDistanceKm: '8.5' });
    expect('location' in body).toBe(false);
  });

  it('taxa fixa: usa a taxa padrão e esconde o ponto', async () => {
    current = unconfirmed;
    signIn(['orders.view', 'storefront.manage']);
    await renderWith(<StorefrontScreen />);
    await screen.findByText('-18.918512, -48.277003');
    await fireEvent.press(screen.getByText('Taxa fixa'));
    expect(await screen.findByText(/cobra R\$ 5,00, a taxa padrão/)).toBeTruthy();
    expect(screen.queryByText('Ponto do restaurante')).toBeNull();
    await act(async () => {
      await fireEvent.press(screen.getByText('Salvar'));
    });
    await waitFor(() => expect(writes).toHaveLength(1));
    expect((writes[0]!.body as { delivery: { feeMode: string } }).delivery.feeMode).toBe('FIXED');
  });

  it('pedido com taxa pela distância: "Entrega · 4,5 km · taxa R$ 3,60"', () => {
    expect(distanceFeeText({ fulfillmentType: 'DELIVERY', deliveryFeeSource: 'DISTANCE', deliveryDistanceKm: '4.5', deliveryFee: '3.60' })).toBe('Entrega · 4,5 km · taxa R$ 3,60');
    expect(distanceFeeText({ fulfillmentType: 'DELIVERY', deliveryFeeSource: 'MANUAL', deliveryDistanceKm: null, deliveryFee: '5.00' })).toBeNull();
    expect(distanceFeeText({ fulfillmentType: 'PICKUP', deliveryFeeSource: null, deliveryDistanceKm: null, deliveryFee: '0.00' })).toBeNull();
  });
});
