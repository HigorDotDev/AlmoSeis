/**
 * Telefone brasileiro na página. A API aceita os formatos comuns e normaliza em E.164 (§6.19); aqui só a máscara
 * enquanto o cliente digita e uma checagem leve antes do envio.
 */
export function phoneDigits(value: string): string {
  let digits = value.replace(/\D/g, '');
  if (digits.startsWith('55') && digits.length > 11) digits = digits.slice(2);
  if (digits.startsWith('0')) digits = digits.slice(1);
  return digits.slice(0, 11);
}

/** "34988887777" → "(34) 98888-7777"; "3432221111" → "(34) 3222-1111". Aceita entrada parcial. */
export function maskPhone(value: string): string {
  const d = phoneDigits(value);
  if (d.length === 0) return '';
  if (d.length <= 2) return `(${d}`;
  const ddd = d.slice(0, 2);
  const rest = d.slice(2);
  if (rest.length <= 4) return `(${ddd}) ${rest}`;
  if (d.length <= 10) return `(${ddd}) ${rest.slice(0, 4)}-${rest.slice(4)}`;
  return `(${ddd}) ${rest.slice(0, 5)}-${rest.slice(5)}`;
}

/** Celular (9 dígitos começando por 9) ou fixo (8 dígitos começando por 2 a 5), com DDD. */
export function isValidBrazilianPhone(value: string): boolean {
  const d = phoneDigits(value);
  if (!/^[1-9]{2}/.test(d)) return false;
  const local = d.slice(2);
  return (local.length === 9 && local.startsWith('9')) || (local.length === 8 && /^[2-5]/.test(local));
}

/** "+5534999990000" → "(34) 99999-0000", para mostrar o contato do restaurante. */
export function formatE164(value: string | null | undefined): string | null {
  if (!value) return null;
  return maskPhone(value);
}

/** Link do WhatsApp (aberto pelo próprio cliente). Sem número: o cliente escolhe o contato. */
export function whatsappLink(e164: string | null, text?: string): string {
  const digits = e164 ? e164.replace(/\D/g, '') : '';
  return `https://wa.me/${digits}${text ? `?text=${encodeURIComponent(text)}` : ''}`;
}
