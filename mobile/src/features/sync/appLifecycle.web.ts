/**
 * Web (ADR-0053 §7.1): o SignalR fica conectado com a aba oculta ou minimizada; `onBackground` nunca é
 * chamado. Ao voltar a aba para a frente, ao voltar a rede e ao acordar o computador, reconecta e sincroniza.
 */
import type { AppLifecycleHandlers } from './appLifecycle.types';

export type { AppLifecycleHandlers } from './appLifecycle.types';

/** Um intervalo que atrasa muito mais que isso = o computador dormiu (ou a aba foi congelada). */
const WAKE_CHECK_MS = 30_000;
const WAKE_GAP_MS = 90_000;

export function subscribeAppLifecycle({ onForeground }: AppLifecycleHandlers): () => void {
  const onVisibility = () => {
    if (document.visibilityState === 'visible') onForeground();
  };
  const onPageShow = (e: PageTransitionEvent) => {
    if (e.persisted) onForeground();
  };
  document.addEventListener('visibilitychange', onVisibility);
  window.addEventListener('online', onForeground);
  window.addEventListener('pageshow', onPageShow);
  let last = Date.now();
  const timer = setInterval(() => {
    const now = Date.now();
    if (now - last > WAKE_GAP_MS) onForeground();
    last = now;
  }, WAKE_CHECK_MS);
  return () => {
    document.removeEventListener('visibilitychange', onVisibility);
    window.removeEventListener('online', onForeground);
    window.removeEventListener('pageshow', onPageShow);
    clearInterval(timer);
  };
}
