/** Web (ADR-0053): folha para imprimir e deixar no balcão, com o nome, o QR e o link. */
import type { PrintQrInput } from './printQr.types';
import { qrSvg } from './qrMatrix';

export const canPrintQr = true;

const escapeHtml = (s: string) => s.replace(/[&<>"']/g, (ch) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[ch]!);

/** Devolve `false` se o navegador bloqueou a janela. */
export function printQr({ publicName, url }: PrintQrInput): boolean {
  const w = window.open('', '_blank', 'width=640,height=800');
  if (!w) return false;
  const name = escapeHtml(publicName);
  w.document.write(`<!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><title>${name} · QR code</title>
<style>
  body { font-family: system-ui, sans-serif; text-align: center; margin: 48px 24px; color: #000; }
  h1 { font-size: 32px; margin: 0 0 8px; }
  p { font-size: 20px; margin: 8px 0; }
  .qr { width: 360px; height: 360px; margin: 24px auto; }
  .qr svg { width: 100%; height: 100%; }
  .url { font-size: 18px; word-break: break-all; }
</style></head><body>
  <h1>${name}</h1>
  <p>Aponte a câmera para fazer seu pedido</p>
  <div class="qr">${qrSvg(url)}</div>
  <p class="url">${escapeHtml(url)}</p>
</body></html>`);
  w.document.close();
  w.focus();
  w.print();
  return true;
}
