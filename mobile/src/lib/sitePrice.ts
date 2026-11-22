/**
 * "Vende no site" e preço de site (ADR-0057, 06 §6.5): cardápio fixo, tamanhos de marmita e bebidas do restaurante.
 * O preço de site pode ser maior, menor ou igual ao de balcão; não é desconto nem taxa (item 11).
 */
import { formatBRL, toCents, type MoneyString } from './money';

const positive = (v: MoneyString | null | undefined) => (v ? toCents(v) > 0 : false);

export interface SiteSale {
  sellsOnSite: boolean;
  /** `"0.00"` = vazio: o servidor grava o preço de balcão (item 2). */
  sitePrice: MoneyString;
}

export function siteSaleFrom(sellsOnSite: boolean | undefined, sitePrice: MoneyString | null | undefined): SiteSale {
  return { sellsOnSite: sellsOnSite ?? false, sitePrice: sitePrice ?? '0.00' };
}

/** Ligar a chave preenche o preço de site com o de balcão quando ele está vazio (ADR-0057 item 2). */
export function toggleSiteSale(current: SiteSale, on: boolean, counterPrice: MoneyString): SiteSale {
  if (on && !positive(current.sitePrice)) return { sellsOnSite: true, sitePrice: counterPrice };
  return { ...current, sellsOnSite: on };
}

/**
 * `sitePrice` do corpo: o valor digitado, ou `null` se vazio (com a chave ligada, o servidor usa o de balcão).
 * Desligada, o valor vai igual e fica guardado no servidor (volta se a chave for religada, 06 §6.5).
 */
export function siteBodyPrice(s: SiteSale): MoneyString | null {
  return positive(s.sitePrice) ? s.sitePrice : null;
}

/** "Site: R$ 23,90" / "Não vende no site". */
export function siteSummary(sellsOnSite: boolean, sitePrice: MoneyString | null, counterPrice: MoneyString | null): string {
  if (!sellsOnSite) return 'Não vende no site';
  const price = sitePrice ?? counterPrice;
  return price ? `Site: ${formatBRL(price)}` : 'Vende no site';
}

/** A página de pedidos do restaurante só ativa com um tamanho de marmita ativo que vende no site (06 §6.18). */
export const NO_SITE_SIZE_TEXT =
  'Nenhum tamanho de marmita vende no site. Para ativar a página de pedidos, ligue “Vende no site” em pelo menos um tamanho ativo.';
