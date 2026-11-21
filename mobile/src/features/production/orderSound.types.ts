/** Tipos de `orderSound.ts`/`orderSound.web.ts` (08 §8.15). */
export interface OrderSound {
  play(): void;
  /** O navegador está bloqueando o som até um clique. No celular, sempre `false`. */
  needsUnlock: boolean;
  /** O clique em "Ativar som". */
  unlock(): void;
}

/**
 * `newOrder`: pedido novo e alteração na cozinha (ADR-0036, `assets/sounds/new-order.wav`). `webOrder`: pedido do
 * site esperando aceite (ADR-0049 item 7), o mesmo `assets/sounds/web_order.wav` do canal de push `web-orders`.
 */
export type OrderSoundKind = 'newOrder' | 'webOrder';
