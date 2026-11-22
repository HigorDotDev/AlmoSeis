# ADR-0057 — "Vende no site" e preço de site (cardápio fixo e tamanhos de marmita)

- **Status:** Proposto
- **Data:** 2026-11-22
- **Origem:** EP-003, RF-309; decisões do dono do produto em 2026-11-22 (itens 5, 6 e 10)
- **Relacionados:** ADR-0020 (complementado), ADR-0021, ADR-0044 (complementado), ADR-0048, ADR-0050 (complementado, item 19), ADR-0056, ADR-0058

## Contexto

Hoje a página vende pelo mesmo preço do balcão: o tamanho da marmita (`MarmitaSize.Price`) e o preço da bebida (`Product.DefaultPrice`, com `IsVisibleOnPage`). O dono do produto decidiu que o **site tem preço próprio**:

- o estabelecimento pode **embutir no preço do site** a taxa da transação online e o custo do serviço, em vez de cobrar uma "taxa de serviço" separada no total;
- a plataforma cobrará do estabelecimento **um percentual sobre as vendas do site, descontada a taxa do Asaas**. A forma de cobrança fica para depois.

O salão e o delivery por telefone usam o mesmo preço (o de balcão). Só o site tem preço próprio.

## Decisão

**Chave e preço**

1. Cada unidade vendável ganha a chave **"Vende no site"** (`SellsOnSite`), com a mesma aparência da chave de disponibilidade, e o **preço de site** (`SitePrice`), que só aparece com a chave ligada:

   | Onde | Unidade |
   |---|---|
   | Prato sem tamanho | o prato |
   | Prato com tamanho | **cada tamanho** do prato |
   | Bebida | **cada tamanho** de bebida |
   | Combo | o combo |
   | Marmita (restaurante) | **cada tamanho de marmita** (`MarmitaSize`) |

2. Ao ligar a chave, o preço de site vem preenchido com o preço de balcão. Com a chave ligada, o preço de site é obrigatório e maior que zero. Pode ser **maior, menor ou igual** ao de balcão.
3. Desligada, a unidade **não aparece** na página. Um prato com tamanho aparece na página se pelo menos um tamanho vende no site.
4. Um combo vende no site pela chave do próprio combo. Os componentes não precisam vender no site sozinhos; basta estarem vendáveis (ADR-0056 item 23).
5. Bebidas do restaurante (`Product` `BEVERAGE`): *recomendação (Q-307)*: o `IsVisibleOnPage` atual passa a ser o "vende no site" delas, e elas ganham `SitePrice`. Adicionais (`Product` `ADDON`, opções de grupo) e acréscimos de sabor: *recomendação (Q-308)*: **mesmo preço** no balcão e no site nesta fase.

**Qual preço o pedido usa**

6. **Todo pedido do site usa o preço de site**, qualquer que seja a forma de pagamento ("pagar na entrega/retirada" ou online, ADR-0050). Salão, retirada e entrega registrados no app usam o preço de balcão.
7. O pedido grava a **tabela de preço** usada (`Order.PriceList`: `COUNTER` ou `SITE`), além do preço unitário de cada item no snapshot (ADR-0020). `Origin = WEB` → `SITE`; `Origin = APP` → `COUNTER`.
8. *Recomendação (Q-309):* item **acrescentado no app** a um pedido do site já aceito usa o preço de site do item, quando existe, e o de balcão quando o item não vende no site. O pedido nunca mistura preços sem estar gravado no snapshot de cada linha.
9. A **taxa de entrega continua separada** do preço dos itens (ADR-0021, ADR-0048), nunca embutida pelo sistema.
10. Mudança de preço de site entre a abertura da página e o envio → `409 CartChanged`, como hoje (ADR-0044 item 21).

**Não é desconto nem acréscimo**

11. Preço de site menor que o de balcão **não é desconto** (ADR-0021 continua: sem desconto), e maior não é taxa de serviço. É outra tabela de preço, decidida antes da venda. O vendido do fechamento soma o que foi cobrado.
12. O fechamento ganha a informação "Vendido pelo site" (soma dos pedidos com `PriceList = SITE`), só informativa, para o estabelecimento conferir a cobrança da plataforma no futuro.

**Motivo de negócio e cobrança da plataforma (em aberto)**

13. Exemplo **ilustrativo** de como o estabelecimento pode embutir a taxa (taxas do Asaas a confirmar, ADR-0050):

    | | Balcão | Site |
    |---|---:|---:|
    | X-Burguer | R$ 22,00 | R$ 23,90 |
    | Combo X-Burguer | R$ 32,00 | R$ 34,90 |

    Pedido do site com 2 X-Burguer em Pix: 2 × R$ 23,90 + entrega R$ 5,00 = **R$ 52,80**. Taxa Pix do Asaas R$ 1,08 → R$ 51,72. Com um percentual **ilustrativo** de 2% da plataforma sobre esse valor (R$ 1,03), o estabelecimento fica com R$ 50,69, contra R$ 49,00 do mesmo pedido a preço de balcão.
14. **O percentual, a base de cálculo (com ou sem a taxa de entrega), se vale também para os pedidos "pagar na entrega/retirada" (que não passam pelo Asaas) e a forma de cobrança ficam em aberto (Q-310).** Nada disso é implementado nesta fase: o sistema só grava a `PriceList` e mostra o "Vendido pelo site".

**Migração**

15. Para nenhuma página existente mudar de preço: todo `MarmitaSize` ativo recebe `SellsOnSite = true` e `SitePrice = Price`; toda bebida com `IsVisibleOnPage = true` recebe `SitePrice = DefaultPrice`. O estabelecimento ajusta depois.

## Alternativas consideradas

- **Taxa de serviço separada no total** (texto anterior do ADR-0050 item 19): transparente, mas o dono do produto preferiu o preço de site. Taxa de serviço visível afasta o cliente da página e vira comparação direta com o balcão.
- **Percentual automático sobre o preço de balcão** (site = balcão + X%): menos cadastro, mas dá preços quebrados (R$ 23,47) e tira do estabelecimento a decisão de preço.
- **Preço de site por forma de pagamento** (Pix mais barato que cartão): mais justo com a taxa de cada método, mas o dono do produto decidiu que o pedido do site usa o preço de site independentemente da forma de pagamento.
- **Preço de site só nos pedidos pagos online:** o cliente veria o preço mudar ao trocar a forma de pagamento.

## Consequências

- `MarmitaSize`, `Dish`, `DishSize`, `BeverageSize`, `Combo` e (Q-307) `Product` `BEVERAGE` ganham `SellsOnSite` e `SitePrice`. `Order` ganha `PriceList`.
- A página e o envio (`/public/*`) passam a ler o preço de site; o recálculo do servidor (ADR-0044 item 21) usa a mesma regra.
- O snapshot do fechamento ganha a coluna informativa "Vendido pelo site". Fechamentos anteriores à migração ficam com 0, como as colunas informativas do ADR-0046.
- Quando a cobrança da plataforma for decidida (Q-310), vira um ADR próprio, provavelmente no módulo Billing (ADR-0017).
