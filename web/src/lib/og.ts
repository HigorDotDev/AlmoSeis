import type { PublicStore } from './api/types';
import { formatBRL, parseMoney } from './money';

/** Título da prévia do link no WhatsApp (Open Graph, RN-201.7). */
export function ogTitle(store: PublicStore): string {
  return `Peça na ${store.name}`;
}

/** "Hoje: Frango grelhado, Costela. Marmita P R$ 16,00 · M R$ 20,00. Peça pela página: retirada." */
export function describeMenu(store: PublicStore): string {
  if (store.status === 'UNAVAILABLE') return 'Página de pedidos indisponível.';
  if (!store.menu) {
    if (store.status === 'CLOSED') return `Fechado agora.${store.hoursText ? ` ${store.hoursText}.` : ''}`;
    return 'Peça sua marmita: entrega e retirada.';
  }
  const dishes = store.menu.mainDishes.filter((d) => d.isAvailable).map((d) => d.name);
  const sizes = store.menu.marmitaSizes.map((s) => `${s.name} ${formatBRL(parseMoney(s.price))}`);
  const parts: string[] = [];
  if (dishes.length) parts.push(`Hoje: ${dishes.join(', ')}.`);
  if (sizes.length) parts.push(`Marmita ${sizes.join(' · ')}.`);
  const modes = [store.deliveryAvailable && 'entrega', store.pickupAvailable && 'retirada'].filter(Boolean).join(' e ');
  if (modes) parts.push(`Peça pela página: ${modes}.`);
  const text = parts.join(' ');
  return text.length > 300 ? `${text.slice(0, 297)}...` : text;
}

/** O perfil público ainda não traz logo nem capa (§6.19): usa a foto de um prato do dia, se houver. */
export function ogImage(store: PublicStore): string | null {
  return store.menu?.mainDishes.find((d) => d.isAvailable && d.imageUrl)?.imageUrl ?? null;
}
