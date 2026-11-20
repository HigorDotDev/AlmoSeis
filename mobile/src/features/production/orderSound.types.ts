/** Tipos de `orderSound.ts`/`orderSound.web.ts` (08 §8.15). */
export interface OrderSound {
  play(): void;
  /** O navegador está bloqueando o som até um clique. No celular, sempre `false`. */
  needsUnlock: boolean;
  /** O clique em "Ativar som". */
  unlock(): void;
}
