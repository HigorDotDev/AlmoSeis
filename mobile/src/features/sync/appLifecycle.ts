/**
 * Primeiro/segundo plano por plataforma (08 §8.6). Celular: `AppState`; em segundo plano o SignalR desconecta
 * (ADR-0036). Web: `appLifecycle.web.ts`.
 */
import { AppState } from 'react-native';

export interface AppLifecycleHandlers {
  /** Voltou para a frente (ou a rede voltou, na web): reconecta e sincroniza. */
  onForeground(): void;
  /** Foi para segundo plano: desconecta o tempo real. */
  onBackground(): void;
}

export function subscribeAppLifecycle({ onForeground, onBackground }: AppLifecycleHandlers): () => void {
  const sub = AppState.addEventListener('change', (s) => {
    if (s === 'active') onForeground();
    else if (s === 'background') onBackground();
  });
  return () => sub.remove();
}
