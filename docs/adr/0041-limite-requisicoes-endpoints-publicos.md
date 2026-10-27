# ADR-0041 — Limite de requisições nos endpoints públicos

- **Status:** Aceito (recomendação aceita pelo usuário em 2026-10-27)
- **Data:** 2026-10-27
- **Origem:** Lacuna encontrada ao escrever `06-contratos-api.md`: login, PIN e códigos de convite de 6 caracteres são alvos de força bruta
- **Relacionados:** ADR-0018, ADR-0027

## Contexto

O bloqueio por conta (5 tentativas de senha ou PIN) protege um alvo específico, mas não impede varrer códigos de convite (≈ 1 bilhão de combinações no alfabeto de 32 símbolos), nem tentar senhas comuns em muitas contas.

## Decisão

Usar o **rate limiter nativo do ASP.NET Core** (`Microsoft.AspNetCore.RateLimiting`), em memória, com janela deslizante:

| Política | Endpoints | Limite |
|---|---|---|
| `auth` | `/auth/login`, `/auth/signup`, `/auth/refresh`, `/auth/password/*` | 10/min por IP **e** 5/min por `login` informado |
| `pin` | `/auth/pin-login`, `/devices/shared/*/employees` | 30/min por `installationId` |
| `invitation` | `GET /invitations/{code}`, `POST /invitations/{code}/accept` | 10/min por IP, 50/dia por IP |
| `webhook` | `/webhooks/asaas` | 120/min por IP |

Excesso → `429 TooManyRequests` com `Retry-After`. Endpoints autenticados não têm limite no MVP.

## Alternativas consideradas

- **WAF/Front Door:** mais robusto, mas com custo fixo desproporcional ao piloto. É a evolução natural quando houver mais de uma réplica.
- **Redis para contadores distribuídos:** desnecessário com 1 réplica (ADR-0037).

## Consequências

- Com mais de uma réplica, os limites passam a ser por réplica. É aceitável até migrar para o WAF.
- O IP real vem de `X-Forwarded-For` confiável do Container Apps (`ForwardedHeaders` configurado).
