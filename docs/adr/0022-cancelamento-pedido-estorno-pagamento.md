# ADR-0022 — Cancelamento de pedido e estorno de pagamento

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** Lacuna em RF-009, RF-010 (cancelamentos e estornos), `POST /payments/{id}/reverse`
- **Relacionados:** ADR-0007, ADR-0014, ADR-0019

## Contexto

O épico lista cancelamentos e estornos como indicadores e operações auditáveis, mas não define quem pode fazê-los, a ordem entre eles nem o efeito sobre o bloqueio de pedido pago.

## Decisão

1. **Cancelar pedido** exige a permissão `orders.cancel`, um **motivo** (texto, mínimo 5 caracteres) e confirmação. O pedido vai para `CancellationStatus = CANCELED` (dimensão própria, independente da produção e do financeiro). Pedido cancelado é somente leitura.
2. **Só é possível cancelar um pedido sem pagamentos válidos.** Se houver pagamentos, é preciso estorná-los antes (`409 OrderHasPayments`). Não existe cancelamento que estorna automaticamente.
3. **Estornar pagamento** exige a permissão `payments.reverse`, um motivo e `Idempotency-Key`. O `Payment` original vira `REVERSED` (nunca é apagado) e é criado um registro de estorno no **expediente aberto** (ADR-0014). Estorno em dinheiro reduz o dinheiro esperado desse expediente.
4. Se o estorno deixar saldo, o pedido volta a `IsPaid = false` e **destrava a edição** (ADR-0007).
5. Remover itens de um pedido não pago é edição comum (auditada, gera alerta para a cozinha), não cancelamento.
6. No fechamento, pedidos cancelados saem do "vendido" e aparecem no indicador **Cancelamentos** (quantidade e valor). Estornos aparecem em **Estornos**.

## Alternativas consideradas

- **Cancelamento com estorno automático:** uma ação a menos, mas esconde o efeito financeiro e facilita erro em pedido de fiado parcialmente pago.

## Consequências

- O cancelamento notifica a cozinha (`OrderCanceled`).
