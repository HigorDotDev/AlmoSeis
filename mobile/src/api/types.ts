/**
 * Tipos da API escritos à mão a partir de docs/06-contratos-api.md (08 §8.3), com os mesmos nomes de campo.
 * Dinheiro continua string (`MoneyString`); peso em gramas inteiras.
 */
import type { MoneyString } from '@/lib/money';

export type { MoneyString };

export type Uuid = string;
/** "2026-10-27" */
export type BusinessDate = string;
/** "2026-10-27T15:04:05Z" */
export type Instant = string;

export interface Page<T> {
  items: T[];
  nextCursor: string | null;
}

export interface Items<T> {
  items: T[];
}

export interface Author {
  id: Uuid;
  displayName: string;
}

// ---------------------------------------------------------------- 6.2 Permissões

export type Role = 'OWNER' | 'MANAGER' | 'ATTENDANT' | 'KITCHEN' | 'CASHIER';

export type Permission =
  | 'orders.view'
  | 'orders.create'
  | 'orders.edit'
  | 'orders.cancel'
  | 'orders.delivery-fee.edit'
  | 'orders.accept'
  | 'orders.fulfillment.update'
  | 'production.view'
  | 'production.update'
  | 'payments.register'
  | 'payments.reverse'
  | 'cash.open'
  | 'cash.movement'
  | 'closing.view'
  | 'closing.finalize'
  | 'closing.adjust'
  | 'menu.manage'
  | 'catalog.manage'
  /** Pausar e reativar itens do cardápio fixo (EP-003 Q-306, ADR-0028): OWNER, MANAGER, ATTENDANT e KITCHEN. */
  | 'catalog.availability'
  | 'customers.manage'
  | 'employees.manage'
  | 'devices.manage'
  | 'settings.manage'
  | 'storefront.manage'
  | 'audit.view'
  | 'subscription.view';

export interface RoleInfo {
  role: Role;
  name: string;
  permissions: Permission[];
}

// ---------------------------------------------------------------- 6.3 Autenticação

export interface Tokens {
  tokenType: 'Bearer';
  accessToken: string;
  accessTokenExpiresAt: Instant;
  /** Web: `null`, o refresh vai no cookie `HttpOnly` `__Secure-almoseis-rt` (ADR-0053 §3). */
  refreshToken: string | null;
  refreshTokenExpiresAt: Instant;
}

export type SubscriptionStatus = 'TRIALING' | 'ACTIVE' | 'PAST_DUE' | 'SUSPENDED' | 'CANCELED';

/** Comportamento do estabelecimento (ADR-0054 item 1, 06 §6.3 "Comportamentos, rótulos e módulos"). */
export type EstablishmentBehavior = 'RESTAURANT' | 'SNACK_BAR';

/** Módulos derivados dos comportamentos (ADR-0054 item 12). O app monta abas e menu só por eles (ADR-0055). */
export type TenantModule = 'MARMITA' | 'PF' | 'KILO' | 'DAILY_MENU' | 'A_LA_CARTE' | 'DELIVERY' | 'FIXED_MENU';

/** Rótulos informativos, lista fixa (ADR-0054 item 8). O texto pt-BR fica em `features/tenant/establishment.ts`. */
export type TenantLabel =
  | 'RESTAURANT'
  | 'MARMITARIA'
  | 'SELF_SERVICE'
  | 'BURGER_JOINT'
  | 'PASTEL_SHOP'
  | 'ACAI_SHOP'
  | 'PIZZERIA'
  | 'SNACK_BAR'
  | 'ICE_CREAM_SHOP'
  | 'CAFE'
  | 'BAKERY'
  | 'SKEWER_BAR'
  | 'SUSHI'
  | 'SWEETS_SHOP'
  | 'OTHER';

export interface Me {
  user: { id: Uuid; fullName: string; email: string | null; phoneNumber: string | null };
  tenant: {
    id: Uuid;
    name: string;
    timeZone: string;
    behaviors: EstablishmentBehavior[];
    labels: TenantLabel[];
    modules: TenantModule[];
  };
  employee: { id: Uuid; displayName: string; roles: Role[]; hasPin: boolean };
  permissions: Permission[];
  memberships: { tenantId: Uuid; tenantName: string; employeeId: Uuid; roles: Role[] }[];
  subscription: { status: SubscriptionStatus | null; trialEndsAt: Instant | null; daysLeft: number | null; canWrite: boolean };
  sharedDevice: { id: Uuid; name: string } | null;
  serverTime: Instant;
}

export interface Session {
  tokens: Tokens;
  me: Me;
}

export interface NewAccount {
  fullName: string;
  email: string | null;
  phoneNumber: string;
  password: string;
}

export interface ExistingAccount {
  login: string;
  password: string;
}

export type DocumentType = 'CPF' | 'CNPJ';

export interface SignupBody {
  newAccount: NewAccount | null;
  existingAccount: ExistingAccount | null;
  tenant: {
    name: string;
    documentType: DocumentType;
    documentNumber: string;
    city: string;
    state: string;
    timeZone: string;
    /** Exatamente um, sem padrão (ADR-0054 item 3). */
    behavior: EstablishmentBehavior;
    /** Um ou mais, todos do comportamento escolhido (ADR-0054 item 9). */
    labels: TenantLabel[];
  };
  acceptedTermsVersion: string;
  installationId: string;
}

export interface LoginBody {
  login: string;
  password: string;
  installationId: string;
  preferredTenantId: Uuid | null;
}

export type Platform = 'ANDROID' | 'IOS' | 'WEB';

export interface SharedDevice {
  id: Uuid;
  tenantId: Uuid;
  installationId: string;
  name: string;
  platform: Platform;
  /** Web: `null`, o segredo vai no cookie `HttpOnly` `__Secure-almoseis-ds` (ADR-0053 §3.8). */
  deviceSecret: string | null;
  registeredAt: Instant;
  registeredBy: Author;
}

/** `PUT /devices/push-token` (06 §6.3 "Push dos pedidos do site", ADR-0049). O token nunca volta. */
export interface PushTokenBody {
  installationId: string;
  expoPushToken: string;
  /** `WEB` é reservado para o Web Push (Q-26) e hoje dá `422`. */
  platform: Exclude<Platform, 'WEB'>;
  /** `null` mantém a opção atual; no primeiro registro ela nasce ligada. */
  webOrdersEnabled: boolean | null;
}

export interface PushRegistration {
  installationId: string;
  platform: Platform;
  webOrdersEnabled: boolean;
  /** Opção ligada **e** o funcionário da sessão tem `orders.accept` agora. */
  receivesWebOrders: boolean;
  registeredAt: Instant;
  updatedAt: Instant;
}

/** `data` da notificação: só isto (ADR-0049 item 3). */
export type WebOrderPushKind = 'WEB_ORDER_AWAITING_ACCEPTANCE' | 'WEB_ORDER_REMINDER';

export interface WebOrderPushData {
  tenantId: Uuid;
  orderId: Uuid;
  kind: WebOrderPushKind;
}

export interface SharedDeviceSummary {
  id: Uuid;
  name: string;
  platform: Platform;
  registeredAt: Instant;
  registeredBy: Author;
  revokedAt: Instant | null;
}

export interface DeviceEmployees {
  device: { id: Uuid; name: string };
  tenant: { id: Uuid; name: string };
  employees: { id: Uuid; displayName: string; roles: Role[] }[];
}

// ---------------------------------------------------------------- 6.4 Estabelecimento

export interface Tenant {
  id: Uuid;
  version: number;
  name: string;
  documentType: DocumentType;
  documentNumber: string;
  city: string;
  state: string;
  timeZone: string;
  behaviors: EstablishmentBehavior[];
  labels: TenantLabel[];
  createdAt: Instant;
}

/** Corpo do `PATCH /tenant` (06 §6.4): campos ausentes não mudam. */
export type TenantPatch = Partial<Pick<Tenant, 'name' | 'city' | 'state' | 'timeZone' | 'labels'>>;

export type PaymentMethod = 'CASH' | 'PIX' | 'CARD';

export interface TenantSettings {
  version: number;
  kiloPricePerKg: MoneyString | null;
  defaultDeliveryFee: MoneyString;
  enabledPaymentMethods: PaymentMethod[];
  sharedDeviceIdleSeconds: number;
}

export interface Subscription {
  status: SubscriptionStatus;
  trialEndsAt: Instant;
  currentPeriodEndsAt: Instant | null;
  pastDueSince: Instant | null;
  graceEndsAt: Instant | null;
  daysLeft: number | null;
  canWrite: boolean;
  message: string | null;
}

export type EmployeeStatus = 'ACTIVE' | 'INACTIVE';

export interface Employee {
  id: Uuid;
  version: number;
  userId: Uuid;
  displayName: string;
  roles: Role[];
  status: EmployeeStatus;
  hasPin: boolean;
  contact: { email: string | null; phoneNumber: string | null } | null;
  joinedAt: Instant;
  deactivatedAt: Instant | null;
  deactivatedBy: Author | null;
}

export type InvitationPurpose = 'INVITE' | 'PASSWORD_RESET';
export type InvitationStatus = 'PENDING' | 'ACCEPTED' | 'REVOKED' | 'EXPIRED';

export interface Invitation {
  id: Uuid;
  purpose: InvitationPurpose;
  code: string;
  link: string;
  roles: Role[];
  targetEmployee: Author | null;
  status: InvitationStatus;
  expiresAt: Instant;
  createdAt: Instant;
  createdBy: Author;
  acceptedAt: Instant | null;
}

export interface InvitationPreview {
  code: string;
  purpose: InvitationPurpose;
  tenantName: string;
  roles: Role[];
  invitedBy: string;
  expiresAt: Instant;
}

// ---------------------------------------------------------------- 6.5 Catálogo

export type ProductKind = 'MAIN_DISH' | 'SIDE_DISH' | 'BEVERAGE' | 'ADDON';

export interface Product {
  id: Uuid;
  version: number;
  kind: ProductKind;
  name: string;
  description: string | null;
  category: string | null;
  defaultPrice: MoneyString | null;
  requiresPreparation: boolean;
  imageUrl: string | null;
  isActive: boolean;
  isAvailable: boolean;
  /** ADR-0044 item 5: bebidas e adicionais com `false` não aparecem na página de pedidos. Nasce `true`. */
  isVisibleOnPage: boolean;
  /**
   * Só em `BEVERAGE` (ADR-0057 item 5, Q-307): o preço cobrado no pedido do site. Na bebida, `isVisibleOnPage` é o
   * "vende no site". Adicionais têm sempre `null` (mesmo preço no site, Q-308).
   */
  sitePrice: MoneyString | null;
  createdAt: Instant;
  updatedAt: Instant;
}

export interface CreateProductBody {
  kind: ProductKind;
  name: string;
  description: string | null;
  category: string | null;
  defaultPrice: MoneyString | null;
  requiresPreparation: boolean | null;
  /** Só bebida; ausente = o `defaultPrice` (06 §6.5). */
  sitePrice?: MoneyString | null;
}

export type UpdateProductBody = Partial<{
  kind: ProductKind;
  name: string;
  description: string | null;
  category: string | null;
  defaultPrice: MoneyString | null;
  requiresPreparation: boolean;
  isActive: boolean;
  isAvailable: boolean;
  isVisibleOnPage: boolean;
  /** Só bebida (06 §6.5). */
  sitePrice: MoneyString | null;
  imageStorageKey: string | null;
}>;

export interface ImageUpload {
  uploadUrl: string;
  method: 'PUT';
  headers: Record<string, string>;
  storageKey: string;
  expiresAt: Instant;
}

export interface MarmitaSize {
  id: Uuid;
  version: number;
  name: string;
  price: MoneyString;
  sortOrder: number;
  isActive: boolean;
  /** "Vende no site" (ADR-0057 item 1): só com `true` o tamanho aparece na página, pelo `sitePrice`. */
  sellsOnSite: boolean;
  /** Ligar sem preço de site grava o `price` (item 2). Desligada, fica guardado. */
  sitePrice: MoneyString | null;
}

// ---------------------------------------------------------------- 6.5 Cardápio fixo (EP-003 fatia 2, ADR-0056/0057)

export interface DishCategory {
  id: Uuid;
  version: number;
  name: string;
  sortOrder: number;
  isActive: boolean;
  createdAt: Instant;
  updatedAt: Instant;
}

export interface DishSize {
  id: Uuid;
  name: string;
  price: MoneyString;
  sellsOnSite: boolean;
  sitePrice: MoneyString | null;
  sortOrder: number;
  isActive: boolean;
  isPaused: boolean;
  /** Derivado no servidor (ADR-0056 item 23), nunca gravado. */
  isSellable: boolean;
}

export interface Dish {
  id: Uuid;
  version: number;
  dishCategoryId: Uuid;
  name: string;
  description: string | null;
  imageUrl: string | null;
  requiresPreparation: boolean;
  /** "Tamanho?" (ADR-0056 item 6): com tamanhos, `price`/`sellsOnSite`/`sitePrice` ficam em cada tamanho. */
  hasSizes: boolean;
  price: MoneyString | null;
  sellsOnSite: boolean;
  sitePrice: MoneyString | null;
  sortOrder: number;
  isActive: boolean;
  isPaused: boolean;
  isSellable: boolean;
  sizes: DishSize[];
  /** Grupos ativos da categoria ∪ do próprio prato, sem repetir (item 18). */
  addonGroupIds: Uuid[];
  createdAt: Instant;
  updatedAt: Instant;
}

export interface BeverageFlavor {
  id: Uuid;
  name: string;
  extraPrice: MoneyString;
  sortOrder: number;
  isActive: boolean;
  isPaused: boolean;
}

export interface BeverageSize {
  id: Uuid;
  name: string;
  price: MoneyString;
  sellsOnSite: boolean;
  sitePrice: MoneyString | null;
  sortOrder: number;
  isActive: boolean;
  isPaused: boolean;
  isSellable: boolean;
  flavors: BeverageFlavor[];
}

export interface BeverageType {
  id: Uuid;
  version: number;
  name: string;
  sortOrder: number;
  isActive: boolean;
  sizes: BeverageSize[];
  createdAt: Instant;
  updatedAt: Instant;
}

export type ComboComponentKind = 'DISH' | 'BEVERAGE';

export interface ComboComponent {
  id: Uuid;
  kind: ComboComponentKind;
  dishId: Uuid | null;
  dishSizeId: Uuid | null;
  beverageSizeId: Uuid | null;
  /** "Batata frita P", "Refrigerante Lata 350 ml". */
  name: string;
  quantity: number;
  sortOrder: number;
  /** `false` = o motivo do combo fora de venda ("Indisponível: Batata frita P pausado"). */
  isSellable: boolean;
}

export interface Combo {
  id: Uuid;
  version: number;
  name: string;
  description: string | null;
  imageUrl: string | null;
  price: MoneyString;
  sellsOnSite: boolean;
  sitePrice: MoneyString | null;
  sortOrder: number;
  isActive: boolean;
  isPaused: boolean;
  isSellable: boolean;
  components: ComboComponent[];
  createdAt: Instant;
  updatedAt: Instant;
}

export interface AddonOption {
  id: Uuid;
  name: string;
  price: MoneyString;
  sortOrder: number;
  isActive: boolean;
  isPaused: boolean;
}

export interface AddonGroup {
  id: Uuid;
  version: number;
  name: string;
  sortOrder: number;
  isActive: boolean;
  options: AddonOption[];
  dishCategoryIds: Uuid[];
  dishIds: Uuid[];
  createdAt: Instant;
  updatedAt: Instant;
}

/** `GET /fixed-menu`: listas planas na ordem de `sortOrder` e nome. */
export interface FixedMenu {
  categories: DishCategory[];
  dishes: Dish[];
  beverageTypes: BeverageType[];
  combos: Combo[];
  addonGroups: AddonGroup[];
}

export interface DishSizeInput {
  id: Uuid | null;
  name: string;
  price: MoneyString;
  sellsOnSite: boolean;
  sitePrice: MoneyString | null;
  sortOrder: number;
}

export interface CreateDishBody {
  dishCategoryId: Uuid;
  name: string;
  description: string | null;
  requiresPreparation: boolean;
  hasSizes: boolean;
  price: MoneyString | null;
  sellsOnSite: boolean;
  sitePrice: MoneyString | null;
  sortOrder: number;
  sizes: DishSizeInput[];
}

/** Só os campos presentes mudam; `sizes` presente substitui a lista (o que fica de fora é inativado). */
export type UpdateDishBody = Partial<CreateDishBody & { isActive: boolean; imageStorageKey: string | null }>;

export interface BeverageFlavorInput {
  id: Uuid | null;
  name: string;
  extraPrice: MoneyString;
  sortOrder: number;
}

export interface BeverageSizeInput {
  id: Uuid | null;
  name: string;
  price: MoneyString;
  sellsOnSite: boolean;
  sitePrice: MoneyString | null;
  sortOrder: number;
  flavors: BeverageFlavorInput[];
}

export interface CreateBeverageTypeBody {
  name: string;
  sortOrder: number;
  sizes: BeverageSizeInput[];
}

export type UpdateBeverageTypeBody = Partial<CreateBeverageTypeBody & { isActive: boolean }>;

export interface ComboComponentInput {
  kind: ComboComponentKind;
  dishId: Uuid | null;
  dishSizeId: Uuid | null;
  beverageSizeId: Uuid | null;
  quantity: number;
}

export interface CreateComboBody {
  name: string;
  description: string | null;
  price: MoneyString;
  sellsOnSite: boolean;
  sitePrice: MoneyString | null;
  sortOrder: number;
  components: ComboComponentInput[];
}

/** `components` presente troca a lista inteira. */
export type UpdateComboBody = Partial<CreateComboBody & { isActive: boolean; imageStorageKey: string | null }>;

export interface AddonOptionInput {
  id: Uuid | null;
  name: string;
  price: MoneyString;
  sortOrder: number;
}

export interface CreateAddonGroupBody {
  name: string;
  sortOrder: number;
  options: AddonOptionInput[];
  dishCategoryIds: Uuid[];
  dishIds: Uuid[];
}

export type UpdateAddonGroupBody = Partial<CreateAddonGroupBody & { isActive: boolean }>;

export interface PauseInput {
  id: Uuid;
  isPaused: boolean;
}

/** Corpos das rotas `…/availability` (`catalog.availability`): só os itens enviados mudam. */
export interface DishAvailabilityBody {
  isPaused?: boolean | null;
  sizes?: PauseInput[];
}

export interface BeverageAvailabilityBody {
  sizes?: PauseInput[];
  flavors?: PauseInput[];
}

export interface ComboAvailabilityBody {
  isPaused: boolean;
}

export interface AddonAvailabilityBody {
  options: PauseInput[];
}

// ---------------------------------------------------------------- 6.6 Expediente

export type BusinessDayStatus = 'OPEN' | 'CLOSED';

export interface BusinessDay {
  id: Uuid;
  version: number;
  businessDate: BusinessDate;
  status: BusinessDayStatus;
  openingBalance: MoneyString;
  openedAt: Instant;
  openedBy: Author;
  closedAt: Instant | null;
  closedBy: Author | null;
  dailyClosingId: Uuid | null;
}

export type MenuType = 'MARMITA' | 'PF';
export type MenuKind = 'DAILY' | 'WEEKLY_TEMPLATE';

export interface OpenBusinessDayResult {
  businessDay: BusinessDay;
  materializedMenus: { id: Uuid; menuType: MenuType; sourceMenuId: Uuid | null }[];
}

// ---------------------------------------------------------------- 6.7 Cardápios

export type MenuSection = 'MAIN_DISH' | 'SIDE_DISH';

export interface MenuItem {
  id: Uuid;
  productId: Uuid;
  section: MenuSection;
  name: string;
  description: string | null;
  imageUrl: string | null;
  pfPrice: MoneyString | null;
  isAvailable: boolean;
  sortOrder: number;
}

export interface Menu {
  id: Uuid;
  version: number;
  menuType: MenuType;
  kind: MenuKind;
  date: BusinessDate | null;
  dayOfWeek: number | null;
  sourceMenuId: Uuid | null;
  items: MenuItem[];
  createdAt: Instant;
  createdBy: Author | null;
  updatedAt: Instant;
}

export type MenuSummary = Omit<Menu, 'items'> & { itemCount: number };

export interface MenuItemInput {
  productId: Uuid;
  pfPrice: MoneyString | null;
  isAvailable: boolean;
  sortOrder: number;
}

export interface CreateMenuBody {
  menuType: MenuType;
  kind: MenuKind;
  date: BusinessDate | null;
  dayOfWeek: number | null;
  items: MenuItemInput[];
}

export interface CloneMenuBody {
  kind: MenuKind;
  date: BusinessDate | null;
  dayOfWeek: number | null;
}

export interface MenuSources {
  yesterdayMenuId: Uuid | null;
  lastWeekSameDayMenuId: Uuid | null;
  weeklyTemplateId: Uuid | null;
}

// ---------------------------------------------------------------- 6.8 Clientes

export interface Address {
  street: string;
  number: string | null;
  complement: string | null;
  neighborhood: string | null;
  reference: string | null;
}

export interface CustomerAddress extends Address {
  id: Uuid;
  label: string | null;
  isDefault: boolean;
}

export interface Customer {
  id: Uuid;
  version: number;
  name: string;
  phoneNumber: string | null;
  notes: string | null;
  addresses: CustomerAddress[];
  pendingAmount: MoneyString;
  pendingOrderCount: number;
  anonymizedAt: Instant | null;
  createdAt: Instant;
}

/** `POST /customers/lookup` (06 §6.8, EP-003 fatia 4): telefone sem cadastro → os dois `null`, nunca `404`. */
export interface CustomerByPhone {
  customer: Customer | null;
  /** O `deliveryAddress` do pedido mais recente do cliente que tinha endereço ("usar o endereço do último pedido"). */
  lastDeliveryAddress: Address | null;
}

export type CustomerAddressInput = Address & { label: string | null; isDefault: boolean };

export interface CreateCustomerBody {
  name: string;
  phoneNumber: string | null;
  notes: string | null;
  addresses: CustomerAddressInput[];
}

export interface PendingOrders {
  customer: { id: Uuid; name: string };
  totalPending: MoneyString;
  orders: {
    id: Uuid;
    orderNumber: number;
    businessDate: BusinessDate;
    orderType: OrderType;
    totalAmount: MoneyString;
    paidAmount: MoneyString;
    balance: MoneyString;
    version: number;
  }[];
}

// ---------------------------------------------------------------- 6.9 Pedidos

/** `A_LA_CARTE`: itens do cardápio fixo (EP-003 fatia 3, ADR-0058). */
export type OrderType = 'MARMITA' | 'PF' | 'KILO' | 'A_LA_CARTE';
export type FulfillmentType = 'DELIVERY' | 'PICKUP' | 'DINE_IN';
export type ProductionStatus = 'OPEN' | 'IN_PROGRESS' | 'COMPLETED';
export type CancellationStatus = 'ACTIVE' | 'CANCELED';
/**
 * Tipos de item (06 §6.9). Cardápio fixo (ADR-0058 item 8): `FIXED_DISH`, `FIXED_BEVERAGE` e `COMBO` no primeiro nível;
 * `COMBO_PART` só dentro de `parts` do `COMBO`; `GROUP_ADDON` em `addons` de um prato avulso ou de uma parte de prato.
 */
export type ItemKind = 'MARMITA' | 'PF' | 'BEVERAGE' | 'ADDON' | 'FIXED_DISH' | 'FIXED_BEVERAGE' | 'COMBO' | 'COMBO_PART' | 'GROUP_ADDON';
/** `APP`: criado no app; `WEB`: página de pedidos (EP-002, ADR-0046). */
export type OrderOrigin = 'APP' | 'WEB';

/** Tabela de preço do pedido (ADR-0057 item 7): `APP` → `COUNTER`, `WEB` → `SITE`. Imutável. */
export type PriceList = 'COUNTER' | 'SITE';
export type WebOrderRiskFlag = 'CAPTCHA_UNAVAILABLE';
export type DeliveryFeeSource = 'FIXED_DEFAULT' | 'DISTANCE' | 'MANUAL';
/** ADR-0046 item 1. `AWAITING_PAYMENT` é reservado à Fase 2. */
export type AcceptanceStatus = 'AWAITING_PAYMENT' | 'AWAITING_ACCEPTANCE' | 'ACCEPTED' | 'REJECTED' | 'EXPIRED' | 'WITHDRAWN';
/** ADR-0046 item 2: só em `DELIVERY`/`PICKUP` aceitos. */
export type FulfillmentStatus = 'PENDING' | 'OUT_FOR_DELIVERY' | 'DELIVERED' | 'PICKED_UP';
/** ADR-0046 item 12. `BUSINESS_DAY_CLOSED` é só do sistema (finalização do fechamento). */
export type RejectionReasonCode = 'ITEM_UNAVAILABLE' | 'OUT_OF_DELIVERY_AREA' | 'TOO_BUSY' | 'CLOSING_SOON' | 'SUSPECTED_PRANK' | 'OTHER' | 'BUSINESS_DAY_CLOSED';
/** Os tempos estimados aceitos por `POST /orders/{id}/accept`. */
export type EstimatedReadyMinutes = 15 | 30 | 45 | 60;

export interface OrderAddon {
  id: Uuid;
  /** `GROUP_ADDON`: opção de grupo do cardápio fixo, com `addonOptionId` e `productId: null`. */
  itemKind: 'ADDON' | 'GROUP_ADDON';
  productId: Uuid | null;
  /** Ausente num pedido guardado no cache por um app anterior. */
  addonOptionId?: Uuid | null;
  name: string;
  quantity: number;
  unitPrice: MoneyString;
  lineTotal: MoneyString;
  requiresPreparation: boolean;
  notes: string | null;
  createdAt: Instant;
  /** `null` no que o cliente montou na página (pedido `WEB`). */
  createdBy: Author | null;
}

export interface OrderItem {
  id: Uuid;
  itemKind: Exclude<ItemKind, 'ADDON' | 'GROUP_ADDON'>;
  marmitaSizeId: Uuid | null;
  productId: Uuid | null;
  /**
   * Campos do cardápio fixo (EP-003 fatia 3): `null` nos itens de marmita, PF e catálogo. Opcionais só porque um
   * pedido guardado no cache por um app anterior vem sem eles.
   */
  dishId?: Uuid | null;
  dishSizeId?: Uuid | null;
  beverageSizeId?: Uuid | null;
  beverageFlavorId?: Uuid | null;
  comboId?: Uuid | null;
  /** Nome de exibição do snapshot: nome base + `sizeName` + " · " + `flavorName` ("Açaí 500 ml"). */
  name: string;
  sizeName?: string | null;
  flavorName?: string | null;
  composition: string | null;
  components: { productId: Uuid; role: MenuSection; name: string }[];
  quantity: number;
  unitPrice: MoneyString;
  lineTotal: MoneyString;
  requiresPreparation: boolean;
  notes: string | null;
  addons: OrderAddon[];
  /** Só no `COMBO`: as `COMBO_PART`, uma por componente, na ordem do combo (06 §6.9). Fora dele, `[]`. */
  parts?: OrderItem[];
  createdAt: Instant;
  createdBy: Author | null;
}

export interface Weighing {
  id: Uuid;
  weightGrams: number;
  pricePerKg: MoneyString;
  amount: MoneyString;
  createdAt: Instant;
  createdBy: Author;
}

export type PaymentStatus = 'CONFIRMED' | 'REVERSED';

export interface OrderPaymentSummary {
  id: Uuid;
  amount: MoneyString;
  paymentMethod: PaymentMethod;
  label: string | null;
  status: PaymentStatus;
  paidAt: Instant;
  businessDate: BusinessDate;
}

export interface Order {
  id: Uuid;
  version: number;
  /** `null` enquanto o pedido do site não é aceito; recusados, expirados e desistências nunca recebem (ADR-0046). */
  orderNumber: number | null;
  businessDayId: Uuid;
  businessDate: BusinessDate;
  orderType: OrderType;
  origin: OrderOrigin;
  /** EP-003, ADR-0057 item 7. Um pedido guardado no cache por um app anterior pode vir sem o campo. */
  priceList?: PriceList;
  fulfillmentType: FulfillmentType;
  customer: { id: Uuid | null; name: string; phoneNumber: string | null } | null;
  deliveryAddress: Address | null;
  tableLabel: string | null;
  tabNumber: string | null;
  notes: string | null;
  /**
   * Forma que o cliente pretende usar: no pedido do site (ADR-0044 item 19) e, desde o EP-003 fatia 4, no delivery
   * por telefone do app (ADR-0059 item 5). **Não** cria pagamento nem marca nada como pago.
   */
  expectedPaymentMethod: PaymentMethod | null;
  /** "Troco para R$ X", só com `CASH`: o motoboy leva `changeFor − balance`. Editável até a quitação (`PATCH`). */
  changeFor: MoneyString | null;
  /**
   * Derivado no servidor (ADR-0059 complementado em 2026-11-23, I2): `true` quando há `changeFor` e ele ficou abaixo
   * do `totalAmount` (item, quantidade ou taxa incluídos depois). O app mostra o alerta em vez de um troco negativo.
   * Um pedido guardado no cache por um app anterior pode vir sem o campo (o app trata como ausente).
   */
  changeForBelowTotal: boolean;
  /** `CAPTCHA_UNAVAILABLE`: o anti-robô não respondeu no envio (ADR-0045 item 12). */
  riskFlag: WebOrderRiskFlag | null;
  deliveryFee: MoneyString;
  /** ADR-0048 item 20, só em entrega: `null` fora de entrega e nas entregas antigas. */
  deliveryFeeSource: DeliveryFeeSource | null;
  /** `"4.5"` só enquanto a origem for `DISTANCE`. */
  deliveryDistanceKm: string | null;
  itemsTotal: MoneyString;
  weighingsTotal: MoneyString;
  totalAmount: MoneyString;
  paidAmount: MoneyString;
  balance: MoneyString;
  isPaid: boolean;
  productionStatus: ProductionStatus | null;
  productionStatusChangedAt: Instant | null;
  cancellationStatus: CancellationStatus;
  cancellation: { canceledAt: Instant; canceledBy: Author; reason: string } | null;
  acceptanceStatus: AcceptanceStatus;
  acceptanceDeadline: Instant | null;
  acceptedAt: Instant | null;
  acceptedBy: Author | null;
  estimatedReadyMinutes: EstimatedReadyMinutes | null;
  rejection: OrderRejection | null;
  expiredAt: Instant | null;
  withdrawnAt: Instant | null;
  fulfillmentStatus: FulfillmentStatus | null;
  fulfillmentStatusChangedAt: Instant | null;
  outForDeliveryAt: Instant | null;
  deliveredAt: Instant | null;
  pickedUpAt: Instant | null;
  /** Derivado no servidor: `PICKUP` + produção `COMPLETED` + atendimento `PENDING`. */
  isReadyForPickup: boolean;
  items: OrderItem[];
  weighings: Weighing[];
  payments: OrderPaymentSummary[];
  pendingAlertCount: number;
  createdAt: Instant;
  /** `null` em pedido `WEB`. */
  createdBy: Author | null;
  updatedAt: Instant;
}

export interface OrderRejection {
  reasonCode: RejectionReasonCode;
  message: string | null;
  rejectedAt: Instant;
  /** `null` na recusa do sistema ao finalizar o fechamento. */
  rejectedBy: Author | null;
}

export interface OrderSummary {
  id: Uuid;
  version: number;
  orderNumber: number | null;
  businessDate: BusinessDate;
  orderType: OrderType;
  origin: OrderOrigin;
  fulfillmentType: FulfillmentType;
  customerName: string | null;
  tableLabel: string | null;
  tabNumber: string | null;
  totalAmount: MoneyString;
  paidAmount: MoneyString;
  balance: MoneyString;
  isPaid: boolean;
  productionStatus: ProductionStatus | null;
  cancellationStatus: CancellationStatus;
  acceptanceStatus: AcceptanceStatus;
  acceptanceDeadline: Instant | null;
  fulfillmentStatus: FulfillmentStatus | null;
  isReadyForPickup: boolean;
  /** EP-003 fatia 4: também no resumo, para o card do delivery. Uma API anterior pode não mandar. */
  expectedPaymentMethod?: PaymentMethod | null;
  changeFor?: MoneyString | null;
  /** 2026-11-23 (I2): mesma regra do `Order`. */
  changeForBelowTotal: boolean;
  itemCount: number;
  pendingAlertCount: number;
  createdAt: Instant;
}

export interface OrderFilters {
  businessDayId?: Uuid;
  allDays?: boolean;
  orderType?: OrderType;
  isPaid?: boolean;
  cancellationStatus?: CancellationStatus;
  productionStatus?: ProductionStatus;
  /** Padrão no servidor: `ACCEPTED`. `AWAITING_ACCEPTANCE` é a faixa "Novos do site". */
  acceptanceStatus?: AcceptanceStatus;
  /** EP-003 fatia 4: repetível (`fulfillmentType=DELIVERY&fulfillmentType=PICKUP`). */
  fulfillmentType?: FulfillmentType | FulfillmentType[];
  /** EP-003 fatia 4: `PENDING` inclui as entregas/retiradas sem marcação; o salão nunca entra. */
  fulfillmentStatus?: FulfillmentStatus;
  customerId?: Uuid;
  search?: string;
  limit?: number;
  cursor?: string;
}

export interface AddonInput {
  productId: Uuid;
  quantity: number;
  notes: string | null;
}

export type NewItemInput =
  | {
      itemKind: 'MARMITA';
      marmitaSizeId: Uuid;
      quantity: number;
      mainDishProductIds: Uuid[];
      sideDishProductIds: Uuid[];
      notes: string | null;
      addons: AddonInput[];
    }
  | { itemKind: 'PF'; productId: Uuid; quantity: number; notes: string | null; addons: AddonInput[] }
  | { itemKind: 'BEVERAGE'; productId: Uuid; quantity: number; notes: string | null }
  | { itemKind: 'ADDON'; parentOrderItemId: Uuid; productId: Uuid; quantity: number; notes: string | null }
  // Cardápio fixo (06 §6.9 "Regras do corpo A_LA_CARTE").
  | { itemKind: 'FIXED_DISH'; dishId: Uuid; dishSizeId: Uuid | null; quantity: number; notes: string | null; groupAddons: GroupAddonInput[] }
  | { itemKind: 'FIXED_BEVERAGE'; beverageSizeId: Uuid; beverageFlavorId: Uuid | null; quantity: number; notes: string | null }
  | { itemKind: 'COMBO'; comboId: Uuid; quantity: number; parts: ComboPartInput[] }
  /** Só em `POST /orders/{id}/items`: num prato avulso ou numa parte de prato de um combo já registrado. */
  | { itemKind: 'GROUP_ADDON'; parentOrderItemId: Uuid; addonOptionId: Uuid; quantity: number; notes: null };

export interface GroupAddonInput {
  addonOptionId: Uuid;
  /** 1 a 10 (ADR-0056 item 19). */
  quantity: number;
}

/** Escolhas de um componente do combo (`comboComponentId` = `components[].id` do `GET /fixed-menu`). */
export interface ComboPartInput {
  comboComponentId: Uuid;
  /** Só na parte de bebida (obrigatório se o tamanho tem sabores). */
  beverageFlavorId: Uuid | null;
  /** Só na parte de prato. */
  notes: string | null;
  groupAddons: GroupAddonInput[] | null;
}

export interface CreateOrderBody {
  orderType: OrderType;
  fulfillmentType: FulfillmentType;
  customerId: Uuid | null;
  customerName: string | null;
  customerPhone: string | null;
  saveCustomer: boolean;
  customerAddressId: Uuid | null;
  deliveryAddress: Address | null;
  deliveryFee: MoneyString | null;
  tableLabel: string | null;
  tabNumber: string | null;
  notes: string | null;
  /**
   * EP-003 fatia 4 (ADR-0059 item 5, Q-316): obrigatória na entrega, opcional na retirada, proibida no salão (o app
   * nem manda o campo no salão). Só formas habilitadas (`422 PaymentMethodDisabled`). Não cria pagamento.
   */
  expectedPaymentMethod?: PaymentMethod | null;
  /** Só com `CASH`, de pelo menos o total (`422` em `changeFor`). */
  changeFor?: MoneyString | null;
  items: NewItemInput[];
  weighings: { weightGrams: number }[];
}

export type UpdateOrderBody = Partial<{
  customerName: string | null;
  customerPhone: string | null;
  fulfillmentType: FulfillmentType;
  deliveryAddress: Address | null;
  deliveryFee: MoneyString;
  tableLabel: string | null;
  tabNumber: string | null;
  notes: string | null;
  /**
   * Desde 2026-11-23 (ADR-0059 complementado, I1): mudam até a quitação (`409 OrderAlreadyPaid`). Virar entrega exige
   * a forma no mesmo `PATCH`; a forma que deixa de ser `CASH` sem `changeFor` no corpo apaga o "troco para".
   */
  expectedPaymentMethod: PaymentMethod | null;
  /** Só com `CASH`; editado, de pelo menos o total resultante (`422` em `changeFor`); `null` apaga. */
  changeFor: MoneyString | null;
}>;

export interface OrderHistoryEntry {
  occurredAt: Instant;
  operationType: string;
  origin: string;
  actor: Author | null;
  before: Record<string, unknown> | null;
  after: Record<string, unknown> | null;
}

export interface OrderAlert {
  eventId: Uuid;
  changeKind: string;
  affectsProduction: boolean;
  orderVersion: number;
  occurredAt: Instant;
  actor: Author | null;
  summary: string;
  details: Record<string, unknown>;
}

export interface ProductionItem {
  id: Uuid;
  name: string;
  composition: string | null;
  quantity: number;
  notes: string | null;
  requiresPreparation: boolean;
  addons: { id: Uuid; name: string; quantity: number; notes: string | null }[];
}

export interface ProductionOrder {
  id: Uuid;
  version: number;
  orderNumber: number;
  orderType: OrderType;
  origin: OrderOrigin;
  fulfillmentType: FulfillmentType;
  fulfillmentStatus: FulfillmentStatus | null;
  label: string | null;
  notes: string | null;
  isPaid: boolean;
  createdAt: Instant;
  /** O painel ordena pelo aceite (no app, igual a `createdAt`). */
  acceptedAt: Instant;
  items: ProductionItem[];
  pendingAlertCount: number;
}

export interface ProductionBoard {
  businessDayId: Uuid;
  groups: Record<ProductionStatus, ProductionOrder[]>;
}

// ---------------------------------------------------------------- 6.10 Pagamentos

export interface Reversal {
  id: Uuid;
  businessDate: BusinessDate;
  reason: string;
  createdAt: Instant;
  createdBy: Author;
}

export interface Payment {
  id: Uuid;
  orderId: Uuid;
  businessDayId: Uuid;
  businessDate: BusinessDate;
  amount: MoneyString;
  paymentMethod: PaymentMethod;
  cashReceived: MoneyString | null;
  changeGiven: MoneyString | null;
  label: string | null;
  status: PaymentStatus;
  paidAt: Instant;
  createdBy: Author;
  reversal: Reversal | null;
}

export interface OrderBalance {
  id: Uuid;
  version: number;
  totalAmount: MoneyString;
  paidAmount: MoneyString;
  balance: MoneyString;
  isPaid: boolean;
}

export interface RegisterPaymentBody {
  amount: MoneyString;
  paymentMethod: PaymentMethod;
  cashReceived: MoneyString | null;
  label: string | null;
}

export interface PaymentResult {
  payment: Payment;
  order: OrderBalance;
}

export interface ReversalResult {
  reversal: Reversal & { paymentId: Uuid; orderId: Uuid; businessDayId: Uuid; amount: MoneyString; paymentMethod: PaymentMethod };
  order: OrderBalance;
}

// ---------------------------------------------------------------- 6.11 Fechamento

export type CashMovementType = 'SUPPLY' | 'WITHDRAWAL';

export interface CashMovement {
  id: Uuid;
  businessDayId: Uuid;
  movementType: CashMovementType;
  amount: MoneyString;
  description: string;
  createdAt: Instant;
  createdBy: Author;
}

export type AdjustmentType = string;

export interface ClosingAdjustment {
  id: Uuid;
  relatedClosingId: Uuid;
  businessDayId: Uuid;
  adjustmentType: AdjustmentType;
  amount: MoneyString;
  reason: string;
  createdAt: Instant;
  createdBy: Author;
}

export interface ClosingSummary {
  businessDay: { id: Uuid; version: number; businessDate: BusinessDate; status: BusinessDayStatus };
  isFinal: boolean;
  dailyClosingId: Uuid | null;
  computedAt: Instant;
  orderCount: number;
  totalSold: MoneyString;
  deliveryFeesTotal: MoneyString;
  /**
   * "Vendido pelo site" (ADR-0057 item 12, complementado em 2026-11-23, M1): Σ (total − taxa) dos pedidos do site,
   * **sem** a taxa de entrega; já contido no `totalSold`. Ausente num resumo guardado por um app anterior.
   */
  siteSoldTotal?: MoneyString;
  /** "Taxa de entrega do site" (M1): Σ `deliveryFee` dos pedidos do site, já contida no `deliveryFeesTotal`. */
  siteDeliveryFeeTotal?: MoneyString;
  totalReceived: MoneyString;
  receivedFromPreviousDays: MoneyString;
  totalPending: MoneyString;
  canceled: { count: number; total: MoneyString };
  reversed: { count: number; total: MoneyString };
  /** Informativo, fora do vendido (ADR-0046 item 13, RF-210.2). */
  webOrders: { awaitingAcceptanceCount: number; autoRejectedCount: number; notAcceptedCount: number };
  /** Entregas aceitas e ativas sem "Entregue" (ADR-0046 item 20). Só informa. */
  unmarkedDeliveryCount: number;
  byOrderType: { orderType: OrderType; count: number; sold: MoneyString; received: MoneyString; pending: MoneyString; reversed: MoneyString }[];
  byPaymentMethod: { paymentMethod: PaymentMethod; count: number; received: MoneyString; reversed: MoneyString }[];
  cash: {
    openingBalance: MoneyString;
    cashReceived: MoneyString;
    cashReversed: MoneyString;
    supplies: MoneyString;
    withdrawals: MoneyString;
    expectedCash: MoneyString;
    countedCash: MoneyString | null;
    cashDifference: MoneyString | null;
    justification: string | null;
  };
  adjustments: ClosingAdjustment[];
  closedAt: Instant | null;
  closedBy: Author | null;
}

export interface ClosingHistoryItem {
  dailyClosingId: Uuid;
  businessDayId: Uuid;
  businessDate: BusinessDate;
  totalSold: MoneyString;
  totalReceived: MoneyString;
  totalPending: MoneyString;
  cashDifference: MoneyString;
  closedAt: Instant;
  closedBy: Author;
  adjustmentCount: number;
}

export interface MonthlyClosing {
  month: string;
  closingCount: number;
  totalSold: MoneyString;
  totalReceived: MoneyString;
  totalPendingAtClosings: MoneyString;
  cashDifferenceTotal: MoneyString;
  /** Somas dos snapshots do mês (2026-11-23, M1), como gravados. Uma API anterior pode não mandar. */
  siteSoldTotal?: MoneyString;
  siteDeliveryFeeTotal?: MoneyString;
  byOrderType: { orderType: OrderType; sold: MoneyString; received: MoneyString }[];
  byPaymentMethod: { paymentMethod: PaymentMethod; received: MoneyString }[];
  days: { businessDate: BusinessDate; dailyClosingIds: Uuid[]; totalSold: MoneyString; totalReceived: MoneyString }[];
}

// ---------------------------------------------------------------- 6.12 Sync e operações

export type EntityType =
  | 'TENANT'
  | 'TENANT_SETTINGS'
  | 'EMPLOYEE'
  | 'PRODUCT'
  | 'MARMITA_SIZE'
  | 'DISH_CATEGORY'
  | 'DISH'
  | 'BEVERAGE_TYPE'
  | 'COMBO'
  | 'ADDON_GROUP'
  | 'MENU'
  | 'CUSTOMER'
  | 'ORDER'
  | 'PAYMENT'
  | 'PAYMENT_REVERSAL'
  | 'BUSINESS_DAY'
  | 'CASH_MOVEMENT'
  | 'DAILY_CLOSING'
  | 'CLOSING_ADJUSTMENT';

export interface Change {
  seq: number;
  entityType: EntityType;
  entityId: Uuid;
  entityVersion: number;
  changeKind: 'UPSERT' | 'DELETE';
}

export interface ChangesPage {
  changes: Change[];
  lastSeq: number;
  hasMore: boolean;
}

export type OperationStatus = 'NOT_FOUND' | 'IN_PROGRESS' | 'COMPLETED';

export interface OperationInfo {
  key: string;
  status: OperationStatus;
  endpoint: string | null;
  responseStatus: number | null;
  resourceId: Uuid | null;
  createdAt: Instant | null;
  completedAt: Instant | null;
}

// ---------------------------------------------------------------- 6.13 Auditoria

export interface AuditLogEntry {
  id: Uuid;
  occurredAt: Instant;
  origin: string;
  operationType: string;
  entityType: string;
  entityId: Uuid;
  orderId: Uuid | null;
  actor: Author | null;
  sharedDevice: { id: Uuid; name: string } | null;
  before: Record<string, unknown> | null;
  after: Record<string, unknown> | null;
  correlationId: string;
}

// ---------------------------------------------------------------- 6.14 Tempo real

export interface RealtimeEnvelope {
  eventId: Uuid;
  eventType: string;
  seq: number | null;
  occurredAt: Instant;
  entityType: EntityType | null;
  entityId: Uuid | null;
  entityVersion: number | null;
  actorEmployeeId: Uuid | null;
  data: Record<string, unknown> | null;
}

// ---------------------------------------------------------------- 6.18 Página de pedidos (EP-002 F-210)

/** Estado que a página mostra agora (06 §6.19), calculado no servidor. */
export type StoreStatus = 'OPEN' | 'CLOSED' | 'PAUSED' | 'UNAVAILABLE';

export type DeliveryFeeMode = 'DISTANCE' | 'FIXED';

/** Endereço do restaurante na página: todos os campos podem vir nulos (página nasce sem endereço). */
export interface StorefrontAddress {
  street: string | null;
  number: string | null;
  complement: string | null;
  neighborhood: string | null;
  city: string | null;
  state: string | null;
  zipCode: string | null;
}

/** Regra da taxa por distância (ADR-0048), usada a partir da fatia 4. `maxDistanceKm` com uma casa ("6.0"). */
export interface StorefrontDelivery {
  feeMode: DeliveryFeeMode;
  pricePerKm: MoneyString | null;
  minimumFee: MoneyString;
  maxDistanceKm: string | null;
}

export interface Storefront {
  version: number;
  slug: string;
  previousSlugs: string[];
  publicName: string;
  isPageActive: boolean;
  isAcceptingOrders: boolean;
  isDeliveryEnabled: boolean;
  offersDelivery: boolean;
  offersPickup: boolean;
  contactPhone: string | null;
  hoursText: string | null;
  address: StorefrontAddress;
  /** Ponto do restaurante (ADR-0048 item 3), 6 casas. Geocodificado ao salvar o endereço, nasce `isConfirmed: false`. */
  location: StorefrontLocation | null;
  delivery: StorefrontDelivery;
  /** Se a página oferece entrega agora e, se não, o que falta (na ordem do servidor). */
  pageDelivery: { available: boolean; missing: PageDeliveryMissing[] };
  status: StoreStatus;
  updatedAt: Instant;
}

/** `PUT /storefront` [M]: substitui a configuração inteira (06 §6.18). */
export interface StorefrontLocation {
  latitude: number;
  longitude: number;
  isConfirmed: boolean;
}

/** `DELIVERY_OFF`, `PRICE_PER_KM`, `CONFIRMED_LOCATION`, `MAPS_PROVIDER` (os três últimos só no modo `DISTANCE`). */
export type PageDeliveryMissing = 'DELIVERY_OFF' | 'PRICE_PER_KM' | 'CONFIRMED_LOCATION' | 'MAPS_PROVIDER';

/**
 * `PUT /storefront` [M]: substitui a configuração inteira (06 §6.18). Sem `location` (ou `null`), o ponto salvo e a
 * confirmação ficam como estão; mudar o `address` descarta o ponto e a API geocodifica de novo.
 */
export type UpdateStorefrontBody = Omit<Storefront, 'version' | 'previousSlugs' | 'location' | 'pageDelivery' | 'status' | 'updatedAt'> & {
  location?: StorefrontLocation | null;
};
