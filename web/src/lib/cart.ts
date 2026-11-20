import { parseMoney, toApiMoney, type Cents } from './money';
import type { PublicLimits, PublicMenu, RecalculatedCart, WebOrderItemInput } from './api/types';

/**
 * Carrinho da página (só no navegador, ADR-0052). A prévia do preço segue o ADR-0020: marmita = (tamanho +
 * Σ adicionais) × quantidade; bebida = preço × quantidade. **O valor oficial é o do servidor** (ADR-0044 item 21):
 * a prévia vira o `expectedTotal`, e se divergir a API responde `409 CartChanged`.
 */

export interface CartAddon {
  productId: string;
  /** Quantidade por marmita. */
  quantity: number;
}

export interface MarmitaLine {
  kind: 'MARMITA';
  id: string;
  sizeId: string;
  mainDishIds: string[];
  sideDishIds: string[];
  addons: CartAddon[];
  quantity: number;
  notes: string;
}

export interface BeverageLine {
  kind: 'BEVERAGE';
  id: string;
  productId: string;
  quantity: number;
}

export type CartLine = MarmitaLine | BeverageLine;

export interface Cart {
  lines: CartLine[];
}

export const EMPTY_CART: Cart = { lines: [] };

export const DEFAULT_LIMITS: PublicLimits = { maxMarmitas: 20, maxLines: 30, maxQuantityPerLine: 20, notesMaxLength: 200 };

export function newLineId(): string {
  return typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
    ? crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

export function addLine(cart: Cart, line: CartLine): Cart {
  if (line.kind === 'BEVERAGE') {
    const existing = cart.lines.find((l): l is BeverageLine => l.kind === 'BEVERAGE' && l.productId === line.productId);
    if (existing) {
      return setQuantity(cart, existing.id, existing.quantity + line.quantity);
    }
  }
  return { lines: [...cart.lines, line] };
}

export function removeLine(cart: Cart, lineId: string): Cart {
  return { lines: cart.lines.filter((l) => l.id !== lineId) };
}

export function setQuantity(cart: Cart, lineId: string, quantity: number, max = DEFAULT_LIMITS.maxQuantityPerLine): Cart {
  if (quantity <= 0) return removeLine(cart, lineId);
  const q = Math.min(Math.floor(quantity), max);
  return { lines: cart.lines.map((l) => (l.id === lineId ? { ...l, quantity: q } : l)) };
}

export function marmitaCount(cart: Cart): number {
  return cart.lines.reduce((sum, l) => sum + (l.kind === 'MARMITA' ? l.quantity : 0), 0);
}

export function itemCount(cart: Cart): number {
  return cart.lines.reduce((sum, l) => sum + l.quantity, 0);
}

export interface LinePreview {
  lineId: string;
  /** Preço de uma unidade (marmita: tamanho + adicionais). */
  unitCents: Cents;
  lineCents: Cents;
  /** Algum item da linha saiu do cardápio ou acabou. */
  unavailable: boolean;
  title: string;
  details: string[];
}

export interface CartPreview {
  lines: LinePreview[];
  itemsCents: Cents;
  deliveryFeeCents: Cents;
  totalCents: Cents;
  hasUnavailable: boolean;
}

function byId<T extends { productId: string }>(list: T[]): Map<string, T> {
  return new Map(list.map((x) => [x.productId, x]));
}

export function previewLine(line: CartLine, menu: PublicMenu): LinePreview {
  if (line.kind === 'BEVERAGE') {
    const product = menu.beverages.find((b) => b.productId === line.productId);
    if (!product) {
      return { lineId: line.id, unitCents: 0, lineCents: 0, unavailable: true, title: 'Bebida indisponível', details: [] };
    }
    const unit = parseMoney(product.price);
    return {
      lineId: line.id,
      unitCents: unit,
      lineCents: unit * line.quantity,
      unavailable: !product.isAvailable,
      title: product.name,
      details: [],
    };
  }

  const size = menu.marmitaSizes.find((s) => s.id === line.sizeId);
  const mains = byId(menu.mainDishes);
  const sides = byId(menu.sideDishes);
  const addons = byId(menu.addons);
  let unavailable = !size;
  let unit = size ? parseMoney(size.price) : 0;
  const details: string[] = [];

  const mainNames = line.mainDishIds.map((id) => {
    const dish = mains.get(id);
    if (!dish || !dish.isAvailable) unavailable = true;
    return dish?.name ?? 'Prato indisponível';
  });
  if (line.mainDishIds.length === 0) unavailable = true;
  if (mainNames.length) details.push(mainNames.join(', '));

  const sideNames = line.sideDishIds.map((id) => {
    const dish = sides.get(id);
    if (!dish || !dish.isAvailable) unavailable = true;
    return dish?.name ?? 'Acompanhamento indisponível';
  });
  if (sideNames.length) details.push(sideNames.join(', '));

  for (const addon of line.addons) {
    const product = addons.get(addon.productId);
    if (!product || !product.isAvailable) {
      unavailable = true;
      details.push(`${addon.quantity}× ${product?.name ?? 'Adicional indisponível'}`);
      continue;
    }
    unit += parseMoney(product.price) * addon.quantity;
    details.push(`${addon.quantity}× ${product.name}`);
  }
  if (line.notes.trim()) details.push(`Obs.: ${line.notes.trim()}`);

  return {
    lineId: line.id,
    unitCents: unit,
    lineCents: unit * line.quantity,
    unavailable,
    title: size ? `Marmita ${size.name}` : 'Tamanho indisponível',
    details,
  };
}

export function previewCart(cart: Cart, menu: PublicMenu, deliveryFeeCents: Cents = 0): CartPreview {
  const lines = cart.lines.map((l) => previewLine(l, menu));
  const itemsCents = lines.reduce((sum, l) => sum + l.lineCents, 0);
  return {
    lines,
    itemsCents,
    deliveryFeeCents,
    totalCents: itemsCents + deliveryFeeCents,
    hasUnavailable: lines.some((l) => l.unavailable),
  };
}

export type CartIssue =
  | { code: 'EMPTY' }
  | { code: 'NO_MARMITA' }
  | { code: 'TOO_MANY_MARMITAS'; max: number }
  | { code: 'TOO_MANY_LINES'; max: number }
  | { code: 'QUANTITY_OUT_OF_RANGE'; lineId: string; max: number }
  | { code: 'MISSING_MAIN_DISH'; lineId: string }
  | { code: 'NOTES_TOO_LONG'; lineId: string; max: number }
  | { code: 'UNAVAILABLE'; lineId: string };

/** As regras de formato da API (§6.19, checagem 1) conferidas antes do envio. */
export function validateCart(cart: Cart, menu: PublicMenu, limits: PublicLimits = DEFAULT_LIMITS): CartIssue[] {
  const issues: CartIssue[] = [];
  if (cart.lines.length === 0) return [{ code: 'EMPTY' }];
  const marmitas = marmitaCount(cart);
  if (marmitas === 0) issues.push({ code: 'NO_MARMITA' });
  if (marmitas > limits.maxMarmitas) issues.push({ code: 'TOO_MANY_MARMITAS', max: limits.maxMarmitas });
  if (cart.lines.length > limits.maxLines) issues.push({ code: 'TOO_MANY_LINES', max: limits.maxLines });
  for (const line of cart.lines) {
    if (line.quantity < 1 || line.quantity > limits.maxQuantityPerLine) {
      issues.push({ code: 'QUANTITY_OUT_OF_RANGE', lineId: line.id, max: limits.maxQuantityPerLine });
    }
    if (line.kind === 'MARMITA') {
      if (line.mainDishIds.length === 0) issues.push({ code: 'MISSING_MAIN_DISH', lineId: line.id });
      if (line.notes.length > limits.notesMaxLength) {
        issues.push({ code: 'NOTES_TOO_LONG', lineId: line.id, max: limits.notesMaxLength });
      }
    }
    if (previewLine(line, menu).unavailable) issues.push({ code: 'UNAVAILABLE', lineId: line.id });
  }
  return issues;
}

export function describeIssue(issue: CartIssue): string {
  switch (issue.code) {
    case 'EMPTY':
      return 'Seu pedido está vazio.';
    case 'NO_MARMITA':
      return 'O pedido precisa ter pelo menos 1 marmita.';
    case 'TOO_MANY_MARMITAS':
      return `Pela página dá para pedir até ${issue.max} marmitas. Para mais, fale com o restaurante.`;
    case 'TOO_MANY_LINES':
      return `O pedido pode ter até ${issue.max} itens diferentes. Para mais, fale com o restaurante.`;
    case 'QUANTITY_OUT_OF_RANGE':
      return `A quantidade de cada item vai de 1 a ${issue.max}.`;
    case 'MISSING_MAIN_DISH':
      return 'Escolha pelo menos 1 prato em cada marmita.';
    case 'NOTES_TOO_LONG':
      return `A observação pode ter até ${issue.max} caracteres.`;
    case 'UNAVAILABLE':
      return 'Um item do seu pedido acabou. Tire-o do pedido para continuar.';
  }
}

/** Linhas do carrinho → `items` do `POST /orders`, na mesma ordem (o `CartChanged` responde por índice). */
export function toOrderItems(cart: Cart): WebOrderItemInput[] {
  return cart.lines.map((line) =>
    line.kind === 'MARMITA'
      ? {
          itemKind: 'MARMITA',
          marmitaSizeId: line.sizeId,
          productId: null,
          quantity: line.quantity,
          mainDishProductIds: [...line.mainDishIds],
          sideDishProductIds: [...line.sideDishIds],
          addons: line.addons.filter((a) => a.quantity > 0).map((a) => ({ productId: a.productId, quantity: a.quantity })),
          notes: line.notes.trim() ? line.notes.trim() : null,
        }
      : {
          itemKind: 'BEVERAGE',
          marmitaSizeId: null,
          productId: line.productId,
          quantity: line.quantity,
          mainDishProductIds: null,
          sideDishProductIds: null,
          addons: null,
          notes: null,
        },
  );
}

export function expectedTotal(preview: CartPreview): string {
  return toApiMoney(preview.totalCents);
}

/**
 * Aplica o carrinho recalculado do `409 CartChanged` (§6.19): tira as linhas indisponíveis (por índice) e, nas que
 * ficam, os acompanhamentos e adicionais que saíram (`unavailableProductIds`). Marmita que perde todos os pratos sai.
 */
export function applyCartChanged(cart: Cart, recalculated: RecalculatedCart): Cart {
  const unavailableIndexes = new Set(recalculated.items.filter((i) => !i.available).map((i) => i.index));
  const gone = new Set(recalculated.unavailableProductIds);
  const lines: CartLine[] = [];
  cart.lines.forEach((line, index) => {
    if (unavailableIndexes.has(index)) return;
    if (line.kind === 'BEVERAGE') {
      if (!gone.has(line.productId)) lines.push(line);
      return;
    }
    const mainDishIds = line.mainDishIds.filter((id) => !gone.has(id));
    if (mainDishIds.length === 0 || gone.has(line.sizeId)) return;
    lines.push({
      ...line,
      mainDishIds,
      sideDishIds: line.sideDishIds.filter((id) => !gone.has(id)),
      addons: line.addons.filter((a) => !gone.has(a.productId)),
    });
  });
  return { lines };
}

/** Lê um carrinho guardado, descartando o que não tiver o formato esperado. */
export function sanitizeCart(raw: unknown): Cart {
  if (!raw || typeof raw !== 'object' || !Array.isArray((raw as Cart).lines)) return EMPTY_CART;
  const lines = (raw as Cart).lines.filter((l): l is CartLine => {
    if (!l || typeof l !== 'object' || typeof l.id !== 'string' || typeof l.quantity !== 'number') return false;
    if (l.kind === 'BEVERAGE') return typeof l.productId === 'string';
    if (l.kind === 'MARMITA') {
      return typeof l.sizeId === 'string' && Array.isArray(l.mainDishIds) && Array.isArray(l.sideDishIds)
        && Array.isArray(l.addons) && typeof l.notes === 'string';
    }
    return false;
  });
  return { lines };
}
