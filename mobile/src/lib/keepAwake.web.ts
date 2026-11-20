/**
 * Web (ADR-0053 §7.3): Wake Lock API. A implementação web do `expo-keep-awake` pede o lock uma vez, não o
 * readquire quando a aba volta para a frente (o navegador o solta ao ocultar) e lança erro fora do Chrome/Edge.
 * Aqui: pede ao montar, de novo a cada `visibilitychange` visível, e segue sem ele se não houver suporte.
 */
import { useEffect } from 'react';

interface WakeLockSentinelLike {
  released: boolean;
  release(): Promise<void>;
}

interface WakeLockLike {
  request(type: 'screen'): Promise<WakeLockSentinelLike>;
}

export function useKeepAwake(): void {
  useEffect(() => {
    const wakeLock = (navigator as Navigator & { wakeLock?: WakeLockLike }).wakeLock;
    if (!wakeLock) return;
    let sentinel: WakeLockSentinelLike | null = null;
    let alive = true;

    const acquire = async () => {
      if (!alive || document.visibilityState !== 'visible' || (sentinel && !sentinel.released)) return;
      try {
        const s = await wakeLock.request('screen');
        if (alive) sentinel = s;
        else void s.release();
      } catch {
        // Bateria fraca, política do navegador: segue sem a tela ligada.
      }
    };

    const onVisibility = () => void acquire();
    void acquire();
    document.addEventListener('visibilitychange', onVisibility);
    return () => {
      alive = false;
      document.removeEventListener('visibilitychange', onVisibility);
      void sentinel?.release().catch(() => {});
    };
  }, []);
}
