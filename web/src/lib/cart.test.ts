import { describe, expect, it } from 'vitest';
import type { PublicMenu } from './api/types';
import {
  addLine,
  applyCartChanged,
  expectedTotal,
  marmitaCount,
  previewCart,
  removeLine,
  sanitizeCart,
  setQuantity,
  toOrderItems,
  validateCart,
  type Cart,
  type MarmitaLine,
} from './cart';
import { formatBRL, parseMoney, parseUserMoney, toApiMoney } from './money';

// Cardápio do exemplo do contrato (06 §6.19).
const menu: PublicMenu = {
  marmitaSizes: [
    { id: 'size-p', name: 'P', price: '16.00' },
    { id: 'size-m', name: 'M', price: '20.00' },
  ],
  mainDishes: [
    { productId: 'frango', name: 'Frango grelhado', description: null, imageUrl: null, isAvailable: true },
    { productId: 'costela', name: 'Costela', description: null, imageUrl: null, isAvailable: false },
  ],
  sideDishes: [
    { productId: 'arroz', name: 'Arroz', description: null, imageUrl: null, isAvailable: true },
    { productId: 'feijao', name: 'Feijão', description: null, imageUrl: null, isAvailable: true },
  ],
  addons: [{ productId: 'ovo', name: 'Ovo frito', description: null, imageUrl: null, price: '2.00', isAvailable: true }],
  beverages: [{ productId: 'coca', name: 'Coca-Cola 350ml', description: null, imageUrl: null, price: '6.00', isAvailable: true }],
};

const marmita = (over: Partial<MarmitaLine> = {}): MarmitaLine => ({
  kind: 'MARMITA',
  id: 'l1',
  sizeId: 'size-m',
  mainDishIds: ['frango'],
  sideDishIds: ['arroz', 'feijao'],
  addons: [{ productId: 'ovo', quantity: 1 }],
  quantity: 2,
  notes: 'Sem cebola',
  ...over,
});

const contractCart: Cart = {
  lines: [marmita(), { kind: 'BEVERAGE', id: 'l2', productId: 'coca', quantity: 1 }],
};

describe('money', () => {
  it('converte string da API em centavos e de volta', () => {
    expect(parseMoney('35.50')).toBe(3550);
    expect(parseMoney('0.05')).toBe(5);
    expect(parseMoney('20')).toBe(2000);
    expect(toApiMoney(5000)).toBe('50.00');
    expect(toApiMoney(5)).toBe('0.05');
    expect(() => parseMoney('1,00')).toThrow();
  });

  it('formata em reais', () => {
    expect(formatBRL(5000)).toBe('R$ 50,00');
    expect(formatBRL(123456)).toBe('R$ 1.234,56');
  });

  it('lê o troco digitado', () => {
    expect(parseUserMoney('50')).toBe(5000);
    expect(parseUserMoney('50,5')).toBe(5050);
    expect(parseUserMoney('R$ 1.000,00')).toBe(100000);
    expect(parseUserMoney('abc')).toBeNull();
    expect(parseUserMoney('')).toBeNull();
  });
});

describe('prévia do carrinho (ADR-0020)', () => {
  it('reproduz o exemplo do contrato: 2 × (20,00 + 2,00) + 6,00 = 50,00', () => {
    const preview = previewCart(contractCart, menu);
    expect(preview.lines[0]!.unitCents).toBe(2200);
    expect(preview.lines[0]!.lineCents).toBe(4400);
    expect(preview.lines[1]!.lineCents).toBe(600);
    expect(preview.totalCents).toBe(5000);
    expect(expectedTotal(preview)).toBe('50.00');
    expect(preview.hasUnavailable).toBe(false);
  });

  it('adicional com quantidade 2 conta por marmita', () => {
    const cart: Cart = { lines: [marmita({ quantity: 3, addons: [{ productId: 'ovo', quantity: 2 }] })] };
    expect(previewCart(cart, menu).totalCents).toBe(3 * (2000 + 400));
  });

  it('soma a taxa de entrega ao total', () => {
    expect(previewCart(contractCart, menu, 360).totalCents).toBe(5360);
  });

  it('marca como indisponível a linha com prato que acabou ou produto que saiu do cardápio', () => {
    const cart: Cart = {
      lines: [marmita({ mainDishIds: ['costela'] }), { kind: 'BEVERAGE', id: 'b', productId: 'sumiu', quantity: 1 }],
    };
    const preview = previewCart(cart, menu);
    expect(preview.lines[0]!.unavailable).toBe(true);
    expect(preview.lines[1]!.unavailable).toBe(true);
    expect(preview.hasUnavailable).toBe(true);
  });
});

describe('montagem do carrinho', () => {
  it('soma a mesma bebida na mesma linha e respeita o máximo por linha', () => {
    let cart: Cart = { lines: [] };
    cart = addLine(cart, { kind: 'BEVERAGE', id: 'a', productId: 'coca', quantity: 1 });
    cart = addLine(cart, { kind: 'BEVERAGE', id: 'b', productId: 'coca', quantity: 1 });
    expect(cart.lines).toHaveLength(1);
    expect(cart.lines[0]!.quantity).toBe(2);
    cart = setQuantity(cart, 'a', 99, 20);
    expect(cart.lines[0]!.quantity).toBe(20);
    cart = setQuantity(cart, 'a', 0);
    expect(cart.lines).toHaveLength(0);
  });

  it('marmitas iguais ficam em linhas separadas (observações diferentes)', () => {
    let cart: Cart = { lines: [] };
    cart = addLine(cart, marmita({ id: 'x' }));
    cart = addLine(cart, marmita({ id: 'y', notes: '' }));
    expect(cart.lines).toHaveLength(2);
    expect(marmitaCount(cart)).toBe(4);
    expect(removeLine(cart, 'x').lines.map((l) => l.id)).toEqual(['y']);
  });

  it('monta os itens do pedido na ordem do carrinho, no formato do contrato', () => {
    expect(toOrderItems(contractCart)).toEqual([
      {
        itemKind: 'MARMITA',
        marmitaSizeId: 'size-m',
        productId: null,
        quantity: 2,
        mainDishProductIds: ['frango'],
        sideDishProductIds: ['arroz', 'feijao'],
        addons: [{ productId: 'ovo', quantity: 1 }],
        notes: 'Sem cebola',
      },
      {
        itemKind: 'BEVERAGE',
        marmitaSizeId: null,
        productId: 'coca',
        quantity: 1,
        mainDishProductIds: null,
        sideDishProductIds: null,
        addons: null,
        notes: null,
      },
    ]);
  });

  it('descarta formatos estranhos lidos do localStorage', () => {
    expect(sanitizeCart(null).lines).toEqual([]);
    expect(sanitizeCart({ lines: [{ kind: 'X' }, contractCart.lines[1]] }).lines).toHaveLength(1);
  });
});

describe('validação do carrinho (§6.19 checagem 1)', () => {
  it('exige pelo menos 1 marmita', () => {
    const cart: Cart = { lines: [{ kind: 'BEVERAGE', id: 'b', productId: 'coca', quantity: 1 }] };
    expect(validateCart(cart, menu).map((i) => i.code)).toContain('NO_MARMITA');
  });

  it('limita a 20 marmitas somadas', () => {
    const cart: Cart = { lines: [marmita({ id: 'a', quantity: 15 }), marmita({ id: 'b', quantity: 6 })] };
    expect(validateCart(cart, menu).map((i) => i.code)).toContain('TOO_MANY_MARMITAS');
  });

  it('exige prato principal e aponta item indisponível', () => {
    const cart: Cart = { lines: [marmita({ mainDishIds: [] })] };
    const codes = validateCart(cart, menu).map((i) => i.code);
    expect(codes).toContain('MISSING_MAIN_DISH');
    expect(codes).toContain('UNAVAILABLE');
  });

  it('carrinho do contrato é válido', () => {
    expect(validateCart(contractCart, menu)).toEqual([]);
  });
});

describe('409 CartChanged', () => {
  it('tira as linhas indisponíveis por índice e os produtos que saíram', () => {
    const cart: Cart = {
      lines: [
        marmita({ id: 'a', mainDishIds: ['costela'] }),
        marmita({ id: 'b', sideDishIds: ['arroz', 'feijao'], addons: [{ productId: 'ovo', quantity: 1 }] }),
        { kind: 'BEVERAGE', id: 'c', productId: 'coca', quantity: 1 },
      ],
    };
    const next = applyCartChanged(cart, {
      items: [
        { index: 0, name: 'Marmita M', available: false, unitPrice: null, lineTotal: null },
        { index: 1, name: 'Marmita M', available: true, unitPrice: '20.00', lineTotal: '40.00' },
        { index: 2, name: 'Coca-Cola 350ml', available: true, unitPrice: '6.00', lineTotal: '6.00' },
      ],
      unavailableProductIds: ['costela', 'feijao', 'ovo'],
      itemsTotal: '46.00',
      deliveryFee: '0.00',
      totalAmount: '46.00',
    });
    expect(next.lines.map((l) => l.id)).toEqual(['b', 'c']);
    const b = next.lines[0] as MarmitaLine;
    expect(b.sideDishIds).toEqual(['arroz']);
    expect(b.addons).toEqual([]);
    expect(previewCart(next, menu).totalCents).toBe(4600);
  });
});

describe('bebida de preço zero (§6.5: sitePrice >= 0 na bebida do restaurante)', () => {
  const menuComAgua: PublicMenu = {
    ...menu,
    beverages: [
      ...menu.beverages,
      { productId: 'agua', name: 'Água da casa', description: null, imageUrl: null, price: '0.00', isAvailable: true },
    ],
  };
  const agua = { kind: 'BEVERAGE', id: 'l3', productId: 'agua', quantity: 2 } as const;

  it('entra no carrinho, soma como linha de R$ 0,00 e não muda o total', () => {
    const cart = addLine(contractCart, agua);
    expect(cart.lines).toHaveLength(3);
    const preview = previewCart(cart, menuComAgua);
    expect(preview.lines[2]).toMatchObject({ title: 'Água da casa', unitCents: 0, lineCents: 0, unavailable: false });
    expect(formatBRL(preview.lines[2]!.lineCents)).toBe('R$ 0,00');
    expect(preview.itemsCents).toBe(5000);
    expect(preview.totalCents).toBe(5000);
    expect(expectedTotal(preview)).toBe('50.00');
    expect(previewCart(cart, menuComAgua, 500).totalCents).toBe(5500);
  });

  it('a mesma bebida de graça soma a quantidade na linha existente', () => {
    const cart = addLine(addLine(contractCart, agua), { ...agua, id: 'l4', quantity: 1 });
    expect(cart.lines.filter((l) => l.kind === 'BEVERAGE' && l.productId === 'agua')).toEqual([{ ...agua, quantity: 3 }]);
    expect(previewCart(cart, menuComAgua).totalCents).toBe(5000);
  });

  it('nenhuma validação local recusa a linha de preço zero; vai no envio e sobrevive ao carrinho guardado', () => {
    const cart = addLine(contractCart, agua);
    expect(validateCart(cart, menuComAgua)).toEqual([]);
    expect(toOrderItems(cart)[2]).toMatchObject({ itemKind: 'BEVERAGE', productId: 'agua', quantity: 2 });
    expect(sanitizeCart(JSON.parse(JSON.stringify(cart)))).toEqual(cart);
  });

  it('o 409 CartChanged mantém a bebida de graça disponível', () => {
    const cart = addLine(contractCart, agua);
    const next = applyCartChanged(cart, {
      items: [
        { index: 0, name: 'Marmita M', available: true, unitPrice: '22.00', lineTotal: '44.00' },
        { index: 1, name: 'Coca-Cola 350ml', available: true, unitPrice: '6.00', lineTotal: '6.00' },
        { index: 2, name: 'Água da casa', available: true, unitPrice: '0.00', lineTotal: '0.00' },
      ],
      unavailableProductIds: [],
      itemsTotal: '50.00',
      deliveryFee: '0.00',
      totalAmount: '50.00',
    });
    expect(next).toEqual(cart);
  });
});
