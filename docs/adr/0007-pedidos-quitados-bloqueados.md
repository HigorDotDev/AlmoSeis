# ADR-0007 — Pedidos quitados bloqueados para edição financeira comum

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** EP-001 RN-004.6, RN-009.7, §16
- **Relacionados:** ADR-0021 (pagamento parcial), ADR-0022 (cancelamento e estorno)

## Contexto

Editar os itens de um pedido já pago faz o caixa divergir sem deixar rastro.

## Decisão

Com `IsPaid = true`, itens, quantidades, taxa de entrega e valores **não podem ser editados** pelos endpoints comuns (`409 OrderAlreadyPaid`). As correções só acontecem por operações financeiras específicas e auditáveis: estorno de pagamento (que pode destravar o pedido, ADR-0022) ou ajuste pós-fechamento (ADR-0014). Observações e status de produção continuam editáveis.

Pedidos **parcialmente pagos** continuam editáveis, mas o total nunca pode ficar abaixo do valor já pago (`422 TotalBelowPaidAmount`).

## Consequências

- O bloqueio é validado no domínio (agregado `Order`), não só na interface.
