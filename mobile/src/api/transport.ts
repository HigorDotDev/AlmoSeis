/**
 * Detalhes de transporte por plataforma (ADR-0053 §3). No celular nada muda: sem cookie, refresh e
 * `deviceSecret` no corpo/header. A versão web fica em `transport.web.ts`.
 */

/** `credentials` do `fetch` para o caminho; `undefined` = padrão do runtime. */
export function credentialsFor(_path: string): RequestCredentials | undefined {
  return undefined;
}

/** Parâmetros extras na URL do hub (06 §6.14). No celular, nenhum. */
export const HUB_EXTRA_QUERY = '';
