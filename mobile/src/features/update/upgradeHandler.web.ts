/**
 * Web (ADR-0053 §6.3): não há loja; cada deploy já entrega a versão nova a quem recarrega a página.
 * No primeiro `426`, grava os rascunhos que ainda estão no debounce e recarrega UMA vez (marca na
 * `sessionStorage`). Se depois de recarregar ainda vier `426` (o deploy do app web atrasou em relação à API),
 * mostra a tela "Estamos atualizando o AlmoSeis".
 */
import { flushAllDrafts } from '@/features/drafts/useDraft';

import type { UpgradeInfo } from './upgradeHandler.types';

export type { UpgradeInfo } from './upgradeHandler.types';

export const RELOAD_MARK_KEY = 'almoseis.reloadedFor426';
/** Uma marca mais velha que isso é de outro deploy: pode recarregar de novo. */
const MARK_TTL_MS = 10 * 60 * 1000;

let reloading = false;

export function handleUpgradeRequired(info: UpgradeInfo, showBlockingScreen: (info: UpgradeInfo) => void): void {
  if (reloading) return;
  const mark = Number(sessionStorage.getItem(RELOAD_MARK_KEY) ?? 0);
  if (mark && Date.now() - mark < MARK_TTL_MS) {
    showBlockingScreen(info);
    return;
  }
  reloading = true;
  sessionStorage.setItem(RELOAD_MARK_KEY, String(Date.now()));
  void flushAllDrafts()
    .catch(() => {})
    .finally(() => window.location.reload());
}
