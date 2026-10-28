/** Telefones BR (08 §8.12). A API usa E.164 ("+5534999991234"). */
export function maskBR(input: string): string {
  const d = input.replace(/\D/g, '').replace(/^55(?=\d{10,11}$)/, '').slice(0, 11);
  if (d.length <= 2) return d.length ? `(${d}` : '';
  if (d.length <= 6) return `(${d.slice(0, 2)}) ${d.slice(2)}`;
  if (d.length <= 10) return `(${d.slice(0, 2)}) ${d.slice(2, 6)}-${d.slice(6)}`;
  return `(${d.slice(0, 2)}) ${d.slice(2, 7)}-${d.slice(7)}`;
}

/** "(34) 99999-1234" → "+5534999991234"; null quando não tem DDD + 8 ou 9 dígitos. */
export function toE164(input: string): string | null {
  const d = input.replace(/\D/g, '').replace(/^55(?=\d{10,11}$)/, '');
  return d.length === 10 || d.length === 11 ? `+55${d}` : null;
}

/** "+5534999991234" → "(34) 99999-1234". */
export function formatPhone(e164: string | null | undefined): string {
  return e164 ? maskBR(e164.replace(/^\+55/, '')) : '';
}

/** Login aceita e-mail ou telefone: telefone vira E.164, e-mail vai como digitado. */
export function normalizeLogin(input: string): string {
  const trimmed = input.trim();
  if (trimmed.includes('@')) return trimmed.toLowerCase();
  return toE164(trimmed) ?? trimmed;
}

/** Máscara de CPF (11) ou CNPJ (14). */
export function maskDocument(input: string): string {
  const d = input.replace(/\D/g, '').slice(0, 14);
  if (d.length <= 11) {
    return d.replace(/(\d{3})(\d)/, '$1.$2').replace(/(\d{3})(\d)/, '$1.$2').replace(/(\d{3})(\d{1,2})$/, '$1-$2');
  }
  return d
    .replace(/^(\d{2})(\d)/, '$1.$2')
    .replace(/^(\d{2})\.(\d{3})(\d)/, '$1.$2.$3')
    .replace(/\.(\d{3})(\d)/, '.$1/$2')
    .replace(/(\d{4})(\d)/, '$1-$2');
}
