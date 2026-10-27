# ADR-0032 — Feed de sincronização com sequência por tenant

- **Status:** Aceito (recomendação aceita pelo usuário em 2026-10-27)
- **Data:** 2026-10-27
- **Origem:** Lacuna em `GET /sync/changes`, RN-014.6, §8 ("eventos perdidos recuperáveis")
- **Relacionados:** ADR-0009, ADR-0031, ADR-0034

## Contexto

Depois de reconectar, o app precisa saber **o que mudou** desde a última vez. Usar `updated_at` como cursor perde alterações por causa da diferença de relógio e de transações concorrentes. Uma sequência global `bigserial` também perde: a transação que obteve o número 101 pode fazer commit depois da que obteve o 102, e o cliente que leu até o 102 pula o 101.

## Decisão

1. A tabela `change_feed` guarda `tenant_id`, `seq bigint`, `entity_type`, `entity_id`, `entity_version`, `change_kind` (`UPSERT`/`DELETE`) e `occurred_at`. É escrita na **mesma transação** da alteração.
2. `seq` é **por tenant**, obtido com `UPDATE tenant_sync_counter SET last_seq = last_seq + 1 WHERE tenant_id = @t RETURNING last_seq`. A trava de linha serializa as escritas de cada tenant, então a ordem de commit acompanha a ordem de `seq`. Isso é aceitável para o volume de um restaurante pequeno.
3. `GET /api/v1/sync/changes?since={seq}&limit=500` devolve as alterações compactadas (a última de cada entidade) e o `lastSeq`. O cliente busca os recursos que estão desatualizados no cache.
4. Os eventos SignalR carregam `seq`. Se o cliente perceber um salto, chama `/sync/changes`.
5. O feed é mantido por **7 dias**. Um cursor mais antigo recebe `410 Gone (SyncCursorExpired)` e o cliente recarrega tudo.
6. O feed respeita as permissões: só lista entidades que o funcionário pode ler.

## Alternativas consideradas

- **Cursor por `updated_at`:** perde alterações.
- **Sequência global + "watermark" por snapshot de transação (`pg_snapshot_xmin`):** correta, mas mais complexa sem necessidade neste volume.
- **Recarregar tudo ao reconectar:** caro em aparelhos de entrada e em redes ruins.

## Consequências

- Toda escrita de tenant passa pelo contador, o que vira um ponto de contenção. Se um tenant grande no futuro saturar esse ponto, revisitar este ADR.
