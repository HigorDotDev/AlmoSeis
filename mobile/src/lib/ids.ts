import * as Crypto from 'expo-crypto';

/** Idempotency-Key, ids locais de rascunho e installationId (ADR-0033). */
export function newKey(): string {
  return Crypto.randomUUID();
}
