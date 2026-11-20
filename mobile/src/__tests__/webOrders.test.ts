/** Pedidos do site e atendimento (ADR-0046), sem React: botões de atendimento, prazo, conflito, recusa, textos. */
import type { Order } from '@/api/types';
import { autoRejectWarning, unmarkedDeliveriesText } from '@/features/closing/components';
import { fulfillmentActions, isDispatched } from '@/features/orders/fulfillment';
import { acceptanceConflictText, countdown, orderNumberLabel, rejectionMessageError } from '@/features/orders/webOrders';
import { formatTabTitle } from '@/features/shell/tabTitleFormat';
import { errorMessage, ApiError } from '@/api/errors';

const all = { canFulfill: true, canProduction: true };
const labels = (r: ReturnType<typeof fulfillmentActions>) => r.actions.map((a) => `${a.kind}:${a.to}:${a.label}${a.completeProduction ? ':complete' : ''}`);

describe('fulfillmentActions (ADR-0046 item 18)', () => {
  it('entrega: Saiu → Entregue, com "Voltou sem entregar" e correção', () => {
    const d = { fulfillmentType: 'DELIVERY' as const, productionStatus: 'COMPLETED' as const };
    expect(labels(fulfillmentActions({ ...d, fulfillmentStatus: 'PENDING' }, all))).toEqual(['forward:OUT_FOR_DELIVERY:Saiu para entrega']);
    expect(labels(fulfillmentActions({ ...d, fulfillmentStatus: 'OUT_FOR_DELIVERY' }, all))).toEqual([
      'forward:DELIVERED:Entregue',
      'back:PENDING:Voltou sem entregar',
    ]);
    expect(labels(fulfillmentActions({ ...d, fulfillmentStatus: 'DELIVERED' }, all))).toEqual(['back:OUT_FOR_DELIVERY:Desfazer "Entregue"']);
  });

  it('retirada: só "Retirado" e a correção; "Pronto para retirar" não é ação', () => {
    const p = { fulfillmentType: 'PICKUP' as const, productionStatus: 'COMPLETED' as const };
    expect(labels(fulfillmentActions({ ...p, fulfillmentStatus: 'PENDING' }, all))).toEqual(['forward:PICKED_UP:Retirado']);
    expect(labels(fulfillmentActions({ ...p, fulfillmentStatus: 'PICKED_UP' }, all))).toEqual(['back:PENDING:Desfazer "Retirado"']);
  });

  it('produção aberta: "Concluir e marcar" só com production.update; sem ele, espera a cozinha', () => {
    const o = { fulfillmentType: 'DELIVERY' as const, fulfillmentStatus: 'PENDING' as const, productionStatus: 'IN_PROGRESS' as const };
    expect(labels(fulfillmentActions(o, all))).toEqual(['forward:OUT_FOR_DELIVERY:Concluir e marcar saída:complete']);
    expect(fulfillmentActions(o, { canFulfill: true, canProduction: false })).toEqual({ actions: [], waitingKitchen: true });
    expect(labels(fulfillmentActions({ ...o, fulfillmentType: 'PICKUP' }, all))).toEqual(['forward:PICKED_UP:Concluir e marcar retirado:complete']);
    // "Entregue" não exige produção concluída (só a saída exige).
    expect(labels(fulfillmentActions({ ...o, fulfillmentStatus: 'OUT_FOR_DELIVERY' }, { canFulfill: true, canProduction: false }))[0]).toBe('forward:DELIVERED:Entregue');
  });

  it('nada para salão, sem permissão, não aceito ou cancelado', () => {
    const base = { fulfillmentType: 'DELIVERY' as const, fulfillmentStatus: 'PENDING' as const, productionStatus: 'COMPLETED' as const };
    expect(fulfillmentActions({ ...base, fulfillmentType: 'DINE_IN', fulfillmentStatus: null }, all).actions).toEqual([]);
    expect(fulfillmentActions(base, { canFulfill: false, canProduction: true }).actions).toEqual([]);
    expect(fulfillmentActions({ ...base, acceptanceStatus: 'AWAITING_ACCEPTANCE' }, all).actions).toEqual([]);
    expect(fulfillmentActions({ ...base, cancellationStatus: 'CANCELED' }, all).actions).toEqual([]);
  });

  it('isDispatched: saiu, entregue ou retirado bloqueiam itens novos (item 19)', () => {
    expect(isDispatched('PENDING')).toBe(false);
    expect(isDispatched(null)).toBe(false);
    expect(isDispatched('OUT_FOR_DELIVERY')).toBe(true);
    expect(isDispatched('DELIVERED')).toBe(true);
    expect(isDispatched('PICKED_UP')).toBe(true);
  });
});

describe('prazo de aceite (ADR-0046 item 15)', () => {
  const deadline = '2026-10-27T12:10:00Z';
  const at = (iso: string) => new Date(iso).getTime();

  it('conta minutos e segundos, fica urgente nos 3 min finais e zera no prazo', () => {
    expect(countdown(deadline, at('2026-10-27T12:00:00Z'))).toMatchObject({ label: '10:00', expired: false, urgent: false });
    expect(countdown(deadline, at('2026-10-27T12:07:01Z'))).toMatchObject({ label: '2:59', urgent: true });
    expect(countdown(deadline, at('2026-10-27T12:09:59.500Z'))).toMatchObject({ label: '0:01', expired: false });
    expect(countdown(deadline, at('2026-10-27T12:12:00Z'))).toMatchObject({ label: '0:00', expired: true, urgent: false });
    expect(countdown(null)).toBeNull();
  });
});

describe('conflito de aceite (ADR-0046 item 9)', () => {
  const current = (patch: Partial<Order>) => ({ acceptanceStatus: 'AWAITING_ACCEPTANCE', acceptedBy: null, rejection: null, ...patch }) as Order;

  it('"Já aceito por Bruno" a partir do current do 409', () => {
    expect(acceptanceConflictText(current({ acceptanceStatus: 'ACCEPTED', acceptedBy: { id: 'e2', displayName: 'Bruno' } }))).toBe('Já aceito por Bruno.');
    expect(
      acceptanceConflictText(
        current({ acceptanceStatus: 'REJECTED', rejection: { reasonCode: 'TOO_BUSY', message: null, rejectedAt: '', rejectedBy: { id: 'e3', displayName: 'Carla' } } }),
      ),
    ).toBe('Já recusado por Carla.');
    expect(acceptanceConflictText(current({ acceptanceStatus: 'EXPIRED' }))).toBe('O prazo acabou, o pedido expirou.');
    expect(acceptanceConflictText(current({ acceptanceStatus: 'WITHDRAWN' }))).toBe('O cliente desistiu deste pedido.');
    expect(acceptanceConflictText(null)).toMatch(/Confira e refaça/);
  });
});

describe('recusa (ADR-0046 item 12)', () => {
  it('texto obrigatório em OTHER e, quando há texto, de 5 a 200 caracteres', () => {
    expect(rejectionMessageError('OTHER', '  ')).toBe('Escreva o motivo para o cliente.');
    expect(rejectionMessageError('OTHER', 'Fechamos mais cedo hoje')).toBeNull();
    expect(rejectionMessageError('ITEM_UNAVAILABLE', '')).toBeNull();
    expect(rejectionMessageError('ITEM_UNAVAILABLE', 'acab')).toBe('Pelo menos 5 caracteres.');
    expect(rejectionMessageError('TOO_BUSY', 'x'.repeat(201))).toBe('No máximo 200 caracteres.');
  });
});

describe('textos', () => {
  it('número só depois do aceite', () => {
    expect(orderNumberLabel(15)).toBe('#15');
    expect(orderNumberLabel(null)).toBe('Pedido do site');
  });

  it('título da aba conta os novos do site (ADR-0053 §7.4)', () => {
    expect(formatTabTitle(0)).toBe('AlmoSeis');
    expect(formatTabTitle(1)).toBe('(1) Novo do site · AlmoSeis');
    expect(formatTabTitle(2)).toBe('(2) Novos do site · AlmoSeis');
  });

  it('fechamento avisa a recusa automática e as entregas sem marcação (itens 13 e 20)', () => {
    expect(autoRejectWarning(0)).toBeNull();
    expect(autoRejectWarning(1)).toBe('1 pedido do site aguardando aceite será recusado.');
    expect(autoRejectWarning(2)).toBe('2 pedidos do site aguardando aceite serão recusados.');
    expect(unmarkedDeliveriesText(0)).toBeNull();
    expect(unmarkedDeliveriesText(4)).toBe('4 entregas sem marcação de entregue.');
  });

  it('mensagens dos errorCodes novos', () => {
    const err = (errorCode: string, ext: Record<string, unknown> = {}) => new ApiError(409, { errorCode, ...ext });
    expect(errorMessage(err('WebOrderExpired'))).toBe('O prazo acabou, o pedido expirou.');
    expect(errorMessage(err('OrderNotAwaitingAcceptance', { acceptanceStatus: 'ACCEPTED' }))).toBe('Este pedido do site já foi aceito.');
    expect(errorMessage(err('OrderNotAwaitingAcceptance', { acceptanceStatus: 'WITHDRAWN' }))).toBe('O cliente desistiu deste pedido.');
    expect(errorMessage(err('OrderAlreadyDispatched'))).toMatch(/crie outro pedido/);
    expect(errorMessage(err('ProductionNotCompleted'))).toMatch(/Concluir e marcar/);
    expect(errorMessage(err('OrderNotAccepted'))).toBe('Este pedido do site ainda não foi aceito.');
  });
});
