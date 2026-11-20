/**
 * Web (ADR-0053 §9.3): teclas no documento inteiro, para o PIN, o `Enter` dos diálogos de confirmação etc.
 * Não intercepta quando o foco está num botão (o próprio botão trata `Enter`/espaço) nem com Ctrl/Alt/Meta.
 */
import { useEffect, useRef } from 'react';

import type { KeyHandler } from './keyboard';

export type { KeyHandler } from './keyboard';

function focusedOnButton(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  return target.tagName === 'BUTTON' || target.getAttribute('role') === 'button' || target.tagName === 'A';
}

export function useKeyDown(handler: KeyHandler, enabled = true): void {
  const ref = useRef(handler);
  useEffect(() => {
    ref.current = handler;
  });

  useEffect(() => {
    if (!enabled) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.ctrlKey || e.altKey || e.metaKey || e.isComposing) return;
      if (e.key === 'Enter' && focusedOnButton(e.target)) return;
      if (ref.current(e.key)) e.preventDefault();
    };
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [enabled]);
}
