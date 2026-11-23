/**
 * Delivery por telefone (EP-003 fatia 4, ADR-0059, 06 §6.9 `POST /orders`), sem React: forma prevista e "troco
 * para", o formulário do pedido à la carte de entrega/retirada, a validação antes do Confirma e o corpo.
 *
 * A forma prevista é só informação para o entregador: **não** cria pagamento nem marca nada como pago (ADR-0059
 * item 8). A conferência do "troco para" usa a prévia do app; quem decide é o servidor (`422` em `changeFor`).
 */
import type { Address, CreateOrderBody, FulfillmentType, PaymentMethod } from '@/api/types';
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

/** O que o card e o detalhe usam para o troco. `changeForBelowTotal` vem do servidor (ausente num cache antigo). */
export interface ChangeInfo {
  expectedPaymentMethod?: PaymentMethod | null;
  changeFor?: MoneyString | null;
  balance: MoneyString;
  isPaid: boolean;
  totalAmount?: MoneyString;
  changeForBelowTotal?: boolean;
}

/**
 * "Troco para" abaixo do total (ADR-0059 complementado em 2026-11-23, I2): um item, quantidade ou taxa incluído
 * depois passou o total. Vale o `changeForBelowTotal` do servidor; só sem ele (pedido guardado por um app anterior)
 * o app compara o "troco para" com o total. Quitado, não há troco a conferir.
 */
export function changeBelowTotal(o: ChangeInfo): boolean {
  if (o.expectedPaymentMethod !== 'CASH' || !hasValue(o.changeFor) || o.isPaid) return false;
  if (o.changeForBelowTotal !== undefined) return o.changeForBelowTotal;
  return toCents(o.changeFor) < toCents(o.totalAmount ?? o.balance);
}

/**
 * Troco que o motoboy leva: `changeFor − balance` (ADR-0059 item 12). `null` sem troco, com o pedido quitado ou com
 * o "troco para" abaixo do total (aí o app mostra o alerta, nunca um troco negativo ou zerado).
 */
export function changeToCarryCents(o: ChangeInfo): number | null {
  if (o.expectedPaymentMethod !== 'CASH' || !hasValue(o.changeFor) || o.isPaid || changeBelowTotal(o)) return null;
  return toCents(o.changeFor) - toCents(o.balance);
}

/** "Levar troco para R$ 100,00 → troco R$ 43,00" (card do delivery e detalhe). */
export function changeToCarryText(o: ChangeInfo): string | null {
  const carry = changeToCarryCents(o);
  if (carry === null) return null;
  return `Levar troco para ${formatBRL(o.changeFor!)} → troco ${formatBRL(carry)}`;
}

/** Alerta do I2: "O troco para R$ 60,00 não cobre o total R$ 61,00 — confirme com o cliente". */
export function changeBelowTotalText(o: ChangeInfo & { totalAmount: MoneyString }): string | null {
  if (!changeBelowTotal(o)) return null;
  return `O troco para ${formatBRL(o.changeFor!)} não cobre o total ${formatBRL(o.totalAmount)} — confirme com o cliente`;
}

// ---------------------------------------------------------------- Edição da forma prevista e do troco (PATCH)

/** O que o pedido tem gravado: a base da comparação na edição. */
export interface ExpectedPaymentOriginal extends ExpectedPayment {
  fulfillmentType: FulfillmentType;
}

const centsOrZero = (m: MoneyString | null | undefined) => (hasValue(m) ? toCents(m) : 0);

/**
 * Campos da forma prevista e do troco no `PATCH /orders/{id}` (06 §6.9, ADR-0059 complementado em 2026-11-23, I1).
 * Só vai o que mudou: a forma quando muda ou quando o pedido vira entrega (Q-316: a forma vai no mesmo `PATCH`); o
 * "troco para" só com dinheiro e quando muda (`null` apaga). Saindo de dinheiro, o troco **não vai**: o servidor
 * apaga o gravado. No salão, nada (a forma e o troco são proibidos lá).
 */
export function expectedPaymentPatch(original: ExpectedPaymentOriginal, next: ExpectedPayment, fulfillmentType: FulfillmentType): { expectedPaymentMethod?: PaymentMethod | null; changeFor?: MoneyString | null } {
  if (fulfillmentType === 'DINE_IN') return {};
  const body: { expectedPaymentMethod?: PaymentMethod | null; changeFor?: MoneyString | null } = {};
  const becameDelivery = fulfillmentType === 'DELIVERY' && original.fulfillmentType !== 'DELIVERY';
  if (next.expectedPaymentMethod !== original.expectedPaymentMethod || becameDelivery) body.expectedPaymentMethod = next.expectedPaymentMethod;
  if (next.expectedPaymentMethod === 'CASH') {
    const before = original.expectedPaymentMethod === 'CASH' ? centsOrZero(original.changeFor) : 0;
    if (centsOrZero(next.changeFor) !== before) body.changeFor = hasValue(next.changeFor) ? next.changeFor : null;
  }
  return body;
}

/**
 * Conferência antes do Confirma da edição, com as regras do servidor: entrega exige a forma quando ela vai no corpo
 * (virou entrega ou a pessoa mexeu; uma entrega antiga sem forma continua editável nos outros campos); a forma nova
 * precisa estar habilitada; o "troco para" editado, de pelo menos o total resultante (prévia; quem decide é o
 * servidor, `422` em `changeFor`).
 */
export function expectedPaymentEditIssues(
  original: ExpectedPaymentOriginal,
  next: ExpectedPayment,
  fulfillmentType: FulfillmentType,
  totalCents: number,
  enabled?: readonly PaymentMethod[],
): { method?: string; changeFor?: string } {
  if (fulfillmentType === 'DINE_IN') return {};
  const patch = expectedPaymentPatch(original, next, fulfillmentType);
  const issues: { method?: string; changeFor?: string } = {};
  if ('expectedPaymentMethod' in patch) {
    const method = expectedPaymentIssue(fulfillmentType, next, enabled);
    if (method) issues.method = method;
  }
  if (patch.changeFor) {
    const change = changeForIssue(next, totalCents);
    if (change) issues.changeFor = change;
  }
  return issues;
}

/**
 * Prévia do total depois do `PATCH` (para conferir o "troco para" editado): itens e pesagens + a taxa que fica. Sem
 * mexer na taxa, a entrega continua com a gravada; a retirada que vira entrega, com a padrão.
 */
export function editedTotalCents(o: { totalAmount: MoneyString; deliveryFee: MoneyString; fulfillmentType: FulfillmentType }, fulfillmentType: FulfillmentType, sentFee: MoneyString | undefined, defaultFee: MoneyString | null | undefined): number {
  const base = toCents(o.totalAmount) - toCents(o.deliveryFee);
  if (fulfillmentType !== 'DELIVERY') return base;
  const fee = sentFee ?? (o.fulfillmentType === 'DELIVERY' ? o.deliveryFee : (defaultFee ?? '0.00'));
  return base + toCents(fee);
}

/** Linha do Confirma: "Dinheiro → Pix", "Não informada → Pix". */
export function expectedPaymentChangeText(before: PaymentMethod | null, after: PaymentMethod | null): string {
  const name = (m: PaymentMethod | null) => (m ? paymentMethodNames[m] : 'Não informada');
  return `${name(before)} → ${name(after)}`;
}

/** Linha do Confirma: "R$ 60,00 → R$ 70,00", "Sem troco → R$ 70,00". */
export function changeForChangeText(before: MoneyString | null, after: MoneyString | null): string {
  const text = (m: MoneyString | null) => (hasValue(m) ? formatBRL(m) : 'Sem troco');
  return `${text(before)} → ${text(after)}`;
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
