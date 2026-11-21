/**
 * Aviso sonoro de pedido do site chegando (`OrderAwaitingAcceptance`, ADR-0046 item 23), em qualquer aba, para quem
 * tem `orders.view`. Toca o som próprio `web_order.wav`, o mesmo do canal de push `web-orders` (ADR-0049 itens 7 e 9).
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
  const { play } = useOrderSound('webOrder');

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
