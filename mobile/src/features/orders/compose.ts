/**
 * Formulários de pedido → corpo do `POST /orders` (06 §6.9) e prévia em centavos com os preços do cache.
 * A prévia é só prévia: o total oficial vem da API (ADR-0030).
 */
import type { Address, CreateOrderBody, MarmitaSize, Menu, NewItemInput, Product } from '@/api/types';
import { formatBRL, toCents, type MoneyString } from '@/lib/money';
import { previewKiloAmountCents } from '@/lib/weight';

export interface AddonLine {
  productId: string;
  quantity: number;
}

export interface MarmitaLine {
  key: string;
  sizeId: string;
  quantity: number;
  mainDishIds: string[];
  sideDishIds: string[];
  notes: string;
  addons: AddonLine[];
}

export interface PfLine {
  key: string;
  productId: string;
  quantity: number;
  notes: string;
  addons: AddonLine[];
}

export interface CustomerPick {
  id: string | null;
  name: string;
  phone: string;
  saveCustomer: boolean;
}

export interface MarmitaForm {
  customer: CustomerPick;
  fulfillmentType: 'DELIVERY' | 'PICKUP';
  addressId: string | null;
  address: Address | null;
  /** null = taxa padrão. */
  deliveryFee: MoneyString | null;
  lines: MarmitaLine[];
  beverages: Record<string, number>;
  notes: string;
}

export interface PfForm {
  tableLabel: string;
  tabNumber: string;
  lines: PfLine[];
  beverages: Record<string, number>;
  notes: string;
}

export interface KiloForm {
  tableLabel: string;
  tabNumber: string;
  weighings: number[];
  beverages: Record<string, number>;
  notes: string;
}

export const emptyMarmitaForm = (): MarmitaForm => ({
  customer: { id: null, name: '', phone: '', saveCustomer: false },
  fulfillmentType: 'PICKUP',
  addressId: null,
  address: null,
  deliveryFee: null,
  lines: [],
  beverages: {},
  notes: '',
});

export const emptyPfForm = (): PfForm => ({ tableLabel: '', tabNumber: '', lines: [], beverages: {}, notes: '' });

export const emptyKiloForm = (): KiloForm => ({ tableLabel: '', tabNumber: '', weighings: [], beverages: {}, notes: '' });

export interface PriceBook {
  sizes: Map<string, MarmitaSize>;
  products: Map<string, Product>;
  /** pfPrice por productId do cardápio PF. */
  pfPrices: Map<string, MoneyString>;
}

export function priceBook(sizes: MarmitaSize[] = [], products: Product[] = [], pfMenu?: Menu | null): PriceBook {
  const pfPrices = new Map<string, MoneyString>();
  for (const item of pfMenu?.items ?? []) {
    const price = item.pfPrice ?? products.find((p) => p.id === item.productId)?.defaultPrice ?? null;
    if (price) pfPrices.set(item.productId, price);
  }
  return { sizes: new Map(sizes.map((s) => [s.id, s])), products: new Map(products.map((p) => [p.id, p])), pfPrices };
}

const productPrice = (book: PriceBook, id: string) => {
  const p = book.products.get(id)?.defaultPrice;
  return p ? toCents(p) : 0;
};

/** Adicional: preço × quantidade × quantidade do pai (06 §6.9, 04 §4.7). */
function addonsCents(book: PriceBook, addons: AddonLine[], parentQty: number): number {
  return addons.reduce((sum, a) => sum + productPrice(book, a.productId) * a.quantity * parentQty, 0);
}

function beveragesCents(book: PriceBook, beverages: Record<string, number>): number {
  return Object.entries(beverages).reduce((sum, [id, qty]) => sum + productPrice(book, id) * qty, 0);
}

export function marmitaLineCents(book: PriceBook, line: MarmitaLine): number {
  const size = book.sizes.get(line.sizeId);
  return (size ? toCents(size.price) : 0) * line.quantity + addonsCents(book, line.addons, line.quantity);
}

export function previewMarmitaCents(form: MarmitaForm, book: PriceBook, defaultDeliveryFee: MoneyString | null): number {
  const items = form.lines.reduce((sum, l) => sum + marmitaLineCents(book, l), 0) + beveragesCents(book, form.beverages);
  const fee = form.fulfillmentType === 'DELIVERY' ? toCents(form.deliveryFee ?? defaultDeliveryFee ?? '0.00') : 0;
  return items + fee;
}

export function pfLineCents(book: PriceBook, line: PfLine): number {
  const price = book.pfPrices.get(line.productId);
  return (price ? toCents(price) : 0) * line.quantity + addonsCents(book, line.addons, line.quantity);
}

export function previewPfCents(form: PfForm, book: PriceBook): number {
  return form.lines.reduce((sum, l) => sum + pfLineCents(book, l), 0) + beveragesCents(book, form.beverages);
}

export function previewKiloCents(form: KiloForm, book: PriceBook, pricePerKg: MoneyString | null): number {
  const weighings = pricePerKg ? form.weighings.reduce((sum, g) => sum + previewKiloAmountCents(g, pricePerKg), 0) : 0;
  return weighings + beveragesCents(book, form.beverages);
}

/** Linhas de bebida para o ConfirmDialog: "2× Coca-Cola 350ml" → "R$ 12,00". */
export function beverageLines(book: PriceBook, beverages: Record<string, number>): { label: string; value: string }[] {
  return Object.entries(beverages)
    .filter(([, qty]) => qty > 0)
    .map(([id, qty]) => ({ label: `${qty}× ${book.products.get(id)?.name ?? '?'}`, value: formatBRL(productPrice(book, id) * qty) }));
}

const nullIfBlank = (s: string) => (s.trim() === '' ? null : s.trim());

export function beverageItems(beverages: Record<string, number>): NewItemInput[] {
  return Object.entries(beverages)
    .filter(([, qty]) => qty > 0)
    .map(([productId, quantity]) => ({ itemKind: 'BEVERAGE' as const, productId, quantity, notes: null }));
}

export function marmitaLineItem(l: MarmitaLine): NewItemInput {
  return {
    itemKind: 'MARMITA',
    marmitaSizeId: l.sizeId,
    quantity: l.quantity,
    mainDishProductIds: l.mainDishIds,
    sideDishProductIds: l.sideDishIds,
    notes: nullIfBlank(l.notes),
    addons: l.addons.filter((a) => a.quantity > 0).map((a) => ({ productId: a.productId, quantity: a.quantity, notes: null })),
  };
}

export function pfLineItem(l: PfLine): NewItemInput {
  return {
    itemKind: 'PF',
    productId: l.productId,
    quantity: l.quantity,
    notes: nullIfBlank(l.notes),
    addons: l.addons.filter((a) => a.quantity > 0).map((a) => ({ productId: a.productId, quantity: a.quantity, notes: null })),
  };
}

/** Telefone já em E.164 (o formulário converte). */
export function buildMarmitaBody(form: MarmitaForm, phoneE164: string | null): CreateOrderBody {
  const delivery = form.fulfillmentType === 'DELIVERY';
  const name = nullIfBlank(form.customer.name);
  return {
    orderType: 'MARMITA',
    fulfillmentType: form.fulfillmentType,
    customerId: form.customer.id,
    customerName: name,
    customerPhone: phoneE164,
    saveCustomer: form.customer.id === null && form.customer.saveCustomer && name !== null,
    customerAddressId: delivery ? form.addressId : null,
    deliveryAddress: delivery && !form.addressId ? form.address : null,
    deliveryFee: delivery ? form.deliveryFee : null,
    tableLabel: null,
    tabNumber: null,
    notes: nullIfBlank(form.notes),
    items: [...form.lines.map(marmitaLineItem), ...beverageItems(form.beverages)],
    weighings: [],
  };
}

export function buildPfBody(form: PfForm): CreateOrderBody {
  return {
    orderType: 'PF',
    fulfillmentType: 'DINE_IN',
    customerId: null,
    customerName: null,
    customerPhone: null,
    saveCustomer: false,
    customerAddressId: null,
    deliveryAddress: null,
    deliveryFee: null,
    tableLabel: nullIfBlank(form.tableLabel),
    tabNumber: nullIfBlank(form.tabNumber),
    notes: nullIfBlank(form.notes),
    items: [...form.lines.map(pfLineItem), ...beverageItems(form.beverages)],
    weighings: [],
  };
}

export function buildKiloBody(form: KiloForm): CreateOrderBody {
  const table = nullIfBlank(form.tableLabel);
  return {
    orderType: 'KILO',
    fulfillmentType: 'DINE_IN',
    customerId: null,
    customerName: table ? `Mesa ${table}` : null,
    customerPhone: null,
    saveCustomer: false,
    customerAddressId: null,
    deliveryAddress: null,
    deliveryFee: null,
    tableLabel: table,
    tabNumber: nullIfBlank(form.tabNumber),
    notes: nullIfBlank(form.notes),
    items: beverageItems(form.beverages),
    weighings: form.weighings.map((weightGrams) => ({ weightGrams })),
  };
}

/** Rótulo de uma linha de marmita: "Marmita M · Frango grelhado · Arroz, Feijão". */
export function marmitaLineLabel(line: MarmitaLine, book: PriceBook): string {
  const size = book.sizes.get(line.sizeId)?.name ?? '?';
  const names = (ids: string[]) => ids.map((id) => book.products.get(id)?.name ?? '?').join(', ');
  return [`Marmita ${size}`, names(line.mainDishIds), names(line.sideDishIds)].filter(Boolean).join(' · ');
}
