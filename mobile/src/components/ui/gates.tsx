/** PermissionGate, ConnectivityBar e SubscriptionBanner (08 §8.6, §8.8, §8.11). */
import type { ReactElement, ReactNode } from 'react';
import { cloneElement, isValidElement } from 'react';
import { View } from 'react-native';

import type { Permission } from '@/api/types';
import { useConnectivity } from '@/stores/connectivity';
import { hasPermission, useSession } from '@/stores/session';

import { Banner, Text } from './primitives';
import { space, useColors } from './theme';

/**
 * Esconde o filho sem a permissão. `fallback="disabled"` mostra o filho desabilitado com a explicação.
 * O app só esconde; quem decide é o backend.
 */
export function PermissionGate({ permission, fallback = 'hidden', children }: { permission: Permission; fallback?: 'hidden' | 'disabled'; children: ReactNode }) {
  const allowed = useSession((s) => hasPermission(s.me, permission));
  if (allowed) return <>{children}</>;
  if (fallback === 'hidden') return null;
  return (
    <View style={{ gap: space.xs }}>
      {isValidElement(children) ? cloneElement(children as ReactElement<{ disabled?: boolean }>, { disabled: true }) : children}
      <Text variant="small" muted>
        Você não tem permissão para isso. Peça a um gerente.
      </Text>
    </View>
  );
}

function formatClock(d: Date): string {
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

export function ConnectivityBar() {
  const c = useColors();
  const online = useConnectivity((s) => s.online);
  const syncState = useConnectivity((s) => s.syncState);
  const lastSyncAt = useConnectivity((s) => s.lastSyncAt);
  let text: string | null = null;
  let bg = c.neutralBg;
  let fg = c.neutral;
  if (!online) {
    text = `Sem internet. ${lastSyncAt ? `Mostrando dados de ${formatClock(lastSyncAt)}. ` : ''}Pedidos novos ficam como rascunho.`;
    bg = c.warningBg;
    fg = c.warning;
  } else if (syncState === 'syncing') {
    text = 'Reconectando…';
    bg = c.infoBg;
    fg = c.info;
  }
  if (!text) return null;
  return (
    <View accessibilityRole="alert" style={{ backgroundColor: bg, paddingHorizontal: space.lg, paddingVertical: space.sm }}>
      <Text variant="small" style={{ color: fg, fontWeight: '600' }}>
        {text}
      </Text>
    </View>
  );
}

/** ADR-0017: situação e dias restantes, sem preço, plano ou link. */
export function SubscriptionBanner() {
  const me = useSession((s) => s.me);
  const readOnly = useSession((s) => s.readOnly);
  if (!me) return null;
  if (readOnly || !me.subscription.canWrite) {
    return (
      <View style={{ paddingHorizontal: space.lg, paddingTop: space.sm }}>
        <Banner tone="danger" text="Modo somente leitura: a assinatura está inativa. Você pode consultar, mas não registrar. Fale com o proprietário." />
      </View>
    );
  }
  const { status, daysLeft } = me.subscription;
  if (status === 'PAST_DUE') {
    return (
      <View style={{ paddingHorizontal: space.lg, paddingTop: space.sm }}>
        <Banner tone="warning" text="Pagamento da assinatura pendente. Verifique a mensagem enviada ao seu e-mail ou WhatsApp." />
      </View>
    );
  }
  if (status === 'TRIALING' && daysLeft !== null && daysLeft <= 2) {
    return (
      <View style={{ paddingHorizontal: space.lg, paddingTop: space.sm }}>
        <Banner tone="info" text={daysLeft <= 0 ? 'O período de teste termina hoje.' : `Faltam ${daysLeft} ${daysLeft === 1 ? 'dia' : 'dias'} do período de teste.`} />
      </View>
    );
  }
  return null;
}
