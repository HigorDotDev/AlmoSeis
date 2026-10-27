# ADR-0042 — Migrations SQL-first geradas a partir do `05-ddl.md`

- **Status:** Aceito (recomendação aceita pelo usuário em 2026-10-27)
- **Data:** 2026-10-27
- **Origem:** Início da implementação do backend. O `07` §7.3 previa migrations do EF com trechos `Sql()` copiados do `05`, o que deixava duas fontes de verdade para o schema
- **Relacionados:** ADR-0003, ADR-0026, ADR-0029, `docs/05-ddl.md`, `docs/07-backend.md` §7.3/§7.12

## Contexto

O `05-ddl.md` é a fonte de verdade do schema físico e foi validado com teste de fumaça. Boa parte dele o EF Core não sabe gerar: RLS e políticas por papel, grants por coluna, `CHECK`s compostos, índices parciais, FKs compostas para colunas que não são PK, funções. Migrations geradas por *diff* do modelo EF produziriam um schema diferente do documentado, e cada correção manual reabriria a divergência.

## Decisão

1. **As migrations são SQL**, não *diff* do modelo EF. Cada migration é uma classe `Migration` mínima que executa um script embutido (`Persistence/Migrations/Sql/NNNN_Nome.sql`).
2. **O script é gerado** do `05-ddl.md` por `backend/tools/Sync-DdlMigration.ps1`, que concatena os blocos `sql` do documento, exceto o de papéis (§5.1, executado pelo administrador de cada ambiente).
3. **Mudança de schema daqui em diante:**
   - edita-se o `05`, acrescentando um bloco `sql` de alteração (`ALTER TABLE …`) numa seção "Alterações" datada;
   - revalida-se com o teste de fumaça;
   - gera-se uma **nova** migration com o script;
   - a migration inicial nunca é editada depois de aplicada em homolog.
4. **Garantias automáticas:**
   - `DdlMigrationSyncTests` (unitário) compara o script embutido com o documento;
   - `SchemaTests` (integração, Testcontainers) aplica a migration com o papel `almoseis_migrator`, roda o `05-ddl-smoke-test.sql` (21 checagens) e confere se **toda entidade mapeada no EF** encontra suas colunas no banco.
5. O EF continua sendo o ORM e o executor das migrations (`dotnet ef database update`, `migrations bundle`). O aviso `PendingModelChangesWarning` é desligado, porque não há snapshot de modelo.

## Alternativas consideradas

- **Migrations por diff do EF + `Sql()` manual:** duas fontes de verdade e um schema gerado diferente do validado.
- **Ferramenta externa (Flyway/DbUp):** funciona, mas acrescenta outra ferramenta ao pipeline. O EF já executa SQL e controla o histórico.

## Consequências

- Mapeamentos EF errados (coluna inexistente, nome divergente) são pegos pelo `Ef_model_matches_database_columns` em vez de "corrigidos" por uma migration automática.
- `dotnet ef migrations add` **não** é usado. Uma migration nova é criada à mão (classe de 10 linhas + script gerado).
