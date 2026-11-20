import type { ApiFailure } from './api/client';
import type {
  DeliveryAddressInput,
  ExpectedPaymentMethod,
  FulfillmentType,
  PlaceWebOrderRequest,
  RecalculatedCart,
  StoreStatus,
} from './api/types';
import { expectedTotal, toOrderItems, type Cart, type CartPreview } from './cart';
import { parseUserMoney, toApiMoney } from './money';
import { isValidBrazilianPhone } from './phone';

export interface CheckoutForm {
  name: string;
  phone: string;
  fulfillmentType: FulfillmentType;
  address: DeliveryAddressInput;
  deliveryQuoteId: string | null;
  paymentMethod: ExpectedPaymentMethod | null;
  /** "Troco para" digitado pelo cliente (só dinheiro). Vazio = não precisa de troco. */
  changeFor: string;
  notes: string;
  /** Campo-isca invisível (ADR-0045). Gente não preenche. */
  website: string;
}

export const EMPTY_ADDRESS: DeliveryAddressInput = {
  street: '',
  number: '',
  complement: null,
  neighborhood: '',
  city: '',
  zipCode: null,
  reference: null,
};

export type FieldErrors = Partial<Record<string, string>>;

/** Checagem local antes do envio; a API confere de novo (§6.19 checagem 1). */
export function validateCheckout(form: CheckoutForm, preview: CartPreview, notesMax = 200): FieldErrors {
  const errors: FieldErrors = {};
  const name = form.name.trim();
  if (name.length < 2 || name.length > 80) errors.customerName = 'Informe seu nome (2 a 80 letras).';
  if (!isValidBrazilianPhone(form.phone)) errors.customerPhone = 'Informe um telefone com DDD, por exemplo (34) 98888-7777.';
  if (!form.paymentMethod) errors.expectedPaymentMethod = 'Escolha como vai pagar.';
  if (form.notes.length > notesMax) errors.notes = `A observação pode ter até ${notesMax} caracteres.`;
  if (form.paymentMethod === 'CASH' && form.changeFor.trim()) {
    const change = parseUserMoney(form.changeFor);
    if (change == null) errors.changeFor = 'Informe um valor, por exemplo 50,00.';
    else if (change < preview.totalCents) errors.changeFor = 'O troco precisa ser para um valor maior ou igual ao total.';
  }
  if (form.fulfillmentType === 'DELIVERY') {
    if (!form.address.street.trim()) errors['deliveryAddress.street'] = 'Informe a rua.';
    if (!form.address.number.trim()) errors['deliveryAddress.number'] = 'Informe o número.';
    if (!form.address.neighborhood.trim()) errors['deliveryAddress.neighborhood'] = 'Informe o bairro.';
    if (!form.address.city.trim()) errors['deliveryAddress.city'] = 'Informe a cidade.';
    if (!form.deliveryQuoteId) errors.deliveryQuoteId = 'Calcule a taxa de entrega antes de enviar.';
  }
  return errors;
}

const blankToNull = (v: string | null | undefined) => (v && v.trim() ? v.trim() : null);

export function buildOrderRequest(form: CheckoutForm, cart: Cart, preview: CartPreview): PlaceWebOrderRequest {
  const isDelivery = form.fulfillmentType === 'DELIVERY';
  const change = form.paymentMethod === 'CASH' ? parseUserMoney(form.changeFor) : null;
  return {
    fulfillmentType: form.fulfillmentType,
    customerName: form.name.trim(),
    customerPhone: form.phone.trim(),
    deliveryAddress: isDelivery
      ? {
          street: form.address.street.trim(),
          number: form.address.number.trim(),
          complement: blankToNull(form.address.complement),
          neighborhood: form.address.neighborhood.trim(),
          city: form.address.city.trim(),
          zipCode: blankToNull(form.address.zipCode),
          reference: blankToNull(form.address.reference),
        }
      : null,
    deliveryQuoteId: isDelivery ? form.deliveryQuoteId : null,
    expectedPaymentMethod: form.paymentMethod ?? 'CASH',
    changeFor: change != null ? toApiMoney(change) : null,
    notes: blankToNull(form.notes),
    items: toOrderItems(cart),
    expectedTotal: expectedTotal(preview),
    website: form.website ? form.website : null,
  };
}

/** O que a tela faz com cada falha do `POST /orders` (§6.19, tabela de erros). */
export type SubmitOutcome =
  | { kind: 'cartChanged'; cart: RecalculatedCart; message: string }
  | { kind: 'fieldErrors'; errors: FieldErrors; message: string }
  | { kind: 'storeUnavailable'; storeStatus: StoreStatus | null; message: string }
  | { kind: 'captcha'; message: string }
  | { kind: 'rateLimited'; retryAfterSeconds: number; message: string }
  | { kind: 'tooManyPending'; message: string }
  | { kind: 'network'; message: string }
  | { kind: 'message'; message: string };

/** A tela usa as chaves em camelCase (`customerPhone`, `items[0].quantity`). */
function normalizeFieldKey(key: string): string {
  return key.charAt(0).toLowerCase() + key.slice(1);
}

export function interpretSubmitFailure(failure: ApiFailure): SubmitOutcome {
  if (failure.kind === 'network') {
    return {
      kind: 'network',
      message: 'Não conseguimos confirmar o envio. Verifique a internet e toque em "Enviar pedido" de novo: o pedido não será duplicado.',
    };
  }
  if (failure.kind === 'moved') {
    return { kind: 'message', message: 'O endereço da página mudou. Recarregue a página e tente de novo.' };
  }
  const { problem, status } = failure;
  const code = problem.errorCode;
  if (code === 'CartChanged' && problem.cart) {
    return { kind: 'cartChanged', cart: problem.cart, message: problem.title ?? 'Seu pedido mudou. Confira os itens e os valores.' };
  }
  if (code === 'StoreUnavailable') {
    const s = problem.storeStatus ?? null;
    const message =
      s === 'PAUSED'
        ? 'O restaurante pausou os pedidos pela página agora. Tente de novo em alguns minutos.'
        : s === 'CLOSED'
          ? 'O restaurante fechou agora e não está recebendo pedidos.'
          : 'A página não está recebendo pedidos agora.';
    return { kind: 'storeUnavailable', storeStatus: s, message };
  }
  if (code === 'CaptchaFailed') {
    return { kind: 'captcha', message: 'Não conseguimos confirmar que você não é um robô. Faça a verificação de novo e reenvie.' };
  }
  if (code === 'TooManyPendingOrders') {
    return {
      kind: 'tooManyPending',
      message: 'Você já tem pedidos aguardando a confirmação do restaurante. Espere a resposta antes de enviar outro.',
    };
  }
  if (status === 429) {
    const seconds = failure.retryAfterSeconds ?? 60;
    return { kind: 'rateLimited', retryAfterSeconds: seconds, message: `Muitas tentativas. Aguarde ${seconds} segundos e tente de novo.` };
  }
  if (code === 'ValidationFailed' && problem.errors) {
    const errors: FieldErrors = {};
    for (const [field, messages] of Object.entries(problem.errors)) {
      errors[normalizeFieldKey(field)] = messages.join(' ');
    }
    return { kind: 'fieldErrors', errors, message: 'Confira os campos destacados.' };
  }
  if (code === 'DeliveryUnavailable') {
    return { kind: 'fieldErrors', errors: { fulfillmentType: 'A entrega não está disponível agora. Escolha retirada.' }, message: 'A entrega não está disponível agora.' };
  }
  if (code === 'PaymentMethodDisabled') {
    return {
      kind: 'fieldErrors',
      errors: { expectedPaymentMethod: 'O restaurante não aceita esta forma de pagamento. Escolha outra.' },
      message: 'Escolha outra forma de pagamento.',
    };
  }
  if (code === 'MenuNotFound') {
    return { kind: 'storeUnavailable', storeStatus: null, message: 'O cardápio de hoje ainda não foi publicado.' };
  }
  if (code === 'StoreNotFound') {
    return { kind: 'message', message: 'Restaurante não encontrado.' };
  }
  if (code === 'IdempotencyKeyReused') {
    return { kind: 'message', message: 'O pedido mudou durante o envio. Toque em "Enviar pedido" de novo.' };
  }
  if (status === 400) {
    return { kind: 'message', message: 'Não foi possível enviar o pedido.' };
  }
  return { kind: 'message', message: problem.title ?? 'Não foi possível enviar o pedido. Tente de novo.' };
}
