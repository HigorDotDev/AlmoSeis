/**
 * Regras de tela da página de pedidos (06 §6.18, ADR-0044). O servidor valida tudo de novo; aqui só se evita uma ida
 * à API com um erro óbvio e se monta o corpo do `PUT`, que substitui a configuração inteira.
 */
import type {
  DeliveryFeeMode,
  FixedMenu,
  MarmitaSize,
  MoneyString,
  PageDeliveryMissing,
  Storefront,
  StorefrontAddress,
  StorefrontLocation,
  UpdateStorefrontBody,
} from '@/api/types';
import type { StorefrontSection } from '@/features/tenant/establishment';
import { toCents } from '@/lib/money';
import { formatPhone, toE164 } from '@/lib/phone';

export const SLUG_MIN = 3;
export const SLUG_MAX = 40;
export const PUBLIC_NAME_MAX = 80;
export const HOURS_TEXT_MAX = 120;

/** Link da página: `{base}/{slug}`. Sem base configurada (ADR-0051 item 8), não há link. */
export function pageUrl(base: string | null, slug: string): string | null {
  return base ? `${base}/${slug}` : null;
}

/** Digitação do slug: minúsculas, sem acento, espaço vira hífen, só `a-z`, `0-9` e `-`. */
export function normalizeSlugInput(text: string): string {
  return text
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/[\s_]+/g, '-')
    .replace(/[^a-z0-9-]/g, '')
    .replace(/-{2,}/g, '-')
    .slice(0, SLUG_MAX);
}

/** Formato do slug (06 §6.18). Reservados, palavras ofensivas e slug em uso só o servidor sabe (`422` em `slug`). */
export function slugProblem(slug: string): string | null {
  if (slug.length < SLUG_MIN || slug.length > SLUG_MAX) return `Use de ${SLUG_MIN} a ${SLUG_MAX} caracteres.`;
  if (!/^[a-z0-9-]+$/.test(slug)) return 'Use só letras minúsculas, números e hífen.';
  if (slug.startsWith('-') || slug.endsWith('-') || slug.includes('--')) return 'Sem hífen no começo, no fim ou repetido.';
  if (/^[0-9-]+$/.test(slug)) return 'Use pelo menos uma letra.';
  return null;
}

export function maskZipCode(input: string): string {
  const d = input.replace(/\D/g, '').slice(0, 8);
  return d.length > 5 ? `${d.slice(0, 5)}-${d.slice(5)}` : d;
}

/** Rua, número, bairro, cidade e UF: o que o servidor exige para ativar a página (ADR-0044 item 11). */
export function missingAddressParts(a: StorefrontAddress): string[] {
  const parts: [keyof StorefrontAddress, string][] = [
    ['street', 'rua'],
    ['number', 'número'],
    ['neighborhood', 'bairro'],
    ['city', 'cidade'],
    ['state', 'UF'],
  ];
  return parts.filter(([k]) => !a[k]?.trim()).map(([, label]) => label);
}

/** Campos editáveis na tela. Telefone e CEP com máscara; o corpo do `PUT` sai de `toUpdateBody`. */
export interface StorefrontForm {
  slug: string;
  publicName: string;
  contactPhone: string;
  hoursText: string;
  street: string;
  number: string;
  complement: string;
  neighborhood: string;
  city: string;
  state: string;
  zipCode: string;
  offersPickup: boolean;
  offersDelivery: boolean;
  feeMode: DeliveryFeeMode;
  /** `'0.00'` = sem preço (vai `null`). */
  pricePerKm: MoneyString;
  minimumFee: MoneyString;
  /** Como digitado ("6", "6,5"); vazio = sem raio. */
  maxDistanceKm: string;
}

export function formFromStorefront(s: Storefront): StorefrontForm {
  const a = s.address;
  return {
    slug: s.slug,
    publicName: s.publicName,
    contactPhone: s.contactPhone ? formatPhone(s.contactPhone) : '',
    hoursText: s.hoursText ?? '',
    street: a.street ?? '',
    number: a.number ?? '',
    complement: a.complement ?? '',
    neighborhood: a.neighborhood ?? '',
    city: a.city ?? '',
    state: a.state ?? '',
    zipCode: a.zipCode ? maskZipCode(a.zipCode) : '',
    offersPickup: s.offersPickup,
    offersDelivery: s.offersDelivery,
    feeMode: s.delivery.feeMode,
    pricePerKm: s.delivery.pricePerKm ?? '0.00',
    minimumFee: s.delivery.minimumFee,
    maxDistanceKm: s.delivery.maxDistanceKm ? s.delivery.maxDistanceKm.replace('.', ',') : '',
  };
}

/**
 * Raio digitado → o formato da API (`"6.0"`, uma casa, `0.1` a `999.9`). Vazio → `null` (sem raio). Inválido →
 * `undefined`.
 */
export function parseKm(text: string): string | null | undefined {
  const t = text.trim().replace(',', '.');
  if (!t) return null;
  const m = /^(\d{1,3})(?:\.(\d))?$/.exec(t);
  if (!m) return undefined;
  const km = `${Number(m[1])}.${m[2] ?? '0'}`;
  return km === '0.0' ? undefined : km;
}

/** Coordenada digitada (ponto ou vírgula) → número com 6 casas, dentro do limite; senão `null`. */
export function parseCoordinate(text: string, limit: 90 | 180): number | null {
  const t = text.trim().replace(',', '.');
  if (!/^-?\d{1,3}(\.\d+)?$/.test(t)) return null;
  const v = Math.round(Number(t) * 1e6) / 1e6;
  return Math.abs(v) <= limit ? v : null;
}

export function formatCoordinate(v: number): string {
  return v.toFixed(6);
}

/** Link que abre o ponto no Google Maps (navegador ou app de mapas), sem módulo nativo. */
export function mapsUrl(l: Pick<StorefrontLocation, 'latitude' | 'longitude'>): string {
  return `https://www.google.com/maps/search/?api=1&query=${formatCoordinate(l.latitude)},${formatCoordinate(l.longitude)}`;
}

const missingText: Record<PageDeliveryMissing, string | null> = {
  DELIVERY_OFF: 'ligue a entrega',
  PRICE_PER_KM: 'informe o preço por km',
  CONFIRMED_LOCATION: 'confirme o ponto do restaurante',
  MAPS_PROVIDER: null,
};

/**
 * `pageDelivery.missing` em linguagem de balcão: "Para entregar pela página: ligue a entrega, informe o preço por km
 * e confirme o ponto do restaurante." `MAPS_PROVIDER` é do servidor e vira um aviso à parte.
 */
export function pageDeliveryText(p: Storefront['pageDelivery']): { steps: string | null; provider: string | null } {
  if (p.available) return { steps: null, provider: null };
  const parts = p.missing.map((m) => missingText[m]).filter((t): t is string => !!t);
  const joined = parts.length <= 1 ? (parts[0] ?? '') : `${parts.slice(0, -1).join(', ')} e ${parts.at(-1)}`;
  return {
    steps: joined ? `Para entregar pela página: ${joined}.` : null,
    provider: p.missing.includes('MAPS_PROVIDER') ? 'Cálculo de distância não configurado no servidor.' : null,
  };
}

export function isFormDirty(s: Storefront, form: StorefrontForm): boolean {
  const base = formFromStorefront(s);
  return (Object.keys(base) as (keyof StorefrontForm)[]).some((k) => {
    const a = base[k];
    const b = form[k];
    return typeof a === 'string' && typeof b === 'string' ? a.trim() !== b.trim() : a !== b;
  });
}

/** Erros que a tela pega antes de enviar. Chaves iguais às do `errors` do servidor. */
export function formErrors(form: StorefrontForm): Record<string, string> {
  const errors: Record<string, string> = {};
  const slug = slugProblem(form.slug);
  if (slug) errors.slug = slug;
  const name = form.publicName.trim();
  if (name.length < 2 || name.length > PUBLIC_NAME_MAX) errors.publicName = `Use de 2 a ${PUBLIC_NAME_MAX} caracteres.`;
  if (form.contactPhone.trim() && !toE164(form.contactPhone)) errors.contactPhone = 'Telefone com DDD, ex.: (34) 99999-1234.';
  if (form.state.trim() && !/^[A-Za-z]{2}$/.test(form.state.trim())) errors['address.state'] = 'UF com 2 letras, ex.: MG.';
  if (form.zipCode.trim() && form.zipCode.replace(/\D/g, '').length !== 8) errors['address.zipCode'] = 'CEP com 8 dígitos.';
  if (!form.offersPickup && !form.offersDelivery) errors.offersPickup = 'Ofereça entrega, retirada ou as duas.';
  if (parseKm(form.maxDistanceKm) === undefined) errors['delivery.maxDistanceKm'] = 'Raio em km, de 0,1 a 999,9 (ex.: 6 ou 6,5).';
  return errors;
}

const orNull = (v: string) => (v.trim() ? v.trim() : null);

/**
 * Corpo do `PUT /storefront` (06 §6.18). Sem `form`, repete a configuração do servidor: é o que as chaves rápidas
 * ("ativar", "pausar", "pausar entrega", "confirmar ponto") mandam, trocando só a chave (`overrides`). Sem `location`
 * no corpo, o servidor mantém o ponto salvo; o formulário nunca manda o ponto.
 */
export function toUpdateBody(
  s: Storefront,
  form?: StorefrontForm | null,
  overrides: Partial<Pick<UpdateStorefrontBody, 'isPageActive' | 'isAcceptingOrders' | 'isDeliveryEnabled' | 'location'>> = {},
): UpdateStorefrontBody {
  const base: UpdateStorefrontBody = {
    slug: s.slug,
    publicName: s.publicName,
    isPageActive: s.isPageActive,
    isAcceptingOrders: s.isAcceptingOrders,
    isDeliveryEnabled: s.isDeliveryEnabled,
    offersDelivery: s.offersDelivery,
    offersPickup: s.offersPickup,
    contactPhone: s.contactPhone,
    hoursText: s.hoursText,
    address: { ...s.address },
    delivery: { ...s.delivery },
  };
  const edited: UpdateStorefrontBody = form
    ? {
        ...base,
        slug: form.slug.trim(),
        publicName: form.publicName.trim(),
        offersDelivery: form.offersDelivery,
        offersPickup: form.offersPickup,
        contactPhone: form.contactPhone.trim() ? toE164(form.contactPhone) : null,
        hoursText: orNull(form.hoursText),
        address: {
          street: orNull(form.street),
          number: orNull(form.number),
          complement: orNull(form.complement),
          neighborhood: orNull(form.neighborhood),
          city: orNull(form.city),
          state: form.state.trim() ? form.state.trim().toUpperCase() : null,
          zipCode: orNull(form.zipCode),
        },
        delivery: {
          feeMode: form.feeMode,
          pricePerKm: toCents(form.pricePerKm) > 0 ? form.pricePerKm : null,
          minimumFee: form.minimumFee,
          maxDistanceKm: parseKm(form.maxDistanceKm) ?? null,
        },
      }
    : base;
  return { ...edited, ...overrides };
}

export type StatusTone = 'success' | 'warning' | 'danger' | 'info';

/** O que a página mostra agora (06 §6.19), em linguagem de balcão. Indisponível com a página ativa = assinatura. */
export function storeStatusInfo(s: Pick<Storefront, 'status' | 'isPageActive'>): { tone: StatusTone; title: string; text: string } {
  switch (s.status) {
    case 'OPEN':
      return { tone: 'success', title: 'Aberta', text: 'Recebendo pedidos pelo site agora.' };
    case 'PAUSED':
      return { tone: 'warning', title: 'Pausada', text: 'O cardápio aparece, mas ninguém consegue enviar pedido até você retomar.' };
    case 'CLOSED':
      return { tone: 'info', title: 'Fechada agora', text: 'Sem expediente aberto: a página mostra "Fechado agora". Abra o expediente para receber pedidos.' };
    case 'UNAVAILABLE':
      return s.isPageActive
        ? { tone: 'danger', title: 'Indisponível', text: 'A página está fora do ar porque a assinatura não permite novos pedidos. Confira a situação da assinatura.' }
        : { tone: 'info', title: 'Desativada', text: 'Quem abrir o link verá "indisponível". Ative quando o cadastro estiver pronto.' };
  }
}

/** Texto do compartilhamento (WhatsApp e afins). */
export function shareMessage(publicName: string, url: string): string {
  return `Faça seu pedido na ${publicName} pelo site: ${url}`;
}

/**
 * Seção `MARMITA` pronta para ativar: um tamanho de marmita ativo que vende no site (06 §6.18, ADR-0057 item 3).
 */
export function marmitaSectionReady(sizes: Pick<MarmitaSize, 'isActive' | 'sellsOnSite'>[]): boolean {
  return sizes.some((z) => z.isActive && z.sellsOnSite);
}

/**
 * Seção `A_LA_CARTE` pronta para ativar (06 §6.18, EP-003 RN-316.5): um prato ativo que vende no site (com tamanho:
 * um tamanho ativo que vende no site) ou um combo ativo que vende no site. A pausa não conta; só bebida não basta.
 */
export function aLaCarteSectionReady(menu: Pick<FixedMenu, 'dishes' | 'combos'>): boolean {
  const dish = menu.dishes.some((d) => d.isActive && (d.hasSizes ? d.sizes.some((z) => z.isActive && z.sellsOnSite) : d.sellsOnSite));
  return dish || menu.combos.some((c) => c.isActive && c.sellsOnSite);
}

/**
 * Avisos de "nada vende no site" por seção, antes do `422` em `isPageActive`. `ready[s]` `undefined` = ainda
 * carregando (sem aviso). `blocking`: nenhuma seção tem o que vender, a ativação vai ser recusada; senão o aviso só
 * diz que aquela seção sai vazia na página (estabelecimento com os dois comportamentos).
 */
export function siteSaleWarnings(sections: StorefrontSection[], ready: Partial<Record<StorefrontSection, boolean>>): { section: StorefrontSection; blocking: boolean }[] {
  const missing = sections.filter((s) => ready[s] === false);
  const blocking = sections.length > 0 && sections.every((s) => ready[s] === false);
  return missing.map((section) => ({ section, blocking }));
}

/** "o cardápio do dia" no restaurante; "o cardápio" na lanchonete (o cardápio fixo não é do dia). */
export function pageMenuWord(sections: StorefrontSection[]): string {
  return sections.includes('MARMITA') ? 'o cardápio do dia' : 'o cardápio';
}
