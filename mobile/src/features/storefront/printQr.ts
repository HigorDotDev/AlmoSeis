/**
 * Celular: sem impressão no app (exigiria módulo nativo). O dono compartilha o link ou tira um print do QR na tela.
 * Web: `printQr.web.ts` abre uma folha só com o nome, o QR e o link.
 */
import type { PrintQrInput } from './printQr.types';

export const canPrintQr = false;

export function printQr(_input: PrintQrInput): boolean {
  return false;
}
