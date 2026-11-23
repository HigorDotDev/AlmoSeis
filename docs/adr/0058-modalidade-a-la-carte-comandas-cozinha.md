# ADR-0058 — Modalidade `A_LA_CARTE`: comandas, entrega e retirada do cardápio fixo; combo no pedido e na cozinha

- **Status:** Proposto
- **Data:** 2026-11-22
- **Origem:** EP-003, RF-310 e RF-311; decisões do dono do produto em 2026-11-22 (itens 4 a 6)
- **Relacionados:** ADR-0005 (complementado), ADR-0006, ADR-0007, ADR-0013, ADR-0020 (complementado), ADR-0021 (complementado), ADR-0023/ADR-0039, ADR-0034, ADR-0046, ADR-0054, ADR-0056, ADR-0057, ADR-0059

## Contexto

O `Order` tem três modalidades (`MARMITA`, `PF`, `KILO`, ADR-0005), e o código amarra cada uma a um atendimento: marmita é entrega ou retirada, PF e quilo são no salão (`Order.EnsureFulfillment`). Os itens vêm do cardápio do dia (`OrderItemResolver` lê o `DAILY`), e o adicional só pode ser filho de uma marmita ou de um PF, num nível só.

A lanchonete vende o mesmo cardápio fixo no salão, na entrega e na retirada, e tem combos: uma linha com preço fechado que vira vários itens na cozinha, com adicionais por prato.

## Decisão

**Modalidade**

1. Novo `OrderType = A_LA_CARTE`. Exige o módulo `A_LA_CARTE` (salão) ou `DELIVERY` (entrega e retirada) do ADR-0054; sem ele, `403 ModuleNotEnabled`.
2. Atendimento permitido por modalidade:

   | `OrderType` | `DINE_IN` | `DELIVERY` | `PICKUP` |
   |---|:-:|:-:|:-:|
   | `MARMITA` | | ✓ | ✓ |
   | `PF`, `KILO` | ✓ | | |
   | `A_LA_CARTE` | ✓ | ✓ | ✓ |

3. **Itens só do cardápio fixo** (ADR-0056). Um pedido `A_LA_CARTE` não aceita marmita, PF, pesagem, `Product` `BEVERAGE` nem `Product` `ADDON`. E os itens do cardápio fixo não entram em `MARMITA`, `PF` nem `KILO`. Assim uma comanda **nunca mistura** PF ou quilo com cardápio fixo (ADR-0055 item 4).
4. **Preço igual no salão e no delivery por telefone** (preço de balcão). Só o pedido do site usa o preço de site (ADR-0057).

**Comanda no salão**

5. Abrir comanda segue o fluxo do PF: identificação por **mesa** (`TableLabel`) e/ou **número de comanda** (`TabNumber`), como hoje. Nome do cliente opcional. O rótulo da cozinha é "Mesa 5" ou "Comanda 47", como no PF.
6. A comanda é um pedido comum: pagamento parcial, troco, divisão de conta e fiado (ADR-0021); quitado bloqueia (ADR-0007); cancelar exige estornar antes (ADR-0022). Dois funcionários na mesma comanda: o segundo recebe `409` pelo `If-Match` (ADR-0034) e o app recarrega.
7. Item com preparo acrescentado depois que a cozinha concluiu reabre a produção (RN-007.10).

**Tipos de item** (*recomendação técnica para o `database-agent` e o `backend-agent`*)

8. Novos valores de `OrderItemKind`:

   | Valor | O que é | Preço da linha | Vai para a cozinha |
   |---|---|---|---|
   | `FIXED_DISH` | prato avulso (com o tamanho no nome do snapshot: "Açaí 500 ml") | preço do prato ou do tamanho | conforme `RequiresPreparation` do prato |
   | `FIXED_BEVERAGE` | bebida avulsa ("Refrigerante lata 350 ml · Guaraná") | tamanho + acréscimo do sabor | não |
   | `COMBO` | o combo | preço do combo | **não aparece** como item |
   | `COMBO_PART` | componente do combo, filho do `COMBO` | `0.00`, salvo o acréscimo do sabor | como um item normal |
   | `GROUP_ADDON` | opção de grupo de adicionais, filha de `FIXED_DISH` ou de um `COMBO_PART` de prato | preço da opção | sim, junto com o pai |

9. O combo vira, no pedido, **um `COMBO`** com os seus **`COMBO_PART`** (um por componente, com a quantidade do componente) e, abaixo de cada parte de prato, os `GROUP_ADDON`. São **três níveis**, e hoje o pedido tem um nível só de filho: a regra de valor do filho passa a ser `preço unitário × quantidade × quantidade do pai × quantidade do avô`.

   Exemplo, 2 combos "X-Burguer + batata + refri" com bacon e Coca Zero:

   | Linha | Qtd. | Unitário | Total |
   |---|---:|---:|---:|
   | `COMBO` X-Burguer + batata + refri | 2 | R$ 32,00 | R$ 64,00 |
   | └ `COMBO_PART` X-Burguer | 1 | R$ 0,00 | R$ 0,00 |
   | &nbsp;&nbsp;└ `GROUP_ADDON` Bacon | 1 | R$ 4,00 | R$ 8,00 |
   | └ `COMBO_PART` Batata P | 1 | R$ 0,00 | R$ 0,00 |
   | └ `COMBO_PART` Refri lata · Coca-Cola Zero | 1 | R$ 0,50 | R$ 1,00 |
   | **Total** | | | **R$ 73,00** |

10. Regras de edição do combo: mudar a quantidade do `COMBO` reprecifica as partes e os adicionais; uma parte não é removida sozinha (remove-se o combo inteiro); observação e adicionais por parte de prato podem ser incluídos depois, como no PF. Trocar o sabor de uma parte é remover e incluir o combo de novo (*a confirmar no contrato*).

**Cozinha**

11. O painel de produção mostra as partes do combo e os pratos avulsos como **lista normal de itens**, com adicionais e observação, sem preço. O que o combo muda é só o valor. *Recomendação:* sem o nome do combo no card da cozinha.
12. Bebida não vai para a cozinha (como hoje). Um pedido só de bebidas segue a regra atual de produção; para despachar, vale o "Concluir e marcar" do ADR-0046.

**Fechamento**

13. O fechamento agrupa por `OrderType` (ADR-0005, ADR-0013) e ganha a linha **"À la carte"**. *Recomendação (Q-311):* com a abertura informativa por atendimento (salão, entrega, retirada). A taxa de entrega continua separada.

**Página**

14. O pedido do site da lanchonete é `A_LA_CARTE` com `DELIVERY` ou `PICKUP`, `Origin = WEB`, preço de site e aceite (ADR-0044, ADR-0046). O evento `OrderAwaitingAcceptance` ganha `itemCount` (pratos, combos e bebidas) ao lado do `marmitaCount`, que fica `0` nesse pedido.

## Alternativas consideradas

- **Reaproveitar `PF` para a comanda da lanchonete:** o PF só existe no salão e lê o cardápio de PF do dia; misturaria preço por prato do dia com preço permanente.
- **Combo achatado** (só as partes, com o preço rateado entre elas): a cozinha veria a mesma coisa, mas o rateio quebra centavos (R$ 32,00 / 3) e o pedido perde a informação de que foi combo.
- **Combo como uma linha só, com a composição em texto:** a cozinha não veria os adicionais de cada prato separados, e a decisão do dono do produto é a cozinha receber a lista normal.
- **Uma modalidade por atendimento** (`A_LA_CARTE_DINE_IN`, `DELIVERY`): o `FulfillmentType` já existe para isso.

## Consequências

- `Order.EnsureFulfillment`, `EnsureTopLevelKind` e a validação de filhos (`AddItems`) mudam; o `OrderItemResolver` ganha o ramo do cardápio fixo, sem `MenuNotFound`.
- `order_items` aceita dois níveis de pai (ou um caminho equivalente) e o `CHECK` dos tipos de item muda; `daily_closing_lines` aceita `A_LA_CARTE`.
- Testes: cada combinação de `OrderType` × atendimento; item do cardápio fixo recusado em PF; combo com 2 de quantidade reprecifica bacon e acréscimo; a cozinha não recebe a linha `COMBO`.

## Complemento (2026-11-22, contrato e backend da fatia 3, detalhados em `docs/06-contratos-api.md` §6.9 e `docs/07-backend.md` §7.8)

- **Módulo por atendimento (item 1):** `A_LA_CARTE` em `DINE_IN` exige o módulo `A_LA_CARTE`; em `DELIVERY` ou `PICKUP`, o módulo `DELIVERY`. Sem ele, `403 ModuleNotEnabled` antes do expediente, como as outras modalidades.
- **Entrega e retirada antes da fatia 4:** o pedido à la carte de entrega ou retirada usa os campos de entrega que a marmita já tem (endereço obrigatório na entrega, taxa padrão, cliente opcional). As exigências do delivery por telefone (nome e telefone obrigatórios, forma prevista, troco, taxa digitada na criação) entram com o ADR-0059.
- **Item 3 e Q-317:** item de outra modalidade num pedido (cardápio fixo em `MARMITA`/`PF`/`KILO`, ou `MARMITA`/`PF`/`BEVERAGE`/`ADDON` do catálogo em `A_LA_CARTE`) → `422 ValidationFailed` em `items[i].itemKind`, como a combinação incompatível de hoje.
- **Itens 9 e 10 (Q-321, Q-322):** confirmados os três níveis. A quantidade da `COMBO_PART` é a do componente e não muda sozinha (`422`); a do `COMBO` reprecifica a árvore; a parte não é removida sozinha (`422`); o adicional de grupo pode ser incluído depois numa parte de prato; trocar o sabor de uma parte é remover e incluir o combo. O `COMBO` não tem observação nem adicionais próprios.
- **Item 11:** na cozinha, a quantidade da parte é a de pratos a fazer (parte × combo); prato com `RequiresPreparation = false` também fica fora do painel, como a bebida.
- **Item 13 (Q-311):** uma linha `A_LA_CARTE` no `byOrderType` do resumo, do fechamento e do mensal, sempre presente como as outras chaves (zerada no restaurante e nos fechamentos anteriores à 0010). A abertura por atendimento ainda não é calculada nem gravada. O "Vendido pelo site" (ADR-0057 item 12) é o `siteSoldTotal`.
- **Item 14:** o `itemCount` do `OrderAwaitingAcceptance` entrou na fatia 5 (2026-11-23): unidades do primeiro nível (marmitas, pratos, combos e bebidas), também no push (`{n} itens` quando `marmitaCount` é 0). Contrato em `docs/06-contratos-api.md` §6.14 e §6.19.
