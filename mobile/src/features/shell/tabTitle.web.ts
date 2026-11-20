/**
 * Web (ADR-0053 §7.4): com a aba oculta, o título mostra quantos pedidos novos chegaram desde que ela saiu da
 * frente. Ao voltar, o título volta ao normal.
 *
 * Hoje conta `OrderCreated` de outras pessoas (o mesmo aviso que toca o som). Quando os pedidos do site
 * (ADR-0046) chegarem ao app, a contagem passa a ser "pedidos do site aguardando aceite" e o texto
 * "Novos do site"; só este arquivo muda.
 */
import { useEffect } from 'react';

import { onRealtimeEvent } from '@/features/realtime/realtime';
import { useSession } from '@/stores/session';

import { BASE_TITLE, formatTabTitle } from './tabTitleFormat';

export function useWaitingOrdersTitle(): void {
  const ownId = useSession((s) => s.me?.employee.id ?? null);

  useEffect(() => {
    let count = 0;
    let base: string | null = null;

    const restore = () => {
      if (base !== null) document.title = base;
      base = null;
      count = 0;
    };

    const off = onRealtimeEvent((e) => {
      if (e.eventType !== 'OrderCreated') return;
      if (e.actorEmployeeId && e.actorEmployeeId === ownId) return;
      if (document.visibilityState === 'visible') return;
      if (base === null) base = document.title || BASE_TITLE;
      count += 1;
      document.title = formatTabTitle(count, base);
    });

    const onVisibility = () => {
      if (document.visibilityState === 'visible') restore();
    };
    document.addEventListener('visibilitychange', onVisibility);
    return () => {
      off();
      document.removeEventListener('visibilitychange', onVisibility);
      restore();
    };
  }, [ownId]);
}
