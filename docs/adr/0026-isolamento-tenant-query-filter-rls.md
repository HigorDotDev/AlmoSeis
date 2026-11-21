# ADR-0026 — Isolamento de tenant: esquema compartilhado, query filter e RLS

- **Status:** Aceito (recomendação aceita pelo usuário em 2026-10-27)
- **Data:** 2026-10-27
- **Origem:** Lacuna em RN-001.3/RN-001.4, ADR-0004
- **Relacionados:** ADR-0025, ADR-0027
- **Complementado por:** ADR-0045 (Proposto): as rotas públicas `/public/*` resolvem o tenant pelo slug ou pelo token de acompanhamento e aplicam o mesmo RLS. As rotas autenticadas continuam com o tenant só do JWT.
- **Complementado em 2026-11-20** (implementação do EP-002, migrations 0004 e 0007, `docs/05-ddl.md` §5.1 e §5.17): item 10, com as exceções pontuais ao "só o tenant da sessão" e a correção de dados em migration.

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
10. **Exceções pontuais e correção de dados** *(adicionado em 2026-11-20, na implementação do EP-002; detalhe em `docs/05-ddl.md` §5.1)*:
    - Quando o papel da aplicação precisa de um efeito pontual numa linha de **outro** tenant, ou antes de o tenant existir na sessão, a regra fica numa função `SECURITY DEFINER` de dono `almoseis_migrator`, com efeito mínimo e **sem devolver a linha**. Ela tem `SET search_path = public, pg_temp`, `REVOKE ALL ... FROM PUBLIC` e `GRANT EXECUTE` só para quem usa. Como o dono também está sob `FORCE`, a tabela ganha uma política `TO almoseis_migrator` só com o comando que a função usa. Hoje existem duas:
      - `release_push_token(token, installation_id)`: libera o registro do mesmo token de push em outro tenant só quando é da mesma instalação. Devolve só um número (ADR-0049);
      - `resolve_tracking_token(token_hash)`: devolve só `tenant_id` e `expires_at` de um hash (ADR-0047). A tabela `order_tracking_tokens` é lida apenas no tenant da sessão.
    - `store_slugs` tem leitura aberta (`USING (true)`) porque o slug é público. A gravação continua presa ao tenant da sessão (`WITH CHECK`).
    - **Correção de dados em migration:** o papel de migration não enxerga linhas sob `FORCE` e não é membro de `almoseis_platform`. A correção tira o `FORCE` só em volta do comando, com `NO FORCE` e `FORCE` **na mesma transação** da migration (exemplos: 0003, 0004 e 0007).
    - Nenhuma dessas exceções aceita tenant vindo de header, rota ou corpo nas rotas autenticadas (item 2).

## Alternativas consideradas

- **Schema por tenant ou banco por tenant:** isolamento forte, mas migrations e operação multiplicadas por tenant. Desproporcional para o público.
- **Só query filter, sem RLS:** um `IgnoreQueryFilters()` ou SQL cru esquecido vaza dados.

## Consequências

- Com pooling de conexões, a variável `app.tenant_id` precisa ser redefinida a cada abertura de conexão (interceptor) e limpa ao devolvê-la.
- Migrations rodam com um papel administrativo diferente do papel da aplicação.
