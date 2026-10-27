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
