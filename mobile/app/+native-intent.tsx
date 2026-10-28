/**
 * Links externos (08 §8.7): `almoseis://convite/K7P3QZ` e `https://almoseis.app/convite/K7P3QZ` → convite;
 * `https://almoseis.app/redefinir-senha?token=…` (e-mail de ADR-0040) → nova senha.
 */
export function redirectSystemPath({ path }: { path: string; initial: boolean }): string {
  try {
    const url = new URL(path, 'almoseis://app');
    const segments = url.pathname.split('/').filter(Boolean);
    // almoseis://convite/X chega com "convite" como host.
    if (url.host === 'convite' && segments[0]) return `/invitation/${segments[0]}`;
    if (segments[0] === 'convite' && segments[1]) return `/invitation/${segments[1]}`;
    if (segments[0] === 'redefinir-senha' || url.host === 'redefinir-senha') {
      const token = url.searchParams.get('token');
      return token ? `/reset-password?emailToken=${encodeURIComponent(token)}` : '/reset-password';
    }
    return path;
  } catch {
    return '/';
  }
}
