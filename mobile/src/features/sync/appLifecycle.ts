/**
 * Primeiro/segundo plano por plataforma (08 §8.6). Celular: `AppState`; em segundo plano o SignalR desconecta
 * (ADR-0036). Web: `appLifecycle.web.ts`.
 */
import { AppState } from 'react-native';

import type { AppLifecycleHandlers } from './appLifecycle.types';

export type { AppLifecycleHandlers } from './appLifecycle.types';

export function subscribeAppLifecycle({ onForeground, onBackground }: AppLifecycleHandlers): () => void {
  const sub = AppState.addEventListener('change', (s) => {
    if (s === 'active') onForeground();
    else if (s === 'background') onBackground();
  });
  return () => sub.remove();
}
