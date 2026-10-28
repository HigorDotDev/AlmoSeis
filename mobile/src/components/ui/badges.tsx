/**
 * Selos (08 §8.11, RNF-001): produção é pílula; financeiro é quadrado. Forma, ícone e texto diferentes,
 * para não depender só de cor.
 */
import { Ionicons } from '@expo/vector-icons';
import { View } from 'react-native';

import type { MoneyString, ProductionStatus } from '@/api/types';
import { formatBRL, toCents } from '@/lib/money';
import { productionNames } from '@/lib/strings';

import { Text, type IconName } from './primitives';
import { radius, space, useColors } from './theme';

function Badge({ shape, icon, label, fg, bg, testID }: { shape: 'pill' | 'square'; icon: IconName; label: string; fg: string; bg: string; testID?: string }) {
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
        borderRadius: shape === 'pill' ? radius.pill : 3,
        backgroundColor: bg,
        borderWidth: shape === 'square' ? 1.5 : 0,
        borderColor: fg,
      }}
    >
      <Ionicons name={icon} size={14} color={fg} />
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

export type SyncPillState = 'draft' | 'sending' | 'unknown' | 'confirmed';

/** RN-014.5: rascunho, enviando, pendente de confirmação e confirmado nunca se confundem. */
export function SyncStatePill({ state }: { state: SyncPillState }) {
  const c = useColors();
  const spec = {
    draft: { icon: 'create-outline' as IconName, label: 'Rascunho', fg: c.neutral, bg: c.neutralBg },
    sending: { icon: 'cloud-upload-outline' as IconName, label: 'Enviando', fg: c.info, bg: c.infoBg },
    unknown: { icon: 'help-circle-outline' as IconName, label: 'Pendente de confirmação', fg: c.warning, bg: c.warningBg },
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
