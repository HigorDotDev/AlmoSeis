export interface ShareLinkInput {
  title: string;
  /** Texto completo (já com o link), para quem só lê a mensagem (WhatsApp no Android). */
  message: string;
  url: string;
}

/** `shared`: a folha de compartilhar abriu; `copied`: foi para a área de transferência; `dismissed`: a pessoa fechou. */
export type ShareLinkResult = 'shared' | 'copied' | 'dismissed' | 'failed';
