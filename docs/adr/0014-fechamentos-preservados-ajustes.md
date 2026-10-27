# ADR-0014 — Fechamentos preservados, com ajustes posteriores rastreáveis

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** EP-001 RF-011, RNF-004, §16

## Contexto

Pagamentos, estornos e correções podem acontecer depois que um expediente foi fechado.

## Decisão

Um fechamento finalizado é **imutável**. Qualquer efeito financeiro posterior vira uma **nova operação** (pagamento, estorno ou `ClosingAdjustment`) registrada no expediente **em que ocorreu**, referenciando o pedido ou fechamento original. Consultas históricas podem mostrar a situação atual do pedido ao lado do snapshot original, sem alterá-lo.

## Consequências

- Não existe "reabrir fechamento" no MVP.
- Relatórios distinguem a data da venda e a data do recebimento (ADR-0015).
