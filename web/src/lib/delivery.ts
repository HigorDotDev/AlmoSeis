import type { ApiFailure } from './api/client';
import type { DeliveryAddressInput, DeliveryQuote, PublicDeliveryRule } from './api/types';
import { formatApiMoney } from './money';

/**
 * Entrega com taxa por distância (ADR-0048, §6.19). A taxa é sempre do servidor: a página pede a cotação quando o
 * cliente confirma o endereço, mostra o que a API entendeu e manda o `quoteId` no envio.
 */

/** "4.5" → "4,5 km". */
export function formatKm(km: string | null | undefined): string | null {
  if (!km) return null;
  return `${km.replace('.', ',')} km`;
}

/** "R$ 0,80 por km, mínimo R$ 3,00, até 6 km" ou "Taxa fixa de R$ 5,00". */
export function describeDeliveryRule(rule: PublicDeliveryRule | null | undefined): string | null {
  if (!rule) return null;
  if (rule.feeMode === 'FIXED') return rule.fixedFee ? `Taxa fixa de ${formatApiMoney(rule.fixedFee)}` : null;
  const parts: string[] = [];
  if (rule.pricePerKm) parts.push(`${formatApiMoney(rule.pricePerKm)} por km`);
  if (rule.minimumFee && rule.minimumFee !== '0.00') parts.push(`mínimo ${formatApiMoney(rule.minimumFee)}`);
  if (rule.maxDistanceKm) parts.push(`até ${formatKm(rule.maxDistanceKm.replace(/\.0$/, ''))}`);
  return parts.length ? parts.join(', ') : null;
}

/** "4,5 km · Entrega R$ 3,60" (FIXED: "Entrega R$ 5,00"). */
export function describeQuote(quote: DeliveryQuote): string {
  const km = formatKm(quote.distanceKm);
  return `${km ? `${km} · ` : ''}Entrega ${formatApiMoney(quote.deliveryFee)}`;
}

const blank = (v: string | null | undefined) => (v && v.trim() ? v.trim() : null);

/** Endereço do formulário → corpo da API (CEP só com dígitos; vazio → null). */
export function normalizeAddressInput(address: DeliveryAddressInput): DeliveryAddressInput {
  const zip = (address.zipCode ?? '').replace(/\D/g, '');
  return {
    street: address.street.trim(),
    number: address.number.trim(),
    complement: blank(address.complement),
    neighborhood: address.neighborhood.trim(),
    city: blank(address.city),
    zipCode: zip ? zip : null,
    reference: blank(address.reference),
  };
}

function fold(value: string | null | undefined): string {
  return (value ?? '')
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, ' ')
    .trim();
}

/**
 * O que a cotação leva em conta (rua, número, bairro, cidade e CEP; complemento e referência não contam, §6.19).
 * Se mudou depois da cotação, a página cota de novo antes de enviar.
 */
export function addressFingerprint(address: DeliveryAddressInput): string {
  const a = normalizeAddressInput(address);
  return [a.street, a.number, a.neighborhood, a.city, a.zipCode].map(fold).join('|');
}

export interface QuotedAddress {
  quote: DeliveryQuote;
  fingerprint: string;
}

export type QuoteState = 'none' | 'valid' | 'expired' | 'stale';

/** Margem para a cotação não vencer no caminho até a API. */
const EXPIRY_MARGIN_MS = 30_000;

export function quoteState(quoted: QuotedAddress | null, address: DeliveryAddressInput, now: number = Date.now()): QuoteState {
  if (!quoted) return 'none';
  if (quoted.fingerprint !== addressFingerprint(address)) return 'stale';
  if (Date.parse(quoted.quote.expiresAt) - EXPIRY_MARGIN_MS <= now) return 'expired';
  return 'valid';
}

/** Checagem local do endereço antes de cotar (formato do §6.19). Chaves no formato `deliveryAddress.*`. */
export function validateAddress(address: DeliveryAddressInput): Partial<Record<string, string>> {
  const a = normalizeAddressInput(address);
  const errors: Partial<Record<string, string>> = {};
  if (!a.street) errors['deliveryAddress.street'] = 'Informe a rua.';
  else if (a.street.length > 120) errors['deliveryAddress.street'] = 'A rua pode ter até 120 caracteres.';
  if (!a.number) errors['deliveryAddress.number'] = 'Informe o número (ou "S/N").';
  else if (a.number.length > 10) errors['deliveryAddress.number'] = 'O número pode ter até 10 caracteres.';
  if (!a.neighborhood) errors['deliveryAddress.neighborhood'] = 'Informe o bairro.';
  else if (a.neighborhood.length > 60) errors['deliveryAddress.neighborhood'] = 'O bairro pode ter até 60 caracteres.';
  if ((a.complement ?? '').length > 60) errors['deliveryAddress.complement'] = 'O complemento pode ter até 60 caracteres.';
  if ((a.city ?? '').length > 80) errors['deliveryAddress.city'] = 'A cidade pode ter até 80 caracteres.';
  if (a.zipCode && a.zipCode.length !== 8) errors['deliveryAddress.zipCode'] = 'O CEP tem 8 dígitos.';
  if ((a.reference ?? '').length > 120) errors['deliveryAddress.reference'] = 'A referência pode ter até 120 caracteres.';
  return errors;
}

export const DELIVERY_UNAVAILABLE_TEXT = 'Não conseguimos calcular a entrega agora. Você pode retirar no local.';

export type QuoteOutcome =
  | { kind: 'address'; errors: Partial<Record<string, string>>; message: string }
  | { kind: 'outOfArea'; distanceKm: string | null; maxDistanceKm: string | null; message: string }
  | { kind: 'unavailable'; message: string }
  | { kind: 'rateLimited'; retryAfterSeconds: number; message: string }
  | { kind: 'captcha'; message: string }
  | { kind: 'storeUnavailable'; message: string }
  | { kind: 'message'; message: string };

export function outOfAreaText(distanceKm: string | null | undefined, maxDistanceKm: string | null | undefined): string {
  const detail =
    distanceKm && maxDistanceKm ? ` Seu endereço fica a ${formatKm(distanceKm)} e entregamos até ${formatKm(maxDistanceKm)}.` : '';
  return `Fora da área de entrega.${detail} Você pode retirar no local.`;
}

/** `address.number` (cotação) ou `deliveryAddress.number` (envio) → `deliveryAddress.number` (campo da tela). */
export function toAddressFieldKey(key: string): string | null {
  const k = key.charAt(0).toLowerCase() + key.slice(1);
  const m = /^(?:address|deliveryAddress)\.(\w+)$/i.exec(k);
  if (!m?.[1]) return null;
  return `deliveryAddress.${m[1].charAt(0).toLowerCase()}${m[1].slice(1)}`;
}

export function interpretQuoteFailure(failure: ApiFailure): QuoteOutcome {
  if (failure.kind !== 'problem') {
    return { kind: 'message', message: 'Não conseguimos calcular a entrega. Verifique a internet e tente de novo.' };
  }
  const { problem, status } = failure;
  const code = problem.errorCode;
  if (code === 'OutOfDeliveryArea') {
    return {
      kind: 'outOfArea',
      distanceKm: problem.distanceKm ?? null,
      maxDistanceKm: problem.maxDistanceKm ?? null,
      message: outOfAreaText(problem.distanceKm, problem.maxDistanceKm),
    };
  }
  if (code === 'DeliveryUnavailable') return { kind: 'unavailable', message: DELIVERY_UNAVAILABLE_TEXT };
  if (code === 'CaptchaFailed') {
    return { kind: 'captcha', message: 'Não conseguimos confirmar que você não é um robô. Tente calcular de novo.' };
  }
  if (code === 'StoreUnavailable') return { kind: 'storeUnavailable', message: 'A página não está recebendo pedidos agora.' };
  if (status === 429) {
    const seconds = failure.retryAfterSeconds ?? 60;
    return { kind: 'rateLimited', retryAfterSeconds: seconds, message: `Muitas tentativas. Aguarde ${seconds} segundos e calcule de novo.` };
  }
  if (code === 'ValidationFailed' && problem.errors) {
    const errors: Partial<Record<string, string>> = {};
    for (const [field, messages] of Object.entries(problem.errors)) {
      errors[toAddressFieldKey(field) ?? 'deliveryQuoteId'] = messages.join(' ');
    }
    return { kind: 'address', errors, message: 'Revise o endereço: não conseguimos localizá-lo.' };
  }
  return { kind: 'message', message: problem.title ?? 'Não conseguimos calcular a entrega. Tente de novo.' };
}
