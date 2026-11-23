/**
 * Comportamentos, rótulos e módulos do estabelecimento (EP-003 fatia 1, ADR-0054, ADR-0055, 06 §6.3).
 * As abas e o menu dependem **só** de `me.tenant.modules` e das permissões, nunca do comportamento nem do rótulo.
 * Sem dependência de plataforma (usado no celular e no navegador).
 */
import type { EstablishmentBehavior, Me, Permission, TenantLabel, TenantModule } from '@/api/types';
import { hasPermission } from '@/stores/session';
import type { TabName } from '@/stores/preferences';

export const behaviorNames: Record<EstablishmentBehavior, string> = {
  RESTAURANT: 'Restaurante',
  SNACK_BAR: 'Lanchonete',
};

/** Explicação curta de cada comportamento no cadastro (o comportamento é fixo depois, ADR-0054 item 4). */
export const behaviorDescriptions: Record<EstablishmentBehavior, string> = {
  RESTAURANT: 'Marmita, PF e quilo, com cardápio do dia ou da semana.',
  SNACK_BAR: 'Cardápio fixo, comandas no salão e delivery.',
};

export const BEHAVIORS: EstablishmentBehavior[] = ['RESTAURANT', 'SNACK_BAR'];

/** Lista fixa do ADR-0054 item 8, na ordem do 06 §6.3. */
export const LABELS: { code: TenantLabel; behavior: EstablishmentBehavior; text: string }[] = [
  { code: 'RESTAURANT', behavior: 'RESTAURANT', text: 'Restaurante' },
  { code: 'MARMITARIA', behavior: 'RESTAURANT', text: 'Marmitaria' },
  { code: 'SELF_SERVICE', behavior: 'RESTAURANT', text: 'Self-service/quilo' },
  { code: 'BURGER_JOINT', behavior: 'SNACK_BAR', text: 'Hamburgueria' },
  { code: 'PASTEL_SHOP', behavior: 'SNACK_BAR', text: 'Pastelaria' },
  { code: 'ACAI_SHOP', behavior: 'SNACK_BAR', text: 'Açaiteria' },
  { code: 'PIZZERIA', behavior: 'SNACK_BAR', text: 'Pizzaria' },
  { code: 'SNACK_BAR', behavior: 'SNACK_BAR', text: 'Lanchonete' },
  { code: 'ICE_CREAM_SHOP', behavior: 'SNACK_BAR', text: 'Sorveteria' },
  { code: 'CAFE', behavior: 'SNACK_BAR', text: 'Cafeteria' },
  { code: 'BAKERY', behavior: 'SNACK_BAR', text: 'Padaria' },
  { code: 'SKEWER_BAR', behavior: 'SNACK_BAR', text: 'Espetaria' },
  { code: 'SUSHI', behavior: 'SNACK_BAR', text: 'Sushi/temaki' },
  { code: 'SWEETS_SHOP', behavior: 'SNACK_BAR', text: 'Doceria' },
  { code: 'OTHER', behavior: 'SNACK_BAR', text: 'Outro' },
];

export function labelText(code: TenantLabel): string {
  return LABELS.find((l) => l.code === code)?.text ?? code;
}

/** Rótulos que valem para os comportamentos dados (ADR-0054 item 10), na ordem da lista. */
export function labelsFor(behaviors: readonly EstablishmentBehavior[]): typeof LABELS {
  return LABELS.filter((l) => behaviors.includes(l.behavior));
}

/** Mantém só os rótulos dos comportamentos dados, sem repetir, na ordem da lista. */
export function keepLabelsOf(labels: readonly TenantLabel[], behaviors: readonly EstablishmentBehavior[]): TenantLabel[] {
  return labelsFor(behaviors)
    .map((l) => l.code)
    .filter((code) => labels.includes(code));
}

/** Marca ou desmarca um rótulo, devolvendo na ordem da lista. */
export function toggleLabel(labels: readonly TenantLabel[], code: TenantLabel): TenantLabel[] {
  const next = labels.includes(code) ? labels.filter((l) => l !== code) : [...labels, code];
  return LABELS.map((l) => l.code).filter((c) => next.includes(c));
}

/** Mensagem de validação dos rótulos (cadastro e "Dados do estabelecimento"); `null` = válido. */
export function labelsError(labels: readonly TenantLabel[], behaviors: readonly EstablishmentBehavior[]): string | null {
  if (labels.length === 0) return 'Marque pelo menos um rótulo.';
  const allowed = new Set(labelsFor(behaviors).map((l) => l.code));
  if (labels.some((l) => !allowed.has(l))) return 'Marque só rótulos do tipo de estabelecimento escolhido.';
  return null;
}

export { moduleNames } from '@/lib/strings';

/**
 * Módulos do `me`. Um `me` guardado por uma versão anterior do app (sem `modules`) só pode ser de um restaurante
 * (06 §6.3: tenant sem comportamento gravado é tratado como `RESTAURANT`); o `GET /auth/me` seguinte corrige.
 */
const LEGACY_MODULES: TenantModule[] = ['MARMITA', 'PF', 'KILO', 'DAILY_MENU'];

export function tenantModules(me: Me | null): TenantModule[] {
  if (!me) return [];
  return Array.isArray(me.tenant.modules) ? me.tenant.modules : LEGACY_MODULES;
}

export function hasModule(me: Me | null, module: TenantModule): boolean {
  return tenantModules(me).includes(module);
}

/**
 * Módulos do catálogo de produtos (`/products`, 06 §6.5): basta um deles. Sem nenhum (a lanchonete pura, que cadastra
 * pelo cardápio fixo), `POST`/`PATCH /products` e o `image-upload` dão `403 ModuleNotEnabled` (ADR-0054 item 14).
 */
export const PRODUCT_CATALOG_MODULES: readonly TenantModule[] = ['MARMITA', 'PF', 'KILO', 'DAILY_MENU'];

export function hasAnyModule(me: Me | null, modules: readonly TenantModule[]): boolean {
  const own = tenantModules(me);
  return modules.some((m) => own.includes(m));
}

/** Seções da página de pedidos (06 §6.19 `sections`, EP-003 Q-317): o `orderType` que o envio do site usa. */
export type StorefrontSection = 'MARMITA' | 'A_LA_CARTE';

/**
 * As seções que a página deste estabelecimento vende, pelos módulos e na ordem do servidor (06 §6.19): `MARMITA` com
 * o módulo `MARMITA` (restaurante); `A_LA_CARTE` com `FIXED_MENU` **e** `DELIVERY` (lanchonete). Vazia = sem página.
 */
export function storefrontSections(me: Me | null): StorefrontSection[] {
  const own = tenantModules(me);
  const sections: StorefrontSection[] = [];
  if (own.includes('MARMITA')) sections.push('MARMITA');
  if (own.includes('FIXED_MENU') && own.includes('DELIVERY')) sections.push('A_LA_CARTE');
  return sections;
}

export interface TabSpec {
  name: TabName;
  title: string;
}

export const TAB_NAMES: TabName[] = ['marmitas', 'salao', 'delivery', 'pedidos', 'fechamento'];

export function isTabName(value: unknown): value is TabName {
  return typeof value === 'string' && (TAB_NAMES as string[]).includes(value);
}

/**
 * Abas superiores (ADR-0055), na ordem de exibição:
 * - Marmitas: `MARMITA` + `orders.view`;
 * - Salão: `PF`, `KILO` ou `A_LA_CARTE` + `orders.view`;
 * - Delivery: `DELIVERY` + `orders.view`;
 * - produção: `production.view`, "Pedidos" quando o tenant só tem os módulos do restaurante e "Cozinha" nos outros casos (item 5);
 * - Fechamento: `closing.view`.
 */
export function visibleTabs(me: Me | null): TabSpec[] {
  if (!me) return [];
  const can = (p: Permission) => hasPermission(me, p);
  const tabs: TabSpec[] = [];
  if (can('orders.view') && hasModule(me, 'MARMITA')) tabs.push({ name: 'marmitas', title: 'Marmitas' });
  if (can('orders.view') && hasAnyModule(me, ['PF', 'KILO', 'A_LA_CARTE'])) tabs.push({ name: 'salao', title: 'Salão' });
  if (can('orders.view') && hasModule(me, 'DELIVERY')) tabs.push({ name: 'delivery', title: 'Delivery' });
  if (can('production.view')) tabs.push({ name: 'pedidos', title: productionTabTitle(me) });
  if (can('closing.view')) tabs.push({ name: 'fechamento', title: 'Fechamento' });
  return tabs;
}

/** "Pedidos" só quando não há nenhum módulo da lanchonete; "Cozinha" nos outros casos (ADR-0055 item 5). */
export function productionTabTitle(me: Me | null): string {
  return hasAnyModule(me, ['A_LA_CARTE', 'DELIVERY', 'FIXED_MENU']) ? 'Cozinha' : 'Pedidos';
}

/**
 * Aba de entrada: a última usada, se ainda existe para este tenant e este funcionário; senão a primeira visível
 * (ADR-0055 item 5: a preferência ignora uma aba que deixou de existir). `null` sem nenhuma aba.
 */
export function initialTab(lastTab: unknown, tabs: readonly TabSpec[]): TabName | null {
  if (isTabName(lastTab) && tabs.some((t) => t.name === lastTab)) return lastTab;
  return tabs[0]?.name ?? null;
}

/** Sub-abas do Salão (ADR-0055 itens 3 e 4): PF e Quilo do restaurante e Comandas da lanchonete. */
export type SalaoKind = 'PF' | 'KILO' | 'A_LA_CARTE';

export function salaoKinds(me: Me | null): SalaoKind[] {
  return (['PF', 'KILO', 'A_LA_CARTE'] as const).filter((k) => hasModule(me, k));
}
