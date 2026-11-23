# ADR-0056 — Cardápio fixo: categorias, pratos com tamanho por prato, bebidas, combos fixos, grupos de adicionais e pausa

- **Status:** Proposto
- **Data:** 2026-11-22
- **Origem:** EP-003, RF-304 a RF-308; decisões do dono do produto em 2026-11-22 (item 6)
- **Relacionados:** ADR-0020 (complementado), ADR-0028 (complementado), ADR-0029, ADR-0030, ADR-0031, ADR-0032, ADR-0034, ADR-0038 (complementado), ADR-0054, ADR-0057, ADR-0058

## Contexto

O cardápio do restaurante é **do dia** (`Menu` `DAILY`, ADR-0038): pratos e acompanhamentos sem preço, com o preço vindo do tamanho da marmita ou do PF. A lanchonete vende sempre a mesma coisa, com preço por item: o X-Burguer custa R$ 22,00 hoje e amanhã. Ela precisa de categorias próprias (Lanches, Pastéis, Porções, Açaí), de tamanhos **por prato** (açaí 300 ml, 500 ml, 700 ml), de bebidas com tamanho e sabor, de combos e de adicionais agrupados.

O catálogo atual não serve como está: `MarmitaSize` é global com um preço, `Product.Category` é texto livre e `ProductKind` não tem combo nem tamanho por prato.

## Decisão

**Princípios**

1. O **cardápio fixo** é permanente: não tem data nem modelo semanal. Uma edição vale **na hora** para os pedidos seguintes. Todo pedido grava o snapshot de nome, composição e preço (ADR-0020); mudar o cardápio nunca altera pedido já registrado.
2. Só existe com o módulo `FIXED_MENU` (ADR-0054). O cadastro exige `catalog.manage` (ADR-0028). Escritas com `If-Match` (ADR-0034), auditoria (ADR-0031), evento `CatalogUpdated` e feed de sincronização (ADR-0032).
3. No app, o cardápio fixo tem **três abas: Pratos, Bebidas e Combos**. Os grupos de adicionais são cadastrados a partir de Pratos.
4. *Recomendação técnica:* as entidades ficam no módulo **Catalog** (cadastro permanente), não no Menu (cardápio datado). São entidades novas, separadas de `Product` e de `MarmitaSize`, para não mudar o significado dos campos que a marmita e o PF já usam (ADR-0020). Os nomes abaixo são sugestão para o `database-agent`.

**Pratos**

5. `DishCategory [T]`: nome, ordem (`SortOrder`, reordenável), ativa. Criada pelo estabelecimento.
6. `Dish [T]`: categoria, nome, descrição, foto opcional (R2, como `Product`), `RequiresPreparation` (padrão `true`), ativo, pausado, ordem dentro da categoria, e a chave **"Tamanho?"** (`HasSizes`):
   - **desligada:** o prato tem **preço único** (`Price`);
   - **ligada:** o prato tem `DishSize [T]` (nome livre, como "300 ml" ou "Família", preço, ordem, ativo, pausado), pelo menos um ativo, e **não** tem preço único. **O tamanho é do prato**, não global como o da marmita.
7. No pedido: escolhe o prato → o **tamanho** (se houver) → os **adicionais** → a **observação**.
8. Ligar ou desligar "Tamanho?" num prato já vendido é permitido: o histórico fica no snapshot. Ao desligar, os tamanhos são inativados (não apagados) e o preço único passa a ser exigido.

**Bebidas**

9. Três níveis:
   - `BeverageType [T]`: tipo ("Refrigerante", "Suco"), ordem, ativo;
   - `BeverageSize [T]`: tamanho **com preço** ("Lata 350 ml", "2 L"), ordem, ativo, pausado;
   - `BeverageFlavor [T]`: sabor ou marca ("Coca-Cola", "Guaraná", "Laranja"), **acréscimo de preço opcional** (`ExtraPrice`, padrão `0.00`, nunca negativo), ordem, ativo, pausado.
10. **Decidido em 2026-11-22 (Q-305):** o sabor pertence ao **tamanho** (cada tamanho tem a sua lista), seguindo a ordem tipo → tamanho → sabor da decisão. Assim "acabou a Coca lata" não pausa a Coca 2 L, e o acréscimo pode ser diferente por tamanho. O app oferece "copiar sabores de outro tamanho" para não redigitar.
11. Um tamanho sem nenhum sabor cadastrado é vendido sem escolha de sabor (por exemplo, "Água 500 ml"). Com sabores, o sabor é obrigatório no pedido.
12. Preço da bebida = preço do tamanho + acréscimo do sabor. Exemplo: Suco 500 ml R$ 9,00 + sabor "Laranja com acerola" (acréscimo R$ 1,00) = **R$ 10,00**.

**Combos fixos**

13. `Combo [T]`: nome, descrição, foto opcional, **preço fechado**, ordem, ativo, pausado. Componentes (`ComboComponent`), pelo menos um:
    - **prato específico com tamanho fixo** (`DishId` + `DishSizeId`, este nulo se o prato não tem tamanho) e quantidade (1 a 10);
    - **bebida de tipo + tamanho** (`BeverageSizeId`) e quantidade, com o **sabor escolhido na hora do pedido**, entre os sabores não pausados daquele tamanho.
14. No pedido, cada **prato do combo** aceita **adicionais e observação próprios**, cobrados à parte. O acréscimo do sabor da bebida também soma.

    | Combo "X-Burguer + batata + refri" | Valor |
    |---|---:|
    | Preço do combo | R$ 32,00 |
    | + Bacon no X-Burguer (adicional) | R$ 4,00 |
    | Refri lata: sabor Coca-Cola Zero (acréscimo) | R$ 0,50 |
    | **Total da linha** | **R$ 36,50** |

15. Na **cozinha**, o combo chega como **lista normal de itens** junto com o resto da comanda (X-Burguer com bacon, Batata P, Coca Zero lata). O que o combo muda é só o valor. A modelagem no pedido está no ADR-0058.
16. Combo variável ("escolha 1 lanche entre 3") fica fora desta fase.

**Adicionais por grupo**

17. `AddonGroup [T]` ("Adicionais de lanche", "Complementos de açaí"): nome, ordem, ativo. `AddonOption [T]`: nome, preço (pode ser `0.00`), ordem, ativo, pausado.
18. O grupo é **ligado a uma categoria ou a um prato** (`AddonGroupLink`). Os adicionais de um prato = grupos da categoria dele ∪ grupos ligados ao próprio prato, sem repetir. O preço do adicional é o mesmo para qualquer tamanho do prato.
19. **Limite mínimo/máximo e "grátis até N"** ficam para depois. Nesta fase, qualquer quantidade de 1 a 10 por opção, sem obrigatoriedade.
20. A marmita **continua** com os adicionais atuais (`Product` `ADDON`, ADR-0020). Os grupos são só do cardápio fixo.

**Pausa e disponibilidade derivada**

21. Pausáveis: prato, tamanho de prato, tamanho de bebida, sabor de bebida, combo e, *por recomendação*, opção de adicional ("acabou o bacon").
22. **A pausa fica até alguém reativar.** Não volta sozinha no fechamento nem na abertura do expediente (diferente do "acabou" do cardápio do dia, que some com o dia).
23. A disponibilidade de venda é **derivada**, calculada no servidor, nunca gravada:
    - prato vendável = ativo, não pausado e, se tem tamanho, com pelo menos um tamanho ativo e não pausado;
    - tamanho de bebida vendável = ativo, não pausado e, se tem sabores, com pelo menos um sabor ativo e não pausado;
    - **combo vendável** = ativo, não pausado e **todos** os componentes vendáveis (o prato no tamanho fixo; o tamanho de bebida com pelo menos um sabor).

    Pausar um componente **pausa os combos que o usam** sem tocar no combo: reativar o componente volta o combo, salvo se o próprio combo foi pausado. O app mostra o motivo ("Pausado: Batata P").
24. Pausar não altera pedidos já registrados. Incluir um item pausado num pedido → `409 ProductUnavailable` (o mesmo código da marmita).
25. **Decidido em 2026-11-22 (Q-306):** pausar e reativar exigem uma permissão nova, `catalog.availability`, dada a quem está no balcão e na cozinha (ADR-0028). Editar preço e estrutura continua com `catalog.manage`.

**Exclusão**

26. Itens já vendidos ou usados em combo não são apagados: são **inativados**. Inativar um prato usado num combo ativo → `409` com a lista dos combos, para o estabelecimento ajustar antes.

## Alternativas consideradas

- **Reaproveitar `Product` (`MAIN_DISH` com preço) e `MarmitaSize`:** menos tabelas, mas o `MarmitaSize` é global e a regra "prato não tem preço na marmita" (ADR-0020) passaria a ter exceção por comportamento em todo o código do pedido.
- **Cardápio fixo como um `Menu` sem data:** o `Menu` tem itens sem preço próprio, seções `MAIN_DISH`/`SIDE_DISH` e a regra de clonagem do ADR-0038. Forçar o cardápio fixo nesse formato não ganha nada.
- **Sabor no tipo de bebida** (vale para todos os tamanhos): menos cadastro, mas a pausa e o acréscimo ficam iguais em todos os tamanhos. Fica como alternativa da Q-305.
- **Pausa com volta automática no dia seguinte:** a lanchonete repõe estoque em horários variados; reativar sozinho venderia o que não existe.
- **Combo com disponibilidade própria, sem derivar:** o atendente esqueceria de pausar o combo quando a batata acaba.

## Consequências

- Novas tabelas por tenant, com RLS: `dish_categories`, `dishes`, `dish_sizes`, `beverage_types`, `beverage_sizes`, `beverage_flavors`, `combos`, `combo_components`, `addon_groups`, `addon_options`, `addon_group_links`.
- Novos endpoints de cadastro e pausa; leitura do cardápio fixo com a disponibilidade derivada (para o app e para a página, ADR-0057).
- A disponibilidade derivada precisa de teste: pausar a batata pausa os dois combos que a usam e nenhum outro.
- O `OrderItemResolver` ganha um resolvedor para o cardápio fixo, sem ler cardápio `DAILY` (ADR-0058).
- Revisitar limites de adicionais, combo variável e pizza meio a meio depois do piloto.

## Complemento (2026-11-22, EP-003 fatia 3: contrato do pedido à la carte)

- **Item 24, status do `ProductUnavailable`:** o texto acima dizia `409 ProductUnavailable`, mas o catálogo de erros (`06-contratos-api.md` §6.1) e a marmita já usam **`422 ProductUnavailable`**. Vale o do 06: incluir num pedido um item do cardápio fixo pausado, inativo ou fora de venda pela pausa derivada (combo com um componente pausado) responde `422 ProductUnavailable`, com o `detail` dizendo o que falta ("X-Burguer + batata + refri está indisponível. Pausado: Batata frita P."). O critério 2 da fatia 3 do EP-003 segue este complemento.
- **Componente no pedido:** o pedido diz as escolhas de cada componente do combo pelo `id` do componente (`parts[].comboComponentId`), que a porta `IFixedMenuReader` passa a expor.
