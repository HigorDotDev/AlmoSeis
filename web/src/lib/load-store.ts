import 'server-only';
import { notFound, permanentRedirect } from 'next/navigation';
import { getStoreOnServer } from './api/server';
import type { PublicStore } from './api/types';

/**
 * Perfil no servidor para as páginas `/{slug}` e `/{slug}/carrinho`. Slug antigo (a API responde 301/308) →
 * redireciona a página para o slug atual, mantendo o resto do caminho (ADR-0044 item 15, ADR-0051 item 1).
 * Slug inexistente → 404 da página. Outra falha → 'error' (a página mostra "tente de novo").
 */
export async function loadStoreForPage(slug: string, suffix = ''): Promise<PublicStore | 'error'> {
  // Fora do formato do slug (ADR-0044 item 13; maiúsculas passam porque a API redireciona): nem chama a API.
  // Evita que `/favicon.ico`, `/robots.txt` e afins virem consultas ao perfil.
  if (!/^[A-Za-z0-9-]{3,40}$/.test(slug)) notFound();
  const result = await getStoreOnServer(slug);
  if (result.ok) {
    if (result.data.slug !== slug) permanentRedirect(`/${result.data.slug}${suffix}`);
    return result.data;
  }
  if (result.kind === 'moved') permanentRedirect(`/${result.slug}${suffix}`);
  if (result.kind === 'problem' && result.status === 404) notFound();
  return 'error';
}
