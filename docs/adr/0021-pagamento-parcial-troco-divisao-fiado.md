# ADR-0021 — Taxa de entrega, pagamento parcial, troco, divisão de conta e fiado

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** Lacuna em RF-004, RF-009, RF-010, RF-011
- **Relacionados:** ADR-0007, ADR-0013, ADR-0016, ADR-0024
- **Refinado por:** ADR-0048 (Proposto): taxa de entrega por distância nos pedidos da página. Os pedidos do app continuam com a taxa padrão editável deste ADR.

## Contexto

O épico fala em "pagamento integral" (o que implica pagamento parcial), em recebimento posterior (fiado) e em entrega, mas não modela taxa de entrega, troco, divisão de conta nem cliente.

## Decisão

**Taxa de entrega**
- Valor padrão configurável no estabelecimento, aplicado a pedidos de `MARMITA` com `FulfillmentType = DELIVERY`.
- Pode ser editada no pedido com a permissão `orders.delivery-fee.edit`. A edição é auditada.
- Aparece separada no total do pedido e no fechamento.

**Sem desconto no MVP** (decisão explícita). Ajuste de preço só por edição de item antes do pagamento.

**Pagamento parcial**
- Um pedido aceita **vários `Payment`** com modalidades diferentes.
- `Balance = TotalAmount − Σ Payment.Amount (válidos)`; `IsPaid = Balance == 0`.
- `Payment.Amount` nunca pode ser maior que o saldo (`422 PaymentExceedsBalance`).

**Troco** (só em dinheiro)
- O pagamento em dinheiro registra `CashReceived` (opcional) e `ChangeGiven = CashReceived − Amount`.
- Para o caixa, conta só o `Amount` aplicado (ADR-0013).

**Divisão de conta** (salão)
- É um **assistente de interface**: dividir igualmente por N pessoas ou por itens selecionados.
- O resultado são N pagamentos parciais com rótulo opcional ("Pessoa 2").
- O pedido continua único. Pagamentos **não são vinculados a itens** no MVP.

**Cliente e fiado**
- Entidade **`Customer`** por tenant: nome, telefone (opcional, E.164), endereços de entrega, observação.
- O pedido de marmita referencia `CustomerId` opcional e guarda um snapshot do nome, telefone e endereço usados.
- "Fiado" = pedido com saldo depois do fechamento do expediente. Não existe entidade de crédito.
- O app consulta **pendências por cliente**, e o pagamento posterior segue o RF-011. Sem limite de crédito no MVP.

## Consequências

- `OrderPaymentConfirmed` só é emitido quando `IsPaid` passa a `true`. Pagamentos parciais emitem `OrderPaymentRegistered`.
- `Customer` é a base para o EP-002 (identificar o cliente pelo telefone do WhatsApp).
- Dados de cliente seguem o ADR-0024.

## Complementado por ADR-0058 e ADR-0059 (2026-11-22)

- A taxa de entrega também se aplica a pedidos `A_LA_CARTE` com `DELIVERY` (ADR-0058).
- No **delivery por telefone** (ADR-0059), a taxa é digitada no pedido. Hoje uma taxa diferente da padrão exige `orders.delivery-fee.edit` (OWNER e MANAGER). Se o atendente passa a digitá-la, isso muda esta regra e precisa de decisão (EP-003 Q-315) antes de virar código.
- O `Customer` passa a ser vinculado ou criado **pelo telefone** nesse fluxo, só com nome e telefone, sem endereço salvo. Fiado, pagamento parcial e troco não mudam: o pedido entregue e não pago continua com saldo.
