import type { PendingSubmission } from './storage';

export function newIdempotencyKey(): string {
  if (typeof crypto.randomUUID === 'function') return crypto.randomUUID();
  // fallback RFC 4122 v4 para navegadores antigos
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  bytes[6] = (bytes[6]! & 0x0f) | 0x40;
  bytes[8] = (bytes[8]! & 0x3f) | 0x80;
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/**
 * A chave nasce no primeiro toque em "Enviar pedido" e é reaproveitada em todo reenvio do **mesmo** corpo (§6.19).
 * Se o corpo mudou (o cliente editou o pedido ou confirmou o carrinho recalculado), é outra tentativa: chave nova,
 * senão a API responderia `422 IdempotencyKeyReused`.
 */
export function keyForAttempt(bodyJson: string, pending: PendingSubmission | null): PendingSubmission {
  if (pending && pending.bodyJson === bodyJson) return pending;
  return { idempotencyKey: newIdempotencyKey(), bodyJson };
}
