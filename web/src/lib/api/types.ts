/**
 * Tipos das rotas públicas da API (docs/06-contratos-api.md §6.19). Dinheiro em string ("35.50"), enums UPPER_SNAKE.
 * ADR-0051 prevê mover estes tipos para `packages/shared`; até lá vivem aqui.
 */
export type Money = string;

export type StoreStatus = 'OPEN' | 'CLOSED' | 'PAUSED' | 'UNAVAILABLE';

export interface PublicAddress {
  street: string;
  number: string | null;
  complement: string | null;
  neighborhood: string | null;
  city: string | null;
  state: string | null;
}

export interface PublicDish {
  productId: string;
  name: string;
  description: string | null;
  imageUrl: string | null;
  isAvailable: boolean;
}

export interface PublicPricedProduct extends PublicDish {
  price: Money;
}

export interface PublicMarmitaSize {
  id: string;
  name: string;
  price: Money;
}

export interface PublicMenu {
  marmitaSizes: PublicMarmitaSize[];
  mainDishes: PublicDish[];
  sideDishes: PublicDish[];
  addons: PublicPricedProduct[];
  beverages: PublicPricedProduct[];
}

/**
 * Seção da página (EP-003 Q-317): o que ela vende, pelo comportamento do estabelecimento. O nome é o `orderType` do
 * pedido: `MARMITA` (marmitas e bebidas do cardápio do dia, em `menu`) e `A_LA_CARTE` (cardápio fixo da lanchonete, em
 * `fixedMenu`). Um pedido é de uma seção só.
 */
export type StoreSection = 'MARMITA' | 'A_LA_CARTE';
export type WebOrderType = StoreSection;

// --- Cardápio fixo da lanchonete (§6.19, EP-003 fatia 5; ADR-0057). Só o que vende no site e não está pausado. ---

export interface PublicDishSize {
  id: string;
  name: string;
  price: Money;
}

/** Sem tamanho: `price` e `sizes: []`. Com tamanho: `price: null` e o cliente escolhe um de `sizes`. */
export interface PublicFixedDish {
  id: string;
  name: string;
  description: string | null;
  imageUrl: string | null;
  hasSizes: boolean;
  price: Money | null;
  sizes: PublicDishSize[];
  addonGroupIds: string[];
}

export interface PublicDishCategory {
  id: string;
  name: string;
  dishes: PublicFixedDish[];
}

export interface PublicBeverageFlavor {
  id: string;
  name: string;
  extraPrice: Money;
}

/** `flavors` não vazio → sabor obrigatório. Preço = `price` + `extraPrice` do sabor. */
export interface PublicBeverageSize {
  id: string;
  name: string;
  price: Money;
  flavors: PublicBeverageFlavor[];
}

export interface PublicBeverageType {
  id: string;
  name: string;
  sizes: PublicBeverageSize[];
}

export interface PublicComboComponent {
  id: string;
  kind: 'DISH' | 'BEVERAGE';
  name: string;
  quantity: number;
  /** `DISH`: grupos de adicionais do prato. `BEVERAGE`: `[]`. */
  addonGroupIds: string[];
  /** `BEVERAGE`: sabores (vazio = sem escolha). `DISH`: `[]`. */
  flavors: PublicBeverageFlavor[];
}

export interface PublicCombo {
  id: string;
  name: string;
  description: string | null;
  imageUrl: string | null;
  price: Money;
  components: PublicComboComponent[];
}

export interface PublicAddonOption {
  id: string;
  name: string;
  price: Money;
}

export interface PublicAddonGroup {
  id: string;
  name: string;
  options: PublicAddonOption[];
}

export interface PublicFixedMenu {
  categories: PublicDishCategory[];
  beverages: PublicBeverageType[];
  combos: PublicCombo[];
  addonGroups: PublicAddonGroup[];
}

export interface PublicLimits {
  maxMarmitas: number;
  /** Lanchonete (Q-318): pratos e combos somados; bebidas não contam. */
  maxDishesAndCombos: number;
  maxLines: number;
  maxQuantityPerLine: number;
  /** Quantidade de cada opção de adicional de grupo. */
  maxAddonQuantity: number;
  notesMaxLength: number;
}

export type ThirdParty = 'ANTI_BOT' | 'MAPS' | 'PAYMENTS';

export interface PublicPrivacy {
  controllerName: string;
  controllerPhone: string | null;
  operatorName: string;
  thirdParties: ThirdParty[];
}

export type DeliveryFeeMode = 'DISTANCE' | 'FIXED';

/** Regra da taxa (§6.19). DISTANCE: pricePerKm, minimumFee e maxDistanceKm (opcional); FIXED: só fixedFee. */
export interface PublicDeliveryRule {
  feeMode: DeliveryFeeMode;
  pricePerKm: Money | null;
  minimumFee: Money | null;
  maxDistanceKm: string | null;
  fixedFee: Money | null;
}

export interface PublicStore {
  slug: string;
  status: StoreStatus;
  name: string;
  /** Ordem `MARMITA`, `A_LA_CARTE`; `[]` em `UNAVAILABLE`. */
  sections: StoreSection[];
  address: PublicAddress | null;
  contactPhone: string | null;
  hoursText: string | null;
  pickupAvailable: boolean;
  deliveryAvailable: boolean;
  deliveryRule: PublicDeliveryRule | null;
  privacy: PublicPrivacy | null;
  businessDate: string | null;
  /** Seção `MARMITA` (senão `null`). */
  menu: PublicMenu | null;
  /** Seção `A_LA_CARTE`, só em `OPEN`/`PAUSED` (senão `null`). */
  fixedMenu: PublicFixedMenu | null;
  limits: PublicLimits;
}

export type FulfillmentType = 'PICKUP' | 'DELIVERY';
export type ExpectedPaymentMethod = 'CASH' | 'PIX' | 'CARD';

/** Endereço de entrega (cotação e pedido). city nula = a do restaurante; a UF é sempre a do restaurante. */
export interface DeliveryAddressInput {
  street: string;
  number: string;
  complement: string | null;
  neighborhood: string;
  city: string | null;
  zipCode: string | null;
  reference: string | null;
}

/** Linha de um pedido `MARMITA`. */
export interface MarmitaOrderItemInput {
  itemKind: 'MARMITA' | 'BEVERAGE';
  marmitaSizeId: string | null;
  productId: string | null;
  quantity: number;
  mainDishProductIds: string[] | null;
  sideDishProductIds: string[] | null;
  addons: { productId: string; quantity: number }[] | null;
  notes: string | null;
}

export interface GroupAddonInput {
  addonOptionId: string;
  quantity: number;
}

/** Linhas de um pedido `A_LA_CARTE` (§6.19, formato do §6.9). Os campos de outro tipo ficam ausentes. */
export interface FixedDishOrderItemInput {
  itemKind: 'FIXED_DISH';
  dishId: string;
  dishSizeId: string | null;
  quantity: number;
  notes: string | null;
  groupAddons: GroupAddonInput[];
}

export interface FixedBeverageOrderItemInput {
  itemKind: 'FIXED_BEVERAGE';
  beverageSizeId: string;
  beverageFlavorId: string | null;
  quantity: number;
  notes: string | null;
}

export interface ComboPartInput {
  comboComponentId: string;
  beverageFlavorId: string | null;
  notes: string | null;
  groupAddons: GroupAddonInput[] | null;
}

/** O combo não tem `notes` nem adicionais próprios: as escolhas vão em `parts`. */
export interface ComboOrderItemInput {
  itemKind: 'COMBO';
  comboId: string;
  quantity: number;
  notes: null;
  parts: ComboPartInput[];
}

export type WebOrderItemInput = MarmitaOrderItemInput | FixedDishOrderItemInput | FixedBeverageOrderItemInput | ComboOrderItemInput;

export interface PlaceWebOrderRequest {
  /** Ausente = `MARMITA`. Precisa ser uma das `sections`. */
  orderType: WebOrderType;
  fulfillmentType: FulfillmentType;
  customerName: string;
  customerPhone: string;
  deliveryAddress: DeliveryAddressInput | null;
  deliveryQuoteId: string | null;
  expectedPaymentMethod: ExpectedPaymentMethod;
  changeFor: Money | null;
  notes: string | null;
  items: WebOrderItemInput[];
  expectedTotal: Money;
  /** Campo-isca (ADR-0045): sempre null quando quem preenche é gente. */
  website: string | null;
}

export type AcceptanceStatus = 'AWAITING_PAYMENT' | 'AWAITING_ACCEPTANCE' | 'ACCEPTED' | 'REJECTED' | 'EXPIRED' | 'WITHDRAWN';

export interface PlacedWebOrder {
  trackingToken: string;
  trackingExpiresAt: string;
  acceptanceStatus: AcceptanceStatus;
  acceptanceDeadline: string;
  itemsTotal: Money;
  deliveryFee: Money;
  totalAmount: Money;
}

export interface DeliveryQuote {
  quoteId: string;
  formattedAddress: string;
  /** Nulo no modo FIXED. */
  distanceKm: string | null;
  deliveryFee: Money;
  expiresAt: string;
}

export interface CartChangedLine {
  index: number;
  name: string;
  available: boolean;
  unitPrice: Money | null;
  lineTotal: Money | null;
}

export interface RecalculatedCart {
  items: CartChangedLine[];
  unavailableProductIds: string[];
  itemsTotal: Money;
  deliveryFee: Money;
  totalAmount: Money;
}

export type TrackingStatus =
  | 'AWAITING_PAYMENT'
  | 'AWAITING_ACCEPTANCE'
  | 'REJECTED'
  | 'EXPIRED'
  | 'WITHDRAWN'
  | 'CONFIRMED'
  | 'IN_PREPARATION'
  | 'READY_FOR_DELIVERY'
  | 'READY_FOR_PICKUP'
  | 'OUT_FOR_DELIVERY'
  | 'DELIVERED'
  | 'PICKED_UP'
  | 'CANCELED';

export type TimelineStep =
  | 'SUBMITTED'
  | 'ACCEPTED'
  | 'IN_PREPARATION'
  | 'READY'
  | 'OUT_FOR_DELIVERY'
  | 'DELIVERED'
  | 'PICKED_UP'
  | 'REJECTED'
  | 'EXPIRED'
  | 'WITHDRAWN'
  | 'CANCELED';

export type RejectionReasonCode =
  | 'ITEM_UNAVAILABLE'
  | 'OUT_OF_DELIVERY_AREA'
  | 'TOO_BUSY'
  | 'CLOSING_SOON'
  | 'SUSPECTED_PRANK'
  | 'OTHER'
  | 'BUSINESS_DAY_CLOSED';

export type TrackingItemKind = 'MARMITA' | 'BEVERAGE' | 'FIXED_DISH' | 'FIXED_BEVERAGE' | 'COMBO' | 'COMBO_PART';

/** `lineTotal` é só da linha: o total de um combo é a soma da árvore (combo + partes + adicionais das partes). */
export interface TrackingItem {
  itemKind: TrackingItemKind;
  name: string;
  composition: string | null;
  quantity: number;
  unitPrice: Money;
  lineTotal: Money;
  notes: string | null;
  addons: { name: string; quantity: number; unitPrice: Money; lineTotal: Money }[];
  /** `COMBO`: as partes (`COMBO_PART`). Demais: `[]`. */
  parts?: TrackingItem[];
}

export interface WebOrderTracking {
  version: number;
  store: { slug: string; name: string; contactPhone: string | null; address: PublicAddress | null };
  orderNumber: number | null;
  submittedAt: string;
  status: TrackingStatus;
  acceptanceStatus: AcceptanceStatus;
  acceptanceDeadline: string | null;
  estimatedReadyMinutes: number | null;
  canWithdraw: boolean;
  timeline: { step: TimelineStep; at: string }[];
  rejection: { reasonCode: RejectionReasonCode; message: string | null } | null;
  fulfillmentType: FulfillmentType;
  customerFirstName: string;
  deliveryAddress: { street: string; number: string | null } | null;
  items: TrackingItem[];
  itemsTotal: Money;
  deliveryFee: Money;
  deliveryDistanceKm: string | null;
  totalAmount: Money;
  expectedPaymentMethod: ExpectedPaymentMethod | null;
  changeFor: Money | null;
  changedByStore: boolean;
  trackingExpiresAt: string;
}

/** Problem Details (§6.0) com as extensões das rotas públicas. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status: number;
  detail?: string;
  errorCode?: string;
  traceId?: string;
  errors?: Record<string, string[]>;
  storeStatus?: StoreStatus;
  acceptanceStatus?: AcceptanceStatus;
  cart?: RecalculatedCart;
  /** 409 DeliveryQuoteChanged: a cotação nova. */
  quote?: DeliveryQuote;
  /** 422 OutOfDeliveryArea (no raio). */
  distanceKm?: string;
  maxDistanceKm?: string;
}
