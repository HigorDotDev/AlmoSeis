# ADR-0055 — Abas do app por comportamento do estabelecimento

- **Status:** Proposto
- **Data:** 2026-11-22
- **Origem:** EP-003, RF-303; decisão do dono do produto em 2026-11-22 (item 4)
- **Relacionados:** ADR-0028, ADR-0046 (complementado, item 21), ADR-0053, ADR-0054, ADR-0058, ADR-0059

## Contexto

O app tem quatro abas superiores filtradas por permissão (`08-mobile.md` §8.8): **Marmitas**, **Salão** (com as sub-abas PF e Quilo), **Pedidos** (painel de produção) e **Fechamento**. Numa lanchonete, as abas Marmitas e PF/Quilo não servem. Ela precisa de comandas no salão, de uma aba de delivery e da cozinha.

## Decisão

1. As abas dependem dos **módulos** do tenant (ADR-0054 item 12) e, como hoje, das permissões (ADR-0028). O app nunca decide pelo rótulo.
2. **Só `RESTAURANT`** (todos os tenants atuais): nada muda.

   | Aba | Conteúdo | Permissão |
   |---|---|---|
   | Marmitas | pedidos de marmita, entrega e retirada | `orders.view` |
   | Salão | sub-abas PF e Quilo | `orders.view` |
   | Pedidos | painel de produção | `production.view` |
   | Fechamento | expediente e fechamento | `closing.view` |

3. **Só `SNACK_BAR`:**

   | Aba | Conteúdo | Permissão |
   |---|---|---|
   | Salão | só comandas à la carte, **sem sub-abas**. Abrir comanda segue o fluxo do PF, com identificação por número de comanda ou mesa, como hoje no PF/Quilo (ADR-0058) | `orders.view` |
   | Delivery | entrega e retirada, do telefone e do site; a faixa "Novos do site" fica aqui (ADR-0059) | `orders.view` |
   | Cozinha | o mesmo painel de produção da aba Pedidos, com outro nome | `production.view` |
   | Fechamento | igual ao restaurante | `closing.view` |

4. **Com os dois comportamentos** (só quando o admin habilitar, ADR-0054 item 5). *Recomendação, a confirmar (Q-304):*

   | Aba | Conteúdo |
   |---|---|
   | Marmitas | como hoje |
   | Salão | sub-abas PF, Quilo e **Comandas** |
   | Delivery | entrega e retirada do cardápio fixo |
   | Cozinha | painel de produção (a aba Pedidos passa a se chamar Cozinha) |
   | Fechamento | igual |

   Uma comanda **nunca mistura** PF ou quilo com itens do cardápio fixo: são `OrderType` diferentes (ADR-0058).
5. O nome da aba de produção é "Pedidos" quando o tenant **só** tem `RESTAURANT` e "Cozinha" nos outros casos. A rota interna continua a mesma (`pedidos`); muda só o título. A preferência "última aba usada" ignora uma aba que deixou de existir.
6. No menu lateral, as telas de cadastro também seguem os módulos: tamanhos de marmita, cardápio do dia/semana e preço do quilo só com `MARMITA`, `DAILY_MENU`, `KILO`; o cardápio fixo (Pratos, Bebidas, Combos) só com `FIXED_MENU`.
7. Vale também no navegador do desktop (ADR-0053): mesma regra, mesmas abas.

## Alternativas consideradas

- **Mesmas quatro abas para todos, com o conteúdo trocado:** "Marmitas" numa açaiteria confunde. O dono do produto escolheu abas próprias.
- **Aba Delivery também para a marmita:** o dono do produto decidiu que a marmita continua com a entrega na aba Marmitas. O delivery é só do cardápio fixo.
- **Sub-aba "Comandas" na lanchonete:** com um tipo só de comanda, a sub-aba é um toque a mais.

## Consequências

- `mobile/app/(app)/(tabs)/_layout.tsx` passa a filtrar as abas por módulo, além da permissão, e ganha a rota `delivery`. O `SalaoTab` ganha o modo "só comandas" e a sub-aba Comandas.
- Testes de interface: cada combinação de comportamento mostra as abas certas, e um funcionário só com `production.view` vê só a Cozinha.
- O `ux-agent` desenha a aba Delivery e o Salão da lanchonete (`10-ux.md`).
