/**
 * Aviso sonoro de pedido do site chegando (`OrderAwaitingAcceptance`, ADR-0046 item 23), em qualquer aba, para quem
 * tem `orders.view`. Por enquanto é o mesmo som de pedido novo (ADR-0036); o som próprio vem com o push (EP-002 F-209).
 * Respeita a preferência "Som de pedidos". Na web, o navegador só toca depois de uma interação (ADR-0053 §7.2).
 */
import { useEffect } from 'react';

import { useOrderSound } from '@/features/production/orderSound';
import { onRealtimeEvent } from '@/features/realtime/realtime';
import { usePreferences } from '@/stores/preferences';
import { hasPermission, useSession } from '@/stores/session';

export function useWebOrderSound(): void {
  const canView = useSession((s) => hasPermission(s.me, 'orders.view'));
  const sound = usePreferences((s) => s.orderSound);
  const { play } = useOrderSound();

  useEffect(
    () =>
      onRealtimeEvent((e) => {
        if (e.eventType !== 'OrderAwaitingAcceptance') return;
        if (!canView || !sound) return;
        play();
      }),
    [canView, sound, play],
  );
}
