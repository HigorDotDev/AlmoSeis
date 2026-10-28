/** Textos pt-BR do app (08 §8.1: sem biblioteca de i18n). */
import type { FulfillmentType, OrderType, PaymentMethod, ProductKind, ProductionStatus, Role } from '@/api/types';

export const roleNames: Record<Role, string> = {
  OWNER: 'Proprietário',
  MANAGER: 'Gerente',
  ATTENDANT: 'Atendente',
  KITCHEN: 'Cozinha',
  CASHIER: 'Caixa',
};

export const orderTypeNames: Record<OrderType, string> = { MARMITA: 'Marmita', PF: 'PF', KILO: 'Quilo' };

export const fulfillmentNames: Record<FulfillmentType, string> = { DELIVERY: 'Entrega', PICKUP: 'Retirada', DINE_IN: 'No salão' };

export const paymentMethodNames: Record<PaymentMethod, string> = { CASH: 'Dinheiro', PIX: 'Pix', CARD: 'Cartão' };

export const productionNames: Record<ProductionStatus, string> = { OPEN: 'Aberto', IN_PROGRESS: 'Em produção', COMPLETED: 'Concluído' };

export const productKindNames: Record<ProductKind, string> = {
  MAIN_DISH: 'Pratos',
  SIDE_DISH: 'Acompanhamentos',
  BEVERAGE: 'Bebidas',
  ADDON: 'Adicionais',
};

export const productKindSingular: Record<ProductKind, string> = {
  MAIN_DISH: 'Prato principal',
  SIDE_DISH: 'Acompanhamento',
  BEVERAGE: 'Bebida',
  ADDON: 'Adicional',
};

/** 08 §8.11: mensagem por errorCode. As que dependem de dados recebem o ProblemDetails. */
export const errorMessages: Record<string, string> = {
  BusinessDayRequired: 'Abra o expediente para registrar pedidos e pagamentos.',
  OrderAlreadyPaid: 'Este pedido já foi quitado. Para corrigir, estorne um pagamento.',
  OrderHasPayments: 'Estorne os pagamentos antes de cancelar.',
  OrderCanceled: 'Este pedido foi cancelado.',
  TotalBelowPaidAmount: 'O total ficaria menor que o já pago.',
  KiloPriceNotConfigured: 'Configure o preço do quilo.',
  PermissionDenied: 'Você não tem permissão para isso. Peça a um gerente.',
  SubscriptionInactive: 'A assinatura está inativa. O app está em modo somente leitura.',
  PaymentAlreadyReversed: 'Este pagamento já foi estornado.',
  PaymentMethodDisabled: 'Essa forma de pagamento está desabilitada.',
  ClosingAlreadyFinalized: 'Este expediente já foi fechado.',
  JustificationRequired: 'Explique a diferença de caixa.',
  BusinessDayAlreadyOpen: 'Já existe um expediente aberto.',
  InvalidCredentials: 'Login ou senha incorretos.',
  NoActiveMembership: 'Sua conta não está ligada a nenhum estabelecimento ativo.',
  InvitationNotPending: 'Este código já foi usado, revogado ou expirou.',
  LastOwner: 'O estabelecimento precisa de pelo menos um proprietário ativo.',
  EmailAlreadyRegistered: 'Este e-mail já tem conta.',
  PhoneAlreadyRegistered: 'Este telefone já está cadastrado.',
  TrialAlreadyClaimed: 'Este documento ou telefone já usou o período de teste. Fale com o suporte do AlmoSeis.',
  MenuAlreadyExists: 'Já existe um cardápio para esse dia.',
  OperationInProgress: 'Essa operação ainda está sendo processada. Aguarde um instante.',
  IdempotencyKeyReused: 'Essa operação já foi enviada com outros dados.',
  InvalidDeviceSecret: 'Este aparelho não está mais registrado como compartilhado.',
  NotFound: 'Não encontrado.',
  NoOpenBusinessDay: 'Nenhum expediente aberto.',
  ValidationFailed: 'Confira os campos destacados.',
};

export const GENERIC_ERROR = 'Algo deu errado.';
export const NETWORK_ERROR = 'Sem conexão com o servidor.';
