/**
 * Web (ADR-0053 §7.2): o navegador só libera áudio depois de uma interação na página (ativação do usuário).
 * Enquanto não houve nenhuma desde o carregamento, `needsUnlock` fica `true` e a tela mostra "Ativar som";
 * o clique toca o aviso uma vez (a pessoa ouve que funcionou) e, daí em diante, o som toca mesmo com a aba oculta.
 */
import { Asset } from 'expo-asset';
import { useCallback, useEffect, useRef, useState } from 'react';

import type { OrderSound } from './orderSound';

const SOUND = require('../../../assets/sounds/new-order.wav');

function hasUserActivation(): boolean {
  const activation = (navigator as Navigator & { userActivation?: { hasBeenActive: boolean } }).userActivation;
  // Sem a API (navegador antigo): não dá para saber; assume bloqueado até o clique.
  return activation?.hasBeenActive ?? false;
}

export function useOrderSound(): OrderSound {
  const audio = useRef<HTMLAudioElement | null>(null);
  const [unlocked, setUnlocked] = useState(hasUserActivation);

  useEffect(() => {
    audio.current = new Audio(Asset.fromModule(SOUND).uri);
    audio.current.preload = 'auto';
    // Qualquer clique ou tecla na página também libera o áudio.
    const onGesture = () => setUnlocked(true);
    window.addEventListener('pointerdown', onGesture, { once: true });
    window.addEventListener('keydown', onGesture, { once: true });
    return () => {
      window.removeEventListener('pointerdown', onGesture);
      window.removeEventListener('keydown', onGesture);
      audio.current = null;
    };
  }, []);

  const play = useCallback(() => {
    const a = audio.current;
    if (!a) return;
    a.currentTime = 0;
    a.play().catch(() => setUnlocked(false));
  }, []);

  const unlock = useCallback(() => {
    setUnlocked(true);
    play();
  }, [play]);

  return { play, needsUnlock: !unlocked, unlock };
}
