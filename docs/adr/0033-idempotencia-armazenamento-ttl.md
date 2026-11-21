# ADR-0033 — Idempotência: armazenamento, escopo e TTL

- **Status:** Aceito (recomendação aceita pelo usuário em 2026-10-27)
- **Data:** 2026-10-27
- **Origem:** Lacuna em §9.1 ("pelo período configurado"), `GET /operations/{id}`
- **Relacionados:** ADR-0010, ADR-0011
- **Complementado por:** ADR-0045 (Proposto) item 10, idempotência das rotas públicas sem usuário; ADR-0046 (Proposto) item 6, aceite do pedido do site.
- **Complementado em 2026-11-20** (implementação do EP-002; `docs/07-backend.md` §7.3 e §7.4, `docs/06-contratos-api.md` §6.19): itens 7 a 9.

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
7. **O corpo guardado nunca contém segredo do cliente** *(adicionado em 2026-11-20)*. Um campo marcado como segredo (`ReplaySecret`, hoje só o `trackingToken` do envio pela página, ADR-0047) é removido antes de `CompleteAsync`. No reenvio, ele é recomposto por um `IReplaySecretSource` a partir do `resource_id`. O banco nunca guarda o token em claro.
8. **Passo de preparação antes da transação** *(adicionado em 2026-11-20)*. Uma rota pode declarar `Preparation`. Hoje só `POST /public/stores/{slug}/orders` usa, com Turnstile e conferência da cotação, que pode chamar o provedor de mapas. A sequência é:
   - primeiro, `FindCompletedAsync` (só leitura, sem transação): um reenvio já concluído responde já, sem repetir o token do Turnstile, que é de uso único;
   - depois, a preparação, fora da transação;
   - só então abre a transação, com `TryBeginAsync` e a ação.

   Nenhuma chamada externa acontece com o `IN_PROGRESS` da chave segurado. O token do Turnstile vai no header `X-Turnstile-Token`, **fora** do `request_hash`. Depois de um erro, o cliente gera um token novo e reenvia com a mesma chave.
9. **Escopo das novas rotas** *(adicionado em 2026-11-20)*:
   - **Rotas públicas:** `Idempotency-Key` anônima, com `user_id` nulo e escopo `(tenant_id, key)` (ADR-0045 item 10). O `endpoint` gravado é o **modelo** da rota (`POST /api/v1/public/stores/{slug}/orders`): o reenvio depois de um `308` de slug continua sendo a mesma operação.
   - **Aceite do pedido do site:** `POST /orders/{id}/accept` entra na lista do item 1 (ADR-0046 item 6), porque consome o número do pedido.
   - **Recusa** (`POST /orders/{id}/reject`): só `If-Match`.
   - **Desistência pública** (`POST /public/tracking/{token}/withdraw`): **sem chave**, idempotente pelo estado. Repetir com o pedido já `WITHDRAWN` devolve o mesmo `200`, sem nova auditoria nem evento.

## Alternativas consideradas

- **Redis:** um componente a mais, e perde a atomicidade com a transação de negócio.
- **TTL de 24 h:** curto para restaurante que fecha domingo e segunda.

## Consequências

- Rascunhos locais com mais de 72 h sem confirmação são reenviados com **chave nova**, mas só depois de consultar `/operations/{key}` e o feed, para evitar duplicidade.
