/**
 * Delivery por telefone (EP-003 fatia 4, ADR-0059, 06 §6.9 `POST /orders`), sem React: forma prevista e "troco
 * para", o formulário do pedido à la carte de entrega/retirada, a validação antes do Confirma e o corpo.
 *
 * A forma prevista é só informação para o entregador: **não** cria pagamento nem marca nada como pago (ADR-0059
 * item 8). A conferência do "troco para" usa a prévia do app; quem decide é o servidor (`422` em `changeFor`).
 */
import type { Address, CreateOrderBody, PaymentMethod } from '@/api/types';
import { formatBRL, toCents, type MoneyString } from '@/lib/money';
import { toE164 } from '@/lib/phone';
import { paymentMethodNames } from '@/lib/strings';

import { lineItem, lineLabel, lineMissing, previewALaCarteCents, type ALaCarteLine, type FixedMenuBook } from './aLaCarte';

// ---------------------------------------------------------------- Forma prevista e troco (marmita e à la carte)

export interface ExpectedPayment {
  expectedPaymentMethod: PaymentMethod | null;
  /** "Troco para quanto": só com dinheiro; `null` (ou zero) = sem troco. */
  changeFor: MoneyString | null;
}

const hasValue = (m: MoneyString | null | undefined): m is MoneyString => !!m && toCents(m) > 0;

/** Q-316: obrigatória na entrega, opcional na retirada. Fora das formas habilitadas → o servidor recusa. */
export function expectedPaymentIssue(fulfillmentType: 'DELIVERY' | 'PICKUP', p: ExpectedPayment, enabled?: readonly PaymentMethod[]): string | null {
  if (p.expectedPaymentMethod === null) return fulfillmentType === 'DELIVERY' ? 'Escolha a forma de pagamento prevista da entrega.' : null;
  if (enabled && !enabled.includes(p.expectedPaymentMethod)) return `${paymentMethodNames[p.expectedPaymentMethod]} está desabilitado. Escolha outra forma.`;
  return null;
}

/** "Troco para" de pelo menos o total (06 §6.9). Igual ao total vale (o motoboy não leva troco). */
export function changeForIssue(p: ExpectedPayment, totalCents: number): string | null {
  if (p.expectedPaymentMethod !== 'CASH' || !hasValue(p.changeFor)) return null;
  return toCents(p.changeFor) < totalCents ? `O troco precisa ser para pelo menos ${formatBRL(totalCents)} (o total).` : null;
}

/** Os dois campos do corpo: `changeFor` só com `CASH` e valor; zero vira `null`. */
export function expectedPaymentBody(p: ExpectedPayment): { expectedPaymentMethod: PaymentMethod | null; changeFor: MoneyString | null } {
  const method = p.expectedPaymentMethod;
  return { expectedPaymentMethod: method, changeFor: method === 'CASH' && hasValue(p.changeFor) ? p.changeFor : null };
}

/** Troco que o motoboy leva: `changeFor − balance` (ADR-0059 item 12). `null` sem troco ou com o pedido quitado. */
export function changeToCarryCents(o: { expectedPaymentMethod?: PaymentMethod | null; changeFor?: MoneyString | null; balance: MoneyString; isPaid: boolean }): number | null {
  if (o.expectedPaymentMethod !== 'CASH' || !hasValue(o.changeFor) || o.isPaid) return null;
  return Math.max(0, toCents(o.changeFor) - toCents(o.balance));
}

/** "Levar troco para R$ 100,00 → troco R$ 43,00" (card do delivery e detalhe). */
export function changeToCarryText(o: { expectedPaymentMethod?: PaymentMethod | null; changeFor?: MoneyString | null; balance: MoneyString; isPaid: boolean }): string | null {
  const carry = changeToCarryCents(o);
  if (carry === null) return null;
  return `Levar troco para ${formatBRL(o.changeFor!)} → troco ${formatBRL(carry)}`;
}

/** Linha curta da forma prevista para o card: "Dinheiro", "Pix", "Cartão" (com "prevista"). */
export function expectedPaymentShort(method: PaymentMethod | null | undefined): string | null {
  return method ? `Paga com ${paymentMethodNames[method].toLowerCase()}` : null;
}

// ---------------------------------------------------------------- Pedido à la carte por telefone

export const emptyAddress = (): Address => ({ street: '', number: null, complement: null, neighborhood: null, reference: null });

export interface DeliveryForm extends ExpectedPayment {
  /** Máscara BR enquanto digita; vai em E.164. */
  phone: string;
  name: string;
  fulfillmentType: 'DELIVERY' | 'PICKUP';
  address: Address;
  /** `null` = taxa padrão; quem cria pode digitar outra (Q-315). */
  deliveryFee: MoneyString | null;
  lines: ALaCarteLine[];
  notes: string;
}

export const emptyDeliveryForm = (): DeliveryForm => ({
  phone: '',
  name: '',
  fulfillmentType: 'DELIVERY',
  address: emptyAddress(),
  deliveryFee: null,
  expectedPaymentMethod: null,
  changeFor: null,
  lines: [],
  notes: '',
});

export function deliveryFeeCents(form: Pick<DeliveryForm, 'fulfillmentType' | 'deliveryFee'>, defaultFee: MoneyString | null): number {
  return form.fulfillmentType === 'DELIVERY' ? toCents(form.deliveryFee ?? defaultFee ?? '0.00') : 0;
}

/** Prévia: itens pelo preço de balcão + taxa (digitada ou a padrão). Só prévia. */
export function previewDeliveryCents(form: DeliveryForm, book: FixedMenuBook, defaultFee: MoneyString | null): number {
  return previewALaCarteCents(form, book) + deliveryFeeCents(form, defaultFee);
}

export type DeliveryField = 'phone' | 'name' | 'street' | 'expectedPaymentMethod' | 'changeFor' | 'lines';

/** Problemas por campo, na ordem da tela; vazio = pode confirmar. */
export function deliveryFormIssues(form: DeliveryForm, book: FixedMenuBook, totalCents: number, enabled?: readonly PaymentMethod[]): Partial<Record<DeliveryField, string>> {
  const issues: Partial<Record<DeliveryField, string>> = {};
  if (form.phone.trim() === '') issues.phone = 'Informe o telefone do cliente.';
  else if (toE164(form.phone) === null) issues.phone = 'Telefone com DDD.';
  if (form.name.trim() === '') issues.name = 'Informe o nome do cliente.';
  if (form.fulfillmentType === 'DELIVERY' && form.address.street.trim() === '') issues.street = 'Informe o endereço de entrega.';
  const payment = expectedPaymentIssue(form.fulfillmentType, form, enabled);
  if (payment) issues.expectedPaymentMethod = payment;
  const change = changeForIssue(form, totalCents);
  if (change) issues.changeFor = change;
  if (form.lines.length === 0) issues.lines = 'Inclua pelo menos um item do cardápio.';
  else {
    for (const l of form.lines) {
      const missing = lineMissing(l, book);
      if (missing) {
        issues.lines = `${lineLabel(book, l)}: ${missing}`;
        break;
      }
    }
  }
  return issues;
}

export function firstDeliveryIssue(issues: Partial<Record<DeliveryField, string>>): string | null {
  const order: DeliveryField[] = ['phone', 'name', 'street', 'expectedPaymentMethod', 'changeFor', 'lines'];
  for (const f of order) if (issues[f]) return issues[f]!;
  return null;
}

const nullIfBlank = (s: string | null | undefined) => (!s || s.trim() === '' ? null : s.trim());

export function cleanAddress(a: Address): Address {
  return { street: a.street.trim(), number: nullIfBlank(a.number), complement: nullIfBlank(a.complement), neighborhood: nullIfBlank(a.neighborhood), reference: nullIfBlank(a.reference) };
}

/**
 * Corpo do delivery por telefone (06 §6.9, exemplo "À la carte, entrega por telefone"): sem `customerId` nem
 * `saveCustomer` (o servidor vincula ou cria o cliente pelo telefone, sem endereço salvo, ADR-0059 item 14);
 * endereço só no pedido; retirada sem endereço nem taxa.
 */
export function buildDeliveryBody(form: DeliveryForm, book: FixedMenuBook): CreateOrderBody {
  const delivery = form.fulfillmentType === 'DELIVERY';
  return {
    orderType: 'A_LA_CARTE',
    fulfillmentType: form.fulfillmentType,
    customerId: null,
    customerName: form.name.trim(),
    customerPhone: toE164(form.phone),
    saveCustomer: false,
    customerAddressId: null,
    deliveryAddress: delivery ? cleanAddress(form.address) : null,
    deliveryFee: delivery ? form.deliveryFee : null,
    tableLabel: null,
    tabNumber: null,
    notes: nullIfBlank(form.notes),
    ...expectedPaymentBody(form),
    items: form.lines.map((l) => lineItem(book, l)),
    weighings: [],
  };
}

/** Endereço numa linha: "Rua das Flores, 123, Centro · Portão verde". */
export function addressText(a: Address): string {
  const main = [a.street, a.number, a.complement, a.neighborhood].filter(Boolean).join(', ');
  return a.reference ? `${main} · ${a.reference}` : main;
}

/**
 * Mensagem de um campo do `422 ValidationFailed` do servidor (`errors`), sem diferença de caixa e aceitando os
 * filhos (`deliveryAddress.street` cai em `deliveryAddress`).
 */
export function fieldMessage(errors: Record<string, string[]> | undefined, field: string): string | null {
  if (!errors) return null;
  const f = field.toLowerCase();
  const key = Object.keys(errors).find((k) => {
    const lk = k.toLowerCase();
    return lk === f || lk.startsWith(`${f}.`) || lk.startsWith(`${f}[`);
  });
  return key ? (errors[key]?.[0] ?? null) : null;
}
