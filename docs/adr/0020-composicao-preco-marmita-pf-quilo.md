# ADR-0020 — Composição e preço de marmita, PF e quilo

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** Lacuna em RF-002, RF-003, RN-004.2, RF-006
- **Relacionados:** ADR-0005, ADR-0030

## Contexto

A RN-004.2 diz que cada marmita tem "tamanho, prato, acompanhamentos e observações", mas o épico não diz de onde vem o preço. Sem essa regra, não dá para modelar `Product`, `MenuItem` nem `OrderItem`.

## Decisão

**Catálogo (`Product.Kind`):** `MAIN_DISH`, `SIDE_DISH`, `BEVERAGE`, `ADDON`. Só `BEVERAGE` e `ADDON` têm preço próprio. Pratos e acompanhamentos não têm preço na marmita (veja abaixo).

**Marmita, com preço por tamanho:**
- `MarmitaSize` por tenant: nome (P/M/G ou livre), preço, ordem e ativo.
- Item de marmita = tamanho + prato(s) principal(is) + acompanhamentos do cardápio do dia + adicionais + observação.
- **Preço = preço do tamanho + soma dos adicionais.** Prato e acompanhamento não alteram o preço.

**PF:**
- Cada `MenuItem` de prato no cardápio de PF tem **preço de PF** (padrão vindo do produto, pode ser alterado no cardápio).
- Preço = preço do PF + adicionais.

**Quilo:**
- Preço do quilo configurado no estabelecimento (`KiloPricePerKg`).
- Cada `WeighingEntry` guarda o preço/kg aplicado (RN-006.3).

**Bebidas e adicionais:** preço do produto, em qualquer modalidade (RN-002.5).

**Snapshot:** todo `OrderItem` grava nome, descrição/composição e preço unitário **no momento da venda** (RN-002.2, RN-003.9). Mudar o catálogo ou o cardápio nunca altera pedidos já registrados.

## Alternativas consideradas

- **Preço por prato e tamanho:** mais flexível, mas multiplica a configuração diária. Pode ser adicionado depois como preço por tamanho em `MenuItem`.
- **Composição livre com preço por componente:** pouco aderente ao público.

## Consequências

- Limites de acompanhamentos por tamanho ficam fora do MVP (a observação cobre).
- O fechamento continua separando marmita, PF e quilo por `OrderType`.
