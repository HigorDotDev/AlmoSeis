/** Textos pt-BR do app (08 §8.1: sem biblioteca de i18n). */
import type {
  AcceptanceStatus,
  FulfillmentStatus,
  FulfillmentType,
  OrderType,
  PaymentMethod,
  ProductKind,
  ProductionStatus,
  RejectionReasonCode,
  Role,
  TenantModule,
} from '@/api/types';

export const roleNames: Record<Role, string> = {
  OWNER: 'Proprietário',
  MANAGER: 'Gerente',
  ATTENDANT: 'Atendente',
  KITCHEN: 'Cozinha',
  CASHIER: 'Caixa',
};

/** Módulos do estabelecimento (ADR-0054 item 12), para a mensagem do `403 ModuleNotEnabled`. */
export const moduleNames: Record<TenantModule, string> = {
  MARMITA: 'Marmita',
  PF: 'PF',
  KILO: 'Quilo',
  DAILY_MENU: 'Cardápio do dia',
  A_LA_CARTE: 'Comandas à la carte',
  DELIVERY: 'Delivery',
  FIXED_MENU: 'Cardápio fixo',
};

export const orderTypeNames: Record<OrderType, string> = { MARMITA: 'Marmita', PF: 'PF', KILO: 'Quilo' };

export const fulfillmentNames: Record<FulfillmentType, string> = { DELIVERY: 'Entrega', PICKUP: 'Retirada', DINE_IN: 'No salão' };

export const paymentMethodNames: Record<PaymentMethod, string> = { CASH: 'Dinheiro', PIX: 'Pix', CARD: 'Cartão' };

export const productionNames: Record<ProductionStatus, string> = { OPEN: 'Aberto', IN_PROGRESS: 'Em produção', COMPLETED: 'Concluído' };

/** Atendimento (ADR-0046 item 2). "Pronto para retirar" é derivado (`isReadyForPickup`), não um valor gravado. */
export const fulfillmentStatusNames: Record<FulfillmentStatus, string> = {
  PENDING: 'Pendente',
  OUT_FOR_DELIVERY: 'Saiu para entrega',
  DELIVERED: 'Entregue',
  PICKED_UP: 'Retirado',
};

export const READY_FOR_PICKUP = 'Pronto para retirar';

export const acceptanceStatusNames: Record<AcceptanceStatus, string> = {
  AWAITING_PAYMENT: 'Aguardando pagamento',
  AWAITING_ACCEPTANCE: 'Aguardando aceite',
  ACCEPTED: 'Aceito',
  REJECTED: 'Recusado',
  EXPIRED: 'Expirado',
  WITHDRAWN: 'Cliente desistiu',
};

/** Motivo da recusa para quem atende (ADR-0046 item 12). */
export const rejectionReasonNames: Record<RejectionReasonCode, string> = {
  ITEM_UNAVAILABLE: 'Item acabou',
  OUT_OF_DELIVERY_AREA: 'Fora da área de entrega',
  TOO_BUSY: 'Muitos pedidos agora',
  CLOSING_SOON: 'Estamos fechando',
  SUSPECTED_PRANK: 'Pedido suspeito',
  OTHER: 'Outro motivo',
  BUSINESS_DAY_CLOSED: 'Expediente fechado',
};

/** O que o cliente vê na página para cada motivo (06 §6.9, `POST /orders/{id}/reject`). */
export const rejectionCustomerText: Record<RejectionReasonCode, string | null> = {
  ITEM_UNAVAILABLE: 'Um item do seu pedido acabou.',
  OUT_OF_DELIVERY_AREA: 'Não entregamos nesse endereço.',
  TOO_BUSY: 'Estamos com muitos pedidos agora.',
  CLOSING_SOON: 'Estamos fechando.',
  SUSPECTED_PRANK: 'O restaurante não pôde aceitar seu pedido.',
  OTHER: null,
  BUSINESS_DAY_CLOSED: 'O restaurante encerrou o atendimento.',
};

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
  OrderNotAccepted: 'Este pedido do site ainda não foi aceito.',
  OrderNotAwaitingAcceptance: 'Este pedido do site não aguarda mais aceite.',
  WebOrderExpired: 'O prazo acabou, o pedido expirou.',
  OrderAlreadyDispatched: 'Este pedido já saiu (ou foi retirado). Para incluir itens, crie outro pedido.',
  ModuleNotEnabled: 'Este recurso não está habilitado no estabelecimento.',
  UsedInActiveCombos: 'Este item é usado em combos ativos. Ajuste ou inative esses combos antes.',
  ProductionNotCompleted: 'A cozinha ainda não concluiu este pedido. Use "Concluir e marcar" ou espere a conclusão.',
};

export const GENERIC_ERROR = 'Algo deu errado.';
export const NETWORK_ERROR = 'Sem conexão com o servidor.';
/** Abrindo o app no navegador sem resposta do servidor (429/5xx/rede, ADR-0053 §3, ADR-0041): não vai ao login. */
export const RECONNECTING_MESSAGE = 'Sem resposta do servidor. Reconectando…';
