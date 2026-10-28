/** Montagem do `POST /orders` (06 §6.9) e prévia em centavos. */
import type { MarmitaSize, Menu, Order, OrderItem, Product } from '@/api/types';
import {
  buildKiloBody,
  buildMarmitaBody,
  buildPfBody,
  cashSuggestions,
  emptyMarmitaForm,
  previewKiloCents,
  previewMarmitaCents,
  previewPfCents,
  priceBook,
  repeatOrder,
} from '@/features/orders/compose';

const product = (id: string, kind: Product['kind'], defaultPrice: string | null, name = id): Product => ({
  id,
  version: 1,
  kind,
  name,
  description: null,
  category: null,
  defaultPrice,
  requiresPreparation: kind !== 'BEVERAGE',
  imageUrl: null,
  isActive: true,
  isAvailable: true,
  createdAt: '',
  updatedAt: '',
});

const sizes: MarmitaSize[] = [{ id: 'M', version: 1, name: 'M', price: '20.00', sortOrder: 2, isActive: true }];
const products = [
  product('frango', 'MAIN_DISH', '22.00', 'Frango grelhado'),
  product('arroz', 'SIDE_DISH', null),
  product('ovo', 'ADDON', '2.00'),
  product('coca', 'BEVERAGE', '6.00'),
];

describe('marmita', () => {
  it('prévia do exemplo de 06 §6.9: 2 M + ovo × 2 + Coca + entrega = 55,00', () => {
    const form = {
      ...emptyMarmitaForm(),
      fulfillmentType: 'DELIVERY' as const,
      lines: [{ key: 'l1', sizeId: 'M', quantity: 2, mainDishIds: ['frango'], sideDishIds: ['arroz'], notes: 'Sem cebola', addons: [{ productId: 'ovo', quantity: 1 }] }],
      beverages: { coca: 1 },
    };
    expect(previewMarmitaCents(form, priceBook(sizes, products), '5.00')).toBe(5500);
    expect(previewMarmitaCents({ ...form, fulfillmentType: 'PICKUP' }, priceBook(sizes, products), '5.00')).toBe(5000);
  });

  it('corpo com cliente avulso, endereço digitado e taxa padrão', () => {
    const body = buildMarmitaBody(
      {
        ...emptyMarmitaForm(),
        customer: { id: null, name: ' João ', phone: '(34) 99999-1234', saveCustomer: true },
        fulfillmentType: 'DELIVERY',
        address: { street: 'Rua A', number: '1', complement: null, neighborhood: null, reference: null },
        lines: [{ key: 'l1', sizeId: 'M', quantity: 1, mainDishIds: ['frango'], sideDishIds: [], notes: '', addons: [{ productId: 'ovo', quantity: 0 }] }],
        beverages: { coca: 0 },
      },
      '+5534999991234',
    );
    expect(body).toMatchObject({
      orderType: 'MARMITA',
      customerName: 'João',
      customerPhone: '+5534999991234',
      saveCustomer: true,
      customerAddressId: null,
      deliveryAddress: { street: 'Rua A' },
      deliveryFee: null,
      weighings: [],
    });
    expect(body.items).toEqual([{ itemKind: 'MARMITA', marmitaSizeId: 'M', quantity: 1, mainDishProductIds: ['frango'], sideDishProductIds: [], notes: null, addons: [] }]);
  });

  it('retirada não manda endereço nem taxa', () => {
    const body = buildMarmitaBody({ ...emptyMarmitaForm(), addressId: 'a1', deliveryFee: '9.00' }, null);
    expect(body).toMatchObject({ fulfillmentType: 'PICKUP', customerAddressId: null, deliveryAddress: null, deliveryFee: null });
  });
});

describe('PF e quilo', () => {
  const pfMenu = { items: [{ productId: 'frango', pfPrice: '24.00' }] } as unknown as Menu;

  it('PF usa o pfPrice do cardápio', () => {
    const form = { tableLabel: '5', tabNumber: '', lines: [{ key: 'a', productId: 'frango', quantity: 2, notes: 'Um sem salada', addons: [] }], beverages: { coca: 2 }, notes: '' };
    expect(previewPfCents(form, priceBook([], products, pfMenu))).toBe(6000);
    expect(buildPfBody(form)).toMatchObject({ orderType: 'PF', fulfillmentType: 'DINE_IN', tableLabel: '5', tabNumber: null });
  });

  it('quilo soma pesagens arredondadas + bebidas', () => {
    const form = { tableLabel: '3', tabNumber: '47', weighings: [452, 318], beverages: { coca: 1 }, notes: '' };
    expect(previewKiloCents(form, priceBook([], products), '59.90')).toBe(2707 + 1905 + 600);
    expect(buildKiloBody(form)).toMatchObject({
      orderType: 'KILO',
      customerName: 'Mesa 3',
      tabNumber: '47',
      weighings: [{ weightGrams: 452 }, { weightGrams: 318 }],
      items: [{ itemKind: 'BEVERAGE', productId: 'coca', quantity: 1, notes: null }],
    });
  });
});

describe('repetir último pedido (08 §8.9)', () => {
  const item = (o: Partial<OrderItem>): OrderItem =>
    ({ id: 'i', itemKind: 'MARMITA', marmitaSizeId: 'M', productId: null, name: 'Marmita M', composition: null, components: [], quantity: 1, unitPrice: '20.00', lineTotal: '20.00', requiresPreparation: true, notes: null, addons: [], createdAt: '', createdBy: { id: 'e', displayName: 'Ana' }, ...o }) as OrderItem;
  const menuToday = {
    items: [
      { productId: 'frango', section: 'MAIN_DISH', isAvailable: true },
      { productId: 'arroz', section: 'SIDE_DISH', isAvailable: true },
      { productId: 'bife', section: 'MAIN_DISH', isAvailable: false },
    ],
  } as unknown as Menu;
  let n = 0;
  const key = () => `k${++n}`;

  it('refaz marmita, adicional e bebida com o que tem hoje e lista o que ficou de fora', () => {
    const order = {
      items: [
        item({
          quantity: 2,
          notes: 'sem cebola',
          components: [
            { productId: 'frango', role: 'MAIN_DISH', name: 'Frango grelhado' },
            { productId: 'arroz', role: 'SIDE_DISH', name: 'Arroz' },
            { productId: 'farofa', role: 'SIDE_DISH', name: 'Farofa' },
          ],
          addons: [{ id: 'a', itemKind: 'ADDON', productId: 'ovo', name: 'Ovo', quantity: 1, unitPrice: '2.00', lineTotal: '2.00', requiresPreparation: true, notes: null, createdAt: '', createdBy: { id: 'e', displayName: 'Ana' } }],
        }),
        item({ itemKind: 'BEVERAGE', marmitaSizeId: null, productId: 'coca', name: 'Coca', quantity: 1 }),
      ],
    } as unknown as Order;
    const r = repeatOrder(order, menuToday, sizes, products, key);
    expect(r.lines).toEqual([{ key: 'k1', sizeId: 'M', quantity: 2, mainDishIds: ['frango'], sideDishIds: ['arroz'], notes: 'sem cebola', addons: [{ productId: 'ovo', quantity: 1 }] }]);
    expect(r.beverages).toEqual({ coca: 1 });
    expect(r.dropped).toEqual(['Farofa']);
  });

  it('marmita sem prato principal disponível ou com tamanho inativo cai inteira', () => {
    const order = {
      items: [
        item({ name: 'Marmita M bife', components: [{ productId: 'bife', role: 'MAIN_DISH', name: 'Bife' }] }),
        item({ name: 'Marmita G', marmitaSizeId: 'G', components: [{ productId: 'frango', role: 'MAIN_DISH', name: 'Frango grelhado' }] }),
      ],
    } as unknown as Order;
    const r = repeatOrder(order, menuToday, sizes, products, key);
    expect(r.lines).toEqual([]);
    expect(r.dropped).toEqual(['Bife', 'Marmita M bife', 'Marmita G']);
  });
});

describe('cédulas do Receber', () => {
  it('as três próximas cédulas acima do valor', () => {
    expect(cashSuggestions(3500)).toEqual([5000, 10000, 20000]);
    expect(cashSuggestions(1800)).toEqual([2000, 5000, 10000]);
    expect(cashSuggestions(5000)).toEqual([10000, 20000]);
    expect(cashSuggestions(25000)).toEqual([]);
  });
});
