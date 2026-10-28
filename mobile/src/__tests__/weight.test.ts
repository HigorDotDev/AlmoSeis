import { gramsToKgLabel, kgInputToGrams, previewKiloAmountCents } from '@/lib/weight';

describe('weight (ADR-0030, 08 §8.12)', () => {
  it('kg digitado vira gramas inteiras', () => {
    expect(kgInputToGrams('0,452')).toBe(452);
    expect(kgInputToGrams('0.452')).toBe(452);
    expect(kgInputToGrams('1,2')).toBe(1200);
    expect(kgInputToGrams('2')).toBe(2000);
    expect(kgInputToGrams('0')).toBeNull();
    expect(kgInputToGrams('0,4525')).toBeNull();
    expect(kgInputToGrams('51')).toBeNull();
    expect(kgInputToGrams('abc')).toBeNull();
  });

  it('rótulo em kg com 3 casas', () => {
    expect(gramsToKgLabel(452)).toBe('0,452 kg');
    expect(gramsToKgLabel(1200)).toBe('1,200 kg');
  });

  it('mesmos casos do teste de DDL', () => {
    expect(previewKiloAmountCents(452, '59.90')).toBe(2707); // 27,07
    expect(previewKiloAmountCents(250, '1.42')).toBe(36); // 0,355 → 0,36 (metade para cima)
    expect(previewKiloAmountCents(318, '59.90')).toBe(1905); // 19,05
  });
});
