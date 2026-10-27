# ADR-0023 — Numeração de pedidos reiniciada por expediente

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** Lacuna em RN-004.8 ("número sequencial por estabelecimento")
- **Relacionados:** ADR-0019
- **Refinado por:** ADR-0039. A sequência passa a ser por **data operacional** (`BusinessDateCounter`), não por expediente, e a unicidade passa a `(tenant_id, business_date, order_number)`. O restante deste ADR continua valendo.

## Contexto

O número do pedido é falado em voz alta entre balcão e cozinha. Números longos e contínuos ("4822") atrapalham, e o épico não diz se a sequência reinicia. Comandas físicas de quilo já têm numeração impressa.

## Decisão

- `OrderNumber` é **sequencial por expediente** (1, 2, 3…), compartilhado entre as três modalidades. É gerado no servidor com `UPDATE business_day SET next_order_number = next_order_number + 1 ... RETURNING` dentro da transação de criação.
- Unicidade: `(tenant_id, business_day_id, order_number)`.
- A identidade real do pedido é o `Id` (UUID). O número é só para exibição.
- `KILO` (e opcionalmente `PF`) aceita **`TabNumber`**, o número da comanda física ou da mesa, informado pelo usuário e não único.
- Em telas que mostram expedientes diferentes, o número é exibido com a data (`#12 · 27/10`).

## Alternativas consideradas

- **Sequência contínua por tenant:** números grandes e pouco práticos no balcão.
- **Sequência por modalidade:** "Marmita 5" e "PF 5" se confundem na cozinha.

## Consequências

- O número só existe depois da confirmação do servidor. Rascunhos locais mostram "Rascunho" e não um número provisório.
