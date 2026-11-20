/** Compartilhar um link (EP-002 RN-205.3). Celular: a folha do sistema (WhatsApp, copiar, etc.). Web: `share.web.ts`. */
import { Share } from 'react-native';

import type { ShareLinkInput, ShareLinkResult } from './share.types';

export type { ShareLinkInput, ShareLinkResult } from './share.types';

export async function shareLink({ title, message }: ShareLinkInput): Promise<ShareLinkResult> {
  try {
    const r = await Share.share({ title, message });
    return r.action === Share.dismissedAction ? 'dismissed' : 'shared';
  } catch {
    return 'failed';
  }
}
