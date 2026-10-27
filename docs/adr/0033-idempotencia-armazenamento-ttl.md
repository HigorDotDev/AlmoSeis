# ADR-0033 — Idempotência: armazenamento, escopo e TTL

- **Status:** Aceito (recomendação aceita pelo usuário em 2026-10-27)
- **Data:** 2026-10-27
- **Origem:** Lacuna em §9.1 ("pelo período configurado"), `GET /operations/{id}`
- **Relacionados:** ADR-0010, ADR-0011

## Contexto

O épico define o header `Idempotency-Key`, mas não diz onde o resultado fica, por quanto tempo, nem o que acontece com uma chave repetida com outro payload ou com duas requisições simultâneas.

## Decisão

1. **Header obrigatório** (`428` se ausente) em:
   - `POST /orders`, `POST /orders/{id}/items`, `POST /orders/{id}/weighings`;
   - `POST /orders/{id}/payments`, `POST /payments/{id}/reverse`;
   - `POST /closing/cash-movements`, `POST /closing/finalize`, `POST /closing/{id}/adjustments`;
   - `POST /business-days` (abrir expediente).
2. Chave: UUID gerado pelo app **quando o usuário confirma o envio**, gravado no SQLite junto da operação pendente e reaproveitado em todo reenvio.
3. A tabela `idempotency_record` guarda `tenant_id`, `key`, `user_id`, `endpoint`, `request_hash` (SHA-256 do corpo canônico), `status` (`IN_PROGRESS`/`COMPLETED`), `response_status`, `response_body`, `resource_id`, `created_at` e `expires_at`. Unicidade em `(tenant_id, key)`. A inserção do registro acontece na mesma transação da operação.
4. Comportamento:
   - mesma chave + mesmo hash + `COMPLETED` → devolve a resposta original, com o header `Idempotent-Replayed: true`;
   - mesma chave + hash diferente → `422 IdempotencyKeyReused`;
   - mesma chave com outra requisição `IN_PROGRESS` → `409 OperationInProgress` (o cliente consulta depois).
5. `GET /api/v1/operations/{key}` devolve `NOT_FOUND`, `IN_PROGRESS` ou `COMPLETED` com o resultado. O "id da operação" do épico **é** a chave de idempotência.
6. **TTL de 72 h**, para cobrir um fim de semana com o app fechado. Um job remove os registros expirados.

## Alternativas consideradas

- **Redis:** um componente a mais, e perde a atomicidade com a transação de negócio.
- **TTL de 24 h:** curto para restaurante que fecha domingo e segunda.

## Consequências

- Rascunhos locais com mais de 72 h sem confirmação são reenviados com **chave nova**, mas só depois de consultar `/operations/{key}` e o feed, para evitar duplicidade.
