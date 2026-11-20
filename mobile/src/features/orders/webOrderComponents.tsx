/**
 * Pedidos do site (ADR-0046, 08 §8.9): faixa "Novos do site", card com prazo, aceite, recusa e ações de atendimento.
 * Usado na aba Marmitas, na aba Pedidos (cozinha) e no detalhe do pedido.
 */
import { Ionicons } from '@expo/vector-icons';
import { router } from 'expo-router';
import { useEffect, useState } from 'react';
import { View, type StyleProp, type ViewStyle } from 'react-native';

import { errorMessage, isApiError } from '@/api/errors';
import type { EstimatedReadyMinutes, Order, OrderSummary } from '@/api/types';
import {
  Banner,
  Button,
  Card,
  Chip,
  ConfirmDialog,
  Row,
  SiteBadge,
  SyncStatePill,
  Text,
  TextField,
  radius,
  space,
  useColors,
} from '@/components/ui';
import { useCurrentBusinessDay } from '@/features/businessDay/hooks';
import { formatBRL } from '@/lib/money';
import { formatPhone } from '@/lib/phone';
import { fulfillmentNames, fulfillmentStatusNames, productionNames, rejectionCustomerText, rejectionReasonNames } from '@/lib/strings';
import { useIsOnline } from '@/stores/connectivity';
import { useBlockedBy } from '@/stores/pendingOps';
import { toast } from '@/stores/toast';
import { useCanWrite, usePermission } from '@/stores/session';

import { fulfillmentActions, type FulfillmentAction, type FulfillmentInput } from './fulfillment';
import { summarizeOrder, useAcceptOrder, useAwaitingOrders, useOrder, useRejectOrder, useSetFulfillment } from './hooks';
import {
  acceptanceConflictText,
  CAPTCHA_UNAVAILABLE_TEXT,
  countdown,
  ESTIMATED_MINUTES,
  expectedPaymentText,
  orderNumberLabel,
  REJECT_MESSAGE_MAX,
  REJECT_REASONS,
  rejectionMessageError,
  type RejectReason,
} from './webOrders';

/** Relógio da tela, para a contagem regressiva. */
export function useNow(intervalMs = 1000): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(id);
  }, [intervalMs]);
  return now;
}

/**
 * Faixa "Novos do site (n)" (ADR-0046 item 21): para quem tem `orders.view`; os botões só para `orders.accept`.
 * Sem pedidos aguardando, não ocupa espaço. `collapsible` (cozinha): começa recolhida, só com a contagem.
 */
export function NewWebOrdersSection({ collapsible = false, style }: { collapsible?: boolean; style?: StyleProp<ViewStyle> }) {
  const c = useColors();
  const canView = usePermission('orders.view');
  const day = useCurrentBusinessDay();
  const awaiting = useAwaitingOrders(canView && !!day.data);
  const [expanded, setExpanded] = useState(!collapsible);
  const items = awaiting.data ?? [];
  if (items.length === 0) return null;
  return (
    <View testID="new-web-orders" style={[{ gap: space.sm, padding: space.md, borderRadius: radius.md, borderWidth: 2, borderColor: c.primary, backgroundColor: c.primaryBg }, style]}>
      <Row style={{ justifyContent: 'space-between' }}>
        <Row style={{ flex: 1 }}>
          <Ionicons name="globe-outline" size={22} color={c.primary} />
          <Text variant="subtitle" accessibilityRole="header" style={{ color: c.primary }}>
            Novos do site ({items.length})
          </Text>
        </Row>
        {collapsible ? <Button title={expanded ? 'Esconder' : 'Ver'} variant="ghost" onPress={() => setExpanded(!expanded)} /> : null}
      </Row>
      {expanded ? items.map((o) => <WebOrderCard key={o.id} summary={o} />) : null}
    </View>
  );
}

/** Contagem até o fim do prazo de aceite (ADR-0046 item 15). */
export function AcceptanceCountdown({ deadline }: { deadline: string | null }) {
  const c = useColors();
  const now = useNow();
  const cd = countdown(deadline, now);
  if (!cd) return null;
  const color = cd.expired ? c.danger : cd.urgent ? c.warning : c.textMuted;
  const text = cd.expired ? 'Prazo esgotado' : `Aceite em ${cd.label}`;
  return (
    <Row gap={space.xs}>
      <Ionicons name="timer-outline" size={18} color={color} />
      <Text testID="acceptance-countdown" accessibilityLabel={cd.expired ? 'Prazo de aceite esgotado' : `Faltam ${cd.label} para aceitar`} style={{ color, fontWeight: '700', fontVariant: ['tabular-nums'] }}>
        {text}
      </Text>
    </Row>
  );
}

/** Card do pedido aguardando aceite: prazo, cliente, itens, total, entrega/retirada e as ações. */
export function WebOrderCard({ summary }: { summary: OrderSummary }) {
  const detail = useOrder(summary.id);
  const pending = useBlockedBy(`/orders/${summary.id}`);
  const d = detail.data;
  // A versão mais nova que o app conhece: a lista e o detalhe são invalidados juntos.
  const version = Math.max(summary.version, d?.version ?? 0);
  return (
    <Card onPress={() => router.push(`/orders/${summary.id}`)} accessibilityLabel={`Pedido do site de ${summary.customerName ?? 'cliente'}, ${formatBRL(summary.totalAmount)}`}>
      <Row style={{ justifyContent: 'space-between', flexWrap: 'wrap' }}>
        <Row>
          <SiteBadge />
          <Text muted style={{ fontWeight: '600' }}>
            {fulfillmentNames[summary.fulfillmentType]}
          </Text>
        </Row>
        <AcceptanceCountdown deadline={summary.acceptanceDeadline} />
      </Row>
      <Row style={{ justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <Text variant="title" numberOfLines={1} style={{ flexShrink: 1 }}>
          {summary.customerName ?? 'Sem nome'}
        </Text>
        <Text variant="title">{formatBRL(summary.totalAmount)}</Text>
      </Row>
      {d ? <WebOrderDetails order={d} /> : <Text muted>{summary.itemCount === 1 ? '1 item' : `${summary.itemCount} itens`}</Text>}
      {pending ? <SyncStatePill state="unknown" /> : null}
      <AcceptRejectActions
        orderId={summary.id}
        version={version}
        lines={[
          { label: 'Cliente', value: summary.customerName ?? 'Sem nome' },
          { label: 'Atendimento', value: fulfillmentNames[summary.fulfillmentType] },
          { label: 'Total', value: formatBRL(summary.totalAmount) },
        ]}
      />
    </Card>
  );
}

/** Itens, endereço, telefone (não verificado) e observação de um pedido do site. */
export function WebOrderDetails({ order: o }: { order: Order }) {
  const c = useColors();
  return (
    <View style={{ gap: 2 }}>
      {o.items.map((i) => (
        <View key={i.id}>
          <Text style={{ fontWeight: '600' }}>
            {i.quantity}× {i.name}
          </Text>
          {i.composition ? <Text variant="small" muted>{i.composition}</Text> : null}
          {i.addons.map((a) => (
            <Text key={a.id} variant="small" muted>
              + {a.quantity}× {a.name}
            </Text>
          ))}
          {i.notes ? <Text variant="small">Obs.: {i.notes}</Text> : null}
        </View>
      ))}
      {o.deliveryAddress ? (
        <Text variant="small" muted>
          {[o.deliveryAddress.street, o.deliveryAddress.number, o.deliveryAddress.complement, o.deliveryAddress.neighborhood].filter(Boolean).join(', ')}
          {o.deliveryAddress.reference ? ` · ${o.deliveryAddress.reference}` : ''}
        </Text>
      ) : null}
      {o.customer?.phoneNumber ? <PhoneLine phone={o.customer.phoneNumber} unverified={o.origin === 'WEB'} /> : null}
      {o.notes ? <Text style={{ color: c.danger, fontWeight: '600' }}>Obs.: {o.notes}</Text> : null}
      <WebOrderPaymentInfo order={o} />
    </View>
  );
}

/**
 * Forma de pagamento escolhida na página ("Vai pagar em dinheiro, troco para R$ 50,00") e o aviso discreto de
 * `riskFlag` (ADR-0044 item 19, ADR-0045 item 12). A forma é só intenção: nada é dado como pago.
 */
export function WebOrderPaymentInfo({ order: o }: { order: Pick<Order, 'expectedPaymentMethod' | 'changeFor' | 'riskFlag'> }) {
  const c = useColors();
  const payment = expectedPaymentText(o);
  if (!payment && o.riskFlag !== 'CAPTCHA_UNAVAILABLE') return null;
  return (
    <View style={{ gap: 2 }}>
      {payment ? (
        <Row gap={space.xs}>
          <Ionicons name={o.expectedPaymentMethod === 'CASH' ? 'cash-outline' : o.expectedPaymentMethod === 'PIX' ? 'qr-code-outline' : 'card-outline'} size={16} color={c.text} />
          <Text variant="small" testID="expected-payment" style={{ fontWeight: '600', flexShrink: 1 }}>
            {payment}
          </Text>
        </Row>
      ) : null}
      {o.riskFlag === 'CAPTCHA_UNAVAILABLE' ? (
        <Row gap={space.xs}>
          <Ionicons name="shield-outline" size={16} color={c.warning} />
          <Text variant="small" testID="risk-flag" style={{ color: c.warning, flexShrink: 1 }}>
            {CAPTCHA_UNAVAILABLE_TEXT}
          </Text>
        </Row>
      ) : null}
    </View>
  );
}

/** ADR-0044: o telefone do pedido do site não é verificado. */
export function PhoneLine({ phone, unverified }: { phone: string; unverified: boolean }) {
  const c = useColors();
  return (
    <Text variant="small" muted>
      {formatPhone(phone)}
      {unverified ? <Text variant="small" style={{ color: c.warning, fontWeight: '600' }}> · telefone não verificado</Text> : null}
    </Text>
  );
}

/**
 * "Aceitar" (tempo estimado opcional) e "Recusar" (motivo da lista). Sem `orders.accept`, só avisa que aguarda o
 * balcão. Os dois passam pelo Confirma (RF-012).
 */
export function AcceptRejectActions({ orderId, version, lines }: { orderId: string; version: number; lines: { label: string; value: string }[] }) {
  const c = useColors();
  const canAccept = useCanWrite('orders.accept');
  const online = useIsOnline();
  const [dialog, setDialog] = useState<'accept' | 'reject' | null>(null);
  if (!canAccept) {
    return (
      <Text variant="small" muted>
        Aguardando o balcão aceitar.
      </Text>
    );
  }
  return (
    <>
      <Row>
        <Button title="Aceitar" icon="checkmark" disabled={!online} onPress={() => setDialog('accept')} style={{ flex: 1 }} />
        <Button title="Recusar" icon="close" variant="secondary" disabled={!online} onPress={() => setDialog('reject')} style={{ flex: 1, borderColor: c.danger }} />
      </Row>
      {dialog === 'accept' ? <AcceptDialog orderId={orderId} version={version} lines={lines} onClose={() => setDialog(null)} /> : null}
      {dialog === 'reject' ? <RejectDialog orderId={orderId} version={version} lines={lines} onClose={() => setDialog(null)} /> : null}
    </>
  );
}

function AcceptDialog({ orderId, version, lines, onClose }: { orderId: string; version: number; lines: { label: string; value: string }[]; onClose: () => void }) {
  const [minutes, setMinutes] = useState<EstimatedReadyMinutes | null>(null);
  const accept = useAcceptOrder(orderId);
  const unknown = accept.state === 'unknown' || accept.state === 'failed';
  return (
    <ConfirmDialog
      visible
      title="Aceitar pedido do site?"
      description="O pedido recebe número e vai para a cozinha."
      lines={lines}
      warning={unknown ? (accept.error ?? 'Sem resposta do servidor. Verificando se o aceite entrou…') : accept.error}
      confirmLabel="Aceitar pedido"
      loading={accept.state === 'sending'}
      onCancel={onClose}
      onConfirm={async () => {
        const outcome = await accept.run({ estimatedReadyMinutes: minutes, version });
        if (outcome.kind === 'confirmed') onClose();
        if (outcome.kind === 'conflict') {
          // ADR-0046 item 9: outra pessoa decidiu antes ("Já aceito por Bruno"). Nada foi gravado.
          toast(acceptanceConflictText(outcome.current as Order | null), 'info');
          onClose();
        }
        if (outcome.kind === 'rejected') {
          // WebOrderExpired, OrderNotAwaitingAcceptance, BusinessDayRequired: o card pode sumir com a recarga.
          toast(errorMessage(outcome.error), 'error');
          onClose();
        }
      }}
    >
      <Text style={{ fontWeight: '600' }}>Tempo estimado (opcional, o cliente vê)</Text>
      <Row style={{ flexWrap: 'wrap' }}>
        <Chip label="Sem estimativa" selected={minutes === null} onPress={() => setMinutes(null)} />
        {ESTIMATED_MINUTES.map((m) => (
          <Chip key={m} label={`${m} min`} selected={minutes === m} onPress={() => setMinutes(m)} />
        ))}
      </Row>
    </ConfirmDialog>
  );
}

function RejectDialog({ orderId, version, lines, onClose }: { orderId: string; version: number; lines: { label: string; value: string }[]; onClose: () => void }) {
  const c = useColors();
  const [reason, setReason] = useState<RejectReason | null>(null);
  const [message, setMessage] = useState('');
  const [tried, setTried] = useState(false);
  const reject = useRejectOrder(orderId);
  const fieldError = rejectionMessageError(reason, message);
  const customerSees =
    reason === null
      ? null
      : reason === 'SUSPECTED_PRANK'
        ? `${rejectionCustomerText.SUSPECTED_PRANK} (o texto abaixo não é mostrado)`
        : [rejectionCustomerText[reason], message.trim() || null].filter(Boolean).join(' ') || null;
  return (
    <ConfirmDialog
      visible
      title="Recusar pedido do site?"
      lines={lines}
      warning={tried && reason === null ? 'Escolha o motivo.' : reject.error}
      confirmLabel="Recusar pedido"
      danger
      loading={reject.busy}
      onCancel={onClose}
      onConfirm={async () => {
        setTried(true);
        if (reason === null || fieldError) return;
        const outcome = await reject.run({ reasonCode: reason, message: message.trim() || null, version });
        if (outcome.kind === 'confirmed') onClose();
        if (outcome.kind === 'conflict') {
          toast(acceptanceConflictText(outcome.current as Order | null), 'info');
          onClose();
        }
        if (outcome.kind === 'rejected' && !isApiError(outcome.error, 'ValidationFailed')) {
          toast(errorMessage(outcome.error), 'error');
          onClose();
        }
      }}
    >
      <Text style={{ fontWeight: '600' }}>Motivo</Text>
      <Row style={{ flexWrap: 'wrap' }}>
        {REJECT_REASONS.map((r) => (
          <Chip key={r} label={rejectionReasonNames[r]} selected={reason === r} onPress={() => setReason(r)} />
        ))}
      </Row>
      <TextField
        label={reason === 'OTHER' ? 'Mensagem para o cliente (obrigatória)' : 'Mensagem para o cliente (opcional)'}
        value={message}
        onChangeText={setMessage}
        maxLength={REJECT_MESSAGE_MAX}
        multiline
        error={tried || message.trim().length > 0 ? fieldError : null}
        placeholder="Ex.: A costela acabou."
      />
      <Banner tone="warning" text="O cliente verá esta mensagem." />
      {customerSees ? (
        <Text variant="small" style={{ color: c.textMuted }}>
          O cliente vai ler: “{customerSees}”
        </Text>
      ) : null}
    </ConfirmDialog>
  );
}

type FulfillmentOrder = FulfillmentInput & { id: string; version: number; orderNumber: number | null };

/**
 * Ações de atendimento (ADR-0046 item 18). `compact` (cards de lista e da cozinha): só o avanço; as voltas ("Voltou sem
 * entregar", correções) ficam no detalhe. Todas passam pelo Confirma.
 */
export function FulfillmentActions({ order: o, compact = false }: { order: FulfillmentOrder; compact?: boolean }) {
  const canFulfill = useCanWrite('orders.fulfillment.update');
  const canProduction = useCanWrite('production.update');
  const { actions, waitingKitchen } = fulfillmentActions(o, { canFulfill, canProduction });
  const shown = compact ? actions.filter((a) => a.kind === 'forward') : actions;
  if (shown.length === 0 && !(waitingKitchen && !compact)) return null;
  // A mutação só existe quando há botão: listas longas não criam um hook de escrita por card.
  return <FulfillmentButtons order={o} actions={shown} waitingKitchen={waitingKitchen && !compact} />;
}

function FulfillmentButtons({ order: o, actions: shown, waitingKitchen }: { order: FulfillmentOrder; actions: FulfillmentAction[]; waitingKitchen: boolean }) {
  const online = useIsOnline();
  const set = useSetFulfillment(o.id, o.orderNumber);
  const [target, setTarget] = useState<FulfillmentAction | null>(null);
  const from = o.fulfillmentStatus ? fulfillmentStatusNames[o.fulfillmentStatus] : '';
  return (
    <>
      {waitingKitchen ? (
        <Text variant="small" muted>
          {o.fulfillmentType === 'DELIVERY' ? '"Saiu para entrega"' : '"Retirado"'} fica disponível quando a cozinha concluir.
        </Text>
      ) : null}
      {shown.length ? (
        <Row style={{ flexWrap: 'wrap' }}>
          {shown.map((a) => (
            <Button
              key={a.to}
              testID={`fulfillment-${a.to}`}
              title={a.label}
              icon={a.kind === 'back' ? 'arrow-undo-outline' : a.to === 'OUT_FOR_DELIVERY' ? 'bicycle-outline' : 'checkmark-done-outline'}
              variant={a.kind === 'back' ? 'secondary' : 'primary'}
              disabled={!online}
              onPress={() => setTarget(a)}
              style={{ flexGrow: 1 }}
            />
          ))}
        </Row>
      ) : null}
      <ConfirmDialog
        visible={target !== null}
        title={target ? `${target.label}?` : ''}
        lines={
          target
            ? [
                { label: 'Pedido', value: orderNumberLabel(o.orderNumber) },
                { label: 'Atendimento', value: `${from} → ${fulfillmentStatusNames[target.to]}` },
                ...(target.completeProduction && o.productionStatus ? [{ label: 'Produção', value: `${productionNames[o.productionStatus]} → ${productionNames.COMPLETED}` }] : []),
              ]
            : []
        }
        description={target?.kind === 'back' ? 'Correção: volta o atendimento um passo. O financeiro não muda.' : 'O financeiro não muda: se não estiver pago, o saldo continua em aberto.'}
        warning={set.error}
        confirmLabel={target?.label ?? ''}
        loading={set.busy}
        onCancel={() => {
          set.reset();
          setTarget(null);
        }}
        onConfirm={async () => {
          const outcome = await set.run({ to: target!.to, completeProduction: target!.completeProduction, version: o.version });
          if (outcome.kind === 'confirmed') setTarget(null);
          if (outcome.kind === 'conflict') {
            // ADR-0034: mostra o que mudou (`current`) e pede para refazer; nada foi gravado.
            const now = outcome.current ? summarizeOrder(outcome.current as Order).filter((l) => l.startsWith('Atendimento') || l.startsWith('Produção')) : [];
            toast(`O pedido mudou enquanto você conferia${now.length ? ` (${now.join(' · ')})` : ''}. Confira e refaça.`, 'info');
            setTarget(null);
          }
        }}
      />
    </>
  );
}

/** Pedido do site não aceito: faixa com o desfecho (recusado, expirado, desistência) no detalhe. */
export function acceptanceOutcomeText(o: Order): string | null {
  switch (o.acceptanceStatus) {
    case 'REJECTED': {
      const r = o.rejection;
      if (!r) return 'Pedido recusado.';
      const who = r.rejectedBy ? ` por ${r.rejectedBy.displayName}` : ' pelo sistema';
      return `Recusado${who}: ${rejectionReasonNames[r.reasonCode]}${r.message ? ` — "${r.message}"` : ''}.`;
    }
    case 'EXPIRED':
      return 'Ninguém aceitou no prazo: o pedido expirou.';
    case 'WITHDRAWN':
      return 'O cliente desistiu do pedido.';
    default:
      return null;
  }
}

