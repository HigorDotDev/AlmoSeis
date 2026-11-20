import { describe, expect, it } from 'vitest';
import type { PublicMenu, PublicStore } from './api/types';
import { previewCart, type Cart } from './cart';
import { buildOrderRequest, EMPTY_ADDRESS, interpretSubmitFailure, validateCheckout, type CheckoutForm } from './checkout';
import { keyForAttempt } from './idempotency';
import { describeMenu, ogTitle } from './og';
import { isValidBrazilianPhone, maskPhone } from './phone';

const menu: PublicMenu = {
  marmitaSizes: [{ id: 'size-m', name: 'M', price: '20.00' }],
  mainDishes: [{ productId: 'frango', name: 'Frango grelhado', description: null, imageUrl: null, isAvailable: true }],
  sideDishes: [],
  addons: [{ productId: 'ovo', name: 'Ovo frito', description: null, imageUrl: null, price: '2.00', isAvailable: true }],
  beverages: [{ productId: 'coca', name: 'Coca-Cola 350ml', description: null, imageUrl: null, price: '6.00', isAvailable: true }],
};
const cart: Cart = {
  lines: [
    {
      kind: 'MARMITA',
      id: 'a',
      sizeId: 'size-m',
      mainDishIds: ['frango'],
      sideDishIds: [],
      addons: [{ productId: 'ovo', quantity: 1 }],
      quantity: 2,
      notes: '',
    },
    { kind: 'BEVERAGE', id: 'b', productId: 'coca', quantity: 1 },
  ],
};
const preview = previewCart(cart, menu);

const form: CheckoutForm = {
  name: '  Carla Souza ',
  phone: '(34) 98888-7777',
  fulfillmentType: 'PICKUP',
  address: { ...EMPTY_ADDRESS, street: 'Rua X' },
  deliveryQuoteId: 'q1',
  paymentMethod: 'CASH',
  changeFor: '50',
  notes: ' Sem talher ',
  website: '',
};

describe('telefone', () => {
  it('aplica a máscara BR enquanto digita', () => {
    expect(maskPhone('3')).toBe('(3');
    expect(maskPhone('3498')).toBe('(34) 98');
    expect(maskPhone('3432221111')).toBe('(34) 3222-1111');
    expect(maskPhone('34988887777')).toBe('(34) 98888-7777');
    expect(maskPhone('+55 34 98888-7777')).toBe('(34) 98888-7777');
    expect(maskPhone('034988887777')).toBe('(34) 98888-7777');
  });

  it('valida celular e fixo com DDD', () => {
    expect(isValidBrazilianPhone('(34) 98888-7777')).toBe(true);
    expect(isValidBrazilianPhone('(34) 3222-1111')).toBe(true);
    expect(isValidBrazilianPhone('(34) 8888-7777')).toBe(false);
    expect(isValidBrazilianPhone('98888-7777')).toBe(false);
  });
});

describe('corpo do POST /orders', () => {
  it('monta o corpo do contrato: retirada, troco, expectedTotal e campo-isca nulo', () => {
    const body = buildOrderRequest(form, cart, preview);
    expect(body).toMatchObject({
      fulfillmentType: 'PICKUP',
      customerName: 'Carla Souza',
      customerPhone: '(34) 98888-7777',
      deliveryAddress: null,
      deliveryQuoteId: null,
      expectedPaymentMethod: 'CASH',
      changeFor: '50.00',
      notes: 'Sem talher',
      expectedTotal: '50.00',
      website: null,
    });
    expect(body.items).toHaveLength(2);
  });

  it('troco só com dinheiro', () => {
    expect(buildOrderRequest({ ...form, paymentMethod: 'PIX' }, cart, preview).changeFor).toBeNull();
  });

  it('entrega leva endereço e cotação', () => {
    const body = buildOrderRequest(
      { ...form, fulfillmentType: 'DELIVERY', address: { ...EMPTY_ADDRESS, street: 'Rua das Palmeiras', number: '45', neighborhood: 'Centro', city: 'Uberlândia', complement: ' ' } },
      cart,
      preview,
    );
    expect(body.deliveryAddress).toEqual({
      street: 'Rua das Palmeiras',
      number: '45',
      complement: null,
      neighborhood: 'Centro',
      city: 'Uberlândia',
      zipCode: null,
      reference: null,
    });
    expect(body.deliveryQuoteId).toBe('q1');
  });

  it('o campo-isca preenchido vai como está (a API recusa)', () => {
    expect(buildOrderRequest({ ...form, website: 'http://spam' }, cart, preview).website).toBe('http://spam');
  });
});

describe('validação local do checkout', () => {
  it('aceita o formulário válido', () => {
    expect(validateCheckout(form, preview)).toEqual({});
  });
  it('troco menor que o total', () => {
    expect(validateCheckout({ ...form, changeFor: '20' }, preview).changeFor).toBeDefined();
  });
  it('nome, telefone e forma de pagamento obrigatórios', () => {
    const errors = validateCheckout({ ...form, name: 'A', phone: '123', paymentMethod: null }, preview);
    expect(Object.keys(errors).sort()).toEqual(['customerName', 'customerPhone', 'expectedPaymentMethod']);
  });
  it('entrega exige endereço e cotação', () => {
    const errors = validateCheckout({ ...form, fulfillmentType: 'DELIVERY', address: EMPTY_ADDRESS, deliveryQuoteId: null }, preview);
    expect(errors['deliveryAddress.street']).toBeDefined();
    expect(errors.deliveryQuoteId).toBeDefined();
  });
});

describe('Idempotency-Key por tentativa', () => {
  it('reaproveita a chave para o mesmo corpo e gera outra quando o corpo muda', () => {
    const first = keyForAttempt('{"a":1}', null);
    expect(first.idempotencyKey).toMatch(/^[0-9a-f-]{36}$/);
    expect(keyForAttempt('{"a":1}', first)).toBe(first);
    expect(keyForAttempt('{"a":2}', first).idempotencyKey).not.toBe(first.idempotencyKey);
  });
});

describe('interpretação dos erros do envio', () => {
  const problem = (status: number, p: Record<string, unknown>, retryAfterSeconds: number | null = null) =>
    interpretSubmitFailure({ kind: 'problem', status, problem: { status, ...p }, retryAfterSeconds });

  it('CartChanged', () => {
    const cartChanged = { items: [], unavailableProductIds: [], itemsTotal: '6.00', deliveryFee: '0.00', totalAmount: '6.00' };
    expect(problem(409, { errorCode: 'CartChanged', cart: cartChanged })).toMatchObject({ kind: 'cartChanged', cart: cartChanged });
  });
  it('StoreUnavailable com o estado', () => {
    expect(problem(409, { errorCode: 'StoreUnavailable', storeStatus: 'PAUSED' })).toMatchObject({
      kind: 'storeUnavailable',
      storeStatus: 'PAUSED',
    });
  });
  it('TooManyPendingOrders não é confundido com o limite por IP', () => {
    expect(problem(429, { errorCode: 'TooManyPendingOrders' }, 60).kind).toBe('tooManyPending');
    expect(problem(429, { errorCode: 'TooManyRequests' }, 30)).toMatchObject({ kind: 'rateLimited', retryAfterSeconds: 30 });
  });
  it('CaptchaFailed', () => {
    expect(problem(403, { errorCode: 'CaptchaFailed' }).kind).toBe('captcha');
  });
  it('422 por campo em camelCase', () => {
    const outcome = problem(422, { errorCode: 'ValidationFailed', errors: { CustomerPhone: ['Telefone inválido.'], 'items[0].quantity': ['1 a 20.'] } });
    expect(outcome).toMatchObject({ kind: 'fieldErrors', errors: { customerPhone: 'Telefone inválido.', 'items[0].quantity': '1 a 20.' } });
  });
  it('DeliveryUnavailable e PaymentMethodDisabled caem no lugar certo', () => {
    expect(problem(422, { errorCode: 'DeliveryUnavailable' })).toMatchObject({ kind: 'deliveryFailed', errors: { deliveryQuoteId: expect.any(String) } });
    expect(problem(422, { errorCode: 'PaymentMethodDisabled' })).toMatchObject({ errors: { expectedPaymentMethod: expect.any(String) } });
  });
  it('rede: pede para reenviar (sem duplicar)', () => {
    expect(interpretSubmitFailure({ kind: 'network', message: 'x' }).kind).toBe('network');
  });
});

describe('prévia do link (Open Graph)', () => {
  const store = {
    slug: 'ana',
    status: 'OPEN',
    name: 'Marmitaria da Ana',
    pickupAvailable: true,
    deliveryAvailable: false,
    hoursText: null,
    menu,
  } as unknown as PublicStore;

  it('título "Peça na {nome}" e descrição com o cardápio do dia', () => {
    expect(ogTitle(store)).toBe('Peça na Marmitaria da Ana');
    expect(describeMenu(store)).toBe('Hoje: Frango grelhado. Marmita M R$ 20,00. Peça pela página: retirada.');
  });
  it('fechado e indisponível', () => {
    expect(describeMenu({ ...store, status: 'CLOSED', menu: null, hoursText: 'Seg a sáb, 10h30 às 14h' })).toBe(
      'Fechado agora. Seg a sáb, 10h30 às 14h.',
    );
    expect(describeMenu({ ...store, status: 'UNAVAILABLE', menu: null })).toBe('Página de pedidos indisponível.');
  });
});
