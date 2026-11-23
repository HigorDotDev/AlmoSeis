import type { PublicStore } from './api/types';
import { storeSections } from './fixed-menu';
import { formatBRL, parseMoney } from './money';

/** Título da prévia do link no WhatsApp (Open Graph, RN-201.7). */
export function ogTitle(store: PublicStore): string {
  return `Peça na ${store.name}`;
}

/**
 * "Hoje: Frango grelhado, Costela. Marmita P R$ 16,00 · M R$ 20,00. Peça pela página: retirada." Lanchonete:
 * "Cardápio: Lanches, Porções, Bebidas, Combos. Peça pela página: entrega e retirada."
 */
export function describeMenu(store: PublicStore): string {
  if (store.status === 'UNAVAILABLE') return 'Página de pedidos indisponível.';
  const fixedMenu = store.fixedMenu ?? null;
  if (!store.menu && !fixedMenu) {
    if (store.status === 'CLOSED') return `Fechado agora.${store.hoursText ? ` ${store.hoursText}.` : ''}`;
    return storeSections(store).includes('MARMITA') ? 'Peça sua marmita: entrega e retirada.' : 'Peça pela página: entrega e retirada.';
  }
  const parts: string[] = [];
  if (store.menu) {
    const dishes = store.menu.mainDishes.filter((d) => d.isAvailable).map((d) => d.name);
    const sizes = store.menu.marmitaSizes.map((s) => `${s.name} ${formatBRL(parseMoney(s.price))}`);
    if (dishes.length) parts.push(`Hoje: ${dishes.join(', ')}.`);
    if (sizes.length) parts.push(`Marmita ${sizes.join(' · ')}.`);
  }
  if (fixedMenu) {
    const tabs = [
      ...fixedMenu.categories.filter((c) => c.dishes.length > 0).map((c) => c.name),
      ...(fixedMenu.beverages.length ? ['Bebidas'] : []),
      ...(fixedMenu.combos.length ? ['Combos'] : []),
    ];
    if (tabs.length) parts.push(`Cardápio: ${tabs.join(', ')}.`);
  }
  const modes = [store.deliveryAvailable && 'entrega', store.pickupAvailable && 'retirada'].filter(Boolean).join(' e ');
  if (modes) parts.push(`Peça pela página: ${modes}.`);
  const text = parts.join(' ');
  return text.length > 300 ? `${text.slice(0, 297)}...` : text;
}

/** O perfil público ainda não traz logo nem capa (§6.19): usa a foto de um prato do dia ou do cardápio, se houver. */
export function ogImage(store: PublicStore): string | null {
  const daily = store.menu?.mainDishes.find((d) => d.isAvailable && d.imageUrl)?.imageUrl;
  if (daily) return daily;
  const fixed = store.fixedMenu;
  return (
    fixed?.categories.flatMap((c) => c.dishes).find((d) => d.imageUrl)?.imageUrl ??
    fixed?.combos.find((c) => c.imageUrl)?.imageUrl ??
    null
  );
}
