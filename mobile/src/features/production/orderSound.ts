/**
 * Aviso sonoro de pedido novo (ADR-0036). Celular: `expo-audio`, sem bloqueio. Web: `orderSound.web.ts`, onde o
 * navegador só toca áudio depois de uma interação (faixa "Ativar som", ADR-0053 §7.2).
 */
import { useAudioPlayer } from 'expo-audio';
import { useCallback } from 'react';

import type { OrderSound, OrderSoundKind } from './orderSound.types';

export type { OrderSound, OrderSoundKind } from './orderSound.types';

const SOUNDS = {
  newOrder: require('../../../assets/sounds/new-order.wav'),
  webOrder: require('../../../assets/sounds/web_order.wav'),
} as const;

export function useOrderSound(kind: OrderSoundKind = 'newOrder'): OrderSound {
  const player = useAudioPlayer(SOUNDS[kind]);
  const play = useCallback(() => {
    player.seekTo(0);
    player.play();
  }, [player]);
  return { play, needsUnlock: false, unlock: () => {} };
}
