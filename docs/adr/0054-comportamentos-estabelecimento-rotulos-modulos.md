# ADR-0054 — Comportamentos do estabelecimento (`RESTAURANT`, `SNACK_BAR`), rótulos informativos e módulos fixos por comportamento

- **Status:** Proposto
- **Data:** 2026-11-22
- **Origem:** EP-003 (`docs/12-epico-ep003.md`), RF-301 e RF-302; decisões do dono do produto em 2026-11-22 (itens 1 a 3)
- **Relacionados:** ADR-0005, ADR-0017 (complementado), ADR-0018, ADR-0019, ADR-0025 (complementado), ADR-0028, ADR-0038 (complementado), ADR-0044, ADR-0055, ADR-0056, ADR-0058

## Contexto

Hoje todo tenant é um restaurante: marmita, PF, quilo e cardápio do dia ou da semana. O dono do produto quer atender também **lanchonetes** (hamburgueria, pastelaria, açaiteria e parecidos), que trabalham com outro modo de venda: comanda à la carte no salão, delivery por telefone e pelo site e um cardápio fixo, sem data.

Sem uma regra explícita, cada tela e cada endpoint teria de adivinhar o que o estabelecimento vende. O app mostraria abas e cadastros inúteis (tamanho de marmita numa açaiteria), e o servidor aceitaria um pedido de PF num tenant que nem tem cardápio de PF.

O dono do produto também quer que um restaurante possa, no futuro, **somar** a lanchonete (por exemplo, a marmitaria que à noite vira hamburgueria), mediante aumento na assinatura.

## Decisão

**Comportamentos**

1. Novo enum `EstablishmentBehavior`: `RESTAURANT` e `SNACK_BAR` (lanchonete).
2. O tenant tem um **conjunto** de comportamentos habilitados (`TenantBehavior [T]`: `Behavior`, `EnabledAt`, `EnabledBy`, `Origin`). O modelo já admite mais de um. Pelo menos um comportamento é obrigatório.
3. **No cadastro** (ADR-0018), o proprietário escolhe **exatamente um** comportamento. A escolha é obrigatória e não tem padrão pré-marcado.
4. **O comportamento é fixo depois do cadastro** nesta fase. O app não oferece troca nem acréscimo.
5. **Acrescentar um segundo comportamento** é feito só pelo administrador da plataforma (ADR-0025), mediante aumento na assinatura. A forma de cobrança será definida depois (Q-301). O app **nunca** oferece a contratação, nem mostra preço ou link de compra (ADR-0017). Remover um comportamento fica fora desta fase.
6. **Migração:** todos os tenants existentes recebem `RESTAURANT`, com `Origin = SYSTEM` e `EnabledAt` = data da migração. Nada muda no funcionamento deles.

**Rótulos informativos**

7. O estabelecimento tem **rótulos** (`TenantLabel [T]`) só informativos: aparecem no perfil, no app e, no futuro, no marketplace (EP-002 Fase 3). **Rótulos não mudam funcionamento.** Nenhum código de regra de negócio pode ler um rótulo.
8. Lista fixa, sem texto livre (para evitar erro de português e manter o marketplace filtrável). Cada rótulo pertence a um comportamento:

   | Comportamento | Código | Texto |
   |---|---|---|
   | `RESTAURANT` | `RESTAURANT` | Restaurante |
   | `RESTAURANT` | `MARMITARIA` | Marmitaria |
   | `RESTAURANT` | `SELF_SERVICE` | Self-service/quilo |
   | `SNACK_BAR` | `BURGER_JOINT` | Hamburgueria |
   | `SNACK_BAR` | `PASTEL_SHOP` | Pastelaria |
   | `SNACK_BAR` | `ACAI_SHOP` | Açaiteria |
   | `SNACK_BAR` | `PIZZERIA` | Pizzaria |
   | `SNACK_BAR` | `SNACK_BAR` | Lanchonete |
   | `SNACK_BAR` | `ICE_CREAM_SHOP` | Sorveteria |
   | `SNACK_BAR` | `CAFE` | Cafeteria |
   | `SNACK_BAR` | `BAKERY` | Padaria |
   | `SNACK_BAR` | `SKEWER_BAR` | Espetaria |
   | `SNACK_BAR` | `SUSHI` | Sushi/temaki |
   | `SNACK_BAR` | `SWEETS_SHOP` | Doceria |
   | `SNACK_BAR` | `OTHER` | Outro |

   A lista vive em código, com teste. Os códigos são sugestão técnica (o `backend-agent` pode ajustá-los antes do contrato).
9. No cadastro, o proprietário marca os rótulos do comportamento escolhido: **um ou mais na lanchonete**. No restaurante, o texto do dono do produto prevê um rótulo só; a recomendação é permitir mais de um também ali (Q-302). Pelo menos um rótulo é obrigatório.
10. Só valem rótulos de comportamentos habilitados no tenant (`422` em `labels`).
11. Os rótulos são **editáveis depois** em "Dados do estabelecimento" (`PATCH /tenant`, `settings.manage`). Pizzaria **meio a meio** está fora do escopo: o rótulo `PIZZERIA` existe, mas o cardápio fixo não tem pizza de dois sabores (ADR-0056).

**Módulos fixos por comportamento**

12. O que o tenant pode fazer vem de **módulos** (`TenantModule`, enum em código), derivados dos comportamentos por um mapa fixo em código:

    | Módulo | O que libera | `RESTAURANT` | `SNACK_BAR` |
    |---|---|:-:|:-:|
    | `MARMITA` | pedidos `MARMITA`, tamanhos de marmita, entrega da aba Marmitas | ✓ | |
    | `PF` | pedidos `PF`, cardápio de PF | ✓ | |
    | `KILO` | pedidos `KILO`, preço do quilo | ✓ | |
    | `DAILY_MENU` | cardápio do dia e modelo semanal (ADR-0038) | ✓ | |
    | `A_LA_CARTE` | comandas à la carte no salão (ADR-0058) | | ✓ |
    | `DELIVERY` | aba Delivery: entrega e retirada do cardápio fixo, por telefone e pelo site (ADR-0059) | | ✓ |
    | `FIXED_MENU` | cardápio fixo: pratos, bebidas, combos e grupos de adicionais (ADR-0056) | | ✓ |

    Módulos efetivos = união dos módulos de cada comportamento habilitado.
13. **Os módulos não são habilitáveis um a um** nesta fase. O desenho deixa isso possível depois: toda checagem de código pergunta "o tenant tem o módulo X?" (`ITenantModules.Has(TenantModule.X)`), **nunca** "o tenant é lanchonete?". Para trocar o mapa por dados no futuro (módulo por tenant), basta mudar a fonte do `ITenantModules`.
14. O servidor **impõe** os módulos: criar pedido de um `OrderType`, criar ou editar o cadastro de um módulo ausente → `403 ModuleNotEnabled` (extensão `module`). Leitura de cadastros de módulo ausente devolve lista vazia. O app esconde o que não está habilitado, mas isso é só interface.
15. `GET /auth/me` passa a trazer `tenant.behaviors`, `tenant.labels` e `tenant.modules`. Quando o admin acrescenta um comportamento, os aparelhos precisam recarregar o `/auth/me`. O mecanismo (evento de tempo real, entrada no feed de sincronização ou o mesmo caminho da troca de perfil do ADR-0027) é decisão técnica do `backend-agent`; enquanto não chega, o servidor já impõe os módulos novos.
16. A assinatura continua uma só por tenant (ADR-0017). `SUSPENDED` bloqueia a escrita de todos os módulos.

**O que não muda**

17. Expediente (`BusinessDay`) com abertura explícita, saldo inicial e um caixa só vale para todos os comportamentos (ADR-0019). Pedido e pagamento continuam exigindo expediente aberto. Numeração por data operacional (ADR-0039), fechamento (ADR-0013), fiado (ADR-0021), cancelamento e estorno (ADR-0022) não mudam.
18. Na abertura do expediente, a clonagem do modelo semanal (ADR-0038) só acontece se o tenant tem `DAILY_MENU`.

## Alternativas consideradas

- **Um campo `Tenant.Type` com um valor só:** mais simples hoje, mas o dono do produto já decidiu que o restaurante poderá somar a lanchonete. Trocar um campo escalar por um conjunto depois exigiria migrar contratos e consultas.
- **Módulos habilitáveis um a um já agora:** mais flexível, mas multiplica as combinações a testar (marmita sem cardápio do dia, delivery sem cardápio fixo). O mapa fixo cobre o piloto, e o `ITenantModules` deixa a porta aberta.
- **Rótulo com texto livre:** "Hamburgeria", "hamburgueria " e "Hambúrgueria" virariam três categorias no marketplace.
- **Rótulo decidindo funcionamento** (açaiteria com módulo próprio): misturaria marketing com regra. O que muda o funcionamento é o comportamento.
- **Permitir trocar o comportamento pelo app:** um restaurante com pedidos, cardápios e fechamentos viraria lanchonete com dados órfãos. Fica para o admin, com análise caso a caso.

## Consequências

- Novas tabelas `tenant_behaviors` e `tenant_labels` (por tenant, com RLS), migração com `RESTAURANT` para todos os tenants existentes e um rótulo inicial `RESTAURANT` (Q-303).
- `SignUpCommand.tenant` ganha `behavior` e `labels`. `PATCH /tenant` ganha `labels`.
- Endpoint de plataforma `POST /admin/tenants/{id}/behaviors` (ADR-0025), auditado com `Origin = PLATFORM_ADMIN`.
- Testes por módulo: cada `OrderType` recusado sem o módulo; cada cadastro recusado sem o módulo; `GET /auth/me` com os módulos certos.
- Revisitar a habilitação de módulos um a um depois do piloto com lanchonetes.
