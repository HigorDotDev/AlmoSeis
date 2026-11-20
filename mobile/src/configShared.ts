/** Configuração igual em todas as plataformas; `config.ts` e `config.web.ts` reexportam daqui (08 §8.15). */

/** Versão dos termos aceitos no cadastro (06 §6.3). */
export const TERMS_VERSION = '2026-10';

export const REQUEST_TIMEOUT_MS = 15_000;

/**
 * Endereço da página pública de pedidos (EP-002, ADR-0051): o link é `{base}/{slug}`. O domínio de produção ainda
 * é lacuna do ADR-0051 item 8, então fora do desenvolvimento só existe com `EXPO_PUBLIC_PUBLIC_PAGE_BASE_URL` no
 * perfil do build. Sem ele, o app não mostra link nem QR (um QR impresso com o domínio errado não tem conserto).
 */
export const PUBLIC_PAGE_BASE_URL: string | null =
  (process.env.EXPO_PUBLIC_PUBLIC_PAGE_BASE_URL ?? (__DEV__ ? 'http://localhost:3000' : ''))
    .trim()
    .replace(/\/+$/, '') || null;
