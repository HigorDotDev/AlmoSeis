# ADR-0038 — Cardápio semanal materializado na abertura do expediente

- **Status:** Aceito (recomendação aceita pelo usuário em 2026-10-27)
- **Data:** 2026-10-27
- **Origem:** Lacuna em RN-003.2, RN-003.5 e RN-003.8 (como o modelo semanal vira cardápio do dia)
- **Relacionados:** ADR-0019, `docs/04-modelo-dados.md` §4.6

## Contexto

A RN-003.8 diz que os cardápios semanais "funcionam como modelos para datas futuras", e a RN-003.5 diz que a reutilização cria uma configuração independente. Faltava definir **quando** o modelo vira cardápio do dia. Se o pedido lesse direto do modelo, uma edição no modelo alteraria o cardápio de um dia já em andamento. Se exigisse um clique diário, a configuração semanal perderia o sentido.

## Decisão

1. Existem dois tipos de `Menu`: `DAILY` (por data) e `WEEKLY_TEMPLATE` (por dia da semana), separados para `MARMITA` e `PF`.
2. Pedidos usam **sempre** o `DAILY` da `BusinessDate` do expediente aberto.
3. Ao **abrir o expediente**, cada `WEEKLY_TEMPLATE` daquele dia da semana sem `DAILY` correspondente é **clonado** como `DAILY`, com `SourceMenuId` apontando o modelo. A clonagem acontece na mesma transação da abertura e é auditada com `Origin = SYSTEM`.
4. Um `DAILY` criado antecipadamente (RN-003.3) tem precedência, e o modelo é ignorado nesse dia.
5. Editar um modelo nunca altera `DAILY` já materializados.
6. Sem `DAILY` nem modelo, a aba de registro oferece criar o cardápio (RN-003.2): do zero, a partir de ontem ou a partir da semana anterior.

## Alternativas consideradas

- **Ler direto do modelo semanal:** a edição do modelo vaza para o dia corrente e para o histórico.
- **Clonagem manual diária:** atrito diário desnecessário.
- **Job à meia-noite:** depende de horário e de fuso, e cria cardápios em dias que o estabelecimento nem abre.

## Consequências

- `POST /business-days` (abrir expediente) também cria cardápios. A resposta inclui os ids criados.
- Um modelo semanal alterado depois da abertura só vale a partir do próximo expediente daquele dia da semana.
