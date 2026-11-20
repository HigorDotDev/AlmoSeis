/**
 * QR code em JavaScript puro (`qrcode-generator`, MIT, sem módulo nativo): funciona no celular e na web sem novo build
 * do Dev Client. Correção `M` (aguenta ~15% de sujeira ou dobra no papel do balcão).
 */
import createQr from 'qrcode-generator';

export interface QrMatrix {
  size: number;
  /** Por linha, os trechos escuros como `[início, comprimento]`: menos `View`s que um quadrado por módulo. */
  rows: [number, number][][];
}

export function qrMatrix(value: string): QrMatrix {
  const qr = createQr(0, 'M');
  qr.addData(value, 'Byte');
  qr.make();
  const size = qr.getModuleCount();
  const rows: [number, number][][] = [];
  for (let r = 0; r < size; r++) {
    const runs: [number, number][] = [];
    let start = -1;
    for (let c = 0; c <= size; c++) {
      const dark = c < size && qr.isDark(r, c);
      if (dark && start < 0) start = c;
      if (!dark && start >= 0) {
        runs.push([start, c - start]);
        start = -1;
      }
    }
    rows.push(runs);
  }
  return { size, rows };
}

/** SVG do QR (para imprimir na web), com 4 módulos de margem branca. */
export function qrSvg(value: string, cellSize = 8): string {
  const qr = createQr(0, 'M');
  qr.addData(value, 'Byte');
  qr.make();
  return qr.createSvgTag({ cellSize, margin: cellSize * 4, scalable: true });
}
