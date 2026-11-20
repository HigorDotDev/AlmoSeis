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

export interface PublicLimits {
  maxMarmitas: number;
  maxLines: number;
  maxQuantityPerLine: number;
  notesMaxLength: number;
}

export type ThirdParty = 'ANTI_BOT' | 'MAPS' | 'PAYMENTS';

export interface PublicPrivacy {
  controllerName: string;
  controllerPhone: string | null;
  operatorName: string;
  thirdParties: ThirdParty[];
}

export interface PublicDeliveryRule {
  pricePerKm: Money;
  minimumFee: Money;
  maxDistanceKm: string | null;
}

export interface PublicStore {
  slug: string;
  status: StoreStatus;
  name: string;
  address: PublicAddress | null;
  contactPhone: string | null;
  hoursText: string | null;
  pickupAvailable: boolean;
  deliveryAvailable: boolean;
  deliveryRule: PublicDeliveryRule | null;
  privacy: PublicPrivacy | null;
  businessDate: string | null;
  menu: PublicMenu | null;
  limits: PublicLimits;
}

export type FulfillmentType = 'PICKUP' | 'DELIVERY';
export type ExpectedPaymentMethod = 'CASH' | 'PIX' | 'CARD';

export interface DeliveryAddressInput {
  street: string;
  number: string;
  complement: string | null;
  neighborhood: string;
  city: string;
  zipCode: string | null;
  reference: string | null;
}

export interface WebOrderItemInput {
  itemKind: 'MARMITA' | 'BEVERAGE';
  marmitaSizeId: string | null;
  productId: string | null;
  quantity: number;
  mainDishProductIds: string[] | null;
  sideDishProductIds: string[] | null;
  addons: { productId: string; quantity: number }[] | null;
  notes: string | null;
}

export interface PlaceWebOrderRequest {
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
  distanceKm: string;
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

export interface TrackingItem {
  itemKind: 'MARMITA' | 'BEVERAGE';
  name: string;
  composition: string | null;
  quantity: number;
  unitPrice: Money;
  lineTotal: Money;
  notes: string | null;
  addons: { name: string; quantity: number; unitPrice: Money; lineTotal: Money }[];
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
}
