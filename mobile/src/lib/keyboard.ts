/**
 * Teclado físico (ADR-0053 §9.3). No celular não há: não faz nada. Web: `keyboard.web.ts`.
 * `handler` devolve `true` quando tratou a tecla (o navegador não faz a ação padrão).
 */
export type KeyHandler = (key: string) => boolean | void;

export function useKeyDown(_handler: KeyHandler, _enabled = true): void {}
