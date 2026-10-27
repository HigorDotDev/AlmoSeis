# ADR-0006 — Independência entre produção e pagamento

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** EP-001 RN-007.8, RN-009.10, §16

## Contexto

Há marmitas pagas antes de serem preparadas (pagamento antecipado por Pix) e PFs comidos antes de serem pagos. Juntar os dois estados num único status gera combinações impossíveis de representar.

## Decisão

`ProductionStatus` (`OPEN`, `IN_PROGRESS`, `COMPLETED`) e a situação financeira (`IsPaid`, derivada dos pagamentos válidos, ADR-0021) são **dimensões independentes** do pedido. Mudar a produção nunca altera o financeiro, e vice-versa.

## Alternativas consideradas

- **Status único linear** (aberto → pago → entregue): não cobre o pagamento antecipado nem o pós-pago.

## Consequências

- A interface diferencia visualmente status operacional e financeiro (RNF-001).
- Pedido pago continua transitando na produção (RN-007.11). Pedido concluído e não pago pode receber itens e volta para `IN_PROGRESS` (RN-007.10).
