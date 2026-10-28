import { digitsToCents, formatBRL, fromCents, splitEvenly, sumCents, toCents } from '@/lib/money';

describe('money (ADR-0030, 08 §8.12)', () => {
  it('converte string em centavos sem float', () => {
    expect(toCents('35.50')).toBe(3550);
    expect(toCents('0.10')).toBe(10);
    expect(toCents('0.1')).toBe(10);
    expect(toCents('1234')).toBe(123400);
    expect(toCents('-10.00')).toBe(-1000);
    // 0.1 + 0.2 em float daria 0.30000000000000004.
    expect(sumCents(['0.10', '0.20'])).toBe(30);
  });

  it('recusa formato inválido', () => {
    expect(() => toCents('35,50')).toThrow();
    expect(() => toCents('1.234')).toThrow();
    expect(() => toCents('abc')).toThrow();
  });

  it('volta para string com 2 casas', () => {
    expect(fromCents(3550)).toBe('35.50');
    expect(fromCents(5)).toBe('0.05');
    expect(fromCents(-1000)).toBe('-10.00');
    expect(() => fromCents(1.5)).toThrow();
  });

  it('formata em pt-BR', () => {
    expect(formatBRL('1234.56')).toBe('R$ 1.234,56');
    expect(formatBRL(5)).toBe('R$ 0,05');
    expect(formatBRL('-10.00')).toBe('-R$ 10,00');
    expect(formatBRL('1000000.00')).toBe('R$ 1.000.000,00');
  });

  it('divide igual com o resto nas últimas parcelas', () => {
    expect(splitEvenly(10000, 3)).toEqual([3333, 3333, 3334]);
    expect(splitEvenly(5500, 4)).toEqual([1375, 1375, 1375, 1375]);
    expect(splitEvenly(101, 2)).toEqual([50, 51]);
    const parts = splitEvenly(3501, 7);
    expect(parts.reduce((a, b) => a + b, 0)).toBe(3501);
  });

  it('digitação da direita para a esquerda', () => {
    expect(digitsToCents('3550')).toBe(3550);
    expect(digitsToCents('R$ 35,50')).toBe(3550);
    expect(digitsToCents('')).toBe(0);
    expect(digitsToCents('0005')).toBe(5);
  });
});
