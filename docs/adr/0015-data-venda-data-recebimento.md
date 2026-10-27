# ADR-0015 — Separação entre data da venda e data do recebimento

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** EP-001 RN-011.2–RN-011.6, §6.4, §16
- **Refinado por:** ADR-0019 (BusinessDate vem do expediente aberto)

## Contexto

Com o fiado (ADR-0021), um pedido de segunda-feira pode ser pago na terça-feira. Contabilizar a receita duas vezes ou mover a venda de dia distorce os dois fechamentos.

## Decisão

- `Order.BusinessDate` e `Order.BusinessDayId` = expediente em que o pedido foi criado (data da venda).
- `Payment.BusinessDate` e `Payment.BusinessDayId` = expediente aberto no momento do recebimento.
- Instantes são gravados em UTC (`timestamptz`). A data operacional nunca é derivada da meia-noite UTC.

## Consequências

- "Vendido" agrega por expediente do pedido e "recebido" agrega por expediente do pagamento.
