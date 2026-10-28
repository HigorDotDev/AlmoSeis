/**
 * Dinheiro no app (ADR-0030, 08 §8.12): a API manda string com 2 casas ("35.50"); contas só em centavos inteiros.
 * Nunca `parseFloat`: o parse é por string. O valor oficial é sempre o da API; aqui é só prévia e formatação.
 */
export type MoneyString = string;

const MONEY = /^(-)?(\d+)(?:\.(\d{1,2}))?$/;

export function toCents(value: MoneyString): number {
  const match = MONEY.exec(value.trim());
  if (!match) {
    throw new Error(`Valor monetário inválido: "${value}"`);
  }
  const [, sign, units, fraction = ''] = match;
  const cents = Number(units) * 100 + Number(fraction.padEnd(2, '0'));
  return sign ? -cents : cents;
}

export function fromCents(cents: number): MoneyString {
  if (!Number.isInteger(cents)) {
    throw new Error(`Centavos precisam ser inteiros: ${cents}`);
  }
  const sign = cents < 0 ? '-' : '';
  const abs = Math.abs(cents);
  return `${sign}${Math.floor(abs / 100)}.${String(abs % 100).padStart(2, '0')}`;
}

export function sumCents(values: readonly MoneyString[]): number {
  return values.reduce((total, v) => total + toCents(v), 0);
}

/** "R$ 1.234,56". Formata a partir dos centavos para não passar por float na casa decimal. */
export function formatBRL(value: MoneyString | number): string {
  const cents = typeof value === 'number' ? value : toCents(value);
  const sign = cents < 0 ? '-' : '';
  const abs = Math.abs(cents);
  const units = Math.floor(abs / 100)
    .toString()
    .replace(/\B(?=(\d{3})+(?!\d))/g, '.');
  return `${sign}R$ ${units},${String(abs % 100).padStart(2, '0')}`;
}

/**
 * Divide em `n` parcelas inteiras. O resto em centavos vai para as últimas parcelas
 * (100 / 3 → 33, 33, 34), de modo que a soma bate sempre com o total.
 */
export function splitEvenly(totalCents: number, n: number): number[] {
  if (!Number.isInteger(totalCents) || !Number.isInteger(n) || n < 1) {
    throw new Error('Divisão inválida.');
  }
  const base = Math.floor(totalCents / n);
  const remainder = totalCents - base * n;
  return Array.from({ length: n }, (_, i) => base + (i >= n - remainder ? 1 : 0));
}

/** Dígitos digitados da direita para a esquerda ("3550" → 3550 centavos), como no MoneyInput. */
export function digitsToCents(digits: string): number {
  const only = digits.replace(/\D/g, '').replace(/^0+/, '');
  return only === '' ? 0 : Number(only.slice(0, 11));
}
