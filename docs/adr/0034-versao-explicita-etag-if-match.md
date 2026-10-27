# ADR-0034 — Versão explícita com ETag e If-Match

- **Status:** Aceito (recomendação aceita pelo usuário em 2026-10-27)
- **Data:** 2026-10-27
- **Origem:** Lacuna em §9.2 (mecanismo de versão e transporte)
- **Relacionados:** ADR-0012, ADR-0032

## Contexto

O épico pede que "o cliente informe a versão conhecida", mas não diz em que campo, em que formato, nem o que a API devolve num conflito.

## Decisão

1. Coluna explícita **`version integer not null`** nos agregados com concorrência: `Order`, `Menu`, `Product`, `BusinessDay`, `TenantSettings`, `TenantEmployee` e `Customer`. Configurada como `IsConcurrencyToken` no EF e incrementada por interceptor a cada alteração do agregado, **inclusive quando só filhos mudam** (adicionar item incrementa `Order.version`).
2. **Transporte:** respostas trazem o header `ETag: "{version}"` e o campo `version` no corpo. Escritas sobre recursos existentes (`PATCH`, `POST` em sub-recursos de pedido, pagamentos) exigem `If-Match: "{version}"`. Sem o header, a API responde `428 Precondition Required`.
3. **Conflito:** `409 Conflict` com `ProblemDetails` (`errorCode: VersionConflict`) e uma extensão `current` contendo o estado atual do recurso, para o app mostrar a diferença.
4. **Pagamentos e operações idempotentes** também validam a versão. Assim, o caixa não quita um total que outro funcionário acabou de alterar.

## Alternativas consideradas

- **`xmin` do PostgreSQL:** não precisa de coluna, mas muda com operações internas, não incrementa quando só os filhos mudam e não serve como versão de negócio para o feed.
- **Versão no corpo da requisição:** funciona, mas não é o padrão HTTP e complica `PATCH` parcial.

## Consequências

- O app guarda `version` de cada entidade em cache e manda `If-Match` sempre.
