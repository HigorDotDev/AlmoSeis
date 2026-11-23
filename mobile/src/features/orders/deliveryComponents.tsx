/**
 * Peças do delivery por telefone (EP-003 fatia 4, ADR-0059): forma prevista e "troco para", taxa digitada na
 * criação, endereço e a linha do troco no card/detalhe. Usadas no `NewDeliveryScreen` e no `NewMarmitaScreen`.
 */
import { Ionicons } from '@expo/vector-icons';
import { router } from 'expo-router';
import { View } from 'react-native';

import type { AcceptanceStatus, Address, CancellationStatus, PaymentMethod } from '@/api/types';
import { Banner, Chip, MoneyInput, Row, Text, TextField, space, useColors } from '@/components/ui';
import { formatBRL, fromCents, toCents, type MoneyString } from '@/lib/money';
import { paymentMethodNames } from '@/lib/strings';
import { useIsOnline } from '@/stores/connectivity';
import { useCanWrite } from '@/stores/session';

import { cashSuggestions } from './compose';
import { changeBelowTotalText, changeToCarryCents, changeToCarryText, expectedPaymentShort, type ChangeInfo, type ExpectedPayment } from './delivery';

const ALL_METHODS: PaymentMethod[] = ['CASH', 'PIX', 'CARD'];

/**
 * Forma prevista (obrigatória na entrega, opcional na retirada, Q-316) e, com dinheiro, "Troco para quanto" com os
 * atalhos de cédula e a prévia do troco que o motoboy leva. Só as formas habilitadas (`['settings']`).
 */
export function ExpectedPaymentFields({
  fulfillmentType,
  value,
  onChange,
  totalCents,
  methods,
  errors,
}: {
  fulfillmentType: 'DELIVERY' | 'PICKUP';
  value: ExpectedPayment;
  onChange: (next: ExpectedPayment) => void;
  totalCents: number;
  methods: readonly PaymentMethod[] | undefined;
  errors?: { method?: string | null; changeFor?: string | null };
}) {
  const c = useColors();
  const enabled = ALL_METHODS.filter((m) => (methods ?? ALL_METHODS).includes(m));
  const optional = fulfillmentType === 'PICKUP';
  const changeCents = value.changeFor ? toCents(value.changeFor) : 0;
  const carry = changeToCarryCents({ ...value, balance: fromCents(totalCents), isPaid: false });
  return (
    <View style={{ gap: space.sm }}>
      <Text style={{ fontWeight: '600' }}>{optional ? 'Forma de pagamento prevista (opcional)' : 'Forma de pagamento prevista'}</Text>
      <Row style={{ flexWrap: 'wrap' }}>
        {optional ? <Chip label="Não informar" selected={value.expectedPaymentMethod === null} onPress={() => onChange({ expectedPaymentMethod: null, changeFor: null })} /> : null}
        {enabled.map((m) => (
          <Chip
            key={m}
            label={paymentMethodNames[m]}
            selected={value.expectedPaymentMethod === m}
            onPress={() => onChange({ expectedPaymentMethod: m, changeFor: m === 'CASH' ? value.changeFor : null })}
          />
        ))}
      </Row>
      {errors?.method ? (
        <Text variant="small" style={{ color: c.danger }}>
          {errors.method}
        </Text>
      ) : null}
      <Text variant="small" muted>
        É só para o entregador: nada fica pago. O pagamento é registrado em “Receber”, quando o dinheiro ou o Pix chegar.
      </Text>
      {value.expectedPaymentMethod === 'CASH' ? (
        <View style={{ gap: space.sm }}>
          <Text muted style={{ fontWeight: '600' }}>
            Troco para quanto?
          </Text>
          <Row style={{ flexWrap: 'wrap' }}>
            <Chip label="Sem troco" selected={changeCents === 0} onPress={() => onChange({ ...value, changeFor: null })} />
            {cashSuggestions(totalCents).map((n) => (
              <Chip key={n} label={formatBRL(n).replace(',00', '')} selected={changeCents === n} onPress={() => onChange({ ...value, changeFor: fromCents(n) })} />
            ))}
          </Row>
          <MoneyInput label="Troco para (opcional)" value={value.changeFor ?? '0.00'} onChange={(v) => onChange({ ...value, changeFor: toCents(v) === 0 ? null : v })} error={errors?.changeFor} />
          {carry !== null && !errors?.changeFor && changeCents >= totalCents ? (
            <Text testID="change-preview" style={{ fontWeight: '700' }}>
              Levar troco: {formatBRL(carry)}
            </Text>
          ) : null}
        </View>
      ) : null}
    </View>
  );
}

/**
 * Taxa de entrega da criação (Q-315): começa com a padrão e quem cria pode digitar outra (auditada no servidor).
 * Igual à padrão vai `null` (`FIXED_DEFAULT`). Depois de registrado, só `orders.delivery-fee.edit` muda.
 */
export function DeliveryFeeField({ value, defaultFee, onChange, error }: { value: MoneyString | null; defaultFee: MoneyString | null | undefined; onChange: (fee: MoneyString | null) => void; error?: string | null }) {
  const standard = defaultFee ?? '0.00';
  const typed = value !== null && toCents(value) !== toCents(standard);
  return (
    <View style={{ gap: space.xs }}>
      <MoneyInput label="Taxa de entrega" value={value ?? standard} onChange={(fee) => onChange(toCents(fee) === toCents(standard) ? null : fee)} error={error} />
      <Text variant="small" muted>
        {typed ? `Taxa digitada (a padrão é ${formatBRL(standard)}). Depois de registrar, só gerente ou dono muda.` : 'Taxa padrão do estabelecimento. Pode digitar outra agora.'}
      </Text>
    </View>
  );
}

/** Rua, número, bairro, complemento e referência (o endereço fica só no pedido, ADR-0059 item 13). */
export function AddressFields({ value, onChange, streetError }: { value: Address; onChange: (a: Address) => void; streetError?: string | null }) {
  const set = (patch: Partial<Address>) => onChange({ ...value, ...patch });
  return (
    <View style={{ gap: space.sm }}>
      <TextField label="Rua" value={value.street} onChangeText={(street) => set({ street })} error={streetError} />
      <Row>
        <View style={{ flex: 1 }}>
          <TextField label="Número" value={value.number ?? ''} onChangeText={(number) => set({ number })} />
        </View>
        <View style={{ flex: 2 }}>
          <TextField label="Bairro" value={value.neighborhood ?? ''} onChangeText={(neighborhood) => set({ neighborhood })} />
        </View>
      </Row>
      <TextField label="Complemento" value={value.complement ?? ''} onChangeText={(complement) => set({ complement })} />
      <TextField label="Referência" value={value.reference ?? ''} onChangeText={(reference) => set({ reference })} />
    </View>
  );
}

/**
 * Forma prevista e troco do card e do detalhe: "Paga com dinheiro · Levar troco para R$ 100,00 → troco R$ 43,00".
 * Não é pagamento: o selo financeiro continua com o saldo.
 */
export function ExpectedPaymentLine({ order: o }: { order: ChangeInfo }) {
  const c = useColors();
  const method = expectedPaymentShort(o.expectedPaymentMethod);
  if (!method) return null;
  const change = changeToCarryText(o);
  return (
    <Row gap={space.xs} style={{ alignItems: 'flex-start' }}>
      <Ionicons name={o.expectedPaymentMethod === 'CASH' ? 'cash-outline' : o.expectedPaymentMethod === 'PIX' ? 'qr-code-outline' : 'card-outline'} size={16} color={c.text} />
      <Text variant="small" testID="expected-payment-line" style={{ fontWeight: '600', flexShrink: 1 }}>
        {change ? `${method} · ${change}` : method}
      </Text>
    </Row>
  );
}

/**
 * "Troco para" abaixo do total (ADR-0059 complementado em 2026-11-23, I2), no card (abas Delivery e Marmitas) e no
 * detalhe: alerta para o atendente conferir com o cliente, no lugar de um troco negativo ou zerado, com o atalho
 * "Editar troco" (`orders/[id]/edit`) para quem pode editar o pedido.
 */
export function ChangeBelowTotalAlert({
  order: o,
}: {
  order: ChangeInfo & { id: string; totalAmount: MoneyString; cancellationStatus: CancellationStatus; acceptanceStatus: AcceptanceStatus };
}) {
  const canEdit = useCanWrite('orders.edit');
  const online = useIsOnline();
  const text = o.cancellationStatus === 'ACTIVE' ? changeBelowTotalText(o) : null;
  if (!text) return null;
  const editable = canEdit && online && o.acceptanceStatus === 'ACCEPTED';
  return (
    <View testID="change-below-total">
      <Banner tone="warning" text={text} action={editable ? { label: 'Editar troco', onPress: () => router.push(`/orders/${o.id}/edit`) } : undefined} />
    </View>
  );
}
