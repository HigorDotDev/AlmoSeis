import type {
  PublicAddonGroup,
  PublicAddonOption,
  PublicBeverageSize,
  PublicBeverageType,
  PublicCombo,
  PublicFixedDish,
  PublicFixedMenu,
  PublicLimits,
  PublicMenu,
  PublicStore,
  StoreSection,
} from './api/types';
import type { ComboLine, ComboPart, FixedBeverageLine, FixedDishLine, GroupAddon } from './cart';
import { formatBRL, parseMoney } from './money';

/**
 * Cardápio fixo da lanchonete na página (§6.19, EP-003 fatia 5): seções e abas, buscas por id e as regras de seleção
 * (prato: tamanho → adicionais → observação; bebida: tamanho → sabor; combo: adicionais e observação por prato, sabor
 * por bebida). Só tipos do carrinho são importados de `./cart` (sem ciclo de valores).
 */

/** Até 10 opções de adicional por prato (§6.19 `groupAddons`). */
export const MAX_ADDON_OPTIONS_PER_DISH = 10;

/** O que a página vende. Perfil antigo sem `sections`: só marmita (EP-002). */
export function storeSections(store: Pick<PublicStore, 'status' | 'sections'>): StoreSection[] {
  if (Array.isArray(store.sections)) return store.sections;
  return store.status === 'UNAVAILABLE' ? [] : ['MARMITA'];
}

/** Cardápios usados pela prévia do carrinho. Só em `OPEN` e `PAUSED` (§6.19). */
export interface Catalog {
  menu: PublicMenu | null;
  fixedMenu: PublicFixedMenu | null;
}

export function catalogOf(store: Pick<PublicStore, 'status' | 'menu' | 'fixedMenu'>): Catalog {
  const visible = store.status === 'OPEN' || store.status === 'PAUSED';
  return { menu: visible ? store.menu : null, fixedMenu: visible ? (store.fixedMenu ?? null) : null };
}

// --- índice por id ---

export interface FixedMenuIndex {
  dishes: Map<string, PublicFixedDish>;
  beverageSizes: Map<string, { type: PublicBeverageType; size: PublicBeverageSize }>;
  combos: Map<string, PublicCombo>;
  addonGroups: Map<string, PublicAddonGroup>;
}

const indexCache = new WeakMap<PublicFixedMenu, FixedMenuIndex>();

export function indexFixedMenu(menu: PublicFixedMenu): FixedMenuIndex {
  const cached = indexCache.get(menu);
  if (cached) return cached;
  const index: FixedMenuIndex = {
    dishes: new Map(menu.categories.flatMap((c) => c.dishes.map((d) => [d.id, d] as const))),
    beverageSizes: new Map(menu.beverages.flatMap((type) => type.sizes.map((size) => [size.id, { type, size }] as const))),
    combos: new Map(menu.combos.map((c) => [c.id, c])),
    addonGroups: new Map(menu.addonGroups.map((g) => [g.id, g])),
  };
  indexCache.set(menu, index);
  return index;
}

/** Os grupos de adicionais (com opções) de um prato ou de um componente de prato do combo. */
export function addonGroupsFor(groupIds: readonly string[], index: FixedMenuIndex): PublicAddonGroup[] {
  return groupIds.map((id) => index.addonGroups.get(id)).filter((g): g is PublicAddonGroup => !!g && g.options.length > 0);
}

export function addonOptionsFor(groupIds: readonly string[], index: FixedMenuIndex): Map<string, PublicAddonOption> {
  return new Map(addonGroupsFor(groupIds, index).flatMap((g) => g.options.map((o) => [o.id, o] as const)));
}

/** "Refrigerante Lata 350 ml · Coca-Cola", o nome de exibição do servidor. */
export function beverageName(typeName: string, sizeName: string, flavorName?: string | null): string {
  return `${typeName} ${sizeName}${flavorName ? ` · ${flavorName}` : ''}`;
}

/** "R$ 23,90" (sem tamanho ou com um só) ou "a partir de R$ 13,00". */
export function dishPriceLabel(dish: PublicFixedDish): string {
  if (!dish.hasSizes) return dish.price ? formatBRL(parseMoney(dish.price)) : '—';
  return fromPrices(dish.sizes.map((s) => parseMoney(s.price)));
}

export function beveragePriceLabel(type: PublicBeverageType): string {
  return fromPrices(type.sizes.flatMap((s) => (s.flavors.length ? s.flavors.map((f) => parseMoney(s.price) + parseMoney(f.extraPrice)) : [parseMoney(s.price)])));
}

function fromPrices(prices: number[]): string {
  if (prices.length === 0) return '—';
  const min = Math.min(...prices);
  return prices.every((p) => p === min) ? formatBRL(min) : `a partir de ${formatBRL(min)}`;
}

/** Bebida vendida sem escolha (um tamanho, sem sabor): a página adiciona direto. */
export function isSingleChoiceBeverage(type: PublicBeverageType): boolean {
  return type.sizes.length === 1 && type.sizes[0]!.flavors.length === 0;
}

// --- abas da página ---

export type PageTab =
  | { key: string; label: string; kind: 'MARMITA' }
  | { key: string; label: string; kind: 'CATEGORY'; categoryId: string }
  | { key: string; label: string; kind: 'BEVERAGES' }
  | { key: string; label: string; kind: 'COMBOS' };

/**
 * Abas por seção (RN-316.1 a RN-316.3): `MARMITA` → "Marmitas"; `A_LA_CARTE` → uma por categoria, "Bebidas" e "Combos"
 * (as vazias não aparecem). Com as duas seções, as duas. Uma aba só → a página mostra o conteúdo sem a barra de abas.
 */
export function pageTabs(sections: readonly StoreSection[], fixedMenu: PublicFixedMenu | null): PageTab[] {
  const tabs: PageTab[] = [];
  if (sections.includes('MARMITA')) tabs.push({ key: 'marmitas', label: 'Marmitas', kind: 'MARMITA' });
  if (sections.includes('A_LA_CARTE') && fixedMenu) {
    for (const c of fixedMenu.categories) {
      if (c.dishes.length > 0) tabs.push({ key: `cat-${c.id}`, label: c.name, kind: 'CATEGORY', categoryId: c.id });
    }
    if (fixedMenu.beverages.some((b) => b.sizes.length > 0)) tabs.push({ key: 'bebidas', label: 'Bebidas', kind: 'BEVERAGES' });
    if (fixedMenu.combos.length > 0) tabs.push({ key: 'combos', label: 'Combos', kind: 'COMBOS' });
  }
  return tabs;
}

// --- regras de seleção (rascunho → linha do carrinho) ---

export type SelectionErrors = Partial<Record<string, string>>;
export type BuildResult<T> = { ok: true; line: T } | { ok: false; errors: SelectionErrors };

export interface DishDraft {
  sizeId: string | null;
  /** optionId → quantidade por prato (0 = não escolhido). */
  addons: Record<string, number>;
  notes: string;
  quantity: number;
}

export interface BeverageDraft {
  sizeId: string | null;
  flavorId: string | null;
  quantity: number;
}

export interface ComboPartDraft {
  flavorId: string | null;
  addons: Record<string, number>;
  notes: string;
}

export interface ComboDraft {
  /** componentId → escolhas. */
  parts: Record<string, ComboPartDraft>;
  quantity: number;
}

export function emptyDishDraft(dish: PublicFixedDish): DishDraft {
  return { sizeId: dish.hasSizes && dish.sizes.length === 1 ? dish.sizes[0]!.id : null, addons: {}, notes: '', quantity: 1 };
}

export function emptyBeverageDraft(type: PublicBeverageType): BeverageDraft {
  const only = type.sizes.length === 1 ? type.sizes[0]! : null;
  return { sizeId: only?.id ?? null, flavorId: only && only.flavors.length === 1 ? only.flavors[0]!.id : null, quantity: 1 };
}

export function emptyComboDraft(combo: PublicCombo): ComboDraft {
  const parts: Record<string, ComboPartDraft> = {};
  for (const c of combo.components) {
    parts[c.id] = { flavorId: c.flavors.length === 1 ? c.flavors[0]!.id : null, addons: {}, notes: '' };
  }
  return { parts, quantity: 1 };
}

const chosenAddons = (addons: Record<string, number>): GroupAddon[] =>
  Object.entries(addons)
    .filter(([, q]) => q > 0)
    .map(([optionId, quantity]) => ({ optionId, quantity }));

/** Rascunho → linha, sem conferir (para a prévia do preço no botão). */
export function dishLineFromDraft(dish: PublicFixedDish, draft: DishDraft, id: string): FixedDishLine {
  return {
    kind: 'FIXED_DISH',
    id,
    dishId: dish.id,
    sizeId: dish.hasSizes ? draft.sizeId : null,
    addons: chosenAddons(draft.addons),
    quantity: draft.quantity,
    notes: draft.notes.trim(),
  };
}

export function beverageLineFromDraft(type: PublicBeverageType, draft: BeverageDraft, id: string): FixedBeverageLine | null {
  const size = type.sizes.find((s) => s.id === draft.sizeId);
  if (!size) return null;
  return { kind: 'FIXED_BEVERAGE', id, sizeId: size.id, flavorId: size.flavors.length ? draft.flavorId : null, quantity: draft.quantity };
}

export function comboLineFromDraft(combo: PublicCombo, draft: ComboDraft, id: string): ComboLine {
  const parts: ComboPart[] = [];
  for (const c of combo.components) {
    const p = draft.parts[c.id];
    if (!p) continue;
    if (c.kind === 'DISH') {
      const addons = chosenAddons(p.addons);
      const notes = p.notes.trim();
      // Componente sem escolha fica fora de `parts` (§6.19).
      if (addons.length || notes) parts.push({ componentId: c.id, flavorId: null, addons, notes });
    } else if (c.flavors.length && p.flavorId) {
      parts.push({ componentId: c.id, flavorId: p.flavorId, addons: [], notes: '' });
    }
  }
  return { kind: 'COMBO', id, comboId: combo.id, quantity: draft.quantity, parts };
}

function checkAddons(
  addons: Record<string, number>,
  allowed: Map<string, PublicAddonOption>,
  limits: PublicLimits,
  key: string,
  errors: SelectionErrors,
): void {
  const chosen = chosenAddons(addons);
  if (chosen.some((a) => !allowed.has(a.optionId))) errors[key] = 'Um adicional escolhido não está mais disponível.';
  else if (chosen.some((a) => a.quantity > limits.maxAddonQuantity)) errors[key] = `Cada adicional vai de 1 a ${limits.maxAddonQuantity}.`;
  else if (chosen.length > MAX_ADDON_OPTIONS_PER_DISH) errors[key] = `Escolha até ${MAX_ADDON_OPTIONS_PER_DISH} adicionais diferentes.`;
}

function checkQuantity(quantity: number, limits: PublicLimits, errors: SelectionErrors): void {
  if (!Number.isInteger(quantity) || quantity < 1 || quantity > limits.maxQuantityPerLine) {
    errors.quantity = `A quantidade vai de 1 a ${limits.maxQuantityPerLine}.`;
  }
}

/** Prato: tamanho (obrigatório se houver) → adicionais dos grupos do prato (1 a 10 cada) → observação. */
export function buildDishLine(
  dish: PublicFixedDish,
  draft: DishDraft,
  index: FixedMenuIndex,
  limits: PublicLimits,
  id: string,
): BuildResult<FixedDishLine> {
  const errors: SelectionErrors = {};
  if (dish.hasSizes && !dish.sizes.some((s) => s.id === draft.sizeId)) errors.size = 'Escolha o tamanho.';
  checkAddons(draft.addons, addonOptionsFor(dish.addonGroupIds, index), limits, 'addons', errors);
  if (draft.notes.trim().length > limits.notesMaxLength) errors.notes = `A observação pode ter até ${limits.notesMaxLength} caracteres.`;
  checkQuantity(draft.quantity, limits, errors);
  return Object.keys(errors).length ? { ok: false, errors } : { ok: true, line: dishLineFromDraft(dish, draft, id) };
}

/** Bebida: tamanho → sabor (obrigatório quando o tamanho tem sabores). */
export function buildBeverageLine(
  type: PublicBeverageType,
  draft: BeverageDraft,
  limits: PublicLimits,
  id: string,
): BuildResult<FixedBeverageLine> {
  const errors: SelectionErrors = {};
  const size = type.sizes.find((s) => s.id === draft.sizeId);
  if (!size) errors.size = 'Escolha o tamanho.';
  else if (size.flavors.length && !size.flavors.some((f) => f.id === draft.flavorId)) errors.flavor = 'Escolha o sabor.';
  checkQuantity(draft.quantity, limits, errors);
  const line = beverageLineFromDraft(type, draft, id);
  return Object.keys(errors).length || !line ? { ok: false, errors } : { ok: true, line };
}

/** Combo: por prato, adicionais (dos grupos do componente) e observação; por bebida com sabores, o sabor. */
export function buildComboLine(
  combo: PublicCombo,
  draft: ComboDraft,
  index: FixedMenuIndex,
  limits: PublicLimits,
  id: string,
): BuildResult<ComboLine> {
  const errors: SelectionErrors = {};
  for (const c of combo.components) {
    const p = draft.parts[c.id] ?? { flavorId: null, addons: {}, notes: '' };
    if (c.kind === 'DISH') {
      checkAddons(p.addons, addonOptionsFor(c.addonGroupIds, index), limits, `parts.${c.id}.addons`, errors);
      if (p.notes.trim().length > limits.notesMaxLength) {
        errors[`parts.${c.id}.notes`] = `A observação pode ter até ${limits.notesMaxLength} caracteres.`;
      }
    } else if (c.flavors.length && !c.flavors.some((f) => f.id === p.flavorId)) {
      errors[`parts.${c.id}.flavor`] = 'Escolha o sabor.';
    }
  }
  checkQuantity(draft.quantity, limits, errors);
  return Object.keys(errors).length ? { ok: false, errors } : { ok: true, line: comboLineFromDraft(combo, draft, id) };
}
