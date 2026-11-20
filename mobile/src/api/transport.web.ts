/**
 * Web (ADR-0053 §3): o refresh token e o `deviceSecret` vivem em cookies `HttpOnly` da API. O navegador só
 * envia e aceita `Set-Cookie` de outra origem com `credentials: 'include'`, então ele vai nas rotas que leem
 * ou emitem esses cookies: `/auth/*` (login, signup, refresh, logout, pin-login, switch-tenant, lista de
 * funcionários do aparelho compartilhado), o registro do aparelho compartilhado e o aceite de convite.
 */
const COOKIE_PATHS = [/^\/auth\//, /^\/devices\/shared$/, /^\/invitations\/[^/]+\/accept$/];

export function credentialsFor(path: string): RequestCredentials | undefined {
  const bare = path.split('?')[0]!;
  return COOKIE_PATHS.some((re) => re.test(bare)) ? 'include' : undefined;
}

/** O navegador não manda header no WebSocket: a plataforma vai na query, como a versão (06 §6.14). */
export const HUB_EXTRA_QUERY = '&appPlatform=WEB';
