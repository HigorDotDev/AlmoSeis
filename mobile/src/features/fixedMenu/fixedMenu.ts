/**
 * Cardápio fixo da lanchonete (EP-003 fatia 2; ADR-0056, ADR-0057; 06 §6.5 "Cardápio fixo"). Regras de tela, sem
 * dependência de plataforma: formulários → corpos da API, validação local, agrupamento, textos de situação.
 * A disponibilidade (`isSellable`) é **sempre** a do servidor: aqui só se escolhe o texto que a explica.
 */
import { isApiError } from '@/api/errors';
import type {
  AddonGroup,
  BeverageSize,
  BeverageType,
  Combo,
  ComboComponentKind,
  CreateAddonGroupBody,
  CreateBeverageTypeBody,
  CreateComboBody,
  CreateDishBody,
  Dish,
  DishCategory,
  FixedMenu,
  MoneyString,
  UpdateAddonGroupBody,
  UpdateBeverageTypeBody,
  UpdateComboBody,
  UpdateDishBody,
  Uuid,
} from '@/api/types';
import { formatBRL, toCents } from '@/lib/money';
import { siteBodyPrice, siteSaleFrom, type SiteSale } from '@/lib/sitePrice';

export { siteSummary, toggleSiteSale, type SiteSale } from '@/lib/sitePrice';

/** Limites do 06 §6.5 "Regras e erros". */
export const LIMITS = {
  categoryName: 40,
  dishName: 80,
  sizeName: 30,
  typeName: 40,
  flavorName: 40,
  comboName: 80,
  groupName: 60,
  optionName: 80,
  description: 300,
  sizes: 20,
  flavors: 50,
  components: 10,
  options: 50,
  componentQuantity: 10,
} as const;

export const EMPTY_MENU: FixedMenu = { categories: [], dishes: [], beverageTypes: [], combos: [], addonGroups: [] };

let rowSeq = 0;
/** Chave local de uma linha nova do formulário (tamanho, sabor, opção, componente). */
export function newRowKey(): string {
  rowSeq += 1;
  return `new-${rowSeq}`;
}

const cents = (v: MoneyString | null | undefined) => (v ? toCents(v) : 0);
const positive = (v: MoneyString | null | undefined) => cents(v) > 0;
const orderOf = (text: string, fallback: number) => {
  const n = Number(text.trim());
  return Number.isInteger(n) && n >= 0 ? n : fallback;
};

// ---------------------------------------------------------------- Situação (pausa e disponibilidade derivada)

export interface ItemStatus {
  tone: 'danger' | 'warning' | 'neutral';
  text: string;
}

function joinNames(names: string[]): string {
  if (names.length <= 1) return names[0] ?? '';
  return `${names.slice(0, -1).join(', ')} e ${names[names.length - 1]}`;
}

/**
 * Combo fora de venda por derivação (ADR-0056 item 23): o motivo são os componentes com `isSellable: false`
 * ("Indisponível: Batata frita P pausado"). Pausado por ele mesmo: "Pausado".
 */
export function comboStatus(combo: Combo): ItemStatus | null {
  if (!combo.isActive) return { tone: 'neutral', text: 'Inativo' };
  if (combo.isPaused) return { tone: 'warning', text: 'Pausado' };
  if (combo.isSellable) return null;
  const blocked = combo.components.filter((c) => !c.isSellable).map((c) => c.name);
  if (blocked.length === 0) return { tone: 'danger', text: 'Indisponível' };
  return { tone: 'danger', text: `Indisponível: ${joinNames(blocked)} ${blocked.length === 1 ? 'pausado' : 'pausados'}` };
}

export function dishStatus(dish: Dish): ItemStatus | null {
  if (!dish.isActive) return { tone: 'neutral', text: 'Inativo' };
  if (dish.isPaused) return { tone: 'warning', text: 'Pausado' };
  if (dish.isSellable) return null;
  return { tone: 'danger', text: 'Indisponível: todos os tamanhos pausados' };
}

export function beverageSizeStatus(size: BeverageSize): ItemStatus | null {
  if (!size.isActive) return { tone: 'neutral', text: 'Inativo' };
  if (size.isPaused) return { tone: 'warning', text: 'Pausado' };
  if (size.isSellable) return null;
  return { tone: 'danger', text: 'Indisponível: todos os sabores pausados' };
}

/** "R$ 22,00" ou "300 ml R$ 14,00 · 500 ml R$ 18,00" (só tamanhos ativos). */
export function dishPriceSummary(dish: Dish): string {
  if (!dish.hasSizes) return dish.price ? formatBRL(dish.price) : 'Sem preço';
  const sizes = dish.sizes.filter((s) => s.isActive);
  return sizes.length ? sizes.map((s) => `${s.name} ${formatBRL(s.price)}`).join(' · ') : 'Sem tamanho ativo';
}

// ---------------------------------------------------------------- Agrupamento e leitura

export interface DishGroup {
  category: DishCategory | null;
  dishes: Dish[];
}

/** Pratos por categoria, na ordem das categorias; prato de categoria fora da lista vai para um grupo sem título. */
export function groupDishes(menu: FixedMenu, showInactive: boolean): DishGroup[] {
  const dishes = menu.dishes.filter((d) => showInactive || d.isActive);
  const categories = menu.categories.filter((c) => showInactive || c.isActive);
  const groups: DishGroup[] = categories.map((category) => ({ category, dishes: dishes.filter((d) => d.dishCategoryId === category.id) }));
  const known = new Set(categories.map((c) => c.id));
  const orphans = dishes.filter((d) => !known.has(d.dishCategoryId));
  if (orphans.length) groups.push({ category: null, dishes: orphans });
  return groups;
}

/** Grupos de adicionais do prato: os ligados à categoria ∪ ao próprio prato, ativos, sem repetir (ADR-0056 item 18). */
export function addonGroupsFor(menu: FixedMenu, dishCategoryId: Uuid | null, dishId: Uuid | null): AddonGroup[] {
  return menu.addonGroups.filter(
    (g) => g.isActive && ((dishCategoryId !== null && g.dishCategoryIds.includes(dishCategoryId)) || (dishId !== null && g.dishIds.includes(dishId))),
  );
}

/** "Lanches, X-Salada" — onde o grupo está ligado. */
export function addonGroupLinksText(menu: FixedMenu, group: AddonGroup): string {
  const names = [
    ...group.dishCategoryIds.map((id) => menu.categories.find((c) => c.id === id)?.name),
    ...group.dishIds.map((id) => menu.dishes.find((d) => d.id === id)?.name),
  ].filter((n): n is string => !!n);
  return names.length ? names.join(', ') : 'Não está ligado a nenhuma categoria ou prato';
}

// ---------------------------------------------------------------- Categorias

/**
 * Mover a categoria `index` uma posição (`-1` sobe, `+1` desce). Devolve os `PATCH` necessários: renumera
 * 1…n na ordem nova e só manda quem mudou de `sortOrder`.
 */
export function reorderCategories(
  categories: readonly DishCategory[],
  index: number,
  direction: -1 | 1,
): { id: Uuid; version: number; sortOrder: number }[] {
  const target = index + direction;
  if (target < 0 || target >= categories.length) return [];
  const next = [...categories];
  const [moved] = next.splice(index, 1);
  next.splice(target, 0, moved!);
  return next
    .map((c, i) => ({ id: c.id, version: c.version, sortOrder: i + 1, changed: c.sortOrder !== i + 1 }))
    .filter((c) => c.changed)
    .map(({ id, version, sortOrder }) => ({ id, version, sortOrder }));
}

export function nextSortOrder(items: readonly { sortOrder: number }[]): number {
  return items.reduce((max, i) => Math.max(max, i.sortOrder), 0) + 1;
}

// ---------------------------------------------------------------- Prato

export interface DishSizeRow extends SiteSale {
  key: string;
  id: Uuid | null;
  name: string;
  price: MoneyString;
}

export interface DishForm extends SiteSale {
  dishCategoryId: Uuid | null;
  name: string;
  description: string;
  requiresPreparation: boolean;
  hasSizes: boolean;
  price: MoneyString;
  sortOrder: string;
  sizes: DishSizeRow[];
}

export function emptyDishSize(): DishSizeRow {
  return { key: newRowKey(), id: null, name: '', price: '0.00', sellsOnSite: false, sitePrice: '0.00' };
}

export function dishFormFrom(dish: Dish | null, defaults: { dishCategoryId: Uuid | null; sortOrder: number }): DishForm {
  if (!dish) {
    return {
      dishCategoryId: defaults.dishCategoryId,
      name: '',
      description: '',
      requiresPreparation: true,
      hasSizes: false,
      price: '0.00',
      sellsOnSite: false,
      sitePrice: '0.00',
      sortOrder: String(defaults.sortOrder),
      sizes: [],
    };
  }
  return {
    dishCategoryId: dish.dishCategoryId,
    name: dish.name,
    description: dish.description ?? '',
    requiresPreparation: dish.requiresPreparation,
    hasSizes: dish.hasSizes,
    price: dish.price ?? '0.00',
    ...siteSaleFrom(dish.sellsOnSite, dish.sitePrice),
    sortOrder: String(dish.sortOrder),
    // Só os tamanhos ativos entram no formulário; os inativos continuam inativos (a lista substitui a atual).
    sizes: dish.sizes
      .filter((s) => s.isActive)
      .map((s) => ({ key: s.id, id: s.id, name: s.name, price: s.price, ...siteSaleFrom(s.sellsOnSite, s.sitePrice) })),
  };
}

/** Validação local (a do servidor continua valendo: `422` por campo). Chaves no formato do servidor. */
export function dishFormErrors(form: DishForm): Record<string, string> {
  const e: Record<string, string> = {};
  if (!form.dishCategoryId) e.dishCategoryId = 'Escolha a categoria.';
  if (!form.name.trim()) e.name = 'Informe o nome.';
  if (form.hasSizes) {
    if (form.sizes.length === 0) e.sizes = 'Com "Tamanho?" ligado, cadastre pelo menos um tamanho.';
    if (form.sizes.length > LIMITS.sizes) e.sizes = `No máximo ${LIMITS.sizes} tamanhos.`;
    form.sizes.forEach((s, i) => {
      if (!s.name.trim()) e[`sizes[${i}].name`] = 'Informe o tamanho.';
      if (!positive(s.price)) e[`sizes[${i}].price`] = 'Informe o preço.';
    });
    const names = form.sizes.map((s) => s.name.trim().toLowerCase()).filter(Boolean);
    names.forEach((n, i) => {
      if (names.indexOf(n) !== i) e[`sizes[${i}].name`] = 'Tamanho repetido.';
    });
  } else if (!positive(form.price)) {
    e.price = 'Informe o preço.';
  }
  return e;
}

function dishSizesBody(form: DishForm) {
  return form.sizes.map((s, i) => ({
    id: s.id,
    name: s.name.trim(),
    price: s.price,
    sellsOnSite: s.sellsOnSite,
    sitePrice: siteBodyPrice(s),
    sortOrder: i + 1,
  }));
}

export function dishCreateBody(form: DishForm): CreateDishBody {
  return {
    dishCategoryId: form.dishCategoryId!,
    name: form.name.trim(),
    description: form.description.trim() || null,
    requiresPreparation: form.requiresPreparation,
    hasSizes: form.hasSizes,
    price: form.hasSizes ? null : form.price,
    sellsOnSite: form.hasSizes ? false : form.sellsOnSite,
    sitePrice: form.hasSizes ? null : siteBodyPrice(form),
    sortOrder: orderOf(form.sortOrder, 1),
    sizes: form.hasSizes ? dishSizesBody(form) : [],
  };
}

/**
 * `PATCH` do prato: com tamanho, `sizes` substitui a lista (o que ficou de fora é inativado) e o prato não leva
 * preço próprio; sem tamanho, vai o preço único e **não** vai `sizes` (o servidor inativa os tamanhos).
 */
export function dishUpdateBody(form: DishForm): UpdateDishBody {
  const base: UpdateDishBody = {
    dishCategoryId: form.dishCategoryId!,
    name: form.name.trim(),
    description: form.description.trim() || null,
    requiresPreparation: form.requiresPreparation,
    hasSizes: form.hasSizes,
    sortOrder: orderOf(form.sortOrder, 1),
  };
  return form.hasSizes
    ? { ...base, sizes: dishSizesBody(form) }
    : { ...base, price: form.price, sellsOnSite: form.sellsOnSite, sitePrice: siteBodyPrice(form) };
}

// ---------------------------------------------------------------- Bebida

export interface FlavorRow {
  key: string;
  id: Uuid | null;
  name: string;
  extraPrice: MoneyString;
}

export interface BeverageSizeRow extends SiteSale {
  key: string;
  id: Uuid | null;
  name: string;
  price: MoneyString;
  flavors: FlavorRow[];
}

export interface BeverageForm {
  name: string;
  sortOrder: string;
  sizes: BeverageSizeRow[];
}

export function emptyBeverageSize(): BeverageSizeRow {
  return { key: newRowKey(), id: null, name: '', price: '0.00', sellsOnSite: false, sitePrice: '0.00', flavors: [] };
}

export function emptyFlavor(): FlavorRow {
  return { key: newRowKey(), id: null, name: '', extraPrice: '0.00' };
}

export function beverageFormFrom(type: BeverageType | null, defaults: { sortOrder: number }): BeverageForm {
  if (!type) return { name: '', sortOrder: String(defaults.sortOrder), sizes: [emptyBeverageSize()] };
  return {
    name: type.name,
    sortOrder: String(type.sortOrder),
    sizes: type.sizes
      .filter((s) => s.isActive)
      .map((s) => ({
        key: s.id,
        id: s.id,
        name: s.name,
        price: s.price,
        ...siteSaleFrom(s.sellsOnSite, s.sitePrice),
        flavors: s.flavors.filter((f) => f.isActive).map((f) => ({ key: f.id, id: f.id, name: f.name, extraPrice: f.extraPrice })),
      })),
  };
}

/**
 * "Copiar sabores de outro tamanho" (Q-305): acrescenta ao tamanho de destino os sabores da origem que ele ainda
 * não tem (pelo nome), como sabores novos com o mesmo acréscimo. Não apaga nada do destino.
 */
export function copyFlavors(form: BeverageForm, fromKey: string, toKey: string): BeverageForm {
  const from = form.sizes.find((s) => s.key === fromKey);
  if (!from || fromKey === toKey) return form;
  return {
    ...form,
    sizes: form.sizes.map((s) => {
      if (s.key !== toKey) return s;
      const existing = new Set(s.flavors.map((f) => f.name.trim().toLowerCase()));
      const added = from.flavors
        .filter((f) => f.name.trim() && !existing.has(f.name.trim().toLowerCase()))
        .map((f) => ({ key: newRowKey(), id: null, name: f.name.trim(), extraPrice: f.extraPrice }));
      return { ...s, flavors: [...s.flavors, ...added] };
    }),
  };
}

export function beverageFormErrors(form: BeverageForm): Record<string, string> {
  const e: Record<string, string> = {};
  if (!form.name.trim()) e.name = 'Informe o tipo (Refrigerante, Suco…).';
  if (form.sizes.length > LIMITS.sizes) e.sizes = `No máximo ${LIMITS.sizes} tamanhos.`;
  const sizeNames = form.sizes.map((s) => s.name.trim().toLowerCase());
  form.sizes.forEach((s, i) => {
    if (!s.name.trim()) e[`sizes[${i}].name`] = 'Informe o tamanho.';
    else if (sizeNames.indexOf(sizeNames[i]!) !== i) e[`sizes[${i}].name`] = 'Tamanho repetido.';
    if (!positive(s.price)) e[`sizes[${i}].price`] = 'Informe o preço.';
    if (s.flavors.length > LIMITS.flavors) e[`sizes[${i}].flavors`] = `No máximo ${LIMITS.flavors} sabores.`;
    const flavorNames = s.flavors.map((f) => f.name.trim().toLowerCase());
    s.flavors.forEach((f, j) => {
      if (!f.name.trim()) e[`sizes[${i}].flavors[${j}].name`] = 'Informe o sabor.';
      else if (flavorNames.indexOf(flavorNames[j]!) !== j) e[`sizes[${i}].flavors[${j}].name`] = 'Sabor repetido.';
    });
  });
  return e;
}

function beverageSizesBody(form: BeverageForm) {
  return form.sizes.map((s, i) => ({
    id: s.id,
    name: s.name.trim(),
    price: s.price,
    sellsOnSite: s.sellsOnSite,
    sitePrice: siteBodyPrice(s),
    sortOrder: i + 1,
    flavors: s.flavors.map((f, j) => ({ id: f.id, name: f.name.trim(), extraPrice: f.extraPrice, sortOrder: j + 1 })),
  }));
}

export function beverageCreateBody(form: BeverageForm): CreateBeverageTypeBody {
  return { name: form.name.trim(), sortOrder: orderOf(form.sortOrder, 1), sizes: beverageSizesBody(form) };
}

/** `sizes` (e os `flavors` de cada um) substitui a lista: o que ficou de fora é inativado. */
export function beverageUpdateBody(form: BeverageForm): UpdateBeverageTypeBody {
  return beverageCreateBody(form);
}

// ---------------------------------------------------------------- Combo

export interface ComponentRow {
  key: string;
  kind: ComboComponentKind;
  dishId: Uuid | null;
  dishSizeId: Uuid | null;
  /** Só da tela (o corpo leva o `beverageSizeId`). */
  beverageTypeId: Uuid | null;
  beverageSizeId: Uuid | null;
  quantity: number;
}

export interface ComboForm extends SiteSale {
  name: string;
  description: string;
  price: MoneyString;
  sortOrder: string;
  components: ComponentRow[];
}

export function emptyComponent(kind: ComboComponentKind): ComponentRow {
  return { key: newRowKey(), kind, dishId: null, dishSizeId: null, beverageTypeId: null, beverageSizeId: null, quantity: 1 };
}

export function beverageTypeOfSize(menu: FixedMenu, beverageSizeId: Uuid | null): BeverageType | undefined {
  return beverageSizeId ? menu.beverageTypes.find((t) => t.sizes.some((s) => s.id === beverageSizeId)) : undefined;
}

export function comboFormFrom(combo: Combo | null, menu: FixedMenu, defaults: { sortOrder: number }): ComboForm {
  if (!combo) {
    return { name: '', description: '', price: '0.00', sellsOnSite: false, sitePrice: '0.00', sortOrder: String(defaults.sortOrder), components: [] };
  }
  return {
    name: combo.name,
    description: combo.description ?? '',
    price: combo.price,
    ...siteSaleFrom(combo.sellsOnSite, combo.sitePrice),
    sortOrder: String(combo.sortOrder),
    components: [...combo.components]
      .sort((a, b) => a.sortOrder - b.sortOrder)
      .map((c) => ({
        key: c.id,
        kind: c.kind,
        dishId: c.dishId,
        dishSizeId: c.dishSizeId,
        beverageTypeId: beverageTypeOfSize(menu, c.beverageSizeId)?.id ?? null,
        beverageSizeId: c.beverageSizeId,
        quantity: c.quantity,
      })),
  };
}

/** "X-Burguer", "Batata frita P", "Refrigerante Lata 350 ml" — o mesmo formato do `name` do servidor. */
export function componentLabel(row: Pick<ComponentRow, 'kind' | 'dishId' | 'dishSizeId' | 'beverageSizeId'>, menu: FixedMenu): string {
  if (row.kind === 'DISH') {
    const dish = menu.dishes.find((d) => d.id === row.dishId);
    if (!dish) return 'Prato';
    const size = dish.sizes.find((s) => s.id === row.dishSizeId);
    return size ? `${dish.name} ${size.name}` : dish.name;
  }
  const type = beverageTypeOfSize(menu, row.beverageSizeId);
  const size = type?.sizes.find((s) => s.id === row.beverageSizeId);
  return type && size ? `${type.name} ${size.name}` : 'Bebida';
}

export function comboFormErrors(form: ComboForm, menu: FixedMenu): Record<string, string> {
  const e: Record<string, string> = {};
  if (!form.name.trim()) e.name = 'Informe o nome.';
  if (!positive(form.price)) e.price = 'Informe o preço do combo.';
  if (form.components.length === 0) e.components = 'Inclua pelo menos um item no combo.';
  if (form.components.length > LIMITS.components) e.components = `No máximo ${LIMITS.components} itens.`;
  form.components.forEach((c, i) => {
    if (c.kind === 'DISH') {
      const dish = menu.dishes.find((d) => d.id === c.dishId);
      if (!dish) e[`components[${i}].dishId`] = 'Escolha o prato.';
      else if (dish.hasSizes && !c.dishSizeId) e[`components[${i}].dishSizeId`] = 'Escolha o tamanho do prato.';
    } else if (!c.beverageSizeId) {
      e[`components[${i}].beverageSizeId`] = 'Escolha o tipo e o tamanho da bebida.';
    }
    if (c.quantity < 1 || c.quantity > LIMITS.componentQuantity) e[`components[${i}].quantity`] = 'Quantidade de 1 a 10.';
  });
  return e;
}

function componentsBody(form: ComboForm) {
  return form.components.map((c) =>
    c.kind === 'DISH'
      ? { kind: 'DISH' as const, dishId: c.dishId, dishSizeId: c.dishSizeId, beverageSizeId: null, quantity: c.quantity }
      : { kind: 'BEVERAGE' as const, dishId: null, dishSizeId: null, beverageSizeId: c.beverageSizeId, quantity: c.quantity },
  );
}

export function comboCreateBody(form: ComboForm): CreateComboBody {
  return {
    name: form.name.trim(),
    description: form.description.trim() || null,
    price: form.price,
    sellsOnSite: form.sellsOnSite,
    sitePrice: siteBodyPrice(form),
    sortOrder: orderOf(form.sortOrder, 1),
    components: componentsBody(form),
  };
}

/** `components` troca a lista inteira (a mesma lista não sobe a versão). */
export function comboUpdateBody(form: ComboForm): UpdateComboBody {
  return comboCreateBody(form);
}

// ---------------------------------------------------------------- Grupo de adicionais

export interface OptionRow {
  key: string;
  id: Uuid | null;
  name: string;
  price: MoneyString;
}

export interface AddonGroupForm {
  name: string;
  sortOrder: string;
  options: OptionRow[];
  dishCategoryIds: Uuid[];
  dishIds: Uuid[];
}

export function emptyOption(): OptionRow {
  return { key: newRowKey(), id: null, name: '', price: '0.00' };
}

export function addonGroupFormFrom(group: AddonGroup | null, defaults: { sortOrder: number }): AddonGroupForm {
  if (!group) return { name: '', sortOrder: String(defaults.sortOrder), options: [emptyOption()], dishCategoryIds: [], dishIds: [] };
  return {
    name: group.name,
    sortOrder: String(group.sortOrder),
    options: group.options.filter((o) => o.isActive).map((o) => ({ key: o.id, id: o.id, name: o.name, price: o.price })),
    dishCategoryIds: [...group.dishCategoryIds],
    dishIds: [...group.dishIds],
  };
}

export function addonGroupFormErrors(form: AddonGroupForm): Record<string, string> {
  const e: Record<string, string> = {};
  if (!form.name.trim()) e.name = 'Informe o nome do grupo.';
  if (form.options.length > LIMITS.options) e.options = `No máximo ${LIMITS.options} opções.`;
  const names = form.options.map((o) => o.name.trim().toLowerCase());
  form.options.forEach((o, i) => {
    if (!o.name.trim()) e[`options[${i}].name`] = 'Informe a opção.';
    else if (names.indexOf(names[i]!) !== i) e[`options[${i}].name`] = 'Opção repetida.';
  });
  return e;
}

export function addonGroupCreateBody(form: AddonGroupForm): CreateAddonGroupBody {
  return {
    name: form.name.trim(),
    sortOrder: orderOf(form.sortOrder, 1),
    options: form.options.map((o, i) => ({ id: o.id, name: o.name.trim(), price: o.price, sortOrder: i + 1 })),
    dishCategoryIds: form.dishCategoryIds,
    dishIds: form.dishIds,
  };
}

/** `options` substitui a lista (opção ausente é inativada); `dishCategoryIds` e `dishIds` substituem os vínculos. */
export function addonGroupUpdateBody(form: AddonGroupForm): UpdateAddonGroupBody {
  return addonGroupCreateBody(form);
}

export function toggleId(ids: readonly Uuid[], id: Uuid): Uuid[] {
  return ids.includes(id) ? ids.filter((x) => x !== id) : [...ids, id];
}

/** Move a linha `index` uma posição dentro da lista (ordem = `sortOrder` no corpo). */
export function moveRow<T>(rows: readonly T[], index: number, direction: -1 | 1): T[] {
  const target = index + direction;
  if (target < 0 || target >= rows.length) return [...rows];
  const next = [...rows];
  const [moved] = next.splice(index, 1);
  next.splice(target, 0, moved!);
  return next;
}

// ---------------------------------------------------------------- Erros

export const FIXED_MENU_CONFLICT_TEXT =
  'Outra pessoa alterou este item enquanto você editava. Nada foi gravado. O formulário foi recarregado com o que está salvo: confira e refaça a alteração.';

/** Combos da extensão `combos` do `409 UsedInActiveCombos` (06 §6.5). */
export function usedInCombos(e: unknown): { id: Uuid; name: string }[] | null {
  if (!isApiError(e, 'UsedInActiveCombos')) return null;
  const list = e.extensions.combos;
  return Array.isArray(list) ? list.filter((c): c is { id: Uuid; name: string } => !!c && typeof c.name === 'string') : [];
}

/** Erro de um campo: local (depois de tentar salvar) ou do `422` do servidor. */
export function fieldErrorOf(local: Record<string, string>, tried: boolean, serverError: unknown, field: string): string | undefined {
  return (tried ? local[field] : undefined) ?? (isApiError(serverError, 'ValidationFailed') ? serverError.fieldError(field) : undefined);
}
