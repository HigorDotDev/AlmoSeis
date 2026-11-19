# ADR-0046 — Aceite do pedido web e status de atendimento/entrega

- **Status:** Proposto
- **Data:** 2026-11-19
- **Origem:** EP-002, RF-206 e RF-207; decisões do dono do produto (itens 2 e 6) em 2026-11-19
- **Relacionados:** ADR-0005, ADR-0006 (refinado), ADR-0007, ADR-0019, ADR-0022, ADR-0023/ADR-0039 (refinados), ADR-0028 (complementado), ADR-0031, ADR-0032, ADR-0044, ADR-0047, ADR-0049, ADR-0050

## Contexto

O dono do produto decidiu:

- pedido **pago online** vai direto para a cozinha;
- pedido **"pagar na entrega/retirada"** passa por **aceite ou recusa** no app antes de ir para a produção;
- o app identifica os pedidos do site e ganha os status **"Saiu para entrega"** e **"Entregue"**.

Hoje o pedido tem três dimensões independentes: produção (`OPEN`/`IN_PROGRESS`/`COMPLETED`), financeiro (`IsPaid`) e cancelamento (`ACTIVE`/`CANCELED`) (ADR-0006, ADR-0022). Falta onde guardar "aguardando aceite" e "saiu para entrega", e falta dizer o que acontece com número, cozinha e fechamento de um pedido que ainda não foi aceito ou foi recusado.

## Decisão

**Duas dimensões novas, independentes das atuais**

1. **`Order.AcceptanceStatus`** (entrada do pedido):

   | Valor | Significado |
   |---|---|
   | `AWAITING_PAYMENT` | Fase 2: pedido online esperando a confirmação do pagamento (ADR-0050) |
   | `AWAITING_ACCEPTANCE` | pedido web "pagar na entrega/retirada" esperando o restaurante |
   | `ACCEPTED` | pedido vale. **Todo pedido criado no app nasce `ACCEPTED`** |
   | `REJECTED` | recusado pelo restaurante, ou recusado pelo sistema ao fechar o expediente |
   | `EXPIRED` | ninguém aceitou no prazo, ou o pagamento online não chegou no prazo |
   | `WITHDRAWN` | o cliente desistiu antes do aceite |

   `REJECTED`, `EXPIRED` e `WITHDRAWN` são finais. Um pedido não aceito **não é cancelado** no sentido do ADR-0022: ele nunca foi venda.

2. **`Order.FulfillmentStatus`** (atendimento), só para `DELIVERY` e `PICKUP` (nulo em `DINE_IN`):

   | Valor | Entrega | Retirada |
   |---|---|---|
   | `PENDING` | ainda no restaurante | ainda não retirado |
   | `OUT_FOR_DELIVERY` | "Saiu para entrega" | — |
   | `DELIVERED` | "Entregue" | — |
   | `PICKED_UP` | — | "Retirado" |

   **"Pronto para retirar" não é um valor gravado.** É derivado: `PICKUP` + `ProductionStatus = COMPLETED` + `FulfillmentStatus = PENDING`. Assim o balcão não precisa tocar duas vezes quando a cozinha conclui.

3. As dimensões continuam independentes (ADR-0006): mudar atendimento não mexe no financeiro nem na produção. Pedido entregue e não pago continua com saldo e vira fiado no fechamento (ADR-0021).

**Regras do pedido não aceito** (`AcceptanceStatus ≠ ACCEPTED`)

4. **Não tem número.** Não aparece na cozinha (`ProductionStatus` nulo). Não pode ser editado, receber pagamento manual nem ser cancelado. Não gera `OrderChangeEvent`. Não vincula `Customer`. **Não entra no fechamento** (nem vendido, nem pendente, nem quantidade).
5. Pertence ao expediente aberto no envio (`BusinessDayId`), porque a página só aceita pedido com expediente aberto (ADR-0019).

**Aceite**

6. Exige a permissão `orders.accept`, `If-Match` e `Idempotency-Key`. Pode informar um **tempo estimado** opcional (15, 30, 45 ou 60 min), mostrado ao cliente.
7. Na mesma transação: `AcceptanceStatus = ACCEPTED`, recebe o **`OrderNumber`** da data operacional (`BusinessDateCounter`, ADR-0039), `ProductionStatus = OPEN`, `FulfillmentStatus = PENDING`, vínculo com `Customer` (ADR-0044) e `AcceptedAt`/`AcceptedByEmployeeId`. Para a cozinha, **o pedido nasce no aceite**.
8. Recusados, expirados e desistências **nunca recebem número**. A sequência do dia não tem buracos.
9. Dois funcionários aceitando ao mesmo tempo: o segundo recebe `409` com o estado atual ("Já aceito por Ana"), pelo `If-Match` (ADR-0034).
10. Pedido pago online (Fase 2) é aceito **pelo sistema** na confirmação do pagamento (`Origin = ORDER_PAYMENT_WEBHOOK`), com o mesmo efeito do item 7.
11. Depois do aceite, o pedido "pagar na entrega" é um pedido comum: pode ser editado (o cliente vê o novo total no acompanhamento, ADR-0047), recebe pagamento manual e pode ser cancelado pelo ADR-0022.

**Recusa**

12. Exige `orders.accept`, `If-Match` e um **motivo da lista**, com texto opcional de 5 a 200 caracteres (obrigatório em `OTHER`). A interface avisa: "o cliente verá esta mensagem".

    | Código | O que o cliente vê |
    |---|---|
    | `ITEM_UNAVAILABLE` | "Um item do seu pedido acabou." + texto |
    | `OUT_OF_DELIVERY_AREA` | "Não entregamos nesse endereço." + texto |
    | `TOO_BUSY` | "Estamos com muitos pedidos agora." + texto |
    | `CLOSING_SOON` | "Estamos fechando." + texto |
    | `SUSPECTED_PRANK` | "O restaurante não pôde aceitar seu pedido." (o texto **não** é mostrado) |
    | `OTHER` | texto |

    Toda recusa mostra também o contato do restaurante e o botão "Fazer outro pedido".
13. Sistema: ao **finalizar o fechamento**, os pedidos `AWAITING_ACCEPTANCE` do expediente são recusados com o código `BUSINESS_DAY_CLOSED` ("O restaurante encerrou o atendimento"), na mesma transação. A confirmação do fechamento avisa: "2 pedidos do site aguardando aceite serão recusados". O fechamento **não fica bloqueado** por eles.

**Prazo de aceite** (*decisão a confirmar com o usuário*)

14. **Proposta: 10 minutos** a partir do envio (`AcceptanceDeadline`). Um job a cada 30 s passa os vencidos para `EXPIRED` (`Origin = SYSTEM`). O cliente vê: "O restaurante não confirmou seu pedido a tempo. Nada foi cobrado." + contato + "Tentar de novo" (o carrinho é recarregado do `localStorage`).
15. O app mostra a contagem regressiva no card. Aos 3 minutos sem resposta, um segundo push de lembrete (ADR-0049).
16. Aceite que chega depois do vencimento → `409 WebOrderExpired`. O job e o aceite disputam pela `version`: só um vence.

**Desistência do cliente**

17. Enquanto `AWAITING_ACCEPTANCE` (ou `AWAITING_PAYMENT`), o cliente pode desistir pela página de acompanhamento → `WITHDRAWN`. Depois do aceite, só falando com o restaurante.

**Transições do atendimento** (`orders.fulfillment.update`, `If-Match`, confirmação, RF-012)

18. Transições permitidas:

    | De | Para | Regra |
    |---|---|---|
    | `PENDING` | `OUT_FOR_DELIVERY` | só `DELIVERY`; exige `ProductionStatus = COMPLETED` |
    | `OUT_FOR_DELIVERY` | `DELIVERED` | — |
    | `OUT_FOR_DELIVERY` | `PENDING` | "Voltou sem entregar" (cliente não atendeu). Pode ser cancelado depois pelo ADR-0022 |
    | `PENDING` | `PICKED_UP` | só `PICKUP`; exige `ProductionStatus = COMPLETED` |
    | `DELIVERED` / `PICKED_UP` | passo anterior | correção de toque errado, com confirmação; auditada |

    Quando a produção não está concluída, o app oferece **"Concluir e marcar como saiu/retirado"**, uma única operação no servidor que exige `production.update` e `orders.fulfillment.update` e faz as duas transições na mesma transação.
19. **Item acrescentado depois que o pedido saiu** (*a confirmar*): com `FulfillmentStatus` em `OUT_FOR_DELIVERY`, `DELIVERED` ou `PICKED_UP`, incluir item → `409 OrderAlreadyDispatched`. O atendente cria outro pedido. A regra RN-007.10 (voltar para `IN_PROGRESS`) continua valendo enquanto o pedido está `PENDING`.
20. **Vale também para pedidos de entrega e retirada criados no app** (recomendação; *a confirmar*). O uso é opcional: se ninguém marcar, o pedido fica `PENDING` e nada é bloqueado. O fechamento só informa "N entregas sem marcação de entregue".

**Identificação no app**

21. Pedidos com `Origin = WEB` têm o selo **"Site"** e o aviso "telefone não verificado". Pedidos `AWAITING_ACCEPTANCE` aparecem numa faixa **"Novos do site"** no topo da aba Marmitas e da aba Pedidos, para quem tem `orders.view`; só quem tem `orders.accept` vê os botões. Se o mesmo telefone tem outro pedido nos últimos 30 minutos, o card mostra "Possível duplicado".

**Permissões novas** (complementa o ADR-0028)

22. Novas permissões e perfis:

    | Permissão | OWNER | MANAGER | ATTENDANT | KITCHEN | CASHIER |
    |---|:-:|:-:|:-:|:-:|:-:|
    | `orders.accept` (aceitar/recusar pedido do site) | ✓ | ✓ | ✓ | | ✓ |
    | `orders.fulfillment.update` (saiu, entregue, retirado) | ✓ | ✓ | ✓ | ✓ | ✓ |
    | `storefront.manage` (página, slug, pausa, entrega por distância) | ✓ | ✓ | | | |
    | `payments.online.manage` (recebedor e recebimento online, Fase 2) | ✓ | | | | |

**Eventos de tempo real e auditoria** (ADR-0009, ADR-0031)

23. Eventos novos:

    | `eventType` | Grupo | `data` |
    |---|---|---|
    | `OrderAwaitingAcceptance` | orders | `fulfillmentType`, `marmitaCount`, `totalAmount`, `acceptanceDeadline` |
    | `OrderAccepted` | orders | `orderNumber`, `acceptedBy` |
    | `OrderCreated` (já existe) | production | emitido **no aceite** para a cozinha |
    | `OrderRejected` | orders | `acceptanceStatus` (`REJECTED`/`EXPIRED`/`WITHDRAWN`), `reasonCode` |
    | `OrderFulfillmentChanged` | orders, production | `orderNumber`, `from`, `to` |

    Nenhum evento leva nome, telefone ou endereço do cliente. Tudo entra no feed (ADR-0032).
24. Auditoria: `WebOrderSubmitted` (`Origin = WEB`, sem ator), `OrderAccepted`, `OrderRejected` (com o motivo), `WebOrderExpired` e `WebOrderAutoRejected` (`SYSTEM`), `WebOrderWithdrawn` (`WEB`), `OrderFulfillmentChanged`.

## Alternativas consideradas

- **Um único `FulfillmentStatus` linear com o aceite dentro** (aguardando aceite → aceito → saiu → entregue, mais recusado): menos colunas, mas mistura duas perguntas diferentes. "Recusado" e "expirado" são estados finais que tiram o pedido de tudo (número, cozinha, fechamento), parecidos com o cancelamento. "Saiu para entrega" é logística de um pedido que vale. No modelo linear, toda consulta de cozinha e de fechamento teria de excluir uma lista de valores de logística.
- **Estender `ProductionStatus`** com `OUT_FOR_DELIVERY`/`DELIVERED`: viola o espírito do ADR-0006 (cozinha ≠ entregador), não serve para `DINE_IN`, e a regra RN-007.10 (voltar para `IN_PROGRESS` ao acrescentar item) apagaria o "saiu para entrega".
- **Entidade separada `WebOrderRequest`**, convertida em `Order` no aceite: o pedido pendente fica fora de todas as listas sem filtro, mas duplica preço, itens, snapshot e acompanhamento, e o pedido online (que vai direto) teria outro caminho. Contraria o ADR-0005 (um domínio de pedidos).
- **Número no envio:** recusas deixariam buracos na sequência do dia e a cozinha veria números que nunca existiram.
- **Fechamento bloqueado por pedido pendente:** trava a operação por causa de um pedido que talvez seja trote.
- **Recusa sem motivo:** o cliente não sabe se deve tentar de novo, pedir outra coisa ou desistir.

## Consequências

- `Order` ganha `AcceptanceStatus`, `AcceptanceDeadline`, `AcceptedAt`, `AcceptedByEmployeeId`, `EstimatedReadyMinutes`, `RejectionReasonCode`, `RejectionMessage`, `RejectedAt`, `RejectedByEmployeeId`, `FulfillmentStatus`, `FulfillmentStatusChangedAt`. `OrderNumber` passa a aceitar nulo (só enquanto não aceito). A unicidade `(tenant_id, business_date, order_number)` passa a valer só para não nulos.
- Todas as consultas de cozinha, fechamento, pendências por cliente e listas de pedidos passam a filtrar `AcceptanceStatus = ACCEPTED`. Teste obrigatório: um pedido recusado nunca aparece no fechamento.
- O resumo do fechamento ganha um indicador informativo "Pedidos do site recusados/expirados" (quantidade), fora do vendido.
- Revisitar o prazo de 10 minutos e o limite do item 19 depois de duas semanas de piloto.
