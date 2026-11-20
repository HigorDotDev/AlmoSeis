/** Tipos de `keyboard.ts`/`keyboard.web.ts` (08 §8.15). */

/** Devolve `true` quando tratou a tecla (o navegador não faz a ação padrão). */
export type KeyHandler = (key: string) => boolean | void;
