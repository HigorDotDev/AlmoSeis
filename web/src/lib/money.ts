/**
 * Dinheiro em centavos inteiros (ADR-0030). A API trafega dinheiro como string decimal com duas casas ("35.50").
 * Nada de ponto flutuante na soma: converte para centavos, soma e volta para string.
 */
export type Cents = number;

const MONEY_PATTERN = /^-?\d+(\.\d{1,2})?$/;

export function parseMoney(value: string): Cents {
  const trimmed = value.trim();
  if (!MONEY_PATTERN.test(trimmed)) {
    throw new Error(`Valor monetário inválido: "${value}"`);
  }
  const negative = trimmed.startsWith('-');
  const [intPart, fracPart = ''] = trimmed.replace('-', '').split('.');
  const cents = Number(intPart) * 100 + Number(fracPart.padEnd(2, '0'));
  return negative ? -cents : cents;
}

/** Centavos → "35.50" (formato da API). */
export function toApiMoney(cents: Cents): string {
  if (!Number.isInteger(cents)) throw new Error('Centavos precisam ser inteiros');
  const negative = cents < 0;
  const abs = Math.abs(cents);
  const text = `${Math.floor(abs / 100)}.${String(abs % 100).padStart(2, '0')}`;
  return negative ? `-${text}` : text;
}

/** Centavos → "R$ 35,50" (interface pt-BR). Sem Intl para não depender do ICU do aparelho. */
export function formatBRL(cents: Cents): string {
  const negative = cents < 0;
  const abs = Math.abs(cents);
  const reais = String(Math.floor(abs / 100)).replace(/\B(?=(\d{3})+(?!\d))/g, '.');
  const text = `R$ ${reais},${String(abs % 100).padStart(2, '0')}`;
  return negative ? `-${text}` : text;
}

/** String da API → "R$ 35,50". */
export function formatApiMoney(value: string | null | undefined): string {
  if (value == null) return '—';
  return formatBRL(parseMoney(value));
}

/**
 * Texto digitado pelo cliente ("50", "50,00", "R$ 50,5") → centavos, ou null se não for um valor.
 * Usado no "troco para".
 */
export function parseUserMoney(input: string): Cents | null {
  const cleaned = input.replace(/R\$\s?/i, '').replace(/\s/g, '').replace(/\./g, '').replace(',', '.');
  if (cleaned === '') return null;
  if (!/^\d+(\.\d{1,2})?$/.test(cleaned)) return null;
  return parseMoney(cleaned);
}
