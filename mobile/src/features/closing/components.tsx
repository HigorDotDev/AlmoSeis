/** Resumo do fechamento em cards (08 §8.9: as tabelas do RF-010 viram cards). */
import { View } from 'react-native';

import type { ClosingSummary, Me, OrderType, TenantModule } from '@/api/types';
import { hasAnyModule } from '@/features/tenant/establishment';
import { Card, Divider, KeyValue, Row, Text, space, useColors } from '@/components/ui';
import { formatDateTime } from '@/lib/dates';
import { formatBRL, toCents, type MoneyString } from '@/lib/money';
import { orderTypeNames, paymentMethodNames } from '@/lib/strings';
import { useSession } from '@/stores/session';

/**
 * "Vendido pelo site" e "Taxa de entrega do site" (ADR-0057 item 12, complementado em 2026-11-23, M1): o primeiro é
 * só os itens a preço de site (sem a taxa); a taxa dos pedidos do site vem à parte, já contida nas taxas de entrega.
 * Os dois já estão no vendido. Informativos (a cobrança da plataforma e a base dela estão em aberto, Q-310). Resumo,
 * fechamento e mensal; ausentes numa resposta guardada por um app anterior.
 */
export function SiteSalesLines({ siteSoldTotal, siteDeliveryFeeTotal }: { siteSoldTotal?: MoneyString; siteDeliveryFeeTotal?: MoneyString }) {
  return (
    <>
      {siteSoldTotal !== undefined ? <KeyValue label="Vendido pelo site (já no total)" value={formatBRL(siteSoldTotal)} /> : null}
      {siteDeliveryFeeTotal !== undefined ? <KeyValue label="Taxa de entrega do site (já no total)" value={formatBRL(siteDeliveryFeeTotal)} /> : null}
    </>
  );
}

export function differenceText(diff: string | null): string | null {
  if (diff === null) return null;
  const cents = toCents(diff);
  if (cents === 0) return 'Caixa conferido, sem diferença.';
  return cents < 0 ? `Faltam ${formatBRL(-cents)}` : `Sobram ${formatBRL(cents)}`;
}

/** "2 pedidos do site aguardando aceite serão recusados" (ADR-0046 item 13), ou `null` sem nenhum. */
export function autoRejectWarning(awaitingAcceptanceCount: number): string | null {
  if (awaitingAcceptanceCount <= 0) return null;
  return awaitingAcceptanceCount === 1
    ? '1 pedido do site aguardando aceite será recusado.'
    : `${awaitingAcceptanceCount} pedidos do site aguardando aceite serão recusados.`;
}

/** "N entregas sem marcação de entregue" (ADR-0046 item 20): só informa, a marcação é opcional. */
export function unmarkedDeliveriesText(count: number): string | null {
  if (count <= 0) return null;
  return count === 1 ? '1 entrega sem marcação de entregue.' : `${count} entregas sem marcação de entregue.`;
}

/**
 * Pedidos do site e entregas (RF-210, ADR-0046 itens 13 e 20): informativo, fora do vendido. Some quando está tudo
 * zerado, para não ocupar a tela de quem não usa a página de pedidos.
 */
function WebOrdersCard({ summary: s }: { summary: ClosingSummary }) {
  const w = s.webOrders;
  const unmarked = unmarkedDeliveriesText(s.unmarkedDeliveryCount);
  if (!w || (w.awaitingAcceptanceCount === 0 && w.autoRejectedCount === 0 && w.notAcceptedCount === 0 && !unmarked)) return null;
  return (
    <Card>
      <Text variant="subtitle">Pedidos do site e entregas</Text>
      {!s.isFinal ? <KeyValue label="Aguardando aceite" value={String(w.awaitingAcceptanceCount)} /> : null}
      {s.isFinal && w.autoRejectedCount > 0 ? <KeyValue label="Recusados ao fechar" value={String(w.autoRejectedCount)} /> : null}
      <KeyValue label="Não aceitos (recusados, expirados, desistências)" value={String(w.notAcceptedCount)} />
      {unmarked ? <Text muted>{unmarked}</Text> : null}
      <Text variant="small" muted>
        Pedidos do site não aceitos não entram no vendido.
      </Text>
    </Card>
  );
}

/** Módulos que fazem cada modalidade existir (ADR-0054 item 12, ADR-0058 item 1: à la carte é salão ou entrega). */
const orderTypeModules: Record<OrderType, TenantModule[]> = {
  MARMITA: ['MARMITA'],
  PF: ['PF'],
  KILO: ['KILO'],
  A_LA_CARTE: ['A_LA_CARTE', 'DELIVERY'],
};

/**
 * Linhas "Por modalidade" do resumo, do fechamento e do mensal (06 §6.11): o servidor manda sempre as quatro; o app
 * mostra as dos módulos do estabelecimento e as que tiveram movimento (um fechamento antigo de outro comportamento).
 */
export function visibleOrderTypeRows<T extends { orderType: OrderType; sold: MoneyString; received: MoneyString; count?: number }>(rows: readonly T[], me: Me | null): T[] {
  return rows.filter((r) => hasAnyModule(me, orderTypeModules[r.orderType] ?? []) || (r.count ?? 0) > 0 || toCents(r.sold) !== 0 || toCents(r.received) !== 0);
}

export function ClosingSummaryView({ summary: s }: { summary: ClosingSummary }) {
  const c = useColors();
  const timeZone = useSession((st) => st.me?.tenant.timeZone ?? 'America/Sao_Paulo');
  const me = useSession((st) => st.me);
  const rows = visibleOrderTypeRows(s.byOrderType, me);
  const total = s.byOrderType.reduce(
    (acc, t) => ({ count: acc.count + t.count, sold: acc.sold + toCents(t.sold), received: acc.received + toCents(t.received), pending: acc.pending + toCents(t.pending) }),
    { count: 0, sold: 0, received: 0, pending: 0 },
  );
  const diff = s.cash.cashDifference;

  return (
    <View style={{ gap: space.md }}>
      <Card>
        <Text variant="subtitle">Por modalidade</Text>
        <Row style={{ justifyContent: 'space-between' }}>
          <Text variant="small" muted style={{ flex: 2 }}>
            Tipo
          </Text>
          {['Vendido', 'Recebido', 'Pendente'].map((h) => (
            <Text key={h} variant="small" muted style={{ flex: 2, textAlign: 'right' }}>
              {h}
            </Text>
          ))}
        </Row>
        {rows.map((t) => (
          <Row key={t.orderType} style={{ justifyContent: 'space-between' }}>
            <Text style={{ flex: 2 }}>
              {orderTypeNames[t.orderType]} ({t.count})
            </Text>
            <Text style={{ flex: 2, textAlign: 'right' }}>{formatBRL(t.sold)}</Text>
            <Text style={{ flex: 2, textAlign: 'right' }}>{formatBRL(t.received)}</Text>
            <Text style={{ flex: 2, textAlign: 'right' }}>{formatBRL(t.pending)}</Text>
          </Row>
        ))}
        <Divider />
        <Row style={{ justifyContent: 'space-between' }}>
          <Text style={{ flex: 2, fontWeight: '700' }}>Total ({total.count})</Text>
          <Text style={{ flex: 2, textAlign: 'right', fontWeight: '700' }}>{formatBRL(s.totalSold)}</Text>
          <Text style={{ flex: 2, textAlign: 'right', fontWeight: '700' }}>{formatBRL(s.totalReceived)}</Text>
          <Text style={{ flex: 2, textAlign: 'right', fontWeight: '700' }}>{formatBRL(s.totalPending)}</Text>
        </Row>
        <Text variant="small" muted>
          Inclui {formatBRL(s.deliveryFeesTotal)} de taxas de entrega.
        </Text>
        <SiteSalesLines siteSoldTotal={s.siteSoldTotal} siteDeliveryFeeTotal={s.siteDeliveryFeeTotal} />
      </Card>

      <Card>
        <Text variant="subtitle">Por forma de pagamento</Text>
        {s.byPaymentMethod.map((p) => (
          <KeyValue
            key={p.paymentMethod}
            label={`${paymentMethodNames[p.paymentMethod]} (${p.count})`}
            value={toCents(p.reversed) > 0 ? `${formatBRL(p.received)} · estornos ${formatBRL(p.reversed)}` : formatBRL(p.received)}
          />
        ))}
      </Card>

      <Card>
        <Text variant="subtitle">Caixa físico</Text>
        <KeyValue label="Saldo inicial" value={formatBRL(s.cash.openingBalance)} />
        <KeyValue label="Recebido em dinheiro" value={formatBRL(s.cash.cashReceived)} />
        <KeyValue label="Suprimentos" value={formatBRL(s.cash.supplies)} />
        <KeyValue label="Retiradas" value={`− ${formatBRL(s.cash.withdrawals)}`} />
        <KeyValue label="Estornos em dinheiro" value={`− ${formatBRL(s.cash.cashReversed)}`} />
        <Divider />
        <KeyValue label="Esperado" value={formatBRL(s.cash.expectedCash)} strong />
        {s.cash.countedCash !== null ? <KeyValue label="Contado" value={formatBRL(s.cash.countedCash)} strong /> : null}
        {diff !== null ? <Text style={{ color: toCents(diff) === 0 ? c.success : c.danger, fontWeight: '700' }}>{differenceText(diff)}</Text> : null}
        {s.cash.justification ? <Text muted>Justificativa: {s.cash.justification}</Text> : null}
      </Card>

      <WebOrdersCard summary={s} />

      <Card>
        <KeyValue label="Recebido de dias anteriores" value={formatBRL(s.receivedFromPreviousDays)} />
        <KeyValue label={`Cancelamentos (${s.canceled.count})`} value={formatBRL(s.canceled.total)} />
        <KeyValue label={`Estornos (${s.reversed.count})`} value={formatBRL(s.reversed.total)} />
        <Text variant="small" muted>
          {s.orderCount} pedidos · calculado às {formatDateTime(s.computedAt, timeZone)}
        </Text>
        {s.closedAt && s.closedBy ? (
          <Text variant="small" muted>
            Fechado por {s.closedBy.displayName} em {formatDateTime(s.closedAt, timeZone)}
          </Text>
        ) : null}
      </Card>

      {s.adjustments.length ? (
        <Card>
          <Text variant="subtitle">Ajustes</Text>
          {s.adjustments.map((a) => (
            <View key={a.id}>
              <KeyValue label={a.adjustmentType === 'CASH_COUNT_CORRECTION' ? 'Correção de contagem' : 'Outro'} value={formatBRL(a.amount)} />
              <Text variant="small" muted>
                {a.reason} · {a.createdBy.displayName} · {formatDateTime(a.createdAt, timeZone)}
              </Text>
            </View>
          ))}
        </Card>
      ) : null}
    </View>
  );
}
