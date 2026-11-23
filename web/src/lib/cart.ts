import { parseMoney, toApiMoney, type Cents } from './money';
import type {
  ComboPartInput,
  GroupAddonInput,
  PublicFixedMenu,
  PublicLimits,
  PublicMenu,
  RecalculatedCart,
  StoreSection,
  WebOrderItemInput,
} from './api/types';
import { addonOptionsFor, beverageName, indexFixedMenu, MAX_ADDON_OPTIONS_PER_DISH, type Catalog } from './fixed-menu';

/**
 * Carrinho da página (só no navegador, ADR-0052). A prévia do preço segue o ADR-0020: marmita = (tamanho +
 * Σ adicionais) × quantidade; bebida = preço × quantidade. Cardápio fixo (EP-003 fatia 5, ADR-0058 item 9): prato =
 * (preço + Σ adicionais) × quantidade; bebida = (tamanho + acréscimo do sabor) × quantidade; combo = (preço do combo +
 * Σ adicionais das partes × quantidade do componente + acréscimos de sabor × quantidade do componente) × quantidade.
 * Tudo em centavos. **O valor oficial é o do servidor** (ADR-0044 item 21): a prévia vira o `expectedTotal`, e se
 * divergir a API responde `409 CartChanged`.
 *
 * Um tipo de pedido por vez (Q-317): `MARMITA` (marmitas e bebidas do cardápio do dia) ou `A_LA_CARTE` (pratos,
 * bebidas e combos do cardápio fixo). O tipo sai das linhas; o formato guardado é o mesmo do EP-002, só com tipos de
 * linha novos, então carrinhos antigos de marmita continuam valendo sem migração.
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

/** Adicional de grupo do cardápio fixo: quantidade por prato (1 a `maxAddonQuantity`). */
export interface GroupAddon {
  optionId: string;
  quantity: number;
}

export interface FixedDishLine {
  kind: 'FIXED_DISH';
  id: string;
  dishId: string;
  /** Obrigatório no prato com tamanho; `null` no prato sem tamanho. */
  sizeId: string | null;
  addons: GroupAddon[];
  quantity: number;
  notes: string;
}

export interface FixedBeverageLine {
  kind: 'FIXED_BEVERAGE';
  id: string;
  sizeId: string;
  /** Obrigatório quando o tamanho tem sabores. */
  flavorId: string | null;
  quantity: number;
}

/** Escolhas de um componente: prato → adicionais e observação; bebida → sabor. Componente sem escolha fica fora. */
export interface ComboPart {
  componentId: string;
  flavorId: string | null;
  addons: GroupAddon[];
  notes: string;
}

export interface ComboLine {
  kind: 'COMBO';
  id: string;
  comboId: string;
  quantity: number;
  parts: ComboPart[];
}

export type FixedLine = FixedDishLine | FixedBeverageLine | ComboLine;
export type CartLine = MarmitaLine | BeverageLine | FixedLine;

export interface Cart {
  lines: CartLine[];
}

export const EMPTY_CART: Cart = { lines: [] };

export const DEFAULT_LIMITS: PublicLimits = {
  maxMarmitas: 20,
  maxDishesAndCombos: 20,
  maxLines: 30,
  maxQuantityPerLine: 20,
  maxAddonQuantity: 10,
  notesMaxLength: 200,
};

export function newLineId(): string {
  return typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
    ? crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

export function isFixedLine(line: CartLine): line is FixedLine {
  return line.kind === 'FIXED_DISH' || line.kind === 'FIXED_BEVERAGE' || line.kind === 'COMBO';
}

/** A seção (= `orderType`) de uma linha. */
export function lineOrderType(line: CartLine): StoreSection {
  return isFixedLine(line) ? 'A_LA_CARTE' : 'MARMITA';
}

/** O tipo do carrinho (o da primeira linha); `null` vazio. */
export function cartOrderType(cart: Cart): StoreSection | null {
  return cart.lines[0] ? lineOrderType(cart.lines[0]) : null;
}

export function isMixedCart(cart: Cart): boolean {
  const type = cartOrderType(cart);
  return cart.lines.some((l) => lineOrderType(l) !== type);
}

/** Um tipo por vez (Q-317): item de outra seção com o carrinho não vazio → a página avisa e oferece esvaziar. */
export function canAddLine(cart: Cart, line: CartLine): { ok: true } | { ok: false; current: StoreSection } {
  const current = cartOrderType(cart);
  return current === null || current === lineOrderType(line) ? { ok: true } : { ok: false, current };
}

/** O que o carrinho tem, para o aviso de troca de tipo ("Seu pedido já tem marmitas"). */
export const ORDER_TYPE_TEXT: Record<StoreSection, string> = {
  MARMITA: 'marmitas',
  A_LA_CARTE: 'itens do cardápio (lanches, bebidas e combos)',
};

/** Mesma bebida (mesmo produto, ou mesmo tamanho e sabor) soma na linha que já existe. */
export function addLine(cart: Cart, line: CartLine, max = DEFAULT_LIMITS.maxQuantityPerLine): Cart {
  const existing = cart.lines.find(
    (l) =>
      (line.kind === 'BEVERAGE' && l.kind === 'BEVERAGE' && l.productId === line.productId) ||
      (line.kind === 'FIXED_BEVERAGE' && l.kind === 'FIXED_BEVERAGE' && l.sizeId === line.sizeId && l.flavorId === line.flavorId),
  );
  if (existing) return setQuantity(cart, existing.id, existing.quantity + line.quantity, max);
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

/** Pratos e combos somados (Q-318); bebidas não contam. */
export function dishAndComboCount(cart: Cart): number {
  return cart.lines.reduce((sum, l) => sum + (l.kind === 'FIXED_DISH' || l.kind === 'COMBO' ? l.quantity : 0), 0);
}

export function itemCount(cart: Cart): number {
  return cart.lines.reduce((sum, l) => sum + l.quantity, 0);
}

export interface LinePreview {
  lineId: string;
  /** Preço de uma unidade (marmita: tamanho + adicionais; combo: combo + adicionais + acréscimos). */
  unitCents: Cents;
  lineCents: Cents;
  /** Algum item da linha saiu do cardápio ou acabou. */
  unavailable: boolean;
  /** Falta uma escolha obrigatória (tamanho ou sabor que o cardápio passou a exigir). */
  incomplete?: boolean;
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

/** O cardápio do dia sozinho (EP-002) ou os dois cardápios (`catalogOf`). */
export type CatalogSource = Catalog | PublicMenu | null;

function toCatalog(source: CatalogSource): Catalog {
  if (!source) return { menu: null, fixedMenu: null };
  return 'marmitaSizes' in source ? { menu: source, fixedMenu: null } : source;
}

function byId<T extends { productId: string }>(list: T[]): Map<string, T> {
  return new Map(list.map((x) => [x.productId, x]));
}

const missing = (lineId: string, title: string): LinePreview => ({
  lineId,
  unitCents: 0,
  lineCents: 0,
  unavailable: true,
  title,
  details: [],
});

export function previewLine(line: CartLine, source: CatalogSource): LinePreview {
  const catalog = toCatalog(source);
  if (isFixedLine(line)) {
    return catalog.fixedMenu ? previewFixedLine(line, catalog.fixedMenu) : missing(line.id, 'Item indisponível');
  }
  const menu = catalog.menu;
  if (!menu) return missing(line.id, line.kind === 'MARMITA' ? 'Marmita indisponível' : 'Bebida indisponível');

  if (line.kind === 'BEVERAGE') {
    const product = menu.beverages.find((b) => b.productId === line.productId);
    if (!product) return missing(line.id, 'Bebida indisponível');
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

/** Σ preço × quantidade dos adicionais de grupo; opção fora dos grupos permitidos → indisponível. */
function groupAddonsCents(
  addons: GroupAddon[],
  allowed: ReturnType<typeof addonOptionsFor>,
  details: string[],
  prefix = '',
): { cents: Cents; unavailable: boolean } {
  let cents = 0;
  let unavailable = false;
  for (const a of addons) {
    const option = allowed.get(a.optionId);
    if (!option) {
      unavailable = true;
      details.push(`${prefix}+ ${a.quantity}× Adicional indisponível`);
      continue;
    }
    cents += parseMoney(option.price) * a.quantity;
    details.push(`${prefix}+ ${a.quantity}× ${option.name}`);
  }
  return { cents, unavailable };
}

function previewFixedLine(line: FixedLine, fixedMenu: PublicFixedMenu): LinePreview {
  const index = indexFixedMenu(fixedMenu);
  const details: string[] = [];

  if (line.kind === 'FIXED_DISH') {
    // Pausado ou fora do site não vem no cardápio (§6.19): some da página e a linha fica indisponível.
    const dish = index.dishes.get(line.dishId);
    if (!dish) return missing(line.id, 'Prato indisponível');
    let unavailable = false;
    let incomplete = false;
    let unit = 0;
    let title = dish.name;
    if (dish.hasSizes) {
      const size = dish.sizes.find((s) => s.id === line.sizeId);
      if (!line.sizeId) incomplete = true;
      else if (!size) unavailable = true;
      else {
        unit = parseMoney(size.price);
        title = `${dish.name} ${size.name}`;
      }
    } else if (line.sizeId || !dish.price) {
      unavailable = true;
    } else {
      unit = parseMoney(dish.price);
    }
    const addons = groupAddonsCents(line.addons, addonOptionsFor(dish.addonGroupIds, index), details);
    unit += addons.cents;
    if (line.notes.trim()) details.push(`Obs.: ${line.notes.trim()}`);
    return {
      lineId: line.id,
      unitCents: unit,
      lineCents: unit * line.quantity,
      unavailable: unavailable || addons.unavailable,
      incomplete,
      title,
      details,
    };
  }

  if (line.kind === 'FIXED_BEVERAGE') {
    const found = index.beverageSizes.get(line.sizeId);
    if (!found) return missing(line.id, 'Bebida indisponível');
    const { type, size } = found;
    let unit = parseMoney(size.price);
    let unavailable = false;
    let incomplete = false;
    let flavorName: string | null = null;
    if (size.flavors.length === 0) {
      if (line.flavorId) unavailable = true;
    } else if (!line.flavorId) {
      incomplete = true;
    } else {
      const flavor = size.flavors.find((f) => f.id === line.flavorId);
      if (!flavor) unavailable = true;
      else {
        unit += parseMoney(flavor.extraPrice);
        flavorName = flavor.name;
      }
    }
    return {
      lineId: line.id,
      unitCents: unit,
      lineCents: unit * line.quantity,
      unavailable,
      incomplete,
      title: beverageName(type.name, size.name, flavorName),
      details,
    };
  }

  const combo = index.combos.get(line.comboId);
  if (!combo) return missing(line.id, 'Combo indisponível');
  let unit = parseMoney(combo.price);
  let unavailable = line.parts.some((p) => !combo.components.some((c) => c.id === p.componentId));
  let incomplete = false;
  const parts = new Map(line.parts.map((p) => [p.componentId, p]));
  for (const c of combo.components) {
    const part = parts.get(c.id);
    const label = `${c.quantity > 1 ? `${c.quantity}× ` : ''}${c.name}`;
    if (c.kind === 'DISH') {
      details.push(label);
      if (part) {
        const addons = groupAddonsCents(part.addons, addonOptionsFor(c.addonGroupIds, index), details, '  ');
        // ADR-0058 item 9: o adicional da parte conta por unidade do componente.
        unit += addons.cents * c.quantity;
        if (addons.unavailable || part.flavorId) unavailable = true;
        if (part.notes.trim()) details.push(`  Obs.: ${part.notes.trim()}`);
      }
      continue;
    }
    if (part && (part.addons.length > 0 || part.notes.trim())) unavailable = true;
    if (c.flavors.length === 0) {
      if (part?.flavorId) unavailable = true;
      details.push(label);
      continue;
    }
    const flavor = part?.flavorId ? c.flavors.find((f) => f.id === part.flavorId) : undefined;
    if (!part?.flavorId) incomplete = true;
    else if (!flavor) unavailable = true;
    else unit += parseMoney(flavor.extraPrice) * c.quantity;
    details.push(flavor ? `${label} · ${flavor.name}` : label);
  }
  return { lineId: line.id, unitCents: unit, lineCents: unit * line.quantity, unavailable, incomplete, title: combo.name, details };
}

export function previewCart(cart: Cart, source: CatalogSource, deliveryFeeCents: Cents = 0): CartPreview {
  const lines = cart.lines.map((l) => previewLine(l, source));
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
  | { code: 'MIXED_TYPES' }
  | { code: 'NO_MARMITA' }
  | { code: 'TOO_MANY_MARMITAS'; max: number }
  | { code: 'NO_DISH_OR_COMBO' }
  | { code: 'TOO_MANY_DISHES'; max: number }
  | { code: 'TOO_MANY_LINES'; max: number }
  | { code: 'QUANTITY_OUT_OF_RANGE'; lineId: string; max: number }
  | { code: 'MISSING_MAIN_DISH'; lineId: string }
  | { code: 'NOTES_TOO_LONG'; lineId: string; max: number }
  | { code: 'ADDONS_OUT_OF_RANGE'; lineId: string; max: number }
  | { code: 'INCOMPLETE'; lineId: string }
  | { code: 'UNAVAILABLE'; lineId: string };

function lineNotes(line: CartLine): string[] {
  if (line.kind === 'MARMITA' || line.kind === 'FIXED_DISH') return [line.notes];
  if (line.kind === 'COMBO') return line.parts.map((p) => p.notes);
  return [];
}

function lineGroupAddons(line: CartLine): GroupAddon[][] {
  if (line.kind === 'FIXED_DISH') return [line.addons];
  if (line.kind === 'COMBO') return line.parts.map((p) => p.addons);
  return [];
}

/** As regras de formato da API (§6.19, checagem 1; lanchonete: Q-318) conferidas antes do envio. */
export function validateCart(cart: Cart, source: CatalogSource, limits: PublicLimits = DEFAULT_LIMITS): CartIssue[] {
  const issues: CartIssue[] = [];
  if (cart.lines.length === 0) return [{ code: 'EMPTY' }];
  if (isMixedCart(cart)) issues.push({ code: 'MIXED_TYPES' });
  if (cartOrderType(cart) === 'A_LA_CARTE') {
    const dishes = dishAndComboCount(cart);
    if (dishes === 0) issues.push({ code: 'NO_DISH_OR_COMBO' });
    if (dishes > limits.maxDishesAndCombos) issues.push({ code: 'TOO_MANY_DISHES', max: limits.maxDishesAndCombos });
  } else {
    const marmitas = marmitaCount(cart);
    if (marmitas === 0) issues.push({ code: 'NO_MARMITA' });
    if (marmitas > limits.maxMarmitas) issues.push({ code: 'TOO_MANY_MARMITAS', max: limits.maxMarmitas });
  }
  if (cart.lines.length > limits.maxLines) issues.push({ code: 'TOO_MANY_LINES', max: limits.maxLines });
  for (const line of cart.lines) {
    if (line.quantity < 1 || line.quantity > limits.maxQuantityPerLine) {
      issues.push({ code: 'QUANTITY_OUT_OF_RANGE', lineId: line.id, max: limits.maxQuantityPerLine });
    }
    if (line.kind === 'MARMITA' && line.mainDishIds.length === 0) issues.push({ code: 'MISSING_MAIN_DISH', lineId: line.id });
    if (lineNotes(line).some((n) => n.length > limits.notesMaxLength)) {
      issues.push({ code: 'NOTES_TOO_LONG', lineId: line.id, max: limits.notesMaxLength });
    }
    const outOfRange = lineGroupAddons(line).some(
      (list) => list.length > MAX_ADDON_OPTIONS_PER_DISH || list.some((a) => a.quantity < 1 || a.quantity > limits.maxAddonQuantity),
    );
    if (outOfRange) issues.push({ code: 'ADDONS_OUT_OF_RANGE', lineId: line.id, max: limits.maxAddonQuantity });
    const preview = previewLine(line, source);
    if (preview.unavailable) issues.push({ code: 'UNAVAILABLE', lineId: line.id });
    else if (preview.incomplete) issues.push({ code: 'INCOMPLETE', lineId: line.id });
  }
  return issues;
}

export function describeIssue(issue: CartIssue): string {
  switch (issue.code) {
    case 'EMPTY':
      return 'Seu pedido está vazio.';
    case 'MIXED_TYPES':
      return 'O carrinho aceita um tipo de pedido por vez: envie as marmitas e os lanches em pedidos separados.';
    case 'NO_MARMITA':
      return 'O pedido precisa ter pelo menos 1 marmita.';
    case 'TOO_MANY_MARMITAS':
      return `Pela página dá para pedir até ${issue.max} marmitas. Para mais, fale com o restaurante.`;
    case 'NO_DISH_OR_COMBO':
      return 'O pedido precisa ter pelo menos 1 prato ou combo (só bebidas não dá).';
    case 'TOO_MANY_DISHES':
      return `Pela página dá para pedir até ${issue.max} pratos e combos. Para mais, fale com o restaurante.`;
    case 'TOO_MANY_LINES':
      return `O pedido pode ter até ${issue.max} itens diferentes. Para mais, fale com o restaurante.`;
    case 'QUANTITY_OUT_OF_RANGE':
      return `A quantidade de cada item vai de 1 a ${issue.max}.`;
    case 'MISSING_MAIN_DISH':
      return 'Escolha pelo menos 1 prato em cada marmita.';
    case 'NOTES_TOO_LONG':
      return `A observação pode ter até ${issue.max} caracteres.`;
    case 'ADDONS_OUT_OF_RANGE':
      return `Cada adicional vai de 1 a ${issue.max}, com até ${MAX_ADDON_OPTIONS_PER_DISH} adicionais diferentes por prato.`;
    case 'INCOMPLETE':
      return 'Falta escolher o tamanho ou o sabor deste item. Tire-o do pedido e escolha de novo.';
    case 'UNAVAILABLE':
      return 'Um item do seu pedido acabou. Tire-o do pedido para continuar.';
  }
}

const blankToNull = (v: string) => (v.trim() ? v.trim() : null);
const toGroupAddons = (addons: GroupAddon[]): GroupAddonInput[] =>
  addons.filter((a) => a.quantity > 0).map((a) => ({ addonOptionId: a.optionId, quantity: a.quantity }));

function toFixedOrderItem(line: FixedLine): WebOrderItemInput {
  switch (line.kind) {
    case 'FIXED_DISH':
      return {
        itemKind: 'FIXED_DISH',
        dishId: line.dishId,
        dishSizeId: line.sizeId,
        quantity: line.quantity,
        notes: blankToNull(line.notes),
        groupAddons: toGroupAddons(line.addons),
      };
    case 'FIXED_BEVERAGE':
      return {
        itemKind: 'FIXED_BEVERAGE',
        beverageSizeId: line.sizeId,
        beverageFlavorId: line.flavorId,
        quantity: line.quantity,
        notes: null,
      };
    case 'COMBO':
      return {
        itemKind: 'COMBO',
        comboId: line.comboId,
        quantity: line.quantity,
        notes: null,
        // Parte de bebida: só o sabor; parte de prato: observação e adicionais (§6.19).
        parts: line.parts.map(
          (p): ComboPartInput =>
            p.flavorId
              ? { comboComponentId: p.componentId, beverageFlavorId: p.flavorId, notes: null, groupAddons: null }
              : { comboComponentId: p.componentId, beverageFlavorId: null, notes: blankToNull(p.notes), groupAddons: toGroupAddons(p.addons) },
        ),
      };
  }
}

/** Linhas do carrinho → `items` do `POST /orders`, na mesma ordem (o `CartChanged` responde por índice). */
export function toOrderItems(cart: Cart): WebOrderItemInput[] {
  return cart.lines.map((line): WebOrderItemInput => {
    if (isFixedLine(line)) return toFixedOrderItem(line);
    return line.kind === 'MARMITA'
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
        };
  });
}

export function expectedTotal(preview: CartPreview): string {
  return toApiMoney(preview.totalCents);
}

/**
 * Aplica o carrinho recalculado do `409 CartChanged` (§6.19): tira as linhas indisponíveis (por índice) e, nas que
 * ficam, os acompanhamentos e adicionais que saíram (`unavailableProductIds`). Marmita que perde todos os pratos sai.
 * Cardápio fixo: sai a linha cujo prato, tamanho, bebida, sabor ou combo saiu (o sabor é escolha obrigatória); os
 * adicionais que saíram são tirados do prato e das partes do combo.
 */
export function applyCartChanged(cart: Cart, recalculated: RecalculatedCart): Cart {
  const unavailableIndexes = new Set(recalculated.items.filter((i) => !i.available).map((i) => i.index));
  const gone = new Set(recalculated.unavailableProductIds);
  const lines: CartLine[] = [];
  cart.lines.forEach((line, index) => {
    if (unavailableIndexes.has(index)) return;
    switch (line.kind) {
      case 'FIXED_DISH':
        if (gone.has(line.dishId) || (line.sizeId && gone.has(line.sizeId))) return;
        lines.push({ ...line, addons: line.addons.filter((a) => !gone.has(a.optionId)) });
        return;
      case 'FIXED_BEVERAGE':
        if (!gone.has(line.sizeId) && !(line.flavorId && gone.has(line.flavorId))) lines.push(line);
        return;
      case 'COMBO':
        if (gone.has(line.comboId) || line.parts.some((p) => p.flavorId && gone.has(p.flavorId))) return;
        lines.push({
          ...line,
          parts: line.parts
            .map((p) => ({ ...p, addons: p.addons.filter((a) => !gone.has(a.optionId)) }))
            .filter((p) => p.flavorId || p.addons.length > 0 || p.notes.trim()),
        });
        return;
      case 'BEVERAGE':
        if (!gone.has(line.productId)) lines.push(line);
        return;
      case 'MARMITA': {
        const mainDishIds = line.mainDishIds.filter((id) => !gone.has(id));
        if (mainDishIds.length === 0 || gone.has(line.sizeId)) return;
        lines.push({
          ...line,
          mainDishIds,
          sideDishIds: line.sideDishIds.filter((id) => !gone.has(id)),
          addons: line.addons.filter((a) => !gone.has(a.productId)),
        });
      }
    }
  });
  return { lines };
}

const isStr = (v: unknown): v is string => typeof v === 'string';
const isStrOrNull = (v: unknown) => v === null || typeof v === 'string';
const isObject = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object';
const isGroupAddons = (v: unknown) => Array.isArray(v) && v.every((a) => isObject(a) && isStr(a.optionId) && typeof a.quantity === 'number');

/**
 * Lê um carrinho guardado, descartando o que não tiver o formato esperado. O formato é o do EP-002 com tipos de linha
 * novos (sem número de versão): carrinho antigo de marmita passa como está; linha de tipo desconhecido é descartada.
 */
export function sanitizeCart(raw: unknown): Cart {
  if (!isObject(raw) || !Array.isArray(raw.lines)) return EMPTY_CART;
  const lines = (raw.lines as unknown[]).filter((item): item is CartLine => {
    if (!isObject(item)) return false;
    const l = item;
    if (!isStr(l.id) || typeof l.quantity !== 'number') return false;
    switch (l.kind) {
      case 'BEVERAGE':
        return isStr(l.productId);
      case 'MARMITA':
        return isStr(l.sizeId) && Array.isArray(l.mainDishIds) && Array.isArray(l.sideDishIds) && Array.isArray(l.addons) && isStr(l.notes);
      case 'FIXED_DISH':
        return isStr(l.dishId) && isStrOrNull(l.sizeId) && isGroupAddons(l.addons) && isStr(l.notes);
      case 'FIXED_BEVERAGE':
        return isStr(l.sizeId) && isStrOrNull(l.flavorId);
      case 'COMBO':
        return (
          isStr(l.comboId) &&
          Array.isArray(l.parts) &&
          l.parts.every((p) => isObject(p) && isStr(p.componentId) && isStrOrNull(p.flavorId) && isGroupAddons(p.addons) && isStr(p.notes))
        );
      default:
        return false;
    }
  });
  return { lines };
}
