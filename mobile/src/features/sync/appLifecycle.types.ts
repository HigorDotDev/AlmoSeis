/** Tipos de `appLifecycle.ts`/`appLifecycle.web.ts` (08 §8.15). */
export interface AppLifecycleHandlers {
  /** Voltou para a frente (ou a rede voltou, na web): reconecta e sincroniza. */
  onForeground(): void;
  /** Foi para segundo plano: desconecta o tempo real. */
  onBackground(): void;
}
