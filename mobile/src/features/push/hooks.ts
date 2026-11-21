/** Push dos pedidos do site (ADR-0049, 08 §8.6): ouvintes na raiz, registro durante a sessão e o toque que abre o pedido. */
import { router } from 'expo-router';
import { useEffect } from 'react';

import { subscribeAppLifecycle } from '@/features/sync/appLifecycle';
import { useSession } from '@/stores/session';

import { notifications } from './notifications';
import { decidePushTarget, parseWebOrderPushData, usePushTarget } from './pushShared';
import { syncPushRegistration } from './registration';

/** Espera a navegação assentar (o login faz `router.replace('/')` logo depois de entrar). */
export const OPEN_TARGET_DELAY_MS = 400;

/**
 * Na raiz, uma vez: canal, supressão em primeiro plano e o toque. O toque vira um destino guardado; quem o abre é
 * `usePushTargetOpener`, já dentro da sessão (sem sessão, a guarda leva ao login ou ao PIN e o destino espera).
 */
export function usePushNotifications(): void {
  useEffect(
    () =>
      notifications.init((data) => {
        const parsed = parseWebOrderPushData(data);
        if (parsed) usePushTarget.getState().setTarget({ ...parsed, tappedAt: Date.now() });
      }),
    [],
  );
}

/** Dentro da sessão: `PUT` ao entrar (inclusive por PIN), a cada abertura, ao voltar ao primeiro plano e com token novo. */
export function usePushRegistration(): void {
  const tenantId = useSession((s) => s.me?.tenant.id ?? null);
  const employeeId = useSession((s) => s.me?.employee.id ?? null);
  useEffect(() => {
    if (!notifications.supported || !tenantId || !employeeId) return;
    void syncPushRegistration();
    const offToken = notifications.onTokenChange(() => void syncPushRegistration({ force: true }));
    // Volta ao primeiro plano: a pessoa pode ter dado (ou tirado) a permissão nas configurações do aparelho.
    const offLifecycle = subscribeAppLifecycle({ onForeground: () => void syncPushRegistration(), onBackground: () => {} });
    return () => {
      offToken();
      offLifecycle();
    };
  }, [tenantId, employeeId]);
}

/** Abre o pedido tocado quando a sessão é do mesmo estabelecimento; de outro, ignora (06 §6.3). */
export function usePushTargetOpener(): void {
  const target = usePushTarget((s) => s.target);
  const tenantId = useSession((s) => (s.status === 'signedIn' ? (s.me?.tenant.id ?? null) : null));
  useEffect(() => {
    if (!target) return;
    const decision = decidePushTarget(target, tenantId);
    if (decision === 'drop') {
      usePushTarget.getState().setTarget(null);
      return;
    }
    if (decision === 'wait') return;
    const timer = setTimeout(() => {
      usePushTarget.getState().setTarget(null);
      router.push(`/orders/${target.orderId}`);
    }, OPEN_TARGET_DELAY_MS);
    return () => clearTimeout(timer);
  }, [target, tenantId]);
}
