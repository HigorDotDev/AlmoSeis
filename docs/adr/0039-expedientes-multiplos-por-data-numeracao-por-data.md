# ADR-0039 — Vários expedientes na mesma data, numeração contínua por data

- **Status:** Aceito (recomendação aceita pelo usuário em 2026-10-27)
- **Data:** 2026-10-27
- **Origem:** Caso de borda encontrado ao consolidar o modelo de dados (ADR-0014 × ADR-0019 × ADR-0023)
- **Relacionados:** ADR-0019, ADR-0023 (refinado por este), `docs/04-modelo-dados.md` §4.9

## Contexto

O ADR-0014 proíbe reabrir fechamento, e o ADR-0019 exige expediente aberto para vender. Se só pudesse existir um expediente por data, um fechamento feito por engano às 14h impediria qualquer venda até o dia seguinte. Se pudessem existir vários expedientes por data com numeração "reiniciada por expediente" (ADR-0023), o pedido #1 apareceria duas vezes no mesmo dia, o que confunde a cozinha e o histórico.

## Decisão

1. `BusinessDate` **não é única** por tenant. Depois de fechar um expediente, é possível abrir outro com a mesma data local, desde que só um esteja `OPEN` por vez. A abertura de um segundo expediente na mesma data exige confirmação explícita ("Já existe um expediente fechado hoje").
2. O `OrderNumber` é sequencial por **(tenant, data operacional)**, vindo de `BusinessDateCounter`, e **continua** entre expedientes da mesma data. Isso refina o ADR-0023 ("reinicia por expediente" passa a "reinicia por data operacional").
3. Cada expediente tem seu próprio saldo inicial, movimentos e `DailyClosing`.
4. Relatórios por data somam todos os expedientes da data. O histórico de fechamentos lista cada expediente separadamente.

## Alternativas consideradas

- **Um expediente por data:** bloqueia a venda após um fechamento acidental.
- **Reabrir fechamento:** viola a imutabilidade do ADR-0014 e a confiança no caixa.

## Consequências

- Unicidade de `Order`: `(tenant_id, business_date, order_number)`.
- Expedientes de turno (almoço e jantar) passam a ser possíveis na prática, sem mudar o modelo de caixa único.
