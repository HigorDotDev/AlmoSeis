# ADR-0031 — Auditoria transacional e outbox para eventos em tempo real

- **Status:** Aceito (recomendação aceita pelo usuário em 2026-10-27)
- **Data:** 2026-10-27
- **Origem:** Lacuna em RF-008, RF-013, §8 (como gravar e publicar com consistência)
- **Relacionados:** ADR-0008, ADR-0009, ADR-0032

## Contexto

Se a auditoria ou o evento SignalR forem emitidos fora da transação, podem existir auditorias de operações que falharam, ou operações sem auditoria. E publicar no SignalR antes do commit faz o cliente buscar um dado que ainda não existe.

## Decisão

1. **Auditoria explícita de negócio:** os handlers registram `AuditEntry` por um `IAuditWriter`, com tipo de operação semântico (`OrderItemAdded`, `PaymentReversed`…) e diffs **escolhidos a dedo** (não um dump do change tracker). A gravação acontece **na mesma transação** da operação.
2. **Imutabilidade:** o papel de banco da aplicação tem só `INSERT` e `SELECT` em `audit_log` (`REVOKE UPDATE, DELETE`).
3. **Eventos de alteração de pedido** (`OrderChangeEvent`, RF-008): gravados na mesma transação. As confirmações de ciência (`AlertAcknowledgement`) são por funcionário (RN-008.7), e o autor da alteração é excluído dos destinatários (RN-008.6).
4. **Outbox:** os eventos de integração e tempo real são gravados em `outbox_message` na mesma transação. Um `BackgroundService` lê as mensagens pendentes (`FOR UPDATE SKIP LOCKED`), publica nos grupos SignalR e marca como processadas. A entrega é pelo menos uma vez, e o cliente deduplica por `eventId` ou `seq`.
5. **Logs operacionais** (OpenTelemetry/Serilog) são outra coisa e nunca substituem a auditoria (RN-013.7).

## Alternativas consideradas

- **Auditoria automática via `ChangeTracker`:** grava ruído e dados sensíveis, e perde a semântica da operação.
- **Publicar no SignalR direto do handler:** perde eventos se o processo cair e publica antes do commit.

## Consequências

- Latência extra de publicação ≈ intervalo de leitura do outbox (meta ≤ 500 ms, dentro dos 3 s do RNF-002).
- Job de limpeza do outbox processado após 7 dias.
