# ÉPICO EP-003 — Tipo de estabelecimento e lanchonete

> **Nota de precedência (2026-11-22):** as decisões deste épico estão nos ADRs 0054–0059 (`docs/adr/README.md`), todos `Proposto`. **Onde houver diferença, o ADR prevalece.** As decisões de negócio foram tomadas pelo dono do produto em 2026-11-22; o que ficou ambíguo ou conflita com o código atual está no §14 (questões em aberto).

**Produto:** AlmoSeis — atendimento de lanchonetes (hamburgueria, pastelaria, açaiteria e parecidos) ao lado dos restaurantes  
**Versão:** 1.0 — Requisitos iniciais  
**Status:** Planejamento  
**Plataformas:** Android, iOS e navegador do desktop (app do restaurante, ADR-0053); web (página de pedidos, ADR-0051)  
**Modelo de negócio:** a mesma assinatura por estabelecimento; um segundo comportamento soma à assinatura, cobrado fora do app (ADR-0017); percentual da plataforma sobre as vendas do site em aberto (Q-310)  
**Arquitetura:** a mesma API, o mesmo domínio de pedidos (ADR-0005) e o mesmo app

---

# 1. Visão geral

## 1.1. Descrição

Hoje todo estabelecimento do AlmoSeis é um restaurante: marmita, PF, quilo e cardápio do dia ou da semana. O EP-003 cria o **tipo de estabelecimento** e atende a **lanchonete**.

- No cadastro, o proprietário escolhe o **comportamento**: **Restaurante** ou **Lanchonete**. O comportamento define os módulos, as abas e os cadastros do app (ADR-0054, ADR-0055).
- O proprietário marca **rótulos** de uma lista fixa (Hamburgueria, Pastelaria, Açaiteria...), só informativos.
- A lanchonete trabalha com um **cardápio fixo**, sem data: pratos por categoria, com tamanho por prato, bebidas com tamanho e sabor, combos e grupos de adicionais, com pausa (ADR-0056).
- Ela vende em **comandas à la carte** no salão e em **delivery** por telefone e pelo site (ADR-0058, ADR-0059).
- Todo item pode ter um **preço de site**, inclusive os tamanhos de marmita do restaurante (ADR-0057).

Os restaurantes existentes migram como **Restaurante** e não percebem diferença, salvo o preço de site da marmita (preenchido igual ao de balcão) e a forma prevista com "troco para" no pedido por telefone.

## 1.2. Objetivos de negócio

- Abrir o mercado de lanchonetes sem um segundo produto.
- Tirar do papel a comanda à la carte e o pedido por telefone da lanchonete.
- Deixar o estabelecimento embutir no preço do site o custo da venda online (ADR-0057).
- Preparar o restaurante que também é lanchonete (habilitado pelo admin, com aumento na assinatura).
- Preparar o marketplace (EP-002 Fase 3) com rótulos padronizados.

## 1.3. Público

- **Lanchonete:** dono, atendente de balcão e de telefone, cozinha, motoboy (que não usa o app; quem registra é o funcionário).
- **Restaurante:** os mesmos perfis do EP-001, que ganham o preço de site da marmita e o pedido por telefone com forma prevista.
- **Consumidor final:** cliente da página do EP-002, que passa a ver o cardápio da lanchonete.

---

# 2. Escopo

| Fatia | Conteúdo | Situação |
|---|---|---|
| **1** | Comportamento, rótulos, cadastro, módulos, abas, migração | Detalhada |
| **2** | Cardápio fixo (categorias, pratos, tamanhos, bebidas, sabores, combos, grupos de adicionais, pausa, preço de site) e preço de site da marmita | Detalhada |
| **3** | Pedido à la carte nas comandas e na cozinha | Detalhada |
| **4** | Delivery por telefone e cliente pelo telefone | Detalhada |
| **5** | Página web com o cardápio fixo e o preço de site | Detalhada |
| Depois | Dois comportamentos no mesmo tenant (habilitação pelo admin já existe na fatia 1), cobrança do comportamento adicional, cobrança da plataforma sobre o site, limites de adicionais, combo variável, pizza meio a meio | Questões em aberto |

---

# 3. Requisitos funcionais

## RF-301 — Comportamento do estabelecimento

- RN-301.1: Comportamentos: `RESTAURANT` (Restaurante) e `SNACK_BAR` (Lanchonete). O tenant tem um conjunto; pelo menos um (ADR-0054).
- RN-301.2: No cadastro, o proprietário escolhe **exatamente um**, sem padrão pré-marcado.
- RN-301.3: O comportamento é **fixo** depois do cadastro. O app não oferece troca nem acréscimo.
- RN-301.4: Acrescentar um segundo comportamento é ação do **administrador da plataforma** (`/admin`, ADR-0025), mediante aumento na assinatura. O app nunca oferece a contratação nem mostra preço (ADR-0017).
- RN-301.5: Todos os tenants existentes migram como `RESTAURANT`.
- RN-301.6: Expediente, caixa único, numeração, fechamento, fiado, cancelamento e estorno valem para todos os comportamentos (ADR-0019, ADR-0013, ADR-0021, ADR-0022, ADR-0039).

**Critérios de aceite:** um cadastro sem comportamento é recusado; um tenant migrado continua com as mesmas abas, cardápios e pedidos; o app não tem tela de troca de comportamento.

---

## RF-302 — Rótulos informativos

- RN-302.1: Lista fixa, sem texto livre (ADR-0054 item 8). Restaurante: Restaurante, Marmitaria, Self-service/quilo. Lanchonete: Hamburgueria, Pastelaria, Açaiteria, Pizzaria, Lanchonete, Sorveteria, Cafeteria, Padaria, Espetaria, Sushi/temaki, Doceria, Outro.
- RN-302.2: Na lanchonete, **um ou mais** rótulos. No restaurante, ver Q-302.
- RN-302.3: Só rótulos de comportamentos habilitados no tenant.
- RN-302.4: Editáveis depois em "Dados do estabelecimento" (`settings.manage`).
- RN-302.5: Rótulo **não muda funcionamento**. Pizzaria meio a meio fora do escopo.

**Critérios de aceite:** um rótulo de lanchonete num restaurante é recusado; trocar o rótulo não muda nenhuma aba nem regra.

---

## RF-303 — Módulos e abas por comportamento

- RN-303.1: Módulos fixos por comportamento (ADR-0054 item 12). Restaurante: `MARMITA`, `PF`, `KILO`, `DAILY_MENU`. Lanchonete: `A_LA_CARTE`, `DELIVERY`, `FIXED_MENU`.
- RN-303.2: O servidor recusa operação de módulo ausente com `403 ModuleNotEnabled`.
- RN-303.3: Abas (ADR-0055):
  - restaurante: Marmitas · Salão (PF, Quilo) · Pedidos · Fechamento (como hoje);
  - lanchonete: **Salão** (só comandas, sem sub-abas) · **Delivery** · **Cozinha** (a aba Pedidos com outro nome) · Fechamento;
  - com os dois (sugestão, Q-304): Marmitas · Salão (PF, Quilo, Comandas) · Delivery · Cozinha · Fechamento.
- RN-303.4: As permissões continuam filtrando as abas (ADR-0028).
- RN-303.5: O menu lateral mostra só os cadastros dos módulos do tenant.

**Critérios de aceite:** uma lanchonete não vê Marmitas, PF, Quilo, tamanhos de marmita nem cardápio do dia; um `POST /orders` `PF` numa lanchonete devolve `403 ModuleNotEnabled`; um funcionário só da cozinha vê só a aba Cozinha.

---

## RF-304 — Cardápio fixo: categorias e pratos

- RN-304.1: Cardápio permanente, sem data; vale na hora em que é editado; o pedido guarda snapshot (ADR-0056 item 1, ADR-0020).
- RN-304.2: Três abas no cadastro: **Pratos**, **Bebidas**, **Combos**.
- RN-304.3: Categorias criadas pelo estabelecimento (Lanches, Pastéis, Porções, Açaí), ordenáveis.
- RN-304.4: Chave **"Tamanho?"** por prato: ligada → cada tamanho do prato com preço próprio; desligada → preço único.
- RN-304.5: No pedido: prato → tamanho → adicionais → observação.

### Exemplo

| Prato | Tamanho? | Preço |
|---|---|---:|
| X-Burguer | não | R$ 22,00 |
| Açaí | 300 ml / 500 ml / 700 ml | R$ 14,00 / R$ 18,00 / R$ 23,00 |
| Pastel de carne | não | R$ 9,00 |

**Critérios de aceite:** um prato com tamanho não salva sem pelo menos um tamanho ativo; mudar o preço não altera pedidos já registrados.

---

## RF-305 — Bebidas

- RN-305.1: Tipo (Refrigerante, Suco) → tamanho com preço (Lata 350 ml, 2 L) → sabores ou marcas (ADR-0056 item 9).
- RN-305.2: Sabor pausável, com acréscimo opcional de preço (padrão R$ 0,00).
- RN-305.3: Tamanho com sabores exige escolher o sabor no pedido; tamanho sem sabores é vendido direto.
- RN-305.4: Onde o sabor fica (no tamanho ou no tipo): Q-305.

**Critérios de aceite:** Suco 500 ml R$ 9,00 com "Laranja com acerola" (+ R$ 1,00) entra no pedido por R$ 10,00; um sabor pausado não aparece para escolha.

---

## RF-306 — Combos fixos

- RN-306.1: Preço fechado. Componentes: prato específico com tamanho fixo (e quantidade) e/ou "bebida de tipo + tamanho", com o sabor escolhido no pedido (ADR-0056 item 13).
- RN-306.2: Adicionais e observação **por prato do combo**, cobrados à parte; o acréscimo do sabor também soma.
- RN-306.3: Na cozinha, o combo chega como lista normal de itens (ADR-0058 item 11).
- RN-306.4: Combo variável ("escolha 1 entre 3") fora desta fase.

**Critérios de aceite:** Combo R$ 32,00 + bacon R$ 4,00 + Coca Zero (+ R$ 0,50) = R$ 36,50; a cozinha vê "X-Burguer + bacon", "Batata P" e "Coca-Cola Zero lata", sem a linha do combo.

---

## RF-307 — Grupos de adicionais

- RN-307.1: Grupos ("Adicionais de lanche", "Complementos de açaí") com opções e preço (ADR-0056 item 17).
- RN-307.2: Ligados a uma categoria ou a um prato; os adicionais do prato são a união.
- RN-307.3: Limite mínimo/máximo e "grátis até N" ficam para depois.
- RN-307.4: A marmita continua com os adicionais atuais.

**Critérios de aceite:** um grupo ligado à categoria Lanches aparece em todos os lanches; um grupo ligado só ao X-Salada não aparece no X-Burguer.

---

## RF-308 — Pausa

- RN-308.1: Pausar prato, tamanho, sabor de bebida, combo e (recomendação) opção de adicional.
- RN-308.2: Fica pausado até alguém reativar; não volta sozinho no dia seguinte.
- RN-308.3: Pausar um componente pausa os combos que o usam, por disponibilidade **derivada** (ADR-0056 item 23).
- RN-308.4: Quem pode pausar: Q-306.

**Critérios de aceite:** pausar "Batata P" tira da venda os dois combos que a usam, e reativar a batata os devolve; um pedido já registrado com batata não muda.

---

## RF-309 — Vende no site e preço de site

- RN-309.1: Chave "Vende no site" e preço de site em prato sem tamanho, cada tamanho de prato, cada tamanho de bebida, combo e **cada tamanho de marmita** (ADR-0057).
- RN-309.2: Todo pedido do site usa o preço de site, qualquer que seja a forma de pagamento. Salão e telefone usam o preço de balcão.
- RN-309.3: A taxa de entrega continua separada.
- RN-309.4: Preço de site não é desconto nem taxa de serviço (ADR-0021).
- RN-309.5: Migração: tamanhos de marmita e bebidas visíveis na página recebem preço de site igual ao de balcão.
- RN-309.6: O fechamento mostra "Vendido pelo site", informativo.
- RN-309.7: A cobrança da plataforma sobre as vendas do site (percentual, descontada a taxa do Asaas) **não é implementada** nesta fase (Q-310).

**Critérios de aceite:** depois da migração, nenhuma página muda de preço; um X-Burguer de R$ 22,00 no balcão e R$ 23,90 no site sai a R$ 23,90 pela página e a R$ 22,00 no salão.

---

## RF-310 — Comanda à la carte no salão

- RN-310.1: `OrderType = A_LA_CARTE`, `DINE_IN`, só itens do cardápio fixo (ADR-0058).
- RN-310.2: Abrir comanda segue o fluxo do PF: mesa e/ou número de comanda.
- RN-310.3: Uma comanda nunca mistura PF ou quilo com cardápio fixo.
- RN-310.4: Pagamento parcial, troco, divisão, fiado, quitado bloqueia, cancelar exige estornar, como em qualquer pedido.

**Critérios de aceite:** dois funcionários incluindo itens na mesma comanda: o segundo recebe `409` e vê a comanda atualizada; item com preparo incluído depois de pronto volta a comanda para "Em preparo".

---

## RF-311 — Cozinha

- RN-311.1: A aba Cozinha da lanchonete é o painel de produção atual (ADR-0055).
- RN-311.2: Pratos avulsos e partes de combo aparecem como itens normais, com adicionais e observação; bebidas não aparecem.
- RN-311.3: Entrega e retirada à la carte seguem os status de atendimento do EP-002.

**Critérios de aceite:** uma comanda com 1 combo e 1 açaí mostra 3 itens de cozinha (lanche, batata, açaí) e nenhuma bebida.

---

## RF-312 — Delivery por telefone

- RN-312.1: Vale para a lanchonete (aba Delivery) e para a marmita por telefone do restaurante (ADR-0059; distinção balcão × telefone na Q-312).
- RN-312.2: Nome e telefone obrigatórios.
- RN-312.3: Entrega: endereço e **taxa de entrega manual** (Q-315). Retirada: sem endereço.
- RN-312.4: Forma de pagamento prevista e, se dinheiro, "troco para quanto" (Q-316).
- RN-312.5: Entra direto na cozinha e já ganha número; sem fila de aceite.
- RN-312.6: Status de atendimento: Saiu para entrega, Entregue, Retirado.

### Exemplo

| | Valor |
|---|---:|
| 1 Combo X-Burguer | R$ 32,00 |
| 1 Açaí 500 ml + granola | R$ 20,00 |
| Taxa de entrega (manual) | R$ 5,00 |
| **Total** | **R$ 57,00** |
| Forma prevista: dinheiro, troco para | R$ 100,00 |
| Troco que o motoboy leva | R$ 43,00 |

**Critérios de aceite:** um pedido de entrega sem telefone é recusado; um pedido por telefone aparece na Cozinha com número na hora; "troco para" menor que o total é recusado.

---

## RF-313 — Pagamento do delivery

- RN-313.1: Registrado só na confirmação do recebimento (RF-009). Pix → o funcionário registra e fica pago. Dinheiro → o motoboy confirma o recebimento e o valor, e o funcionário registra a forma e o valor.
- RN-313.2: A forma prevista não marca nada como pago.
- RN-313.3: Entregue e não pago vira fiado no fechamento (ADR-0021).

**Critérios de aceite:** escolher Pix como forma prevista deixa o pedido com saldo; o fechamento conta o dinheiro do delivery só quando registrado.

---

## RF-314 — Cliente pelo telefone

- RN-314.1: Reaproveita o `Customer` do fiado: nome e telefone; endereço só no pedido (ADR-0059).
- RN-314.2: Ao digitar o telefone, o app preenche o nome.
- RN-314.3: O pedido vincula o cliente existente ou cria um novo, sem "salvar cliente".
- RN-314.4: O cliente do delivery aparece na lista de clientes, sem saldo.
- RN-314.5: LGPD pelo ADR-0024.

**Critérios de aceite:** o segundo pedido do mesmo telefone vem com o nome preenchido; dois atendentes registrando o mesmo telefone novo ao mesmo tempo criam um cliente só (índice único `(tenant_id, phone_number)`).

---

## RF-315 — Aba Delivery

- RN-315.1: Lista entrega e retirada à la carte, do telefone e do site, do expediente aberto.
- RN-315.2: A faixa "Novos do site" da lanchonete fica aqui (ADR-0046 complementado).
- RN-315.3: O card mostra forma prevista, "troco para", saldo e status de atendimento.

**Critérios de aceite:** um pedido do site aceito e um por telefone aparecem na mesma lista, com o selo "Site" só no primeiro.

---

## RF-316 — Página de pedidos da lanchonete

- RN-316.1: Abas por categoria do cardápio fixo, Bebidas e Combos, só com itens "vende no site" e não pausados, pelo preço de site (ADR-0044 complementado).
- RN-316.2: O restaurante continua com Marmitas (agora com preço de site) e bebidas.
- RN-316.3: Com os dois comportamentos, a página mostra os dois (Q-317).
- RN-316.4: O pedido é `A_LA_CARTE`, entrega ou retirada, com aceite (ADR-0046), taxa por distância (ADR-0048) e acompanhamento (ADR-0047).
- RN-316.5: Ativar a página da lanchonete exige endereço completo e pelo menos 1 item vendável no site.
- RN-316.6: Limites e pedido só de bebidas: Q-318.

**Critérios de aceite:** pausar um prato no app o tira da página em até 30 s; um combo com a batata pausada não aparece; o total cobrado é o do preço de site.

---

# 4. Requisitos não funcionais

## RNF-301 — Compatibilidade

- Nenhuma mudança de comportamento para os tenants migrados, a não ser as listadas no §1.1. Testes de regressão do EP-001 e do EP-002 verdes.
- Versão mínima do app (ADR-0035): a fatia 1 sobe a versão mínima se o app antigo não souber ler `tenant.modules`.

## RNF-302 — Desempenho

- Cardápio fixo de até 200 itens (pratos, tamanhos, bebidas, sabores, combos) carregado no app em até 1 s depois do cache; disponibilidade derivada calculada no servidor numa consulta.
- Página da lanchonete dentro das metas do RNF-201 do EP-002.

## RNF-303 — Segurança

- Módulos impostos no servidor, nunca só no app.
- Tenant só do JWT nas rotas autenticadas e do slug nas públicas (ADR-0026, ADR-0045).

## RNF-304 — Privacidade

- Nenhum telefone ou endereço em log, auditoria ou evento (ADR-0024).

---

# 5. Arquitetura

| Componente | Mudança |
|---|---|
| Módulo Identity | `TenantBehavior`, `TenantLabel`, `ITenantModules`, cadastro com comportamento, `/auth/me` com módulos, endpoint de admin (ADR-0054) |
| Módulo Catalog | Cardápio fixo (ADR-0056) e preço de site (ADR-0057), inclusive em `MarmitaSize` |
| Módulo Menu | Clonagem do modelo semanal só com `DAILY_MENU` (ADR-0038 complementado) |
| Módulo Ordering | `A_LA_CARTE`, novos tipos de item, combo em três níveis, `PriceList`, forma prevista no pedido do app, cliente pelo telefone (ADR-0058, ADR-0059) |
| Módulo Storefront | Cardápio público por módulo, preço de site, ativação da página da lanchonete |
| App | Abas por módulo, cadastro do cardápio fixo, comanda, aba Delivery, formulário de delivery |
| `web/` | Abas por categoria, Bebidas, Combos, montagem de prato com tamanho e adicionais, combo com sabor |

---

# 6. Impacto nos documentos (trabalho dos outros agentes)

Este épico **não** altera agora os documentos abaixo. Cada agente atualiza o seu depois da revisão dos ADRs:

| Documento | Agente | O que muda |
|---|---|---|
| `04-modelo-dados.md` | `database-agent` | Entidades `TenantBehavior`, `TenantLabel`; `DishCategory`, `Dish`, `DishSize`, `BeverageType`, `BeverageSize`, `BeverageFlavor`, `Combo`, `ComboComponent`, `AddonGroup`, `AddonOption`, `AddonGroupLink`; `SellsOnSite`/`SitePrice` em `MarmitaSize`, `Dish`, `DishSize`, `BeverageSize`, `Combo` e (Q-307) `Product`; `Order.OrderType` com `A_LA_CARTE`, `Order.PriceList`, `ExpectedPaymentMethod`/`ChangeFor` também no pedido do app; `OrderItemKind` novos e o segundo nível de pai em `OrderItem`; referências do item ao cardápio fixo (`DishId`, `DishSizeId`, `BeverageSizeId`, `BeverageFlavorId`, `ComboId`, `AddonOptionId`); disponibilidade derivada; fechamento com `A_LA_CARTE` e "Vendido pelo site" |
| `05-ddl.md` | `database-agent` | Tabelas acima com RLS e grants; `CHECK`s de `order_type`, `item_kind`, `fulfillment_type` por modalidade (hoje `ck` de marmita = entrega/retirada); migração `RESTAURANT` para todos os tenants, rótulo inicial (Q-303) e preço de site igual ao de balcão; colunas novas no snapshot do fechamento; índices da disponibilidade derivada e da busca de cliente por telefone (já existe `ux_customers_phone`) |
| `06-contratos-api.md` | `backend-agent` (contrato) | `POST /auth/signup` com `tenant.behavior` e `tenant.labels`; `PATCH /tenant` com `labels`; `GET /auth/me` com `behaviors`, `labels`, `modules`; `POST /admin/tenants/{id}/behaviors`; rotas do cardápio fixo (categorias, pratos, tamanhos, bebidas, sabores, combos, grupos, vínculos, pausa) com exemplos JSON; `sellsOnSite`/`sitePrice` em `/marmita-sizes`; `POST /orders` `A_LA_CARTE` com os novos `itemKind`s, combo com partes e sabores, `expectedPaymentMethod`/`changeFor`; `GET /orders` com filtro de atendimento para a aba Delivery; `/public/stores/{slug}` com o cardápio fixo e o preço de site; `errorCode` novo `ModuleNotEnabled`; permissão `catalog.availability` (Q-306) na §6.2; `itemCount` no `OrderAwaitingAcceptance` (§6.14) |
| `07-backend.md` | `backend-agent` | `ITenantModules` e o filtro de módulo; resolvedor do cardápio fixo no `OrderItemResolver` (sem ler o `DAILY`); `Order.EnsureFulfillment`/`EnsureTopLevelKind` por modalidade; reprecificação em três níveis; `CustomerResolver` com vínculo automático por telefone sem `CustomerAddress`; clonagem do ADR-0038 condicionada a `DAILY_MENU`; `PublicMenuBuilder` por módulo e preço de site; testes por módulo e de disponibilidade derivada |
| `08-mobile.md` | `mobile-agent` | `(tabs)/_layout.tsx` por módulo, rota `delivery`, título "Cozinha"; `SalaoTab` só comandas e sub-aba Comandas; telas do cardápio fixo (Pratos, Bebidas, Combos, grupos, pausa, "vende no site"); preço de site em `menu/sizes`; tela de nova comanda (fluxo do PF); formulário de delivery (telefone → nome, endereço, taxa manual, forma prevista, troco); forma prevista e "troco para" no `NewMarmitaScreen`; cadastro com comportamento e rótulos; cache local do cardápio fixo |
| `web/` | `mobile-agent` / `backend-agent` (contrato público) | Abas por categoria, Bebidas e Combos; montagem de prato (tamanho → adicionais → observação); combo com sabor e adicionais por prato; preço de site; regras de limite da lanchonete (Q-318) |
| `09-devops.md` | `devops-agent` | Migração de dados da fatia 1 (expand/contract) e a ordem do deploy da versão mínima do app |
| `10-ux.md` | `ux-agent` | Cadastro com comportamento e rótulos, abas da lanchonete, cardápio fixo, comanda, aba Delivery, página da lanchonete |

---

# 7. Fora do escopo

- Trocar ou remover o comportamento pelo app; habilitar módulos um a um.
- Cobrança do comportamento adicional e cobrança da plataforma sobre o site (Q-301, Q-310).
- Pizza meio a meio e combo variável.
- Mínimo/máximo e "grátis até N" de adicionais.
- Preço de site por forma de pagamento.
- Gestão de motoboys, impressão de comanda, integração com iFood ou outros marketplaces.
- Estoque (a pausa é manual).
- Desconto e cupom (ADR-0021).

---

# 8. Backlog de features

| ID | Feature | Fatia | Prioridade |
|---|---|---|---|
| F-301 | Comportamento e rótulos no cadastro e em "Dados do estabelecimento" | 1 | P0 |
| F-302 | Módulos por comportamento (`ITenantModules`, `ModuleNotEnabled`, `/auth/me`) | 1 | P0 |
| F-303 | Abas e menu por módulo | 1 | P0 |
| F-304 | Migração dos tenants existentes como `RESTAURANT` | 1 | P0 |
| F-305 | Admin: acrescentar comportamento | 1 | P1 |
| F-306 | Categorias e pratos com tamanho por prato | 2 | P0 |
| F-307 | Bebidas: tipo, tamanho, sabor | 2 | P0 |
| F-308 | Combos fixos | 2 | P0 |
| F-309 | Grupos de adicionais | 2 | P0 |
| F-310 | Pausa com disponibilidade derivada | 2 | P0 |
| F-311 | "Vende no site" e preço de site (cardápio fixo e tamanhos de marmita) | 2 | P0 |
| F-312 | Comanda à la carte no salão | 3 | P0 |
| F-313 | Combo no pedido e na cozinha | 3 | P0 |
| F-314 | Fechamento com `A_LA_CARTE` e "Vendido pelo site" | 3 | P0 |
| F-315 | Delivery por telefone (lanchonete) | 4 | P0 |
| F-316 | Forma prevista e "troco para" na marmita por telefone | 4 | P0 |
| F-317 | Cliente pelo telefone | 4 | P0 |
| F-318 | Aba Delivery com "Novos do site" | 4 | P0 |
| F-319 | Página com o cardápio fixo e o preço de site | 5 | P0 |
| F-320 | Página do restaurante com o preço de site da marmita | 5 | P0 |
| F-321 | Abas com os dois comportamentos | — | P2 |
| F-322 | Limites de adicionais e "grátis até N" | — | P2 |
| F-323 | Combo variável | — | P2 |

**P0:** obrigatório para o piloto com lanchonetes. **P1:** logo depois. **P2:** evolução.

## 8.1. Fatias e critérios de aceite

### Fatia 1 — Comportamento, rótulos, cadastro, abas e migração (F-301 a F-305)

1. O cadastro exige um comportamento e pelo menos um rótulo do comportamento escolhido; um rótulo de outro comportamento é recusado com `422`.
2. Depois da migração, todo tenant existente tem `RESTAURANT` e um rótulo, e os testes do EP-001 e do EP-002 passam sem mudança.
3. `GET /auth/me` traz `behaviors`, `labels` e `modules`.
4. Numa lanchonete, `POST /orders` `MARMITA`, `PF` ou `KILO`, `POST /menus` e `POST /marmita-sizes` devolvem `403 ModuleNotEnabled`.
5. A lanchonete vê Salão · Delivery · Cozinha · Fechamento; o restaurante vê as abas de hoje. O menu lateral esconde os cadastros sem módulo.
6. Abrir o expediente numa lanchonete não cria cardápio do dia.
7. O app não tem tela nem botão para trocar ou contratar comportamento.
8. O admin acrescenta `SNACK_BAR` a um restaurante, com auditoria `PLATFORM_ADMIN`, e o app passa a mostrar os módulos novos.

### Fatia 2 — Cardápio fixo e preço de site (F-306 a F-311)

1. Cadastro de categorias (ordenáveis), pratos com e sem tamanho, bebidas (tipo → tamanho → sabor com acréscimo), combos (prato com tamanho fixo e bebida de tipo + tamanho), grupos de adicionais ligados a categoria ou prato.
2. Prato com "Tamanho?" ligado não salva sem tamanho ativo; desligado exige preço único.
3. Pausar um componente pausa os combos que o usam, sem gravar nada no combo; reativar devolve.
4. A pausa sobrevive ao fechamento e à abertura do expediente.
5. Inativar um prato usado em combo ativo devolve `409` com os combos.
6. "Vende no site" liga o preço de site, preenchido com o de balcão, em pratos, tamanhos, bebidas, combos e tamanhos de marmita.
7. Depois da migração, todo tamanho de marmita ativo e toda bebida visível na página têm preço de site igual ao de balcão.
8. Toda escrita com `If-Match`, auditoria, `CatalogUpdated` e feed de sincronização.

### Fatia 3 — Pedido à la carte nas comandas e na cozinha (F-312 a F-314)

1. Comanda `A_LA_CARTE` `DINE_IN` com mesa e/ou comanda, itens só do cardápio fixo, preço de balcão.
2. Prato com tamanho exige o tamanho; bebida com sabores exige o sabor; item pausado → `409 ProductUnavailable`.
3. Combo gravado como `COMBO` + `COMBO_PART` + `GROUP_ADDON`; 2 combos com bacon e Coca Zero = R$ 73,00 (ADR-0058 item 9).
4. A cozinha mostra as partes e os pratos, sem a linha do combo e sem bebidas.
5. Item do cardápio fixo num pedido `PF` é recusado, e vice-versa.
6. O fechamento mostra a linha "À la carte" e o "Vendido pelo site".
7. Pagamento parcial, troco, divisão, quitado bloqueia, estorno antes de cancelar: os testes do EP-001 repetidos para `A_LA_CARTE`.

### Fatia 4 — Delivery por telefone e cliente (F-315 a F-318)

1. Pedido `A_LA_CARTE` de entrega ou retirada pelo app exige nome e telefone; entrega exige endereço; a taxa digitada segue a regra decidida na Q-315.
2. Forma prevista e "troco para" gravados; "troco para" menor que o total → `422`; a forma prevista não cria `Payment`.
3. O pedido nasce aceito, com número, e aparece na Cozinha e na aba Delivery.
4. Digitar um telefone cadastrado preenche o nome; registrar cria ou vincula o `Customer`, sem `CustomerAddress`.
5. A marmita por telefone ganha forma prevista e "troco para", com nome e telefone obrigatórios conforme a Q-312.
6. A aba Delivery lista telefone e site, com "Novos do site" e os status de atendimento.
7. Registro com internet caindo no meio não duplica pedido nem cliente.

### Fatia 5 — Página web com cardápio fixo e preço de site (F-319, F-320)

1. A página da lanchonete mostra abas por categoria, Bebidas e Combos, só com itens "vende no site" e não pausados.
2. Montagem: prato → tamanho → adicionais → observação; combo com sabor e adicionais por prato.
3. Todo valor é o preço de site; o servidor recalcula e devolve `409 CartChanged` se mudou.
4. O pedido do site da lanchonete passa pelo aceite e aparece na faixa "Novos do site" da aba Delivery.
5. A página do restaurante cobra o preço de site da marmita.
6. Ativar a página da lanchonete exige endereço completo e 1 item vendável no site.
7. Pausar no app reflete na página em até 30 s.

---

# 9. Histórias de usuário

### US-301 — Escolher o tipo no cadastro

Como dono de hamburgueria, quero dizer no cadastro que sou lanchonete para não ver marmita, PF e quilo no app.

### US-302 — Montar o cardápio fixo

Como dono de açaiteria, quero cadastrar o açaí em 300, 500 e 700 ml, com os complementos, uma vez só, para não refazer o cardápio todo dia.

### US-303 — Vender combo

Como atendente, quero lançar o combo com o sabor do refrigerante e o bacon no lanche para cobrar certo sem calcular na mão.

### US-304 — Pausar o que acabou

Como atendente, quero pausar a batata e ver que os combos com batata saíram da venda para não vender o que não tenho.

### US-305 — Comanda da mesa

Como garçom, quero abrir a comanda da mesa 5 e ir incluindo itens para fechar a conta no fim.

### US-306 — Pedido por telefone

Como atendente, quero digitar o telefone e já ver o nome do cliente, anotar o endereço, a taxa e o troco para mandar o pedido direto para a cozinha.

### US-307 — Troco certo

Como motoboy, quero ver no pedido que o cliente paga com R$ 100,00 para sair com o troco certo.

### US-308 — Preço do site

Como dono, quero um preço próprio no site para cobrir o custo da venda online sem cobrar taxa separada do cliente.

### US-309 — Ver o que vem da cozinha

Como cozinheiro, quero ver o combo como os itens que preciso preparar, com os adicionais de cada lanche.

---

# 10. Critérios gerais de conclusão

1. Uma lanchonete do piloto opera um expediente inteiro (abertura, comandas, delivery por telefone e pelo site, fechamento) sem usar marmita, PF ou quilo.
2. Os restaurantes existentes não percebem mudança além do preço de site da marmita e da forma prevista no pedido por telefone.
3. Nenhum módulo ausente é acessível pela API (testes de integração).
4. Combo, adicionais e acréscimos somam certo nos exemplos deste épico.
5. Pausa derivada correta nos combos.
6. Nenhum dado pessoal em log, auditoria ou evento.

---

# 11. Métricas de sucesso

- Lanchonetes ativas e pedidos por dia por lanchonete.
- % de pedidos por canal: salão, telefone, site.
- Tempo de registro de um pedido por telefone (meta: menos de 60 s).
- Itens pausados por dia e combos pausados por derivação.
- Diferença média entre preço de site e de balcão.

**Meta inicial:** 3 lanchonetes no piloto operando 2 semanas com pelo menos 50% dos pedidos de delivery registrados no app.

---

# 12. Riscos

| ID | Risco | Mitigação |
|---|---|---|
| R-301 | O `if` por comportamento se espalhar pelo código e travar a habilitação de módulos no futuro | Só `ITenantModules.Has(...)`, com teste de arquitetura que proíbe ler o comportamento fora do Identity |
| R-302 | Combo em três níveis complicar a reprecificação e a edição | Testes com os exemplos do ADR-0058; partes não removíveis sozinhas |
| R-303 | Cardápio fixo grande pesar no app e na página | Cache local, disponibilidade derivada numa consulta, meta do RNF-302 |
| R-304 | Atendente digitar taxa de entrega errada (taxa manual) | Auditoria, prévia do total na confirmação, decisão da Q-315 |
| R-305 | Telefone de outra pessoa ligando o pedido ao cadastro errado | O cadastro não muda sozinho (Q-314) |
| R-306 | Preço de site muito acima do balcão afastar o cliente da página | Decisão do estabelecimento; métrica de diferença |
| R-307 | Cobrança da plataforma sobre o site decidida depois exigir dados que não foram gravados | `PriceList` e "Vendido pelo site" gravados desde já |
| R-308 | Migração dos tenants errar e mudar o preço de alguma página | Migração com preço de site = balcão e teste de comparação antes/depois |

---

# 13. Decisões arquiteturais e de negócio

| ADR | Decisão |
|---|---|
| [0054](adr/0054-comportamentos-estabelecimento-rotulos-modulos.md) | Comportamentos, rótulos e módulos fixos por comportamento |
| [0055](adr/0055-abas-app-por-comportamento.md) | Abas do app por comportamento |
| [0056](adr/0056-cardapio-fixo-pratos-bebidas-combos-adicionais.md) | Cardápio fixo, combos, grupos de adicionais e pausa |
| [0057](adr/0057-preco-de-site-vende-no-site.md) | "Vende no site" e preço de site |
| [0058](adr/0058-modalidade-a-la-carte-comandas-cozinha.md) | Modalidade `A_LA_CARTE`, combo no pedido e na cozinha |
| [0059](adr/0059-delivery-por-telefone-cliente.md) | Delivery por telefone e cliente pelo telefone |

Complementados (status mantido): 0005, 0017, 0020, 0021, 0024, 0025, 0028, 0038, 0044, 0046, 0050 (item 19).

---

# 14. Questões em aberto

Precisam de decisão do usuário antes do código que depende delas ir para produção. Em 2026-11-22 o usuário decidiu as Q-305, Q-306, Q-312, Q-315 e Q-316 e autorizou seguir; nas demais (exceto Q-301 e Q-310, que ficam sem código), a implementação segue a recomendação até a revisão. **N** = negócio (o usuário decide); **T** = técnica (há recomendação).

| # | Tipo | Questão | Recomendação atual | ADR |
|---|---|---|---|---|
| Q-301 | N | **Cobrança do comportamento adicional** (restaurante que soma a lanchonete): valor e forma | Em aberto por decisão do usuário. Nada no app | 0017, 0054 |
| Q-302 | N | **Restaurante com mais de um rótulo?** A decisão diz "na lanchonete pode marcar mais de um" | Permitir também no restaurante (marmitaria que também é self-service) | 0054 |
| Q-303 | N | **Rótulo inicial dos tenants migrados** | `RESTAURANT` (Restaurante), editável pelo proprietário | 0054 |
| Q-304 | N | **Abas com os dois comportamentos** | Marmitas · Salão (PF, Quilo, Comandas) · Delivery · Cozinha · Fechamento | 0055 |
| Q-305 | N | **Sabor da bebida no tamanho ou no tipo?** No tamanho, "acabou a Coca lata" não pausa a 2 L e o acréscimo varia por tamanho; no tipo, menos cadastro | **Decidido em 2026-11-22:** No tamanho, com "copiar sabores de outro tamanho" | 0056 |
| Q-306 | N | **Quem pode pausar.** Hoje o "acabou" do cardápio do dia exige `menu.manage` (só OWNER e MANAGER) | **Decidido em 2026-11-22:** `catalog.availability` para OWNER, MANAGER, ATTENDANT e KITCHEN | 0028, 0056 |
| Q-307 | N | **Bebidas do restaurante na página** (`Product` `BEVERAGE` com `IsVisibleOnPage`): ganham preço de site? | Sim: `IsVisibleOnPage` vira o "vende no site" delas, com `SitePrice` | 0057 |
| Q-308 | N | **Adicionais e acréscimo de sabor no site:** preço próprio? | Mesmo preço do balcão nesta fase | 0057 |
| Q-309 | N | **Item acrescentado no app a um pedido do site:** qual preço? | Preço de site quando o item vende no site; senão o de balcão | 0057 |
| Q-310 | N | **Cobrança da plataforma sobre as vendas do site:** percentual, base (com ou sem a taxa de entrega), se vale também para "pagar na entrega/retirada" (sem Asaas), forma de cobrança e relação com o split da Fase 2 | Em aberto por decisão do usuário. Só gravar `PriceList` e mostrar "Vendido pelo site". Relacionada à Q-01 e à Q-04 do EP-002 | 0050, 0057 |
| Q-311 | N | **Fechamento da lanchonete:** "À la carte" numa linha só ou aberta por salão, entrega e retirada? | Uma linha, com a abertura por atendimento como informação | 0058 |
| Q-312 | N | **Marmita por telefone × marmita de balcão.** A aba Marmitas atende os dois, e hoje o telefone é opcional. Como saber que é "por telefone" para exigir nome e telefone? | **Decidido em 2026-11-22:** Telefone e nome obrigatórios só na entrega de marmita | 0059 |
| Q-313 | N | **Endereços salvos da marmita.** Hoje "salvar cliente" grava o endereço em `CustomerAddress` e o app oferece os endereços salvos. A decisão diz "endereço só no pedido". Remover os endereços salvos da marmita? | Manter os endereços salvos já existentes e o fluxo da marmita; o delivery da lanchonete não grava endereço e oferece "usar o endereço do último pedido" | 0021, 0059 |
| Q-314 | N | **Telefone cadastrado com outro nome:** atualizar o cadastro? | Não: o pedido guarda o nome digitado; o cadastro muda só pela tela de clientes | 0059 |
| Q-315 | N | **Taxa de entrega manual:** hoje taxa diferente da padrão exige `orders.delivery-fee.edit` (OWNER e MANAGER). O atendente pode digitar a taxa ao criar o pedido por telefone? O campo começa com a taxa padrão ou vazio? | **Decidido em 2026-11-22:** Começa com a taxa padrão; quem cria edita só na criação, com auditoria; depois, `orders.delivery-fee.edit` (ADR-0021 complementado) | 0021, 0028, 0059 |
| Q-316 | N | **Forma prevista obrigatória?** | **Decidido em 2026-11-22:** Obrigatória na entrega, opcional na retirada | 0059 |
| Q-317 | N | **Página com os dois comportamentos:** um pedido do site pode ter marmita e hambúrguer? Um `Order` tem um `OrderType` só | Um tipo por pedido: a página mostra as duas seções, e o carrinho aceita um tipo só por vez | 0044, 0058 |
| Q-318 | N | **Limites do pedido do site da lanchonete e pedido só de bebidas** (no restaurante: pelo menos 1 marmita, até 20) | Pelo menos 1 prato ou combo; até 20 pratos e combos e 30 linhas | 0044 |
| Q-319 | N | **Bebidas com os dois comportamentos:** a marmita usa `Product` `BEVERAGE` e a comanda usa as bebidas do cardápio fixo. Duas listas de bebidas no mesmo tenant? | Só aparece quando o admin habilitar o segundo comportamento; nessa ocasião, migrar as bebidas do restaurante para a estrutura nova. Até lá, cada comportamento usa a sua | 0056, 0058 |
| Q-320 | N | **Balcão "para viagem" na lanchonete:** comanda do Salão ou retirada da aba Delivery (que exige nome e telefone)? | Comanda do Salão, com o número da comanda; a aba Delivery fica para telefone e site | 0058, 0059 |
| Q-321 | T | **Modelagem do combo no pedido:** três níveis (`COMBO` → `COMBO_PART` → `GROUP_ADDON`) ou outro caminho? | Três níveis, como no ADR-0058 item 9; confirmar com o `database-agent` | 0058 |
| Q-322 | T | **Trocar o sabor de uma parte do combo** depois do registro | Remover e incluir o combo de novo nesta fase | 0058 |
| Q-323 | T | **Versão mínima do app** na fatia 1: o app antigo ignora `tenant.modules` e mostraria as abas do restaurante numa lanchonete | Subir a versão mínima antes do primeiro cadastro de lanchonete (ADR-0035) | 0035, 0054 |
| Q-324 | T | **Como o aparelho sabe que o admin acrescentou um comportamento** | Evento de tempo real `TenantModulesChanged` + entrada no feed; o app recarrega o `/auth/me` | 0054 |
