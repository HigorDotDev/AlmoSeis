/**
 * Web (ADR-0053 §7.4): com a aba oculta, o título mostra quantos pedidos do site aguardam aceite (ADR-0046),
 * "(2) Novos do site · AlmoSeis". Ao voltar para a frente, o título volta ao normal.
 *
 * A contagem é a da faixa "Novos do site" (`GET /orders?acceptanceStatus=AWAITING_ACCEPTANCE`), mantida em dia por
 * `OrderAwaitingAcceptance`/`OrderAccepted`/`OrderRejected` e pelo sync: o SignalR continua conectado com a aba
 * oculta (ADR-0053 §7.1). Aceito em outro computador ou expirado, o número cai sozinho.
 */
import { useEffect, useRef, useState } from 'react';

import { useCurrentBusinessDay } from '@/features/businessDay/hooks';
import { useAwaitingOrders } from '@/features/orders/hooks';
import { hasPermission, useSession } from '@/stores/session';

import { BASE_TITLE, formatTabTitle } from './tabTitleFormat';

export function useWaitingOrdersTitle(): void {
  const canView = useSession((s) => hasPermission(s.me, 'orders.view'));
  const day = useCurrentBusinessDay();
  const awaiting = useAwaitingOrders(canView && !!day.data);
  const count = awaiting.data?.length ?? 0;
  const [hidden, setHidden] = useState(() => document.visibilityState === 'hidden');
  // Título de quando a aba saiu da frente (a navegação pode mudá-lo); devolvido quando ela volta.
  const base = useRef<string | null>(null);

  useEffect(() => {
    const onVisibility = () => setHidden(document.visibilityState === 'hidden');
    document.addEventListener('visibilitychange', onVisibility);
    return () => document.removeEventListener('visibilitychange', onVisibility);
  }, []);

  useEffect(() => {
    if (hidden) {
      if (base.current === null) base.current = document.title || BASE_TITLE;
      document.title = formatTabTitle(count, base.current);
    } else if (base.current !== null) {
      document.title = base.current;
      base.current = null;
    }
  }, [hidden, count]);

  useEffect(
    () => () => {
      if (base.current !== null) document.title = base.current;
      base.current = null;
    },
    [],
  );
}
