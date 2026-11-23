import { describe, expect, it } from 'vitest';
import type { PublicFixedMenu, PublicMenu, TrackingItem } from './api/types';
import {
  addLine,
  applyCartChanged,
  canAddLine,
  cartOrderType,
  DEFAULT_LIMITS,
  expectedTotal,
  previewCart,
  sanitizeCart,
  toOrderItems,
  validateCart,
  type Cart,
  type ComboLine,
  type FixedBeverageLine,
  type FixedDishLine,
  type MarmitaLine,
} from './cart';
import { buildOrderRequest, EMPTY_ADDRESS, interpretSubmitFailure, splitFieldErrors, type CheckoutForm } from './checkout';
import {
  beveragePriceLabel,
  buildBeverageLine,
  buildComboLine,
  buildDishLine,
  catalogOf,
  dishPriceLabel,
  emptyBeverageDraft,
  emptyComboDraft,
  emptyDishDraft,
  indexFixedMenu,
  pageTabs,
  storeSections,
} from './fixed-menu';
import { fixedMenu } from './test-fixtures';
import { trackingItemTotalCents } from './tracking';

const catalog = { menu: null, fixedMenu };
const index = indexFixedMenu(fixedMenu);

const combo = (over: Partial<ComboLine> = {}): ComboLine => ({
  kind: 'COMBO',
  id: 'l-combo',
  comboId: 'combo',
  quantity: 1,
  parts: [
    { componentId: 'c-x', flavorId: null, addons: [{ optionId: 'bacon', quantity: 1 }], notes: 'Sem cebola' },
    { componentId: 'c-refri', flavorId: 'zero', addons: [], notes: '' },
  ],
  ...over,
});
const xburguer = (over: Partial<FixedDishLine> = {}): FixedDishLine => ({
  kind: 'FIXED_DISH',
  id: 'l-x',
  dishId: 'xburguer',
  sizeId: null,
  addons: [],
  quantity: 1,
  notes: '',
  ...over,
});
const lata = (over: Partial<FixedBeverageLine> = {}): FixedBeverageLine => ({
  kind: 'FIXED_BEVERAGE',
  id: 'l-lata',
  sizeId: 'lata',
  flavorId: 'coca',
  quantity: 1,
  ...over,
});

/** O pedido do exemplo do contrato: 1 combo com bacon e Coca Zero, 1 X-Burguer e 1 lata de Coca-Cola. */
const contractCart: Cart = { lines: [combo(), xburguer(), lata()] };

const marmitaMenu: PublicMenu = {
  marmitaSizes: [{ id: 'size-m', name: 'M', price: '20.00' }],
  mainDishes: [{ productId: 'frango', name: 'Frango', description: null, imageUrl: null, isAvailable: true }],
  sideDishes: [],
  addons: [],
  beverages: [],
};
const marmita: MarmitaLine = {
  kind: 'MARMITA',
  id: 'm1',
  sizeId: 'size-m',
  mainDishIds: ['frango'],
  sideDishIds: [],
  addons: [],
  quantity: 1,
  notes: '',
};

describe('preço do carrinho da lanchonete (ADR-0058 item 9, §6.19)', () => {
  it('reproduz o exemplo do contrato: 34,90 + 4,00 + 0,50 + 23,90 + 6,50 = 69,80', () => {
    const preview = previewCart(contractCart, catalog);
    expect(preview.lines.map((l) => l.lineCents)).toEqual([3940, 2390, 650]);
    expect(expectedTotal(preview)).toBe('69.80');
    expect(preview.hasUnavailable).toBe(false);
  });

  it('2 combos dobram a linha inteira: 2 × (34,90 + 4,00 + 0,50) = 78,80', () => {
    expect(expectedTotal(previewCart({ lines: [combo({ quantity: 2 })] }, catalog))).toBe('78.80');
  });

  it('adicional da parte conta por unidade do componente', () => {
    const menu: PublicFixedMenu = {
      ...fixedMenu,
      combos: [{ ...fixedMenu.combos[0]!, components: fixedMenu.combos[0]!.components.map((c) => (c.id === 'c-x' ? { ...c, quantity: 2 } : c)) }],
    };
    // 34,90 + 2 × 4,00 + 0,50
    expect(previewCart({ lines: [combo()] }, { menu: null, fixedMenu: menu }).totalCents).toBe(4340);
  });

  it('prato com tamanho e adicionais: (tamanho + Σ adicionais) × quantidade', () => {
    const cart: Cart = {
      lines: [xburguer({ addons: [{ optionId: 'bacon', quantity: 2 }, { optionId: 'ovo', quantity: 1 }], quantity: 3 }), { ...xburguer(), id: 'b', dishId: 'batata', sizeId: 'batata-g' }],
    };
    const preview = previewCart(cart, catalog);
    expect(preview.lines[0]!.lineCents).toBe(3 * (2390 + 800 + 250));
    expect(preview.lines[1]!.title).toBe('Batata frita G');
    expect(preview.lines[1]!.lineCents).toBe(1900);
  });

  it('nomes de exibição e detalhes do combo', () => {
    const [comboPreview, , lataPreview] = previewCart(contractCart, catalog).lines;
    expect(lataPreview!.title).toBe('Refrigerante Lata 350 ml · Coca-Cola');
    expect(comboPreview!.details).toEqual([
      'X-Burguer',
      '  + 1× Bacon',
      '  Obs.: Sem cebola',
      'Batata frita P',
      'Refrigerante Lata 350 ml · Coca-Cola Zero',
    ]);
  });

  it('item pausado (some do cardápio) fica indisponível; sabor faltando fica incompleto', () => {
    const paused: PublicFixedMenu = { ...fixedMenu, combos: [] };
    const preview = previewCart(contractCart, { menu: null, fixedMenu: paused });
    expect(preview.lines[0]!.unavailable).toBe(true);
    expect(validateCart(contractCart, { menu: null, fixedMenu: paused }).map((i) => i.code)).toContain('UNAVAILABLE');
    const noFlavor = { lines: [xburguer(), lata({ flavorId: null })] };
    expect(validateCart(noFlavor, catalog).map((i) => i.code)).toEqual(['INCOMPLETE']);
  });

  it('rótulos de preço da lista', () => {
    expect(dishPriceLabel(fixedMenu.categories[0]!.dishes[0]!)).toBe('R$ 23,90');
    expect(dishPriceLabel(fixedMenu.categories[1]!.dishes[0]!)).toBe('a partir de R$ 13,00');
    expect(beveragePriceLabel(fixedMenu.beverages[0]!)).toBe('a partir de R$ 6,50');
    expect(beveragePriceLabel(fixedMenu.beverages[1]!)).toBe('R$ 4,00');
  });
});

describe('corpo do envio A_LA_CARTE (§6.19)', () => {
  it('itens no formato do contrato, na ordem do carrinho; componente sem escolha fica fora de parts', () => {
    expect(toOrderItems(contractCart)).toEqual([
      {
        itemKind: 'COMBO',
        comboId: 'combo',
        quantity: 1,
        notes: null,
        parts: [
          { comboComponentId: 'c-x', beverageFlavorId: null, notes: 'Sem cebola', groupAddons: [{ addonOptionId: 'bacon', quantity: 1 }] },
          { comboComponentId: 'c-refri', beverageFlavorId: 'zero', notes: null, groupAddons: null },
        ],
      },
      { itemKind: 'FIXED_DISH', dishId: 'xburguer', dishSizeId: null, quantity: 1, notes: null, groupAddons: [] },
      { itemKind: 'FIXED_BEVERAGE', beverageSizeId: 'lata', beverageFlavorId: 'coca', quantity: 1, notes: null },
    ]);
  });

  it('manda orderType do carrinho e o expectedTotal da prévia', () => {
    const form: CheckoutForm = {
      name: 'Carla Souza',
      phone: '(34) 98888-7777',
      fulfillmentType: 'PICKUP',
      address: EMPTY_ADDRESS,
      deliveryQuoteId: null,
      paymentMethod: 'PIX',
      changeFor: '',
      notes: 'Sem talher',
      website: '',
    };
    const body = buildOrderRequest(form, contractCart, previewCart(contractCart, catalog));
    expect(body.orderType).toBe('A_LA_CARTE');
    expect(body.expectedTotal).toBe('69.80');
    expect(buildOrderRequest(form, { lines: [marmita] }, previewCart({ lines: [marmita] }, marmitaMenu)).orderType).toBe('MARMITA');
  });
});

describe('regras de seleção', () => {
  const dish = fixedMenu.categories[0]!.dishes[0]!;
  const batata = fixedMenu.categories[1]!.dishes[0]!;
  const refri = fixedMenu.beverages[0]!;
  const comboItem = fixedMenu.combos[0]!;

  it('prato com tamanho exige o tamanho; sem tamanho manda dishSizeId null', () => {
    expect(buildDishLine(batata, emptyDishDraft(batata), index, DEFAULT_LIMITS, 'x')).toEqual({ ok: false, errors: { size: 'Escolha o tamanho.' } });
    const ok = buildDishLine(batata, { ...emptyDishDraft(batata), sizeId: 'batata-p' }, index, DEFAULT_LIMITS, 'x');
    expect(ok.ok && ok.line.sizeId).toBe('batata-p');
    const plain = buildDishLine(dish, { ...emptyDishDraft(dish), sizeId: 'qualquer' }, index, DEFAULT_LIMITS, 'x');
    expect(plain.ok && plain.line.sizeId).toBeNull();
  });

  it('adicionais: só dos grupos do prato, de 1 a 10 cada, zeros ficam fora', () => {
    const draft = { ...emptyDishDraft(dish), addons: { bacon: 2, ovo: 0 }, notes: '  sem cebola ' };
    const ok = buildDishLine(dish, draft, index, DEFAULT_LIMITS, 'x');
    expect(ok).toEqual({ ok: true, line: { kind: 'FIXED_DISH', id: 'x', dishId: 'xburguer', sizeId: null, addons: [{ optionId: 'bacon', quantity: 2 }], quantity: 1, notes: 'sem cebola' } });
    expect(buildDishLine(dish, { ...draft, addons: { bacon: 11 } }, index, DEFAULT_LIMITS, 'x').ok).toBe(false);
    // A batata não tem grupo de adicionais.
    expect(buildDishLine(batata, { ...emptyDishDraft(batata), sizeId: 'batata-p', addons: { bacon: 1 } }, index, DEFAULT_LIMITS, 'x').ok).toBe(false);
    expect(buildDishLine(dish, { ...draft, notes: 'x'.repeat(201) }, index, DEFAULT_LIMITS, 'x')).toMatchObject({ ok: false, errors: { notes: expect.any(String) } });
  });

  it('bebida: tamanho → sabor obrigatório quando há sabores', () => {
    const draft = emptyBeverageDraft(refri);
    expect(draft.sizeId).toBe('lata');
    expect(buildBeverageLine(refri, draft, DEFAULT_LIMITS, 'x')).toEqual({ ok: false, errors: { flavor: 'Escolha o sabor.' } });
    expect(buildBeverageLine(refri, { ...draft, flavorId: 'zero' }, DEFAULT_LIMITS, 'x')).toEqual({
      ok: true,
      line: { kind: 'FIXED_BEVERAGE', id: 'x', sizeId: 'lata', flavorId: 'zero', quantity: 1 },
    });
    const agua = fixedMenu.beverages[1]!;
    const okAgua = buildBeverageLine(agua, { ...emptyBeverageDraft(agua), flavorId: 'coca' }, DEFAULT_LIMITS, 'x');
    expect(okAgua.ok && okAgua.line.flavorId).toBeNull();
    expect(buildBeverageLine(refri, { sizeId: null, flavorId: null, quantity: 1 }, DEFAULT_LIMITS, 'x')).toMatchObject({ ok: false, errors: { size: expect.any(String) } });
  });

  it('combo: sabor obrigatório na bebida; adicionais e observação por prato', () => {
    const draft = emptyComboDraft(comboItem);
    expect(buildComboLine(comboItem, draft, index, DEFAULT_LIMITS, 'x')).toEqual({ ok: false, errors: { 'parts.c-refri.flavor': 'Escolha o sabor.' } });
    draft.parts['c-refri']!.flavorId = 'zero';
    draft.parts['c-x']!.addons = { bacon: 1 };
    draft.parts['c-x']!.notes = 'Sem cebola';
    const ok = buildComboLine(comboItem, draft, index, DEFAULT_LIMITS, 'l-combo');
    expect(ok).toEqual({ ok: true, line: combo() });
    // A batata do combo não tem adicionais.
    draft.parts['c-batata']!.addons = { bacon: 1 };
    expect(buildComboLine(comboItem, draft, index, DEFAULT_LIMITS, 'x')).toMatchObject({ ok: false, errors: { 'parts.c-batata.addons': expect.any(String) } });
  });

  it('quantidade de 1 a 20', () => {
    expect(buildDishLine(dish, { ...emptyDishDraft(dish), quantity: 21 }, index, DEFAULT_LIMITS, 'x')).toMatchObject({ ok: false, errors: { quantity: expect.any(String) } });
  });
});

describe('um tipo por pedido (Q-317)', () => {
  it('carrinho com marmita não aceita item do cardápio fixo, e vice-versa', () => {
    const marmitaCart: Cart = { lines: [marmita] };
    expect(canAddLine(marmitaCart, xburguer())).toEqual({ ok: false, current: 'MARMITA' });
    expect(canAddLine(contractCart, { kind: 'BEVERAGE', id: 'b', productId: 'coca', quantity: 1 })).toEqual({ ok: false, current: 'A_LA_CARTE' });
    expect(canAddLine(contractCart, xburguer({ id: 'y' }))).toEqual({ ok: true });
    expect(canAddLine({ lines: [] }, marmita)).toEqual({ ok: true });
    expect(cartOrderType(contractCart)).toBe('A_LA_CARTE');
    expect(cartOrderType({ lines: [] })).toBeNull();
  });

  it('carrinho misturado (guardado de outra versão) não passa na validação', () => {
    const mixed: Cart = { lines: [marmita, xburguer()] };
    expect(validateCart(mixed, { menu: marmitaMenu, fixedMenu }).map((i) => i.code)).toContain('MIXED_TYPES');
  });

  it('mesma bebida com o mesmo sabor soma; sabor diferente vira outra linha', () => {
    let cart: Cart = { lines: [xburguer()] };
    cart = addLine(cart, lata({ id: 'a' }));
    cart = addLine(cart, lata({ id: 'b' }));
    cart = addLine(cart, lata({ id: 'c', flavorId: 'zero' }));
    expect(cart.lines.map((l) => [l.id, l.quantity])).toEqual([
      ['l-x', 1],
      ['a', 2],
      ['c', 1],
    ]);
  });
});

describe('limites da lanchonete (Q-318)', () => {
  const codes = (cart: Cart) => validateCart(cart, catalog).map((i) => i.code);

  it('carrinho do contrato é válido', () => {
    expect(codes(contractCart)).toEqual([]);
  });

  it('só bebidas não é pedido', () => {
    expect(codes({ lines: [lata()] })).toContain('NO_DISH_OR_COMBO');
  });

  it('até 20 pratos e combos somados; bebidas não contam', () => {
    expect(codes({ lines: [xburguer({ quantity: 10 }), combo({ quantity: 10 }), lata({ quantity: 20 })] })).toEqual([]);
    expect(codes({ lines: [xburguer({ quantity: 11 }), combo({ quantity: 10 })] })).toContain('TOO_MANY_DISHES');
  });

  it('até 30 linhas, 1 a 20 por linha e adicionais de 1 a 10', () => {
    const many: Cart = { lines: Array.from({ length: 31 }, (_, i) => lata({ id: `b${i}` })).concat([]) };
    many.lines.unshift(xburguer());
    expect(codes(many)).toContain('TOO_MANY_LINES');
    expect(codes({ lines: [xburguer({ quantity: 21 })] })).toContain('QUANTITY_OUT_OF_RANGE');
    expect(codes({ lines: [xburguer({ addons: [{ optionId: 'bacon', quantity: 11 }] })] })).toContain('ADDONS_OUT_OF_RANGE');
  });
});

describe('erros do envio', () => {
  it('422 por campo: linha do carrinho (inclusive partes do combo), bloco dos itens e formulário', () => {
    const outcome = interpretSubmitFailure({
      kind: 'problem',
      status: 422,
      retryAfterSeconds: null,
      problem: {
        status: 422,
        errorCode: 'ValidationFailed',
        errors: {
          'items[1].dishSizeId': ['Escolha o tamanho de Batata frita.'],
          'items[0].parts[1].beverageFlavorId': ['Escolha o sabor de Refrigerante Lata 350 ml.'],
          'items[0].itemKind': ['O carrinho aceita um tipo de pedido por vez.'],
          Items: ['O pedido precisa de pelo menos 1 prato ou combo.'],
          OrderType: ['A página não vende este tipo.'],
          CustomerPhone: ['Telefone inválido.'],
        },
      },
    });
    expect(outcome.kind).toBe('fieldErrors');
    if (outcome.kind !== 'fieldErrors') return;
    const split = splitFieldErrors(outcome.errors);
    expect(split.lines).toEqual({
      0: 'Escolha o sabor de Refrigerante Lata 350 ml. O carrinho aceita um tipo de pedido por vez.',
      1: 'Escolha o tamanho de Batata frita.',
    });
    expect(split.items).toBe('O pedido precisa de pelo menos 1 prato ou combo. A página não vende este tipo.');
    expect(split.fields).toEqual({ customerPhone: 'Telefone inválido.' });
  });

  it('409 CartChanged: tira as linhas indisponíveis e os ids do cardápio fixo que saíram', () => {
    const cart: Cart = {
      lines: [
        combo(),
        xburguer({ addons: [{ optionId: 'bacon', quantity: 1 }, { optionId: 'ovo', quantity: 1 }] }),
        lata(),
        lata({ id: 'zero', flavorId: 'zero' }),
      ],
    };
    const next = applyCartChanged(cart, {
      items: [
        { index: 0, name: 'X-Burguer + batata + refri', available: false, unitPrice: null, lineTotal: null },
        { index: 1, name: 'X-Burguer', available: true, unitPrice: '23.90', lineTotal: '26.40' },
        { index: 2, name: 'Refrigerante Lata 350 ml · Coca-Cola', available: true, unitPrice: '6.50', lineTotal: '6.50' },
        { index: 3, name: 'Refrigerante Lata 350 ml · Coca-Cola Zero', available: true, unitPrice: '7.00', lineTotal: '7.00' },
      ],
      unavailableProductIds: ['combo', 'batata-p', 'bacon', 'zero'],
      itemsTotal: '32.90',
      deliveryFee: '0.00',
      totalAmount: '32.90',
    });
    expect(next.lines.map((l) => l.id)).toEqual(['l-x', 'l-lata']);
    expect((next.lines[0] as FixedDishLine).addons).toEqual([{ optionId: 'ovo', quantity: 1 }]);
  });
});

describe('seções e abas da página', () => {
  it('lanchonete: uma aba por categoria, Bebidas e Combos', () => {
    expect(pageTabs(['A_LA_CARTE'], fixedMenu).map((t) => t.label)).toEqual(['Lanches', 'Porções', 'Bebidas', 'Combos']);
  });
  it('as duas seções: Marmitas e as abas da lanchonete', () => {
    expect(pageTabs(['MARMITA', 'A_LA_CARTE'], fixedMenu).map((t) => t.label)).toEqual(['Marmitas', 'Lanches', 'Porções', 'Bebidas', 'Combos']);
  });
  it('restaurante: só Marmitas; abas vazias não aparecem', () => {
    expect(pageTabs(['MARMITA'], null).map((t) => t.kind)).toEqual(['MARMITA']);
    expect(pageTabs(['A_LA_CARTE'], { ...fixedMenu, beverages: [], combos: [] }).map((t) => t.label)).toEqual(['Lanches', 'Porções']);
    expect(pageTabs(['A_LA_CARTE'], null)).toEqual([]);
  });
  it('perfil sem sections (API antiga) é só marmita; cardápios só em OPEN/PAUSED', () => {
    expect(storeSections({ status: 'OPEN', sections: undefined as never })).toEqual(['MARMITA']);
    expect(storeSections({ status: 'UNAVAILABLE', sections: undefined as never })).toEqual([]);
    expect(catalogOf({ status: 'CLOSED', menu: marmitaMenu, fixedMenu })).toEqual({ menu: null, fixedMenu: null });
  });
});

describe('carrinho guardado', () => {
  it('carrinho antigo de marmita continua valendo; linhas novas são conferidas', () => {
    expect(sanitizeCart({ lines: [marmita] }).lines).toEqual([marmita]);
    expect(sanitizeCart(JSON.parse(JSON.stringify(contractCart))).lines).toHaveLength(3);
    expect(sanitizeCart({ lines: [{ ...combo(), parts: [{ componentId: 1 }] }, { ...xburguer(), addons: 'x' }, lata({ sizeId: 3 as never })] }).lines).toEqual([]);
  });
});

describe('acompanhamento', () => {
  it('total do combo é a soma da árvore (34,90 + 4,00 + 0,50 = 39,40)', () => {
    const part = (name: string, lineTotal: string, addons: TrackingItem['addons'] = []): TrackingItem => ({
      itemKind: 'COMBO_PART',
      name,
      composition: null,
      quantity: 1,
      unitPrice: lineTotal,
      lineTotal,
      notes: null,
      addons,
      parts: [],
    });
    const item: TrackingItem = {
      itemKind: 'COMBO',
      name: 'X-Burguer + batata + refri',
      composition: null,
      quantity: 1,
      unitPrice: '34.90',
      lineTotal: '34.90',
      notes: null,
      addons: [],
      parts: [
        part('X-Burguer', '0.00', [{ name: 'Bacon', quantity: 1, unitPrice: '4.00', lineTotal: '4.00' }]),
        part('Batata frita P', '0.00'),
        part('Refrigerante Lata 350 ml · Coca-Cola Zero', '0.50'),
      ],
    };
    expect(trackingItemTotalCents(item)).toBe(3940);
  });
});
