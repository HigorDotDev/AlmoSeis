/**
 * Atendimento de entrega e retirada (ADR-0046 item 18, 06 §6.9 `PATCH /orders/{id}/fulfillment`), sem React.
 * Quais botões aparecer para um pedido; a regra oficial é do servidor (transição fora da tabela → 422).
 */
import type { AcceptanceStatus, CancellationStatus, FulfillmentStatus, FulfillmentType, ProductionStatus } from '@/api/types';
import { fulfillmentStatusNames } from '@/lib/strings';

export interface FulfillmentAction {
  to: FulfillmentStatus;
  label: string;
  /** Avanço (Saiu, Entregue, Retirado) ou volta (Voltou sem entregar, correção de toque errado). */
  kind: 'forward' | 'back';
  /** Saiu/Retirado com a produção não concluída: "Concluir e marcar", com `completeProduction: true`. */
  completeProduction: boolean;
}

export interface FulfillmentInput {
  fulfillmentType: FulfillmentType;
  fulfillmentStatus: FulfillmentStatus | null;
  productionStatus: ProductionStatus | null;
  acceptanceStatus?: AcceptanceStatus;
  cancellationStatus?: CancellationStatus;
}

export interface FulfillmentPermissions {
  /** `orders.fulfillment.update` (e fora do somente leitura). */
  canFulfill: boolean;
  /** `production.update`: necessário para "Concluir e marcar". */
  canProduction: boolean;
}

/** Os passos que exigem a produção concluída (ADR-0046 item 18). */
const NEEDS_COMPLETED: FulfillmentStatus[] = ['OUT_FOR_DELIVERY', 'PICKED_UP'];

/** Depois destes, não entra item novo (`409 OrderAlreadyDispatched`, ADR-0046 item 19). */
export function isDispatched(status: FulfillmentStatus | null): boolean {
  return status === 'OUT_FOR_DELIVERY' || status === 'DELIVERED' || status === 'PICKED_UP';
}

function forwardLabel(to: FulfillmentStatus, complete: boolean): string {
  if (!complete) return fulfillmentStatusNames[to];
  return to === 'OUT_FOR_DELIVERY' ? 'Concluir e marcar saída' : 'Concluir e marcar retirado';
}

/**
 * Ações possíveis agora. Sem a produção concluída, o avanço só aparece como "Concluir e marcar" para quem também
 * tem `production.update`; quem não tem espera a cozinha (`waitingKitchen`).
 */
export function fulfillmentActions(o: FulfillmentInput, perms: FulfillmentPermissions): { actions: FulfillmentAction[]; waitingKitchen: boolean } {
  const none = { actions: [], waitingKitchen: false };
  if (!perms.canFulfill) return none;
  if (o.fulfillmentType === 'DINE_IN' || o.fulfillmentStatus === null) return none;
  if (o.acceptanceStatus !== undefined && o.acceptanceStatus !== 'ACCEPTED') return none;
  if (o.cancellationStatus === 'CANCELED') return none;

  const completed = o.productionStatus === 'COMPLETED';
  const forward = (to: FulfillmentStatus): FulfillmentAction | null => {
    if (!NEEDS_COMPLETED.includes(to) || completed) return { to, label: forwardLabel(to, false), kind: 'forward', completeProduction: false };
    if (!perms.canProduction) return null;
    return { to, label: forwardLabel(to, true), kind: 'forward', completeProduction: true };
  };
  const back = (to: FulfillmentStatus, label: string): FulfillmentAction => ({ to, label, kind: 'back', completeProduction: false });

  if (o.fulfillmentType === 'DELIVERY') {
    switch (o.fulfillmentStatus) {
      case 'PENDING': {
        const a = forward('OUT_FOR_DELIVERY');
        return a ? { actions: [a], waitingKitchen: false } : { actions: [], waitingKitchen: true };
      }
      case 'OUT_FOR_DELIVERY':
        return { actions: [forward('DELIVERED')!, back('PENDING', 'Voltou sem entregar')], waitingKitchen: false };
      case 'DELIVERED':
        return { actions: [back('OUT_FOR_DELIVERY', 'Desfazer "Entregue"')], waitingKitchen: false };
      default:
        return none;
    }
  }
  switch (o.fulfillmentStatus) {
    case 'PENDING': {
      const a = forward('PICKED_UP');
      return a ? { actions: [a], waitingKitchen: false } : { actions: [], waitingKitchen: true };
    }
    case 'PICKED_UP':
      return { actions: [back('PENDING', 'Desfazer "Retirado"')], waitingKitchen: false };
    default:
      return none;
  }
}
