/** QR do link da página (EP-002 RN-205.3). Sempre preto no branco, com margem, mesmo no tema escuro (leitura da câmera). */
import { useMemo } from 'react';
import { View } from 'react-native';

import { qrMatrix } from './qrMatrix';

const QUIET = 4;

export function QrCode({ value, size = 240, testID }: { value: string; size?: number; testID?: string }) {
  const matrix = useMemo(() => qrMatrix(value), [value]);
  // Módulo em pixels inteiros: sem frestas entre os trechos.
  const cell = Math.max(2, Math.floor(size / (matrix.size + QUIET * 2)));
  const side = cell * (matrix.size + QUIET * 2);
  return (
    <View
      testID={testID}
      accessible
      accessibilityRole="image"
      accessibilityLabel={`QR code do link ${value}`}
      style={{ width: side, height: side, backgroundColor: '#FFFFFF', padding: cell * QUIET, alignSelf: 'center' }}
    >
      {matrix.rows.map((runs, r) => (
        <View key={r} style={{ height: cell, flexDirection: 'row' }}>
          {runs.map(([start, length], i) => {
            const prevEnd = i === 0 ? 0 : runs[i - 1]![0] + runs[i - 1]![1];
            return <View key={start} style={{ marginLeft: (start - prevEnd) * cell, width: length * cell, height: cell, backgroundColor: '#000000' }} />;
          })}
        </View>
      ))}
    </View>
  );
}
