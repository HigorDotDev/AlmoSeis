# ADR-0026 — Isolamento de tenant: esquema compartilhado, query filter e RLS

- **Status:** Aceito (recomendação aceita pelo usuário em 2026-10-27)
- **Data:** 2026-10-27
- **Origem:** Lacuna em RN-001.3/RN-001.4, ADR-0004
- **Relacionados:** ADR-0025, ADR-0027

## Contexto

O vazamento de dados entre estabelecimentos é o pior defeito possível do produto (dados financeiros de concorrentes). O épico exige isolamento, mas não diz como.

## Decisão

1. **Banco e esquema compartilhados**, com coluna `tenant_id uuid not null` em toda tabela de negócio.
2. **Tenant ativo vem só do JWT** (claim `tenant_id`, ADR-0027). Nunca vem de header, rota ou corpo. `ITenantContext` é preenchido por middleware e é imutável durante a requisição.
3. **Primeira barreira:** global query filter do EF Core em toda entidade `ITenantOwned`. `TenantId` é preenchido em `SaveChanges`, e uma tentativa de gravar entidade de outro tenant lança exceção.
4. **Segunda barreira:** **Row-Level Security** do PostgreSQL. Políticas `USING (tenant_id = current_setting('app.tenant_id')::uuid)` em toda tabela de tenant. Um `DbConnectionInterceptor` executa `SET app.tenant_id` ao abrir a conexão, e a aplicação usa um papel de banco **sem** `BYPASSRLS`.
5. **Integridade referencial com tenant:** FKs entre entidades de tenant são compostas (`tenant_id`, `x_id`), o que impede referenciar registro de outro tenant.
6. **Troca de estabelecimento:** `POST /auth/switch-tenant` emite novos tokens para o outro vínculo ativo do usuário.
7. **Admin** (ADR-0025) e jobs de plataforma usam um papel de banco separado, com bypass explícito e auditado.
8. **Testes de integração obrigatórios** criam dois tenants e verificam, para cada endpoint, que um não lê nem altera o outro (critério do RF-001).
9. **Tabelas de vínculo** (`tenant_employee`, `tenant_employee_role`): a política aceita `tenant_id = app.tenant_id` **ou** `user_id = app.user_id`. O interceptor também define `app.user_id` a partir do `sub` do JWT. Isso permite que login e troca de tenant listem os estabelecimentos do próprio usuário sem bypass. `User` e `RefreshToken` são tabelas de identidade, sem RLS de tenant, acessadas só pelo módulo Identity (`docs/04-modelo-dados.md` §4.12). *(Adicionado em 2026-10-27, ao consolidar o modelo de dados.)*

## Alternativas consideradas

- **Schema por tenant ou banco por tenant:** isolamento forte, mas migrations e operação multiplicadas por tenant. Desproporcional para o público.
- **Só query filter, sem RLS:** um `IgnoreQueryFilters()` ou SQL cru esquecido vaza dados.

## Consequências

- Com pooling de conexões, a variável `app.tenant_id` precisa ser redefinida a cada abertura de conexão (interceptor) e limpa ao devolvê-la.
- Migrations rodam com um papel administrativo diferente do papel da aplicação.
