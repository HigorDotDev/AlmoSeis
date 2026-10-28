import { Stack, useLocalSearchParams } from 'expo-router';
import { useMemo, useState } from 'react';
import { Pressable, View } from 'react-native';

import type { Order, Payment, PaymentMethod } from '@/api/types';
import {
  Banner,
  Button,
  Card,
  Chip,
  ConfirmDialog,
  ConflictSheet,
  KeyValue,
  MoneyInput,
  QuantityStepper,
  Row,
  Screen,
  Section,
  SegmentedControl,
  Skeleton,
  SyncStatePill,
  Text,
  TextField,
  radius,
  space,
  useColors,
} from '@/components/ui';
import { useCurrentBusinessDay } from '@/features/businessDay/hooks';
import { cashSuggestions } from '@/features/orders/compose';
import { summarizeOrder, useOrder } from '@/features/orders/hooks';
import { useSettings } from '@/features/session/hooks';
import { formatBusinessDate } from '@/lib/dates';
import { formatBRL, fromCents, splitEvenly, toCents } from '@/lib/money';
import { paymentMethodNames } from '@/lib/strings';
import { useIsOnline } from '@/stores/connectivity';
import { useBlockedBy } from '@/stores/pendingOps';
import { useCanWrite } from '@/stores/session';

import { useOrderPayments, useRegisterPayment, useReversePayment } from '../hooks';

type SplitMode = 'NONE' | 'EQUAL' | 'ITEMS';

/** 08 §8.9 `orders/[id]/pay`. Cada parcela é um pagamento separado, com a versão devolvida pelo anterior. */
export default function PayScreen() {
  const { id } = useLocalSearchParams<{ id: string }>();
  const order = useOrder(id);
  const o = order.data;
  if (!o) return <View style={{ padding: space.lg }}><Skeleton lines={4} /></View>;
  return <PayForm order={o} />;
}

function PayForm({ order: o }: { order: Order }) {
  const c = useColors();
  const online = useIsOnline();
  const settings = useSettings();
  const day = useCurrentBusinessDay();
  const canPay = useCanWrite('payments.register');
  const blocked = useBlockedBy(`/orders/${o.id}/payments`);
  const methods = settings.data?.enabledPaymentMethods ?? ['CASH', 'PIX', 'CARD'];
  const balanceCents = toCents(o.balance);

  const [method, setMethod] = useState<PaymentMethod>(methods[0] ?? 'CASH');
  const [typedAmount, setAmount] = useState<string | null>(null);
  const [received, setReceived] = useState<string | null>(null);
  const [split, setSplit] = useState<SplitMode>('NONE');
  const [people, setPeople] = useState(2);
  const [selectedItems, setSelectedItems] = useState<string[]>([]);
  const [parcelIndex, setParcelIndex] = useState(0);
  const [confirm, setConfirm] = useState(false);
  const [conflict, setConflict] = useState<Order | null>(null);

  // Parcelas (RN-009): iguais com o resto nos últimos centavos, ou pela soma dos itens marcados.
  const parcels = useMemo(() => {
    if (split === 'EQUAL') return splitEvenly(balanceCents, people);
    if (split === 'ITEMS') {
      const sum = o.items.filter((i) => selectedItems.includes(i.id)).reduce((s, i) => s + toCents(i.lineTotal) + i.addons.reduce((a, x) => a + toCents(x.lineTotal), 0), 0);
      return sum > 0 ? [Math.min(sum, balanceCents)] : [];
    }
    return [];
  }, [split, people, selectedItems, balanceCents, o.items]);

  // Valor sugerido: o saldo ou a parcela da vez; o que for digitado vale até mudar a divisão.
  const suggested = split === 'NONE' ? o.balance : parcels[parcelIndex] !== undefined ? fromCents(parcels[parcelIndex]!) : '0.00';
  const amount = typedAmount ?? suggested;

  const register = useRegisterPayment(o, () => {
    setConfirm(false);
    setReceived(null);
    setAmount(null);
    if (split === 'EQUAL' && parcelIndex + 1 < parcels.length) setParcelIndex(parcelIndex + 1);
    else if (split !== 'NONE') {
      setSplit('NONE');
      setParcelIndex(0);
      setSelectedItems([]);
    }
  });

  const amountCents = toCents(amount);
  const changeCents = method === 'CASH' && received ? toCents(received) - amountCents : 0;
  const otherDay = day.data && o.businessDate !== day.data.businessDate;
  const label = split === 'EQUAL' ? `Pessoa ${parcelIndex + 1}` : split === 'ITEMS' ? 'Por itens' : null;
  const invalid = amountCents <= 0 || amountCents > balanceCents || (method === 'CASH' && received !== null && toCents(received) > 0 && toCents(received) < amountCents);

  const submit = async () => {
    const outcome = await register.run({
      body: { amount, paymentMethod: method, cashReceived: method === 'CASH' && received && toCents(received) > 0 ? received : null, label },
      version: o.version,
    });
    if (outcome.kind === 'conflict') {
      setConfirm(false);
      setConflict(outcome.current as Order);
    } else if (outcome.kind === 'rejected') setConfirm(false);
  };

  return (
    <Screen>
      <Stack.Screen options={{ title: `Receber · #${o.orderNumber}` }} />
      <Card>
        <Text muted>Saldo</Text>
        <Text variant="display">{formatBRL(o.balance)}</Text>
        <Text variant="small" muted>
          Total {formatBRL(o.totalAmount)} · pago {formatBRL(o.paidAmount)}
        </Text>
      </Card>

      {o.isPaid ? <Banner tone="success" text="Pedido quitado." /> : null}
      {blocked ? (
        <Card>
          <SyncStatePill state="unknown" />
          <Text>Há um recebimento deste pedido com resultado desconhecido. Aguarde a verificação antes de receber de novo.</Text>
        </Card>
      ) : null}

      {!o.isPaid && o.cancellationStatus === 'ACTIVE' ? (
        <>
          <Section title="Forma de pagamento">
            <Row>
              {methods.map((m) => (
                <Pressable
                  key={m}
                  accessibilityRole="radio"
                  accessibilityState={{ selected: method === m }}
                  onPress={() => setMethod(m)}
                  style={{ flex: 1, minHeight: 64, borderRadius: radius.md, borderWidth: 2, borderColor: method === m ? c.primary : c.border, backgroundColor: method === m ? c.primaryBg : c.surface, alignItems: 'center', justifyContent: 'center' }}
                >
                  <Text style={{ fontWeight: '700', color: method === m ? c.primary : c.text }}>{paymentMethodNames[m]}</Text>
                </Pressable>
              ))}
            </Row>
          </Section>

          <Section title="Dividir conta">
            <SegmentedControl
              value={split}
              onChange={(s) => {
                setSplit(s);
                setParcelIndex(0);
                setAmount(null);
              }}
              options={[
                { value: 'NONE', label: 'Não dividir' },
                { value: 'EQUAL', label: 'Igual' },
                { value: 'ITEMS', label: 'Por itens' },
              ]}
            />
            {split === 'EQUAL' ? (
              <View style={{ gap: space.sm }}>
                <Row style={{ justifyContent: 'space-between' }}>
                  <Text>Pessoas</Text>
                  <QuantityStepper label="Pessoas" min={2} max={20} value={people} onChange={(n) => { setPeople(n); setParcelIndex(0); setAmount(null); }} />
                </Row>
                <Text muted>
                  {parcels.map((p, i) => `Pessoa ${i + 1}: ${formatBRL(p)}${i < parcelIndex ? ' ✓' : ''}`).join(' · ')}
                </Text>
              </View>
            ) : null}
            {split === 'ITEMS' ? (
              <Row style={{ flexWrap: 'wrap' }}>
                {o.items.map((i) => (
                  <Chip
                    key={i.id}
                    label={`${i.quantity}× ${i.name} · ${formatBRL(i.lineTotal)}`}
                    selected={selectedItems.includes(i.id)}
                    onPress={() => { setAmount(null); setSelectedItems((s) => (s.includes(i.id) ? s.filter((x) => x !== i.id) : [...s, i.id])); }}
                  />
                ))}
              </Row>
            ) : null}
          </Section>

          <MoneyInput label={label ? `Valor (${label})` : 'Valor'} value={amount} onChange={setAmount} error={amountCents > balanceCents ? `O valor passa do saldo (${formatBRL(o.balance)}).` : null} />
          {method === 'CASH' ? (
            <>
              {/* Atalhos de cédulas e troco em destaque (docs/10-ux.md D11). */}
              <Text muted style={{ fontWeight: '600' }}>
                Cliente deu
              </Text>
              <Row style={{ flexWrap: 'wrap' }}>
                <Chip label="Exato" selected={received !== null && toCents(received) === amountCents} onPress={() => setReceived(amount)} />
                {cashSuggestions(amountCents).map((n) => (
                  <Chip key={n} label={formatBRL(n).replace(',00', '')} selected={received !== null && toCents(received) === n} onPress={() => setReceived(fromCents(n))} />
                ))}
              </Row>
              <MoneyInput label="Recebido em dinheiro (opcional)" value={received ?? '0.00'} onChange={setReceived} />
              {changeCents > 0 ? (
                <View style={{ flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', padding: space.lg, borderRadius: radius.md, backgroundColor: c.successBg }}>
                  <Text variant="subtitle" style={{ color: c.success }}>
                    Troco
                  </Text>
                  <Text variant="big" style={{ color: c.success }}>
                    {formatBRL(changeCents)}
                  </Text>
                </View>
              ) : null}
              {received && toCents(received) > 0 && changeCents < 0 ? <Text style={{ color: c.danger }}>O recebido é menor que o valor.</Text> : null}
            </>
          ) : null}
          {otherDay ? <Banner tone="info" text={`Pedido de ${formatBusinessDate(o.businessDate)}. O recebimento entra no caixa de hoje.`} /> : null}
          {register.error ? <Banner tone="danger" text={register.error} /> : null}
          {register.state === 'unknown' ? <Banner tone="warning" text="Não sabemos se o pagamento foi registrado. Verificando…" /> : null}
          <Button
            title={label ? `Receber ${label}` : 'Confirmar recebimento'}
            icon="cash-outline"
            disabled={!canPay || !online || invalid || !!blocked || register.state === 'unknown'}
            onPress={() => setConfirm(true)}
          />
        </>
      ) : null}

      <PaymentsList order={o} />

      <ConfirmDialog
        visible={confirm}
        title="Confirmar recebimento?"
        description={`Receber ${formatBRL(amount)} em ${paymentMethodNames[method].toLowerCase()} do pedido #${o.orderNumber}?${changeCents > 0 ? ` Troco ${formatBRL(changeCents)}.` : ''}`}
        lines={[
          { label: 'Valor', value: formatBRL(amount) },
          { label: 'Forma', value: paymentMethodNames[method] },
          ...(changeCents > 0 ? [{ label: 'Troco', value: formatBRL(changeCents) }] : []),
          { label: 'Saldo depois', value: formatBRL(balanceCents - amountCents) },
        ]}
        warning={otherDay ? `Pedido de ${formatBusinessDate(o.businessDate)}. O recebimento entra no caixa de hoje.` : null}
        confirmLabel={`Receber ${formatBRL(amount)}`}
        loading={register.busy}
        onCancel={() => setConfirm(false)}
        onConfirm={() => void submit()}
      />
      <ConflictSheet visible={conflict !== null} summary={conflict ? summarizeOrder(conflict) : []} onClose={() => setConflict(null)} onRedo={() => setConflict(null)} />
    </Screen>
  );
}

function PaymentsList({ order: o }: { order: Order }) {
  const c = useColors();
  const payments = useOrderPayments(o.id);
  const canReverse = useCanWrite('payments.reverse');
  const online = useIsOnline();
  const [target, setTarget] = useState<Payment | null>(null);
  const [reason, setReason] = useState('');
  const reverse = useReversePayment(o);
  const blocked = useBlockedBy(target ? `/payments/${target.id}` : null);

  if (!payments.data?.length) return null;
  return (
    <Section title="Pagamentos">
      {payments.data.map((p) => {
        const reversed = p.status === 'REVERSED';
        return (
          <Card key={p.id}>
            <Row style={{ justifyContent: 'space-between' }}>
              <Text style={reversed ? { textDecorationLine: 'line-through', color: c.textMuted } : { fontWeight: '700' }}>
                {formatBRL(p.amount)} · {paymentMethodNames[p.paymentMethod]}
                {p.label ? ` · ${p.label}` : ''}
              </Text>
              <Text variant="small" muted>
                {formatBusinessDate(p.businessDate)} · {p.createdBy.displayName}
              </Text>
            </Row>
            {p.changeGiven && toCents(p.changeGiven) > 0 ? <KeyValue label="Troco" value={formatBRL(p.changeGiven)} /> : null}
            {p.reversal ? <Text variant="small" style={{ color: c.danger }}>Estornado por {p.reversal.createdBy.displayName}: {p.reversal.reason}</Text> : null}
            {!reversed && canReverse ? <Button title="Estornar" variant="secondary" disabled={!online} onPress={() => { setReason(''); setTarget(p); }} /> : null}
          </Card>
        );
      })}
      <ConfirmDialog
        visible={target !== null}
        title="Estornar pagamento?"
        lines={target ? [{ label: 'Valor', value: formatBRL(target.amount) }, { label: 'Forma', value: paymentMethodNames[target.paymentMethod] }] : []}
        description="O estorno entra no caixa do expediente aberto e pode destravar o pedido para edição ou cancelamento."
        warning={reverse.state === 'unknown' ? 'Não sabemos se o estorno foi registrado. Verificando…' : reverse.error}
        confirmLabel={target ? `Estornar ${formatBRL(target.amount)}` : 'Estornar'}
        danger
        loading={reverse.busy || !!blocked}
        onCancel={() => setTarget(null)}
        onConfirm={async () => {
          if (!target || reason.trim().length < 3) return;
          const outcome = await reverse.run({ paymentId: target.id, reason: reason.trim(), amount: target.amount, version: o.version });
          if (outcome.kind === 'confirmed' || outcome.kind === 'rejected') setTarget(null);
        }}
      >
        <TextField label="Motivo (obrigatório)" value={reason} onChangeText={setReason} placeholder="Ex.: cobrado em duplicidade" />
      </ConfirmDialog>
    </Section>
  );
}
