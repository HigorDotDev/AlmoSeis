import { describe, expect, it } from 'vitest';
import type { DeliveryAddressInput, DeliveryQuote } from './api/types';
import { previewCart, type Cart } from './cart';
import { buildOrderRequest, EMPTY_ADDRESS, interpretSubmitFailure, validateCheckout, type CheckoutForm } from './checkout';
import {
  addressFingerprint,
  describeDeliveryRule,
  describeQuote,
  interpretQuoteFailure,
  normalizeAddressInput,
  quoteState,
  toAddressFieldKey,
  validateAddress,
} from './delivery';

const address: DeliveryAddressInput = {
  street: 'Rua das Palmeiras',
  number: '45',
  complement: 'Casa 2',
  neighborhood: 'Centro',
  city: 'Uberlândia',
  zipCode: '38400-100',
  reference: null,
};

const quote: DeliveryQuote = {
  quoteId: 'q1',
  formattedAddress: 'R. das Palmeiras, 45 - Centro, Uberlândia - MG',
  distanceKm: '4.5',
  deliveryFee: '3.60',
  expiresAt: '2026-10-27T12:15:00Z',
};
const before = Date.parse('2026-10-27T12:05:00Z');

describe('regra da taxa', () => {
  it('por distância: "R$ 0,80 por km, mínimo R$ 3,00, até 6 km"', () => {
    expect(
      describeDeliveryRule({ feeMode: 'DISTANCE', pricePerKm: '0.80', minimumFee: '3.00', maxDistanceKm: '6.0', fixedFee: null }),
    ).toBe('R$ 0,80 por km, mínimo R$ 3,00, até 6 km');
    expect(
      describeDeliveryRule({ feeMode: 'DISTANCE', pricePerKm: '0.85', minimumFee: '0.00', maxDistanceKm: '7.5', fixedFee: null }),
    ).toBe('R$ 0,85 por km, até 7,5 km');
  });
  it('fixa e ausente', () => {
    expect(describeDeliveryRule({ feeMode: 'FIXED', pricePerKm: null, minimumFee: null, maxDistanceKm: null, fixedFee: '5.00' })).toBe(
      'Taxa fixa de R$ 5,00',
    );
    expect(describeDeliveryRule(null)).toBeNull();
  });
  it('cotação: "4,5 km · Entrega R$ 3,60"; FIXED sem distância', () => {
    expect(describeQuote(quote)).toBe('4,5 km · Entrega R$ 3,60');
    expect(describeQuote({ ...quote, distanceKm: null, deliveryFee: '5.00' })).toBe('Entrega R$ 5,00');
  });
});

describe('endereço e validade da cotação', () => {
  it('normaliza para o corpo da API: CEP só dígitos, vazio → null', () => {
    expect(normalizeAddressInput({ ...address, complement: '  ', city: '', zipCode: '38400-100' })).toEqual({
      ...address,
      complement: null,
      city: null,
      zipCode: '38400100',
    });
  });

  it('complemento e referência não mudam a cotação; rua, número, bairro, cidade e CEP mudam', () => {
    const fp = addressFingerprint(address);
    expect(addressFingerprint({ ...address, complement: 'Apto 9', reference: 'Perto da padaria' })).toBe(fp);
    expect(addressFingerprint({ ...address, street: 'RUA DAS PALMEIRAS ' })).toBe(fp);
    expect(addressFingerprint({ ...address, number: '46' })).not.toBe(fp);
    expect(addressFingerprint({ ...address, neighborhood: 'Martins' })).not.toBe(fp);
  });

  it('estados: sem cotação, válida, endereço alterado, vencida (com margem de 30 s)', () => {
    const quoted = { quote, fingerprint: addressFingerprint(address) };
    expect(quoteState(null, address, before)).toBe('none');
    expect(quoteState(quoted, address, before)).toBe('valid');
    expect(quoteState(quoted, { ...address, number: '50' }, before)).toBe('stale');
    expect(quoteState(quoted, address, Date.parse('2026-10-27T12:14:45Z'))).toBe('expired');
  });

  it('valida o formato antes de cotar', () => {
    expect(validateAddress(address)).toEqual({});
    const errors = validateAddress({ ...address, street: '', number: '', neighborhood: '', zipCode: '123' });
    expect(Object.keys(errors).sort()).toEqual([
      'deliveryAddress.neighborhood',
      'deliveryAddress.number',
      'deliveryAddress.street',
      'deliveryAddress.zipCode',
    ]);
  });

  it('chaves de erro da API viram o campo da tela', () => {
    expect(toAddressFieldKey('address.number')).toBe('deliveryAddress.number');
    expect(toAddressFieldKey('Address.Street')).toBe('deliveryAddress.street');
    expect(toAddressFieldKey('deliveryAddress.street')).toBe('deliveryAddress.street');
    expect(toAddressFieldKey('customerPhone')).toBeNull();
  });
});

describe('erros da cotação', () => {
  const problem = (status: number, p: Record<string, unknown>, retryAfterSeconds: number | null = null) =>
    interpretQuoteFailure({ kind: 'problem', status, problem: { status, ...p }, retryAfterSeconds });

  it('422 em address.number pede para revisar o número', () => {
    const outcome = problem(422, {
      errorCode: 'ValidationFailed',
      errors: { 'address.number': ['Não achamos esse número nessa rua. Confira o número e o bairro.'] },
    });
    expect(outcome).toMatchObject({
      kind: 'address',
      errors: { 'deliveryAddress.number': 'Não achamos esse número nessa rua. Confira o número e o bairro.' },
    });
  });
  it('OutOfDeliveryArea com distância e raio', () => {
    const outcome = problem(422, { errorCode: 'OutOfDeliveryArea', distanceKm: '6.1', maxDistanceKm: '6.0' });
    expect(outcome).toMatchObject({ kind: 'outOfArea', distanceKm: '6.1', maxDistanceKm: '6.0' });
    expect(outcome.message).toBe('Fora da área de entrega. Seu endereço fica a 6,1 km e entregamos até 6,0 km. Você pode retirar no local.');
    expect(problem(422, { errorCode: 'OutOfDeliveryArea' }).message).toBe('Fora da área de entrega. Você pode retirar no local.');
  });
  it('DeliveryUnavailable oferece a retirada', () => {
    expect(problem(422, { errorCode: 'DeliveryUnavailable' })).toEqual({
      kind: 'unavailable',
      message: 'Não conseguimos calcular a entrega agora. Você pode retirar no local.',
    });
  });
  it('429 com Retry-After, CaptchaFailed e rede', () => {
    expect(problem(429, { errorCode: 'TooManyRequests' }, 25)).toMatchObject({ kind: 'rateLimited', retryAfterSeconds: 25 });
    expect(problem(403, { errorCode: 'CaptchaFailed' }).kind).toBe('captcha');
    expect(interpretQuoteFailure({ kind: 'network', message: 'x' }).kind).toBe('message');
  });
});

describe('envio com entrega', () => {
  const menu = {
    marmitaSizes: [{ id: 'size-m', name: 'M', price: '20.00' }],
    mainDishes: [{ productId: 'frango', name: 'Frango', description: null, imageUrl: null, isAvailable: true }],
    sideDishes: [],
    addons: [],
    beverages: [],
  };
  const cart: Cart = {
    lines: [{ kind: 'MARMITA', id: 'a', sizeId: 'size-m', mainDishIds: ['frango'], sideDishIds: [], addons: [], quantity: 2, notes: '' }],
  };
  const form: CheckoutForm = {
    name: 'Carla',
    phone: '(34) 98888-7777',
    fulfillmentType: 'DELIVERY',
    address,
    deliveryQuoteId: 'q1',
    paymentMethod: 'PIX',
    changeFor: '',
    notes: '',
    website: '',
  };

  it('manda deliveryQuoteId, endereço normalizado e expectedTotal com a taxa', () => {
    const preview = previewCart(cart, menu, 360);
    const body = buildOrderRequest(form, cart, preview);
    expect(body.fulfillmentType).toBe('DELIVERY');
    expect(body.deliveryQuoteId).toBe('q1');
    expect(body.deliveryAddress).toEqual({ ...address, zipCode: '38400100' });
    expect(body.expectedTotal).toBe('43.60');
  });

  it('exige cotação válida para o endereço atual', () => {
    const preview = previewCart(cart, menu, 360);
    expect(validateCheckout(form, preview, 200, 'valid')).toEqual({});
    expect(validateCheckout(form, preview, 200, 'stale').deliveryQuoteId).toMatch(/mudou o endereço/);
    expect(validateCheckout(form, preview, 200, 'expired').deliveryQuoteId).toMatch(/venceu/);
    expect(validateCheckout({ ...form, deliveryQuoteId: null }, preview, 200, 'none').deliveryQuoteId).toBeDefined();
    expect(validateCheckout({ ...form, address: EMPTY_ADDRESS }, preview, 200, 'valid')['deliveryAddress.street']).toBeDefined();
  });

  it('409 DeliveryQuoteChanged traz a cotação nova', () => {
    const newQuote = { ...quote, quoteId: 'q2', deliveryFee: '4.50' };
    const outcome = interpretSubmitFailure({
      kind: 'problem',
      status: 409,
      problem: { status: 409, errorCode: 'DeliveryQuoteChanged', detail: 'A taxa de entrega agora é R$ 4,50. Confira e envie de novo.', quote: newQuote },
      retryAfterSeconds: null,
    });
    expect(outcome).toEqual({ kind: 'quoteChanged', quote: newQuote, message: 'A taxa de entrega agora é R$ 4,50. Confira e envie de novo.' });
  });

  it('no envio, OutOfDeliveryArea e DeliveryUnavailable oferecem retirada; 422 em deliveryAddress.number vai para o campo', () => {
    const fail = (p: Record<string, unknown>) =>
      interpretSubmitFailure({ kind: 'problem', status: 422, problem: { status: 422, ...p }, retryAfterSeconds: null });
    expect(fail({ errorCode: 'OutOfDeliveryArea', distanceKm: '6.1', maxDistanceKm: '6.0' }).kind).toBe('deliveryFailed');
    expect(fail({ errorCode: 'DeliveryUnavailable' })).toMatchObject({
      kind: 'deliveryFailed',
      message: 'Não conseguimos calcular a entrega agora. Você pode retirar no local.',
    });
    expect(fail({ errorCode: 'ValidationFailed', errors: { 'DeliveryAddress.Number': ['Confira o número.'] } })).toMatchObject({
      kind: 'fieldErrors',
      errors: { 'deliveryAddress.number': 'Confira o número.' },
    });
  });
});
