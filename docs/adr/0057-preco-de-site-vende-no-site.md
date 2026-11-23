# ADR-0057 — "Vende no site" e preço de site (cardápio fixo e tamanhos de marmita)

- **Status:** Proposto
- **Data:** 2026-11-22
- **Atualizado:** 2026-11-23, decisões do usuário sobre a revisão do EP-003 (itens 2 e 12; ver "Complemento (2026-11-23)")
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
   *(Complementado em 2026-11-23, I6: a **bebida do restaurante** (`Product` `BEVERAGE`) aceita preço de site **`>= 0`**; sem preço de site informado vale o de balcão também quando é `0,00`, e a bebida de `0,00` continua na página. As outras unidades continuam `> 0`. O tamanho de marmita criado sem `sellsOnSite` continua fora do site.)*
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
    *(Complementado em 2026-11-23, M1: o "Vendido pelo site" passa a ser **sem a taxa de entrega**, Σ (total − taxa), e a taxa dos pedidos do site vai para um número próprio, "Taxa de entrega do site". Ver o complemento abaixo.)*

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

## Complemento (2026-11-23, revisão do EP-003: itens 2 e 12, detalhados em `docs/05-ddl.md` §5.17 0012, `docs/06-contratos-api.md` §6.5 e §6.11 e `docs/07-backend.md` §7.8 e §7.9)

Decisões do usuário sobre os achados I6 e M1 da revisão do EP-003.

**Item 2: bebida do restaurante com preço de site `0,00` (I6)**

- A bebida do restaurante (água da casa, cafezinho) pode ter **preço de site `>= 0`** (`products_site_price_check`, 0012). `ck_products_site_price` continua: só `BEVERAGE` tem preço de site, e o adicional continua sem ele (Q-308).
- Bebida visível **sem** preço de site informado recebe o de balcão, **mesmo `0,00`**: no cadastro ela nasce visível com preço de site `0,00` (antes nascia fora do site), e ao ser mostrada de novo sem preço também (antes, `422`). Apagar o preço de site de uma bebida que continua visível continua `422`. Preço de site negativo → `422`.
- A bebida de `0,00` que a 0009 tirou da página volta pela correção de dados da 0012 (05 §5.17), salvo as que o dono escondeu.
- Tamanho de marmita, prato, tamanho de prato, tamanho de bebida do cardápio fixo e combo **continuam `> 0`**.
- **Tamanho de marmita criado sem `sellsOnSite`** continua `false` (fora do site). O app do EP-003 sempre envia a chave, e a versão mínima do app sobe na mesma release que leva esta API (09 §9.4, "Ordem de deploy do EP-003"), então o app antigo não cria tamanho por muito tempo. Os tamanhos **inativos** que nunca tiveram a chave ligada recebem `sellsOnSite = true` pela correção da 0012.

**Item 12: "Vendido pelo site" sem a taxa de entrega (M1)**

- `siteSoldTotal` = Σ (`total_amount` − `delivery_fee`) dos pedidos `SITE` aceitos e ativos do expediente: os **itens a preço de site**.
- Novo `siteDeliveryFeeTotal` = Σ `delivery_fee` desses pedidos, contido no `deliveryFeesTotal`. Os dois juntos são a parte do site no vendido (`ck_daily_closings_site_sold`: `site_sold_total + site_delivery_fee_total <= total_sold`).
- Exemplo: retirada do site R$ 40,00 + entrega do site R$ 45,00 (R$ 40,00 + R$ 5,00 de taxa) + PF do app R$ 48,00 → vendido R$ 133,00; "Vendido pelo site" R$ 80,00; "Taxa de entrega do site" R$ 5,00.
- Vale no resumo parcial, no snapshot do fechamento (coluna nova `daily_closings.site_delivery_fee_total`) e no relatório mensal (soma dos snapshots).
- Continua só informativo. A base da cobrança da plataforma (com ou sem a taxa) segue em aberto (Q-310): com os dois números no snapshot, qualquer das bases sai dele.
- Fechamentos existentes: a 0012 separa a taxa quando os pedidos do expediente ainda batem com o snapshot; os demais ficam com a taxa dentro do `siteSoldTotal` e `siteDeliveryFeeTotal = 0` (limitação registrada em 09 §9.4).

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
