# ADR-0043 — Login separado do administrador de plataforma

- **Status:** Aceito (decisão do usuário em 2026-10-27)
- **Data:** 2026-10-27
- **Origem:** Implementação das rotas `/admin/*` de `06` §6.15, que exigem "token de usuário de plataforma, sem tenant". Nenhum fluxo emitia esse token: todo login escolhe um estabelecimento
- **Relacionados:** ADR-0017, ADR-0025, ADR-0026, ADR-0027, `docs/06-contratos-api.md` §6.3/§6.15, `docs/07-backend.md` §7.4

## Contexto

O módulo de administração (ADR-0025) atua sobre todos os estabelecimentos: lista tenants, estende teste grátis, suspende, reativa e reprocessa webhooks de cobrança. Ele usa o papel `almoseis_platform` e não pode carregar o tenant de um estabelecimento, porque o tenant vem só do token e liga a RLS (ADR-0026).

O login comum (`POST /auth/login`) sempre escolhe um vínculo e emite token com `tenant_id`. Um administrador de plataforma pode nem ter vínculo com estabelecimento.

## Decisão

1. **Rota própria:** `POST /auth/admin/login` (pública), com `{ "login": "...", "password": "..." }`. Só aceita usuários com `users.is_platform_admin = true`. Qualquer outro caso (usuário inexistente, senha errada, usuário sem a marca) responde o mesmo `401 InvalidCredentials`, e falhas contam no bloqueio de senha do usuário, como no login comum.
2. **Token sem tenant:** o access token leva `sub`, a claim `platform_admin = "true"` e `security_stamp` = o stamp do **usuário**. Não leva `tenant_id`, `employee_id` nem `device_id`.
3. **Sem refresh token:** `refresh_tokens` exige tenant e vínculo. A sessão de admin dura o access token (15 min) e é renovada com novo login. A resposta é `{ "accessToken", "expiresAt", "user": { "id", "fullName", "email" } }`.
4. **Validação a cada requisição:** um token com `platform_admin` e sem `tenant_id` só é aceito se o usuário ainda tem `is_platform_admin` e o mesmo `security_stamp`. Tirar a marca ou trocar a senha revoga o acesso na hora.
5. **Separação dos mundos:**
   - token de admin **não** acessa rotas de estabelecimento (sem tenant → `401`);
   - token de estabelecimento **não** acessa `/admin/*`, mesmo de um usuário admin (`403`);
   - tokens de estabelecimento deixam de carregar a claim `platform_admin`.
6. **Política `PlatformAdmin`** nas rotas `/admin/*`. As ações usam o `PlatformDbContext` e geram auditoria com `origin = PLATFORM_ADMIN`, `actor_user_id` do administrador e `tenant_id` do estabelecimento afetado.
7. **Limite de requisições:** a rota entra na política `auth` do ADR-0041 (10/min por IP e 5/min por login).

## Alternativas consideradas

- **Login comum com "tenant vazio" quando o usuário é admin:** mistura os dois fluxos e deixa o app de estabelecimento recebendo tokens sem tenant.
- **Refresh token de admin numa tabela própria:** mais infraestrutura para um uso raro (poucos administradores, sessões curtas).
- **Ferramenta interna fora da API (acesso direto ao banco):** sem auditoria pelo mesmo caminho e sem as regras do domínio de assinatura.

## Consequências

- O app de administração (ou o uso por ferramenta HTTP) refaz login a cada 15 minutos.
- O teste de arquitetura e os testes de integração passam a cobrir os dois tipos de token.
