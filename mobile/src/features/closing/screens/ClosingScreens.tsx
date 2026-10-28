/** Telas de caixa (08 §8.9): movimentação, fechar expediente, histórico, detalhe e mensal. */
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useMemo, useState } from 'react';
import { View } from 'react-native';

import { errorMessage } from '@/api/errors';
import type { CashMovementType, ClosingSummary } from '@/api/types';
import {
  Banner,
  Button,
  Card,
  ConfirmDialog,
  EmptyState,
  ErrorState,
  KeyValue,
  MoneyInput,
  QuantityStepper,
  Row,
  Screen,
  SegmentedControl,
  Skeleton,
  Text,
  TextField,
  space,
  useColors,
} from '@/components/ui';
import { useCurrentBusinessDay } from '@/features/businessDay/hooks';
import { useOrders } from '@/features/orders/hooks';
import { formatBusinessDate, formatMonth } from '@/lib/dates';
import { formatBRL, fromCents, toCents } from '@/lib/money';
import { orderTypeNames, paymentMethodNames } from '@/lib/strings';
import { useIsOnline } from '@/stores/connectivity';
import { useCanWrite } from '@/stores/session';

import { ClosingSummaryView, differenceText } from '../components';
import { useAddAdjustment, useAddCashMovement, useCashMovements, useClosing, useClosingHistory, useClosingSummary, useFinalizeClosing, useMonthlyClosing } from '../hooks';

export function CashMovementScreen() {
  const online = useIsOnline();
  const canMove = useCanWrite('cash.movement');
  const [type, setType] = useState<CashMovementType>('WITHDRAWAL');
  const [amount, setAmount] = useState('0.00');
  const [description, setDescription] = useState('');
  const [confirm, setConfirm] = useState(false);
  const movements = useCashMovements();
  const add = useAddCashMovement(() => {
    setConfirm(false);
    router.back();
  });
  const name = type === 'SUPPLY' ? 'Suprimento' : 'Retirada';
  const valid = toCents(amount) > 0 && description.trim().length >= 3;

  return (
    <Screen>
      <Stack.Screen options={{ title: 'Suprimento ou retirada' }} />
      <SegmentedControl
        value={type}
        onChange={setType}
        options={[
          { value: 'WITHDRAWAL', label: 'Retirada (sangria)' },
          { value: 'SUPPLY', label: 'Suprimento' },
        ]}
      />
      <MoneyInput label="Valor" value={amount} onChange={setAmount} autoFocus />
      <TextField label="Motivo (obrigatório)" value={description} onChangeText={setDescription} placeholder={type === 'SUPPLY' ? 'Ex.: troco trazido do cofre' : 'Ex.: sangria para depósito'} />
      {add.error ? <Banner tone="danger" text={add.error} /> : null}
      {add.state === 'unknown' ? <Banner tone="warning" text="Não sabemos se a movimentação foi registrada. Verificando…" /> : null}
      <Button title={`Registrar ${name.toLowerCase()}`} disabled={!valid || !online || !canMove || add.state === 'unknown'} onPress={() => setConfirm(true)} />
      {movements.data?.length ? (
        <View style={{ gap: space.sm }}>
          <Text variant="subtitle">Movimentações de hoje</Text>
          {movements.data.map((m) => (
            <KeyValue key={m.id} label={`${m.movementType === 'SUPPLY' ? 'Suprimento' : 'Retirada'} · ${m.description}`} value={formatBRL(m.amount)} />
          ))}
        </View>
      ) : null}
      <ConfirmDialog
        visible={confirm}
        title={`Registrar ${name.toLowerCase()}?`}
        lines={[
          { label: name, value: formatBRL(amount) },
          { label: 'Motivo', value: description.trim() },
        ]}
        confirmLabel={`Registrar ${formatBRL(amount)}`}
        loading={add.busy}
        onCancel={() => setConfirm(false)}
        onConfirm={() => void add.run({ movementType: type, amount, description: description.trim() })}
      />
    </Screen>
  );
}

const NOTES = [20000, 10000, 5000, 2000, 1000, 500, 200, 100, 50, 25, 10, 5];

/** Calculadora opcional de cédulas e moedas (08 §8.9). */
function CashCounter({ onTotal }: { onTotal: (cents: number) => void }) {
  const [counts, setCounts] = useState<Record<number, number>>({});
  const total = NOTES.reduce((s, n) => s + n * (counts[n] ?? 0), 0);
  return (
    <Card>
      {NOTES.map((n) => (
        <Row key={n} style={{ justifyContent: 'space-between' }}>
          <Text>{formatBRL(n)}</Text>
          <QuantityStepper label={formatBRL(n)} max={999} value={counts[n] ?? 0} onChange={(q) => setCounts({ ...counts, [n]: q })} />
        </Row>
      ))}
      <KeyValue label="Soma" value={formatBRL(total)} strong />
      <Button title="Usar esta soma" variant="secondary" onPress={() => onTotal(total)} />
    </Card>
  );
}

export function FinalizeScreen() {
  const c = useColors();
  const online = useIsOnline();
  const day = useCurrentBusinessDay();
  const summary = useClosingSummary(undefined, !!day.data);
  const pendingOrders = useOrders({ isPaid: false, cancellationStatus: 'ACTIVE' }, !!day.data);
  const [step, setStep] = useState<1 | 2>(1);
  const [counted, setCounted] = useState('0.00');
  const [justification, setJustification] = useState('');
  const [showCounter, setShowCounter] = useState(false);
  const [confirm, setConfirm] = useState(false);
  const [conflictMsg, setConflictMsg] = useState(false);

  const finalize = useFinalizeClosing(
    (closing: ClosingSummary) => {
      setConfirm(false);
      router.replace(`/closing/${closing.dailyClosingId}?justClosed=1`);
    },
    () => {
      setConfirm(false);
      setConflictMsg(true);
      setStep(1);
    },
  );

  if (day.isLoading || summary.isLoading) return <View style={{ padding: space.lg }}><Skeleton lines={4} /></View>;
  if (!day.data) return <EmptyState title="Nenhum expediente aberto" />;
  if (!summary.data) return <ErrorState message={errorMessage(summary.error)} onRetry={() => void summary.refetch()} />;
  const s = summary.data;
  const diffCents = toCents(counted) - toCents(s.cash.expectedCash);
  const needsJustification = diffCents !== 0;
  const pending = pendingOrders.data?.pages.flatMap((p) => p.items) ?? [];

  return (
    <Screen>
      <Stack.Screen options={{ title: `Fechar ${formatBusinessDate(day.data.businessDate)}` }} />
      {conflictMsg ? <Banner tone="warning" text="Houve movimentação enquanto você conferia. O resumo foi recarregado; confira de novo." /> : null}
      {step === 1 ? (
        <>
          <Text variant="subtitle">Passo 1 de 2 · Conferir</Text>
          {toCents(s.totalPending) > 0 ? (
            <Banner tone="warning" text={`${pending.length || 'Alguns'} ${pending.length === 1 ? 'pedido ficará pendente' : 'pedidos ficarão pendentes'}: ${formatBRL(s.totalPending)}.`} />
          ) : null}
          <ClosingSummaryView summary={s} />
          <Button title="Conferido, contar o dinheiro" onPress={() => { setConflictMsg(false); setStep(2); }} />
        </>
      ) : (
        <>
          <Text variant="subtitle">Passo 2 de 2 · Contar o dinheiro</Text>
          <KeyValue label="Esperado no caixa" value={formatBRL(s.cash.expectedCash)} strong />
          <MoneyInput label="Quanto há no caixa?" value={counted} onChange={setCounted} autoFocus />
          <Button title={showCounter ? 'Esconder calculadora' : 'Contar cédulas e moedas'} variant="ghost" onPress={() => setShowCounter(!showCounter)} />
          {showCounter ? <CashCounter onTotal={(cents) => { setCounted(fromCents(cents)); setShowCounter(false); }} /> : null}
          <Text variant="subtitle" style={{ color: diffCents === 0 ? c.success : c.danger }}>
            {differenceText(fromCents(diffCents))}
          </Text>
          {needsJustification ? (
            <TextField label="Justificativa (obrigatória com diferença)" value={justification} onChangeText={setJustification} multiline error={justification.trim().length > 0 && justification.trim().length < 5 ? 'Pelo menos 5 caracteres.' : null} />
          ) : null}
          {finalize.error ? <Banner tone="danger" text={finalize.error} /> : null}
          {finalize.state === 'unknown' ? <Banner tone="warning" text="Não sabemos se o expediente foi fechado. Verificando…" /> : null}
          <Button
            title="Fechar expediente"
            icon="lock-closed-outline"
            disabled={!online || (needsJustification && justification.trim().length < 5) || finalize.state === 'unknown'}
            onPress={() => setConfirm(true)}
          />
          <Button title="Voltar para a conferência" variant="ghost" onPress={() => setStep(1)} />
        </>
      )}
      <ConfirmDialog
        visible={confirm}
        title="Fechar expediente?"
        description="Depois de fechado, o expediente não muda. Correções viram ajustes (ADR-0014)."
        lines={[
          { label: 'Total vendido', value: formatBRL(s.totalSold) },
          { label: 'Esperado no caixa', value: formatBRL(s.cash.expectedCash) },
          { label: 'Contado', value: formatBRL(counted) },
          { label: 'Diferença', value: formatBRL(diffCents) },
        ]}
        confirmLabel="Fechar expediente"
        loading={finalize.busy}
        onCancel={() => setConfirm(false)}
        onConfirm={() =>
          void finalize.run({ businessDayId: s.businessDay.id, countedCash: counted, justification: needsJustification ? justification.trim() : null, version: s.businessDay.version })
        }
      />
    </Screen>
  );
}

export function ClosingHistoryScreen() {
  const c = useColors();
  const history = useClosingHistory({});
  const items = history.data?.pages.flatMap((p) => p.items) ?? [];
  return (
    <Screen>
      <Stack.Screen options={{ title: 'Histórico de fechamentos' }} />
      <Button title="Visão mensal" variant="secondary" icon="calendar-outline" onPress={() => router.push('/closing/monthly')} />
      {history.isLoading ? <Skeleton /> : null}
      {history.isError ? <ErrorState message={errorMessage(history.error)} onRetry={() => void history.refetch()} /> : null}
      {!history.isLoading && items.length === 0 ? <EmptyState title="Nenhum fechamento ainda" /> : null}
      {items.map((h) => {
        const diff = toCents(h.cashDifference);
        return (
          <Card key={h.dailyClosingId} onPress={() => router.push(`/closing/${h.dailyClosingId}`)}>
            <Row style={{ justifyContent: 'space-between' }}>
              <Text style={{ fontWeight: '700' }}>{formatBusinessDate(h.businessDate, true)}</Text>
              <Text>{formatBRL(h.totalSold)}</Text>
            </Row>
            <Text variant="small" style={{ color: diff === 0 ? c.textMuted : c.danger }}>
              Diferença de caixa: {formatBRL(h.cashDifference)}
              {h.adjustmentCount ? ` · ${h.adjustmentCount} ajuste(s)` : ''}
            </Text>
          </Card>
        );
      })}
      {history.hasNextPage ? <Button title="Carregar mais" variant="secondary" loading={history.isFetchingNextPage} onPress={() => void history.fetchNextPage()} /> : null}
    </Screen>
  );
}

export function ClosingDetailScreen() {
  const { id, justClosed } = useLocalSearchParams<{ id: string; justClosed?: string }>();
  const closing = useClosing(id);
  const canAdjust = useCanWrite('closing.adjust');
  const canOpen = useCanWrite('cash.open');
  const [adjusting, setAdjusting] = useState(false);
  if (closing.isLoading) return <View style={{ padding: space.lg }}><Skeleton lines={5} /></View>;
  if (!closing.data) return <ErrorState message={errorMessage(closing.error)} onRetry={() => void closing.refetch()} />;
  return (
    <Screen>
      <Stack.Screen options={{ title: `Fechamento ${formatBusinessDate(closing.data.businessDay.businessDate)}` }} />
      {justClosed ? <Banner tone="success" text="Expediente fechado." /> : null}
      <ClosingSummaryView summary={closing.data} />
      {justClosed && canOpen ? <Button title="Abrir próximo expediente" onPress={() => router.replace('/business-day/open')} /> : null}
      {canAdjust ? <Button title="Registrar ajuste" variant="secondary" onPress={() => setAdjusting(true)} /> : null}
      {adjusting ? <AdjustmentForm closingId={id} onDone={() => setAdjusting(false)} /> : null}
    </Screen>
  );
}

function AdjustmentForm({ closingId, onDone }: { closingId: string; onDone: () => void }) {
  const online = useIsOnline();
  const [type, setType] = useState<'CASH_COUNT_CORRECTION' | 'OTHER'>('CASH_COUNT_CORRECTION');
  const [sign, setSign] = useState<'+' | '-'>('+');
  const [amount, setAmount] = useState('0.00');
  const [reason, setReason] = useState('');
  const [confirm, setConfirm] = useState(false);
  const add = useAddAdjustment(closingId, () => {
    setConfirm(false);
    onDone();
  });
  const signed = sign === '-' ? fromCents(-toCents(amount)) : amount;
  return (
    <Card>
      <Text variant="subtitle">Ajuste (o fechamento original não muda)</Text>
      <SegmentedControl
        value={type}
        onChange={setType}
        options={[
          { value: 'CASH_COUNT_CORRECTION', label: 'Correção de contagem' },
          { value: 'OTHER', label: 'Outro' },
        ]}
      />
      <SegmentedControl
        value={sign}
        onChange={setSign}
        options={[
          { value: '+', label: 'A mais (+)' },
          { value: '-', label: 'A menos (−)' },
        ]}
      />
      <MoneyInput label="Valor" value={amount} onChange={setAmount} />
      <TextField label="Motivo" value={reason} onChangeText={setReason} multiline />
      {add.error ? <Banner tone="danger" text={add.error} /> : null}
      <Button title="Registrar ajuste" disabled={!online || toCents(amount) === 0 || reason.trim().length < 5} onPress={() => setConfirm(true)} />
      <ConfirmDialog
        visible={confirm}
        title="Registrar ajuste?"
        lines={[
          { label: 'Valor', value: formatBRL(signed) },
          { label: 'Motivo', value: reason.trim() },
        ]}
        confirmLabel={`Registrar ${formatBRL(signed)}`}
        loading={add.busy}
        onCancel={() => setConfirm(false)}
        onConfirm={() => void add.run({ adjustmentType: type, amount: signed, reason: reason.trim() })}
      />
    </Card>
  );
}

function shiftMonth(month: string, delta: number): string {
  const [y, m] = month.split('-').map(Number) as [number, number];
  const d = new Date(Date.UTC(y, m - 1 + delta, 1));
  return d.toISOString().slice(0, 7);
}

export function MonthlyScreen() {
  const [month, setMonth] = useState(() => new Date().toISOString().slice(0, 7));
  const monthly = useMonthlyClosing(month);
  const m = monthly.data;
  const days = useMemo(() => m?.days ?? [], [m]);
  return (
    <Screen>
      <Stack.Screen options={{ title: 'Fechamento mensal' }} />
      <Row style={{ justifyContent: 'space-between' }}>
        <Button title="‹" variant="secondary" onPress={() => setMonth(shiftMonth(month, -1))} />
        <Text variant="subtitle">{formatMonth(month)}</Text>
        <Button title="›" variant="secondary" onPress={() => setMonth(shiftMonth(month, 1))} />
      </Row>
      {monthly.isLoading ? <Skeleton lines={3} /> : null}
      {m ? (
        <>
          <Card>
            <KeyValue label={`Fechamentos`} value={String(m.closingCount)} />
            <KeyValue label="Vendido" value={formatBRL(m.totalSold)} strong />
            <KeyValue label="Recebido" value={formatBRL(m.totalReceived)} />
            <KeyValue label="Pendente nos fechamentos" value={formatBRL(m.totalPendingAtClosings)} />
            <KeyValue label="Diferenças de caixa" value={formatBRL(m.cashDifferenceTotal)} />
          </Card>
          <Card>
            {m.byOrderType.map((t) => (
              <KeyValue key={t.orderType} label={orderTypeNames[t.orderType]} value={`${formatBRL(t.sold)} · rec. ${formatBRL(t.received)}`} />
            ))}
            {m.byPaymentMethod.map((p) => (
              <KeyValue key={p.paymentMethod} label={paymentMethodNames[p.paymentMethod]} value={formatBRL(p.received)} />
            ))}
          </Card>
          {days.map((d) => (
            <Card key={d.businessDate} onPress={d.dailyClosingIds[0] ? () => router.push(`/closing/${d.dailyClosingIds[0]}`) : undefined}>
              <Row style={{ justifyContent: 'space-between' }}>
                <Text style={{ fontWeight: '700' }}>{formatBusinessDate(d.businessDate)}</Text>
                <Text>{formatBRL(d.totalSold)}</Text>
              </Row>
            </Card>
          ))}
        </>
      ) : null}
    </Screen>
  );
}
