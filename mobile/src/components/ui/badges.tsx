/**
 * Selos (08 §8.11, RNF-001): produção é pílula; financeiro é quadrado; atendimento é pílula com contorno; origem
 * "Site" é etiqueta. Forma, ícone e texto diferentes, para não depender só de cor.
 */
import { Ionicons } from '@expo/vector-icons';
import { View } from 'react-native';

import type { FulfillmentStatus, FulfillmentType, MoneyString, ProductionStatus } from '@/api/types';
import { formatBRL, toCents } from '@/lib/money';
import { fulfillmentStatusNames, productionNames, READY_FOR_PICKUP } from '@/lib/strings';

import { Text, type IconName } from './primitives';
import { radius, space, useColors } from './theme';

type Shape = 'pill' | 'square' | 'outline' | 'tag';

function Badge({ shape, icon, label, fg, bg, testID }: { shape: Shape; icon: IconName; label: string; fg: string; bg: string; testID?: string }) {
  return (
    <View
      testID={testID}
      accessibilityLabel={label}
      style={{
        flexDirection: 'row',
        alignItems: 'center',
        gap: 4,
        paddingHorizontal: space.sm,
        paddingVertical: 3,
        borderRadius: shape === 'pill' || shape === 'outline' ? radius.pill : shape === 'tag' ? radius.sm : 4,
        backgroundColor: bg,
        borderWidth: shape === 'square' || shape === 'outline' ? 1.5 : 0,
        borderStyle: shape === 'outline' ? 'dashed' : 'solid',
        borderColor: fg,
      }}
    >
      <Ionicons name={icon} size={16} color={fg} />
      <Text variant="small" style={{ color: fg, fontWeight: '700' }}>
        {label}
      </Text>
    </View>
  );
}

export function ProductionBadge({ status }: { status: ProductionStatus | null }) {
  const c = useColors();
  if (!status) return null;
  const spec = {
    OPEN: { icon: 'time-outline' as IconName, fg: c.neutral, bg: c.neutralBg },
    IN_PROGRESS: { icon: 'flame-outline' as IconName, fg: c.warning, bg: c.warningBg },
    COMPLETED: { icon: 'checkmark-circle-outline' as IconName, fg: c.success, bg: c.successBg },
  }[status];
  return <Badge testID="production-badge" shape="pill" icon={spec.icon} label={productionNames[status]} fg={spec.fg} bg={spec.bg} />;
}

export function PaymentBadge({ totalAmount, paidAmount, isPaid, canceled }: { totalAmount: MoneyString; paidAmount: MoneyString; isPaid: boolean; canceled?: boolean }) {
  const c = useColors();
  if (canceled) {
    return <Badge testID="payment-badge" shape="square" icon="close-circle-outline" label="Cancelado" fg={c.neutral} bg={c.neutralBg} />;
  }
  if (isPaid) {
    return <Badge testID="payment-badge" shape="square" icon="cash-outline" label="Pago" fg={c.success} bg={c.successBg} />;
  }
  if (toCents(paidAmount) > 0) {
    return (
      <Badge
        testID="payment-badge"
        shape="square"
        icon="pie-chart-outline"
        label={`${formatBRL(paidAmount).replace(',00', '')} de ${formatBRL(totalAmount).replace(',00', '')}`}
        fg={c.info}
        bg={c.infoBg}
      />
    );
  }
  return <Badge testID="payment-badge" shape="square" icon="alert-circle-outline" label="A pagar" fg={c.danger} bg={c.dangerBg} />;
}

/** Pedido feito pela página de pedidos (`origin = WEB`, ADR-0046 item 21). */
export function SiteBadge() {
  const c = useColors();
  return <Badge testID="site-badge" shape="tag" icon="globe-outline" label="Site" fg={c.primary} bg={c.primaryBg} />;
}

/**
 * Atendimento de entrega/retirada (ADR-0046 item 2). `PENDING` só aparece como "Pronto para retirar" (derivado,
 * `isReadyForPickup`); fora isso, pendente não tem selo, porque a marcação é opcional.
 */
export function FulfillmentBadge({ fulfillmentType, status, isReadyForPickup }: { fulfillmentType: FulfillmentType; status: FulfillmentStatus | null; isReadyForPickup: boolean }) {
  const c = useColors();
  if (fulfillmentType === 'DINE_IN' || status === null) return null;
  if (status === 'PENDING') {
    if (!isReadyForPickup) return null;
    return <Badge testID="fulfillment-badge" shape="outline" icon="bag-check-outline" label={READY_FOR_PICKUP} fg={c.info} bg={c.infoBg} />;
  }
  const spec = {
    OUT_FOR_DELIVERY: { icon: 'bicycle-outline' as IconName, fg: c.info, bg: c.infoBg },
    DELIVERED: { icon: 'home-outline' as IconName, fg: c.success, bg: c.successBg },
    PICKED_UP: { icon: 'bag-handle-outline' as IconName, fg: c.success, bg: c.successBg },
  }[status];
  return <Badge testID="fulfillment-badge" shape="outline" icon={spec.icon} label={fulfillmentStatusNames[status]} fg={spec.fg} bg={spec.bg} />;
}

export type SyncPillState = 'draft' | 'sending' | 'unknown' | 'failed' | 'confirmed';

/** RN-014.5: rascunho, enviando, pendente de confirmação e confirmado nunca se confundem. */
export function SyncStatePill({ state }: { state: SyncPillState }) {
  const c = useColors();
  const spec = {
    draft: { icon: 'create-outline' as IconName, label: 'Rascunho', fg: c.neutral, bg: c.neutralBg },
    sending: { icon: 'cloud-upload-outline' as IconName, label: 'Enviando', fg: c.info, bg: c.infoBg },
    unknown: { icon: 'help-circle-outline' as IconName, label: 'Pendente de confirmação', fg: c.warning, bg: c.warningBg },
    failed: { icon: 'cloud-offline-outline' as IconName, label: 'Não enviado', fg: c.danger, bg: c.dangerBg },
    confirmed: { icon: 'cloud-done-outline' as IconName, label: 'Confirmado', fg: c.success, bg: c.successBg },
  }[state];
  return <Badge shape="pill" icon={spec.icon} label={spec.label} fg={spec.fg} bg={spec.bg} />;
}

export function AlertDot({ count }: { count: number }) {
  const c = useColors();
  if (count <= 0) return null;
  return (
    <View
      accessibilityLabel={count === 1 ? '1 alteração não vista' : `${count} alterações não vistas`}
      style={{ minWidth: 22, height: 22, borderRadius: 11, backgroundColor: c.alert, alignItems: 'center', justifyContent: 'center', paddingHorizontal: 5 }}
    >
      <Text variant="small" style={{ color: '#FFFFFF', fontWeight: '700' }}>
        {count}
      </Text>
    </View>
  );
}
