/**
 * Pedido à la carte (EP-003 fatia 3, ADR-0058, 06 §6.9): linhas montadas com o cardápio fixo (`GET /fixed-menu`),
 * corpo do `POST /orders` / `POST /orders/{id}/items` e prévia em centavos. A prévia usa os preços de balcão do
 * cache e é só prévia: o total oficial vem da API (ADR-0030). A disponibilidade (`isSellable`) é sempre a do servidor.
 * Sem dependência de plataforma.
 */
import type {
  AddonGroup,
  AddonOption,
  BeverageFlavor,
  BeverageSize,
  BeverageType,
  Combo,
  ComboComponent,
  ComboPartInput,
  CreateOrderBody,
  Dish,
  DishSize,
  FixedMenu,
  GroupAddonInput,
  NewItemInput,
  Order,
  OrderItem,
  Uuid,
} from '@/api/types';
import { formatBRL, toCents } from '@/lib/money';

/** ADR-0056 item 19: de 1 a 10 por opção, sem mínimo nem máximo por grupo nesta fase. */
export const MAX_GROUP_ADDON_QTY = 10;
export const MAX_LINE_QTY = 99;

export interface GroupAddonLine {
  addonOptionId: Uuid;
  quantity: number;
}

export interface DishLine {
  key: string;
  kind: 'DISH';
  dishId: Uuid;
  dishSizeId: Uuid | null;
  quantity: number;
  notes: string;
  addons: GroupAddonLine[];
}

export interface BeverageLine {
  key: string;
  kind: 'BEVERAGE';
  beverageSizeId: Uuid;
  beverageFlavorId: Uuid | null;
  quantity: number;
}

/** Escolhas de um componente do combo: prato → observação e adicionais; bebida → sabor. */
export interface ComboPartChoice {
  comboComponentId: Uuid;
  beverageFlavorId: Uuid | null;
  notes: string;
  addons: GroupAddonLine[];
}

export interface ComboLine {
  key: string;
  kind: 'COMBO';
  comboId: Uuid;
  quantity: number;
  /** Uma por componente, na ordem do combo. */
  parts: ComboPartChoice[];
}

export type ALaCarteLine = DishLine | BeverageLine | ComboLine;

export interface ALaCarteForm {
  tableLabel: string;
  tabNumber: string;
  customerName: string;
  lines: ALaCarteLine[];
  notes: string;
}

export const emptyALaCarteForm = (): ALaCarteForm => ({ tableLabel: '', tabNumber: '', customerName: '', lines: [], notes: '' });

// ---------------------------------------------------------------- Índice do cardápio

export interface BeverageSizeEntry {
  type: BeverageType;
  size: BeverageSize;
}

export interface FixedMenuBook {
  menu: FixedMenu;
  dishes: Map<Uuid, Dish>;
  dishSizes: Map<Uuid, { dish: Dish; size: DishSize }>;
  beverageSizes: Map<Uuid, BeverageSizeEntry>;
  flavors: Map<Uuid, BeverageFlavor>;
  combos: Map<Uuid, Combo>;
  components: Map<Uuid, { combo: Combo; component: ComboComponent }>;
  options: Map<Uuid, AddonOption>;
  groups: Map<Uuid, AddonGroup>;
}

export function fixedMenuBook(menu: FixedMenu): FixedMenuBook {
  const book: FixedMenuBook = {
    menu,
    dishes: new Map(),
    dishSizes: new Map(),
    beverageSizes: new Map(),
    flavors: new Map(),
    combos: new Map(),
    components: new Map(),
    options: new Map(),
    groups: new Map(),
  };
  for (const dish of menu.dishes) {
    book.dishes.set(dish.id, dish);
    for (const size of dish.sizes) book.dishSizes.set(size.id, { dish, size });
  }
  for (const type of menu.beverageTypes) {
    for (const size of type.sizes) {
      book.beverageSizes.set(size.id, { type, size });
      for (const flavor of size.flavors) book.flavors.set(flavor.id, flavor);
    }
  }
  for (const combo of menu.combos) {
    book.combos.set(combo.id, combo);
    for (const component of combo.components) book.components.set(component.id, { combo, component });
  }
  for (const group of menu.addonGroups) {
    book.groups.set(group.id, group);
    for (const option of group.options) book.options.set(option.id, option);
  }
  return book;
}

// ---------------------------------------------------------------- Disponibilidade (só lê o que o servidor derivou)

/** Sabor escolhível: ativo e não pausado (o `GET /fixed-menu` não traz `isSellable` no sabor). */
export const flavorSellable = (f: BeverageFlavor) => f.isActive && !f.isPaused;
export const optionSellable = (o: AddonOption) => o.isActive && !o.isPaused;

/** Sabores ativos do tamanho; com algum, o sabor é obrigatório no pedido (ADR-0056 item 11). */
export function activeFlavors(size: BeverageSize | undefined): BeverageFlavor[] {
  return (size?.flavors ?? []).filter((f) => f.isActive);
}

export const needsFlavor = (size: BeverageSize | undefined) => activeFlavors(size).length > 0;

/** Tamanhos ativos do prato, na ordem do cadastro. */
export function activeDishSizes(dish: Dish): DishSize[] {
  return dish.hasSizes ? dish.sizes.filter((s) => s.isActive) : [];
}

/** Grupos de adicionais do prato (`addonGroupIds` já é a união categoria ∪ prato, ADR-0056 item 18), com opções ativas. */
export function dishAddonGroups(book: FixedMenuBook, dishId: Uuid | null): AddonGroup[] {
  const dish = dishId ? book.dishes.get(dishId) : undefined;
  if (!dish) return [];
  return dish.addonGroupIds
    .map((id) => book.groups.get(id))
    .filter((g): g is AddonGroup => !!g && g.isActive)
    .map((g) => ({ ...g, options: g.options.filter((o) => o.isActive) }))
    .filter((g) => g.options.length > 0);
}

/** Motivo de um item fora de venda, para mostrar ao lado do item desabilitado. `null` = vendável. */
export function dishUnavailableReason(dish: Dish): string | null {
  if (dish.isSellable) return null;
  if (!dish.isActive) return 'Inativo';
  if (dish.isPaused) return 'Pausado';
  return 'Indisponível: todos os tamanhos pausados';
}

export function beverageSizeUnavailableReason(size: BeverageSize): string | null {
  if (size.isSellable) return null;
  if (!size.isActive) return 'Inativo';
  if (size.isPaused) return 'Pausado';
  return 'Indisponível: todos os sabores pausados';
}

/** Combo fora de venda pela pausa derivada: os `name` dos componentes com `isSellable: false` (06 §6.5). */
export function comboUnavailableReason(combo: Combo): string | null {
  if (combo.isSellable) return null;
  if (!combo.isActive) return 'Inativo';
  if (combo.isPaused) return 'Pausado';
  const blocked = combo.components.filter((c) => !c.isSellable).map((c) => c.name);
  return blocked.length ? `Indisponível: ${blocked.join(', ')} ${blocked.length === 1 ? 'pausado' : 'pausados'}` : 'Indisponível';
}

// ---------------------------------------------------------------- Linhas novas

export function newDishLine(key: string, dish: Dish): DishLine {
  const sellable = activeDishSizes(dish).filter((s) => s.isSellable);
  // Com um tamanho só à venda, já vem escolhido; com vários, a pessoa escolhe.
  return { key, kind: 'DISH', dishId: dish.id, dishSizeId: dish.hasSizes && sellable.length === 1 ? sellable[0]!.id : null, quantity: 1, notes: '', addons: [] };
}

export function newBeverageLine(key: string, size: BeverageSize): BeverageLine {
  const flavors = activeFlavors(size).filter(flavorSellable);
  return { key, kind: 'BEVERAGE', beverageSizeId: size.id, beverageFlavorId: flavors.length === 1 && needsFlavor(size) ? flavors[0]!.id : null, quantity: 1 };
}

export function newComboLine(key: string, combo: Combo, book: FixedMenuBook): ComboLine {
  return {
    key,
    kind: 'COMBO',
    comboId: combo.id,
    quantity: 1,
    parts: combo.components.map((c) => {
      const size = c.beverageSizeId ? book.beverageSizes.get(c.beverageSizeId)?.size : undefined;
      const flavors = activeFlavors(size).filter(flavorSellable);
      return { comboComponentId: c.id, beverageFlavorId: c.kind === 'BEVERAGE' && flavors.length === 1 ? flavors[0]!.id : null, notes: '', addons: [] };
    }),
  };
}

/** Liga/ajusta a quantidade de uma opção de adicional (0 = tira). */
export function setAddonQty(addons: readonly GroupAddonLine[], addonOptionId: Uuid, quantity: number): GroupAddonLine[] {
  const rest = addons.filter((a) => a.addonOptionId !== addonOptionId);
  return quantity > 0 ? [...rest, { addonOptionId, quantity }] : rest;
}

// ---------------------------------------------------------------- O que falta (o botão desabilitado diz, docs/10-ux.md D3)

export function lineMissing(line: ALaCarteLine, book: FixedMenuBook): string | null {
  if (line.kind === 'DISH') {
    const dish = book.dishes.get(line.dishId);
    if (!dish) return 'Este prato saiu do cardápio.';
    if (dish.hasSizes && !line.dishSizeId) return 'Escolha o tamanho.';
    return null;
  }
  if (line.kind === 'BEVERAGE') {
    const entry = book.beverageSizes.get(line.beverageSizeId);
    if (!entry) return 'Esta bebida saiu do cardápio.';
    if (needsFlavor(entry.size) && !line.beverageFlavorId) return 'Escolha o sabor.';
    return null;
  }
  const combo = book.combos.get(line.comboId);
  if (!combo) return 'Este combo saiu do cardápio.';
  for (const c of combo.components) {
    if (c.kind !== 'BEVERAGE') continue;
    const size = c.beverageSizeId ? book.beverageSizes.get(c.beverageSizeId)?.size : undefined;
    const part = line.parts.find((p) => p.comboComponentId === c.id);
    if (needsFlavor(size) && !part?.beverageFlavorId) return `Escolha o sabor de ${c.name}.`;
  }
  return null;
}

// ---------------------------------------------------------------- Prévia em centavos (06 §6.9 "Valor")

const priceOf = (v: string | null | undefined) => (v ? toCents(v) : 0);

/** Adicionais de grupo: preço × quantidade × quantidade do pai (× a do avô no combo). */
function addonsCents(book: FixedMenuBook, addons: readonly GroupAddonLine[], multiplier: number): number {
  return addons.reduce((sum, a) => sum + priceOf(book.options.get(a.addonOptionId)?.price) * a.quantity * multiplier, 0);
}

export function dishUnitCents(book: FixedMenuBook, line: Pick<DishLine, 'dishId' | 'dishSizeId'>): number {
  if (line.dishSizeId) return priceOf(book.dishSizes.get(line.dishSizeId)?.size.price);
  return priceOf(book.dishes.get(line.dishId)?.price);
}

export function beverageUnitCents(book: FixedMenuBook, line: Pick<BeverageLine, 'beverageSizeId' | 'beverageFlavorId'>): number {
  const size = priceOf(book.beverageSizes.get(line.beverageSizeId)?.size.price);
  return size + (line.beverageFlavorId ? priceOf(book.flavors.get(line.beverageFlavorId)?.extraPrice) : 0);
}

export function lineCents(book: FixedMenuBook, line: ALaCarteLine): number {
  if (line.kind === 'DISH') return dishUnitCents(book, line) * line.quantity + addonsCents(book, line.addons, line.quantity);
  if (line.kind === 'BEVERAGE') return beverageUnitCents(book, line) * line.quantity;
  const combo = book.combos.get(line.comboId);
  let total = priceOf(combo?.price) * line.quantity;
  for (const part of line.parts) {
    const component = book.components.get(part.comboComponentId)?.component;
    const partQty = component?.quantity ?? 1;
    if (part.beverageFlavorId) total += priceOf(book.flavors.get(part.beverageFlavorId)?.extraPrice) * partQty * line.quantity;
    total += addonsCents(book, part.addons, partQty * line.quantity);
  }
  return total;
}

export function previewALaCarteCents(form: Pick<ALaCarteForm, 'lines'>, book: FixedMenuBook): number {
  return form.lines.reduce((sum, l) => sum + lineCents(book, l), 0);
}

// ---------------------------------------------------------------- Rótulos (o mesmo formato do snapshot do servidor)

export function dishName(book: FixedMenuBook, dishId: Uuid, dishSizeId: Uuid | null): string {
  const dish = book.dishes.get(dishId);
  const size = dishSizeId ? book.dishSizes.get(dishSizeId)?.size : undefined;
  return [dish?.name ?? '?', size?.name].filter(Boolean).join(' ');
}

/** "Refrigerante Lata 350 ml · Coca-Cola". */
export function beverageName(book: FixedMenuBook, beverageSizeId: Uuid, beverageFlavorId: Uuid | null): string {
  const entry = book.beverageSizes.get(beverageSizeId);
  const base = entry ? `${entry.type.name} ${entry.size.name}` : '?';
  const flavor = beverageFlavorId ? book.flavors.get(beverageFlavorId)?.name : undefined;
  return flavor ? `${base} · ${flavor}` : base;
}

export function addonsText(book: FixedMenuBook, addons: readonly GroupAddonLine[]): string | null {
  if (addons.length === 0) return null;
  return `+ ${addons.map((a) => `${a.quantity}× ${book.options.get(a.addonOptionId)?.name ?? '?'}`).join(', ')}`;
}

export function lineLabel(book: FixedMenuBook, line: ALaCarteLine): string {
  if (line.kind === 'DISH') return dishName(book, line.dishId, line.dishSizeId);
  if (line.kind === 'BEVERAGE') return beverageName(book, line.beverageSizeId, line.beverageFlavorId);
  return book.combos.get(line.comboId)?.name ?? '?';
}

/** Detalhes da linha (adicionais, observação e as partes do combo), uma frase por linha. */
export function lineDetails(book: FixedMenuBook, line: ALaCarteLine): string[] {
  if (line.kind === 'DISH') return [addonsText(book, line.addons), line.notes.trim() ? `Obs.: ${line.notes.trim()}` : null].filter((t): t is string => !!t);
  if (line.kind === 'BEVERAGE') return [];
  const combo = book.combos.get(line.comboId);
  return (combo?.components ?? []).map((c) => {
    const part = line.parts.find((p) => p.comboComponentId === c.id);
    const name = c.kind === 'BEVERAGE' && c.beverageSizeId ? beverageName(book, c.beverageSizeId, part?.beverageFlavorId ?? null) : c.name;
    const extras = [part ? addonsText(book, part.addons) : null, part?.notes.trim() ? `Obs.: ${part.notes.trim()}` : null].filter(Boolean).join(' · ');
    return `${c.quantity}× ${name}${extras ? ` (${extras})` : ''}`;
  });
}

/** Linhas do ConfirmDialog (RF-012): "2× X-Burguer + batata + refri" → "R$ 73,00", com as partes no rótulo. */
export function confirmLines(book: FixedMenuBook, lines: readonly ALaCarteLine[]): { label: string; value: string }[] {
  return lines.map((l) => {
    const details = lineDetails(book, l);
    return { label: [`${l.quantity}× ${lineLabel(book, l)}`, ...details].join('\n'), value: formatBRL(lineCents(book, l)) };
  });
}

// ---------------------------------------------------------------- Corpo (06 §6.9)

const nullIfBlank = (s: string) => (s.trim() === '' ? null : s.trim());

const groupAddonInputs = (addons: readonly GroupAddonLine[]): GroupAddonInput[] =>
  addons.filter((a) => a.quantity > 0).map((a) => ({ addonOptionId: a.addonOptionId, quantity: a.quantity }));

/**
 * Partes do combo: só os componentes com alguma escolha (o componente sem escolha pode ficar fora de `parts`).
 * Parte de prato leva `notes` e `groupAddons`; parte de bebida, só `beverageFlavorId`.
 */
function partInputs(book: FixedMenuBook, line: ComboLine): ComboPartInput[] {
  const combo = book.combos.get(line.comboId);
  const kindOf = (id: Uuid) => combo?.components.find((c) => c.id === id)?.kind ?? book.components.get(id)?.component.kind;
  return line.parts.flatMap((p): ComboPartInput[] => {
    if (kindOf(p.comboComponentId) === 'BEVERAGE') {
      return p.beverageFlavorId ? [{ comboComponentId: p.comboComponentId, beverageFlavorId: p.beverageFlavorId, notes: null, groupAddons: null }] : [];
    }
    const notes = nullIfBlank(p.notes);
    const groupAddons = groupAddonInputs(p.addons);
    if (notes === null && groupAddons.length === 0) return [];
    return [{ comboComponentId: p.comboComponentId, beverageFlavorId: null, notes, groupAddons }];
  });
}

export function lineItem(book: FixedMenuBook, line: ALaCarteLine): NewItemInput {
  if (line.kind === 'DISH') {
    return { itemKind: 'FIXED_DISH', dishId: line.dishId, dishSizeId: line.dishSizeId, quantity: line.quantity, notes: nullIfBlank(line.notes), groupAddons: groupAddonInputs(line.addons) };
  }
  if (line.kind === 'BEVERAGE') {
    return { itemKind: 'FIXED_BEVERAGE', beverageSizeId: line.beverageSizeId, beverageFlavorId: line.beverageFlavorId, quantity: line.quantity, notes: null };
  }
  return { itemKind: 'COMBO', comboId: line.comboId, quantity: line.quantity, parts: partInputs(book, line) };
}

/** Comanda no salão (ADR-0058 item 5): mesa e/ou comanda, nome opcional, preço de balcão. */
export function buildALaCarteBody(form: ALaCarteForm, book: FixedMenuBook): CreateOrderBody {
  return {
    orderType: 'A_LA_CARTE',
    fulfillmentType: 'DINE_IN',
    customerId: null,
    customerName: nullIfBlank(form.customerName),
    customerPhone: null,
    saveCustomer: false,
    customerAddressId: null,
    deliveryAddress: null,
    deliveryFee: null,
    tableLabel: nullIfBlank(form.tableLabel),
    tabNumber: nullIfBlank(form.tabNumber),
    notes: nullIfBlank(form.notes),
    items: form.lines.map((l) => lineItem(book, l)),
    weighings: [],
  };
}

/** Validação antes do Confirma; `null` = pode enviar. */
export function aLaCarteFormIssue(form: ALaCarteForm, book: FixedMenuBook): string | null {
  if (form.tableLabel.trim() === '' && form.tabNumber.trim() === '') return 'Informe a mesa ou a comanda.';
  if (form.lines.length === 0) return 'Inclua pelo menos um item do cardápio.';
  for (const l of form.lines) {
    const missing = lineMissing(l, book);
    if (missing) return `${lineLabel(book, l)}: ${missing}`;
  }
  return null;
}

// ---------------------------------------------------------------- Pedido registrado (árvore do 06 §6.9)

/**
 * Valor de um item do pedido com os filhos, somando os `lineTotal` da API (nunca recalcula): o item, os adicionais
 * e, no combo, as partes com os adicionais delas ("X-Burguer + batata + refri" 2× = R$ 73,00). Usado na divisão
 * "por itens" do Receber.
 */
export function itemWithChildrenCents(item: OrderItem): number {
  return toCents(item.lineTotal) + item.addons.reduce((sum, a) => sum + toCents(a.lineTotal), 0) + (item.parts ?? []).reduce((sum, p) => sum + itemWithChildrenCents(p), 0);
}

/** Pratos do pedido que aceitam adicional de grupo depois: prato avulso e parte **de prato** de combo. */
export interface AddonTarget {
  item: OrderItem;
  /** Quantidade que multiplica o adicional (a do pai × a do avô). */
  multiplier: number;
  label: string;
}

export function addonTargets(order: Order): AddonTarget[] {
  const out: AddonTarget[] = [];
  for (const item of order.items) {
    if (item.itemKind === 'FIXED_DISH' && item.dishId) out.push({ item, multiplier: item.quantity, label: `${item.quantity}× ${item.name}` });
    if (item.itemKind === 'COMBO') {
      for (const part of item.parts ?? []) {
        if (part.itemKind === 'COMBO_PART' && part.dishId) {
          out.push({ item: part, multiplier: part.quantity * item.quantity, label: `${part.quantity * item.quantity}× ${part.name} (${item.name})` });
        }
      }
    }
  }
  return out;
}

export interface ALaCarteAddItemsForm {
  lines: ALaCarteLine[];
  /** Adicionais de grupo em pratos já registrados: orderItemId → adicionais. */
  addons: Record<string, GroupAddonLine[]>;
}

export const emptyALaCarteAddItems = (): ALaCarteAddItemsForm => ({ lines: [], addons: {} });

export function addItemsBody(form: ALaCarteAddItemsForm, book: FixedMenuBook): NewItemInput[] {
  return [
    ...form.lines.map((l) => lineItem(book, l)),
    ...Object.entries(form.addons).flatMap(([parentOrderItemId, list]) =>
      groupAddonInputs(list).map((a) => ({ itemKind: 'GROUP_ADDON' as const, parentOrderItemId, addonOptionId: a.addonOptionId, quantity: a.quantity, notes: null })),
    ),
  ];
}

export function addItemsConfirmLines(form: ALaCarteAddItemsForm, book: FixedMenuBook, order: Order): { label: string; value: string }[] {
  const targets = new Map(addonTargets(order).map((t) => [t.item.id, t]));
  return [
    ...confirmLines(book, form.lines),
    ...Object.entries(form.addons).flatMap(([itemId, list]) => {
      const target = targets.get(itemId);
      return list
        .filter((a) => a.quantity > 0)
        .map((a) => ({
          label: `+${a.quantity}× ${book.options.get(a.addonOptionId)?.name ?? '?'} em ${target?.item.name ?? '?'}`,
          value: formatBRL(priceOf(book.options.get(a.addonOptionId)?.price) * a.quantity * (target?.multiplier ?? 1)),
        }));
    }),
  ];
}

export function previewAddItemsCents(form: ALaCarteAddItemsForm, book: FixedMenuBook, order: Order): number {
  const targets = new Map(addonTargets(order).map((t) => [t.item.id, t]));
  const extra = Object.entries(form.addons).reduce((sum, [itemId, list]) => sum + addonsCents(book, list, targets.get(itemId)?.multiplier ?? 1), 0);
  return previewALaCarteCents(form, book) + extra;
}
