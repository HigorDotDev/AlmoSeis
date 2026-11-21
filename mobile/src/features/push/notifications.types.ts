/** Tipos de `notifications.ts`/`notifications.web.ts` (08 §8.6 "Push dos pedidos do site", §8.15). */

/** Situação da permissão do sistema operacional para notificações. */
export interface PushPermission {
  status: 'granted' | 'denied' | 'undetermined';
  /** `false` quando o sistema não mostra mais o pedido (iOS depois de negar; Android 13+ depois de duas recusas). */
  canAskAgain: boolean;
}

export type PushTokenResult =
  | { kind: 'ok'; token: string }
  /** `extra.eas.projectId` ausente no `app.json` (o `eas init` ainda não foi feito): não há como pedir o token. */
  | { kind: 'noProjectId' }
  | { kind: 'error'; message: string };

export interface PushNotifications {
  /** `false` na web: o Web Push é P2 (ADR-0053); nada de push nesta fase. */
  supported: boolean;
  /**
   * Uma vez, na raiz: suprime a notificação com o app em primeiro plano (o SignalR + som já avisaram, ADR-0049
   * item 9), cria o canal Android `web-orders` e repassa o `data` de cada toque numa notificação, inclusive a que
   * abriu o app. Devolve a função que desliga os ouvintes.
   */
  init(onOpen: (data: unknown) => void): () => void;
  getPermission(): Promise<PushPermission>;
  /** Mostra o pedido do sistema. Só depois da explicação do app (ADR-0049 item 6). */
  requestPermission(): Promise<PushPermission>;
  getToken(): Promise<PushTokenResult>;
  /** O sistema trocou o token do aparelho: o app pede o token da Expo de novo e registra. */
  onTokenChange(listener: () => void): () => void;
  /** Atalho para as configurações do app no aparelho (permissão negada). */
  openSettings(): void;
}
