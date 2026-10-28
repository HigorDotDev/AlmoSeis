import { toCents, type MoneyString } from './money';

/** Limite do WeightInput (08 §8.11). */
export const MAX_WEIGHT_GRAMS = 50_000;

/** "0,452" ou "0.452" → 452. Até 3 casas; null se não for um peso válido. */
export function kgInputToGrams(input: string): number | null {
  const match = /^(\d{1,2})(?:[.,](\d{0,3}))?$/.exec(input.trim());
  if (!match) {
    return null;
  }
  const [, kg, fraction = ''] = match;
  const grams = Number(kg) * 1000 + Number(fraction.padEnd(3, '0'));
  return grams > 0 && grams <= MAX_WEIGHT_GRAMS ? grams : null;
}

/** 452 → "0,452 kg". */
export function gramsToKgLabel(grams: number): string {
  return `${Math.floor(grams / 1000)},${String(grams % 1000).padStart(3, '0')} kg`;
}

/**
 * Prévia de uma pesagem (ADR-0030): `round(gramas × preço/kg em centavos / 1000)`, metade para cima.
 * Só inteiros. O valor oficial vem da API.
 */
export function previewKiloAmountCents(grams: number, pricePerKg: MoneyString): number {
  return Math.round((grams * toCents(pricePerKg)) / 1000);
}
