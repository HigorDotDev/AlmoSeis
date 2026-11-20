import 'server-only';
import { cache } from 'react';
import { createPublicApi, DEFAULT_API_BASE_URL } from './client';

/**
 * Cliente do servidor da página (ADR-0051 item 2): leituras na renderização, com `X-Web-Renderer-Key` (isento do
 * limite por IP, ADR-0045). Não segue o redirecionamento sozinho: o slug antigo vira redirecionamento da página.
 * A chave nunca vai para o navegador (sem prefixo NEXT_PUBLIC_).
 */
function serverApi() {
  const key = process.env.WEB_RENDERER_KEY;
  return createPublicApi({
    baseUrl: process.env.API_BASE_URL || process.env.NEXT_PUBLIC_API_BASE_URL || DEFAULT_API_BASE_URL,
    headers: key ? { 'X-Web-Renderer-Key': key } : {},
    redirect: 'manual',
    // A API responde `Cache-Control: no-cache` (§6.19): estado e "Acabou" mudam a qualquer momento.
    init: { cache: 'no-store' },
    timeoutMs: 8000,
  });
}

/** Uma busca por requisição, compartilhada entre `generateMetadata` e a página. */
export const getStoreOnServer = cache((slug: string) => serverApi().getStore(slug));
