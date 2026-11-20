import { sanitizeCart, type Cart } from './cart';
import type { DeliveryAddressInput, ExpectedPaymentMethod } from './api/types';

/**
 * Tudo o que a página guarda fica **só** no `localStorage` deste navegador (ADR-0052 item 6), por restaurante.
 * Nunca vai para o servidor fora do próprio pedido. "Esquecer meus dados" apaga tudo.
 */
const PREFIX = 'almoseis:';
const key = (kind: string, slug: string) => `${PREFIX}${kind}:${slug}`;

function read<T>(k: string): T | null {
  try {
    const raw = window.localStorage.getItem(k);
    return raw ? (JSON.parse(raw) as T) : null;
  } catch {
    return null;
  }
}

function write(k: string, value: unknown): void {
  try {
    window.localStorage.setItem(k, JSON.stringify(value));
  } catch {
    // modo anônimo ou cota cheia: a página continua funcionando sem guardar
  }
  notify();
}

function remove(k: string): void {
  try {
    window.localStorage.removeItem(k);
  } catch {
    // ignore
  }
  notify();
}

// --- assinatura para useSyncExternalStore ---
const listeners = new Set<() => void>();
let revision = 0;
function notify() {
  revision++;
  listeners.forEach((l) => l());
}
/** Muda a cada escrita: serve de snapshot barato para `useSyncExternalStore`. */
export function storageRevision(): number {
  return revision;
}
export function subscribeStorage(listener: () => void): () => void {
  listeners.add(listener);
  const onStorage = (e: StorageEvent) => {
    if (!e.key || e.key.startsWith(PREFIX)) {
      revision++;
      listener();
    }
  };
  window.addEventListener('storage', onStorage);
  return () => {
    listeners.delete(listener);
    window.removeEventListener('storage', onStorage);
  };
}

// --- carrinho ---
export function loadCart(slug: string): Cart {
  return sanitizeCart(read(key('cart', slug)));
}
export function saveCart(slug: string, cart: Cart): void {
  if (cart.lines.length === 0) remove(key('cart', slug));
  else write(key('cart', slug), cart);
}

// --- dados do cliente ("Lembrar meus dados neste aparelho") ---
export interface SavedCustomer {
  name: string;
  phone: string;
  address: DeliveryAddressInput | null;
  paymentMethod: ExpectedPaymentMethod | null;
}
export function loadCustomer(slug: string): SavedCustomer | null {
  return read<SavedCustomer>(key('customer', slug));
}
export function saveCustomer(slug: string, customer: SavedCustomer): void {
  write(key('customer', slug), customer);
}
export function clearCustomer(slug: string): void {
  remove(key('customer', slug));
}

// --- pedidos enviados ("Acompanhar pedido em andamento", ADR-0047 item 10) ---
export interface SavedOrder {
  trackingToken: string;
  submittedAt: string;
  trackingExpiresAt: string;
  totalAmount: string;
  /** Cópia do carrinho enviado, para "Tentar de novo" depois de expirar ou ser recusado (ADR-0046 item 14). */
  cart: Cart;
  /** O cliente já viu o pedido terminar (recusado, entregue...): não aparece mais como "em andamento". */
  finished?: boolean;
}
export function loadOrders(slug: string, now: number = Date.now()): SavedOrder[] {
  const orders = read<SavedOrder[]>(key('orders', slug)) ?? [];
  return Array.isArray(orders) ? orders.filter((o) => Date.parse(o.trackingExpiresAt) > now) : [];
}
export function saveOrder(slug: string, order: SavedOrder): void {
  const others = loadOrders(slug).filter((o) => o.trackingToken !== order.trackingToken);
  write(key('orders', slug), [order, ...others].slice(0, 10));
}
export function findOrder(slug: string, token: string): SavedOrder | null {
  return loadOrders(slug).find((o) => o.trackingToken === token) ?? null;
}
export function markOrderFinished(slug: string, token: string): void {
  const order = findOrder(slug, token);
  if (order && !order.finished) saveOrder(slug, { ...order, finished: true });
}

// --- tentativa de envio em andamento (Idempotency-Key reaproveitada em reenvio, inclusive após recarregar) ---
export interface PendingSubmission {
  idempotencyKey: string;
  bodyJson: string;
}
export function loadPendingSubmission(slug: string): PendingSubmission | null {
  try {
    const raw = window.sessionStorage.getItem(key('pending', slug));
    return raw ? (JSON.parse(raw) as PendingSubmission) : null;
  } catch {
    return null;
  }
}
export function savePendingSubmission(slug: string, pending: PendingSubmission | null): void {
  try {
    if (pending) window.sessionStorage.setItem(key('pending', slug), JSON.stringify(pending));
    else window.sessionStorage.removeItem(key('pending', slug));
  } catch {
    // ignore
  }
}

/** "Esquecer meus dados": apaga carrinho, dados do cliente e pedidos guardados de todos os restaurantes. */
export function forgetEverything(): void {
  try {
    const keys: string[] = [];
    for (let i = 0; i < window.localStorage.length; i++) {
      const k = window.localStorage.key(i);
      if (k?.startsWith(PREFIX)) keys.push(k);
    }
    keys.forEach((k) => window.localStorage.removeItem(k));
    for (let i = window.sessionStorage.length - 1; i >= 0; i--) {
      const k = window.sessionStorage.key(i);
      if (k?.startsWith(PREFIX)) window.sessionStorage.removeItem(k);
    }
  } catch {
    // ignore
  }
  notify();
}
