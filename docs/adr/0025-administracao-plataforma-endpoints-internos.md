# ADR-0025 — Administração da plataforma por endpoints internos

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** Lacuna no perfil "Administrador da plataforma" (RF-001)
- **Relacionados:** ADR-0001, ADR-0017, ADR-0028

## Contexto

O épico prevê um administrador SaaS, mas não diz por qual interface ele opera, e não existe dashboard web.

## Decisão

- O papel de plataforma `PlatformAdmin` é **global** e fica fora dos vínculos com tenants. Usuários comuns nunca o recebem pelo app.
- Rotas em `/api/v1/admin/*`, protegidas por política própria e usadas via Swagger (somente em homologação) ou scripts autenticados:
  - listar tenants e assinaturas;
  - estender o teste grátis;
  - suspender ou reativar;
  - reprocessar um webhook;
  - consultar saúde do tenant.
- Toda ação administrativa é auditada com `Origin = PLATFORM_ADMIN`.
- **Sem personificação** (logar como o tenant) no MVP. O suporte consulta só metadados e contagens; dados de pedidos exigem autorização registrada do proprietário.
- O ciclo normal da assinatura é automático pelos webhooks (ADR-0017). O admin é exceção.

## Alternativas consideradas

- **Backoffice web mínimo:** fica para quando o volume de tenants justificar.

## Consequências

- Endpoints `/admin` ignoram o filtro de tenant **explicitamente**, por uma conexão e política próprias (ADR-0026), nunca por um "if admin" espalhado no código.

## Complementado por ADR-0054 (2026-11-22)

- Nova ação administrativa: **acrescentar um comportamento** ao tenant (`RESTAURANT` ou `SNACK_BAR`), por exemplo `POST /admin/tenants/{id}/behaviors`, auditada com `Origin = PLATFORM_ADMIN`. É feita mediante aumento na assinatura, cobrado fora do app (ADR-0017). Remover um comportamento fica fora desta fase.

## Complementado por ADR-0054 (2026-11-23)

O acréscimo de comportamento é a primeira ação de `/admin` que **grava dentro de um tenant** com o papel da aplicação, e não pela conexão de plataforma: assim a escrita tem a RLS do tenant, o feed (`TENANT`), o outbox (`TenantModulesChanged`) e a auditoria na mesma transação, como qualquer escrita do estabelecimento (ADR-0031, ADR-0032). O padrão e as suas restrições:

- **Como:** o handler chama `ITenantContext.InitializeForPlatformAdmin(tenantId, adminUserId)` (origem `TenantSource.PlatformAdmin`) e reaplica a sessão (`ITenantSession.ApplyAsync`). `app.tenant_id` = o tenant da rota; o token continua sendo o de `POST /auth/admin/login` (sem tenant, ADR-0043), e o tenant **nunca** vem do corpo.
- **`app.user_id` vazio:** a sessão do banco não leva o id do administrador (`ITenantContext.SessionUserId` é nulo nessa origem). As políticas do grupo 2 de 05 §5.12 (`tenants`, `tenant_employees`, `tenant_employee_roles`) também liberam as linhas do próprio usuário; com o id do administrador, elas abririam os estabelecimentos em que ele seja funcionário. Com o valor vazio, só o tenant da rota é visível. O administrador continua sendo o ator da auditoria e o `enabled_by_user_id`.
- **Tabelas:** só as do acréscimo de comportamento (`tenants`, para a versão; `tenant_behaviors`; e as técnicas da transação: `audit_logs`, `change_feed_entries`, `tenant_sync_counters`, `outbox_messages`). Uma ação nova de `/admin` que precise gravar em outra tabela do tenant por este caminho exige complementar este ADR.
- **Auditoria:** `Origin = PLATFORM_ADMIN`, com `actor_user_id` = administrador e sem funcionário, mais o motivo digitado.
- **Resposta:** só o necessário para confirmar a ação (id do tenant, comportamentos, rótulos e módulos, 06 §6.15). Nenhum dado de pedido, cliente ou funcionário, mantendo a regra "sem personificação" acima.
- **Inexistente:** tenant da rota que não existe → `404`, como as outras rotas de `/admin`.
