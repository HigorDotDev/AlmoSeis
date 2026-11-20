/**
 * Aviso sonoro de pedido novo (ADR-0036). Celular: `expo-audio`, sem bloqueio. Web: `orderSound.web.ts`, onde o
 * navegador só toca áudio depois de uma interação (faixa "Ativar som", ADR-0053 §7.2).
 */
import { useAudioPlayer } from 'expo-audio';
import { useCallback } from 'react';

import type { OrderSound } from './orderSound.types';

export type { OrderSound } from './orderSound.types';

export function useOrderSound(): OrderSound {
  const player = useAudioPlayer(require('../../../assets/sounds/new-order.wav'));
  const play = useCallback(() => {
    player.seekTo(0);
    player.play();
  }, [player]);
  return { play, needsUnlock: false, unlock: () => {} };
}
