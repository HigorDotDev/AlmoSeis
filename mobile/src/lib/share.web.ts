/**
 * Web (ADR-0053): `navigator.share` quando o navegador tem (Edge/Chrome no Windows, celular); senão copia o link para
 * a área de transferência. O `Share` do react-native-web cai no mesmo `navigator.share` e falha sem ele.
 */
import type { ShareLinkInput, ShareLinkResult } from './share.types';

export type { ShareLinkInput, ShareLinkResult } from './share.types';

export async function shareLink({ title, message, url }: ShareLinkInput): Promise<ShareLinkResult> {
  const nav = typeof navigator === 'undefined' ? undefined : navigator;
  if (nav && typeof nav.share === 'function') {
    try {
      await nav.share({ title, text: message, url });
      return 'shared';
    } catch (e) {
      if (e instanceof Error && e.name === 'AbortError') return 'dismissed';
      // NotAllowedError e afins: tenta copiar.
    }
  }
  if (!nav?.clipboard) return 'failed';
  try {
    await nav.clipboard.writeText(url);
    return 'copied';
  } catch {
    return 'failed';
  }
}
