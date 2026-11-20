/**
 * `426 AppVersionUnsupported` (ADR-0035). Celular: tela bloqueante com o link da loja. Web:
 * `upgradeHandler.web.ts` (recarrega a página uma vez, ADR-0053 §6.3).
 */
export interface UpgradeInfo {
  minVersion?: string;
  storeUrl?: string;
}

export function handleUpgradeRequired(info: UpgradeInfo, showBlockingScreen: (info: UpgradeInfo) => void): void {
  showBlockingScreen(info);
}
