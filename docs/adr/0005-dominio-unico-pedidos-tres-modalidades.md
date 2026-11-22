# ADR-0005 — Um único domínio de pedidos com três modalidades

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** EP-001 §6.2, §16
- **Relacionados:** ADR-0020 (preço de marmita, PF e quilo)

## Contexto

Marmitas, PF e refeições por quilo têm telas e regras de preço diferentes, mas compartilham comanda, itens de bebida e adicionais, pagamentos, fechamento, auditoria e alertas. O EP-002 (WhatsApp) também vai criar pedidos.

## Decisão

Um agregado único **`Order`** com `OrderType` (`MARMITA`, `PF`, `KILO`) e `Origin` (`APP` no MVP; `WHATSAPP` no futuro). As diferenças ficam em:

- `OrderItem` com subtipo/composição (marmita com tamanho, prato e acompanhamentos; PF; bebida; adicional);
- `WeighingEntry`, exclusivo de `KILO`;
- regras por modalidade dentro do agregado (por exemplo, `KILO` não entra no painel de produção, RN-006.7).

## Alternativas consideradas

- **Um agregado por modalidade:** triplicaria pagamento, fechamento e auditoria, e o fechamento consolidado ficaria mais caro.

## Consequências

- O fechamento agrupa por `OrderType` (RN-010.2) sem precisar de `UNION`.
- Validações específicas de cada modalidade precisam de testes por modalidade.

## Complementado por ADR-0058 (2026-11-22)

- O EP-003 (lanchonete) acrescenta uma **quarta modalidade**, `OrderType = A_LA_CARTE`, no mesmo agregado `Order`. O título deste ADR fica como registro histórico; a decisão de um domínio único continua valendo.
- `A_LA_CARTE` aceita `DINE_IN`, `DELIVERY` e `PICKUP`, e só itens do cardápio fixo (ADR-0056). Marmita continua entrega/retirada; PF e quilo continuam no salão.
- `OrderItem` ganha os subtipos `FIXED_DISH`, `FIXED_BEVERAGE`, `COMBO`, `COMBO_PART` e `GROUP_ADDON` (ADR-0058 item 8).
- Cada modalidade exige o módulo correspondente do tenant (ADR-0054): `403 ModuleNotEnabled`.
- O fechamento ganha a linha `A_LA_CARTE` no agrupamento por `OrderType`.
