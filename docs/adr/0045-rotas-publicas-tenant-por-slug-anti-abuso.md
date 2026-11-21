# ADR-0045 — Rotas públicas: tenant resolvido pelo slug, RLS e anti-abuso

- **Status:** Proposto
- **Data:** 2026-11-19
- **Atualizado:** 2026-11-20, texto alinhado à implementação das fatias 1 a 5 do EP-002 e às correções da revisão (migration 0007, até o commit `2f65bff`). Itens alterados: 3, 4, 5, 9, 11, 12, 14. Ver também "Implementação (2026-11-20)".
- **Origem:** EP-002, RF-201 a RF-208, RNF-203; a página não tem usuário autenticado
- **Relacionados:** ADR-0026 (complementado), ADR-0031, ADR-0033 (complementado), ADR-0041 (complementado), ADR-0044, ADR-0047, ADR-0050, ADR-0051

## Contexto

O ADR-0026 diz que o tenant ativo vem **só do JWT**. A página pública não tem JWT: o cliente final é anônimo. O servidor precisa descobrir o tenant pelo slug, abrir a sessão do RLS e gravar o pedido, sem abrir uma brecha nas rotas autenticadas.

Rotas públicas que gravam pedido e consultam rota no Google atraem robôs: pedidos falsos (trote), varredura de slugs e de tokens de acompanhamento, e custo de API de mapas.

## Decisão

**Separação das rotas**

1. Todas as rotas anônimas ficam sob **`/api/v1/public/`** e num grupo de controllers próprio:
   - `GET  /public/stores/{slug}` (perfil, estado, cardápio do dia, tamanhos, bebidas);
   - `POST /public/stores/{slug}/delivery-quotes` (ADR-0048);
   - `POST /public/stores/{slug}/orders`;
   - `GET  /public/tracking/{trackingToken}` (ADR-0047);
   - `POST /public/tracking/{trackingToken}/withdraw` (desistência, ADR-0046);
   - Fase 2: `POST /public/tracking/{trackingToken}/payment` (gera ou reabre a cobrança, ADR-0050).

   Os nomes finais vão para `docs/06-contratos-api.md`.
2. As rotas públicas **ignoram** qualquer `Authorization` recebido. As rotas autenticadas **continuam** tirando o tenant só do JWT e nunca aceitam slug, header ou corpo como fonte do tenant. O ADR-0026 não é enfraquecido.

**Resolução do tenant**

3. `ITenantContext` ganha a origem da resolução: `Source = JWT | PUBLIC_SLUG | TRACKING_TOKEN | GATEWAY_WEBHOOK | SYSTEM`. Continua imutável durante a requisição. `SYSTEM` é o dos jobs que gravam no tenant (expiração e retenção do pedido do site, envio de push), sem usuário nem funcionário; a auditoria dessas operações sai com `Origin = SYSTEM`. *(`SYSTEM` adicionado em 2026-11-20.)*
4. Um middleware das rotas públicas (`PublicTenantMiddleware`, antes da autenticação) resolve:
   - `{slug}` → `tenant_id` pela tabela de plataforma **`store_slugs`** (`slug` PK, `tenant_id`, `is_current`, `created_at`, `replaced_at`, `blocked_until`). Slug antigo ou com maiúsculas → `301` (GET) ou `308` (POST, preserva método e corpo) para o atual. Slug inexistente → `404 StoreNotFound`;
   - `{trackingToken}` → `tenant_id` pela tabela de plataforma **`order_tracking_tokens`** (guarda só o **SHA-256** do token, ADR-0047), lida pela função `resolve_tracking_token`. Token inexistente → `404 NotFound`; vencido → `410 TrackingExpired`.
5. *(Reescrito em 2026-11-20, migrations 0004 e 0007.)* As duas tabelas têm RLS (`FORCE`), com regras diferentes:
   - **`store_slugs`:** **só o slug tem leitura aberta** (`public_lookup`, `USING (true)`), porque o slug é público. Gravação só no tenant da sessão (`WITH CHECK`).
   - **`order_tracking_tokens`:** leitura só no tenant da sessão. Antes de o tenant existir, a resolução é feita pela função `SECURITY DEFINER` `resolve_tracking_token(token_hash)`, que devolve só `tenant_id` e `expires_at` de **um** hash, nunca a linha (ADR-0026 item 10). Uma leitura aberta daria a qualquer sessão da aplicação todos os hashes, tenants e pedidos.

   Nenhuma das duas guarda dado pessoal.
6. Depois de resolvido, o mesmo `DbConnectionInterceptor` do ADR-0026 executa `SET app.tenant_id`. **Toda leitura e gravação seguinte passa pelo query filter e pelo RLS** como numa rota autenticada. `app.user_id` fica vazio.
7. Só handlers marcados como públicos (`IPublicHandler`) podem ser chamados pelos controllers públicos. Um teste de arquitetura garante isso. Os DTOs públicos são próprios (nunca reaproveitam os DTOs do app) e não carregam ids de funcionário, custos nem dados de outros clientes.
8. Teste de integração obrigatório: com dois tenants, o slug de A nunca lê nem grava dados de B, e um token de acompanhamento de A nunca abre pedido de B (critério do RF-001 estendido).

**Auditoria e idempotência sem usuário**

9. `AuditLog.Origin` ganha `WEB` (operações do cliente pela página) e `SYSTEM` (operações automáticas: expiração, recusa no fechamento, anonimização). Pedido web gravado com `ActorUserId` e `ActorEmployeeId` nulos. O `CorrelationId` continua. Telefone, endereço e IP **nunca** vão para a auditoria (ADR-0024).
10. `POST /public/stores/{slug}/orders` exige `Idempotency-Key` (ADR-0033):
    - a página gera um UUID **quando o cliente toca em "Enviar pedido"**, guarda no `localStorage` junto do carrinho e reaproveita em todo reenvio;
    - escopo: `(tenant_id, key)`, como hoje, com `user_id` nulo e `endpoint` = rota pública. O `request_hash` impede reaproveitar a chave com outro corpo (`422 IdempotencyKeyReused`);
    - a página **não** usa `GET /operations/{key}`. Se a resposta se perder, reenvia a mesma requisição com a mesma chave e recebe a resposta original (`Idempotent-Replayed: true`), que inclui o token de acompanhamento;
    - o TTL continua 72 h.

**Anti-abuso**

11. **Limite de requisições** (complementa a tabela do ADR-0041; mesmo rate limiter nativo):

    | Política | Rotas | Limite |
    |---|---|---|
    | `public-read` | `GET /public/stores/*` | 120/min por IP |
    | `public-quote` | `delivery-quotes` | 10/min e 60/dia por IP; 300/dia por **tenant resolvido** (`PublicStoreQuotaMiddleware`, depois de o slug virar tenant: slug antigo e maiúsculas contam na mesma cota; `RateLimiting:PublicQuotesPerStorePerDay`) |
    | `public-order` | `POST .../orders`, `POST .../withdraw` | 5/min e 20/h por IP |
    | `public-tracking` | `GET /public/tracking/*` | 60/min por IP |
    | `public-render` | leituras feitas pelo servidor da página (ADR-0051) | isentas do limite por IP, autenticadas por `X-Web-Renderer-Key` |

12. **Desafio anti-robô: Cloudflare Turnstile** (modo gerenciado, quase sempre invisível) no envio do pedido e na cotação de entrega. O token vai no header **`X-Turnstile-Token`**, fora do corpo e fora do hash da idempotência, porque é de uso único (ADR-0033 item 8). O servidor valida o token no `siteverify` antes de gravar. Sem token ou com token inválido, a resposta é `403 CaptchaFailed`. Se o Cloudflare não responder em 3 s (erro, tempo esgotado ou `internal-error`), o servidor **aceita** e marca o pedido com `riskFlag: "CAPTCHA_UNAVAILABLE"`, que o app pode destacar. Na cotação, o Cloudflare fora do ar também não bloqueia. Isso é seguro porque o pedido "pagar na entrega" sempre passa pelo aceite humano (ADR-0046) e o pedido online só vai para a cozinha depois de pago.
13. **Limites de negócio:**
    - no máximo **2 pedidos aguardando aceite** por telefone por tenant e **3 por IP** por tenant (`429 TooManyPendingOrders`);
    - tamanho do pedido do ADR-0044 (20 marmitas, 30 linhas), observações até 200 caracteres, corpo até 32 KB;
    - campo-isca (honeypot) escondido no formulário: preenchido → `400` sem detalhes.
14. O IP é guardado **só como hash com sal rotativo** no registro de entrada do pedido (`web_order_intakes`), por 7 dias, para os limites e para a investigação de trote (ADR-0052). O hash é HMAC-SHA-256 com sal **diário** derivado de `PublicPage:IpHashSecret`. *(Detalhado em 2026-11-20.)*
    - **O segredo é obrigatório fora de Development**, com 32 caracteres ou mais e diferente do `PublicPage:TrackingTokenSecret`. Sem ele, a API não sobe. Em Development, vazio vira um valor aleatório por processo.
    - **Chaves de teste do Turnstile** (`1x0000…`, `2x0000…`, `3x0000…`), ou `Turnstile:Enabled` sem chave, **fora de Development** também impedem a API de subir.
15. **CORS:** as rotas públicas aceitam só a origem da página (domínio a definir). As demais rotas da API não aceitam CORS de navegador.
16. Assinatura do tenant sem escrita (`SUSPENDED`/`CANCELED`, ADR-0017): leitura pública responde `UNAVAILABLE` e gravação responde `409 StoreUnavailable`. O motivo nunca é exposto.

## Alternativas consideradas

- **Tenant no header ou na query, com o mesmo pipeline das rotas autenticadas:** reabre exatamente o que o ADR-0026 fechou.
- **JWT anônimo emitido para a página (token de "visitante" com `tenant_id`):** permite reaproveitar o pipeline, mas cria um token de longa vida no navegador e não reduz o risco real, que é a resolução do slug.
- **Função `SECURITY DEFINER` para resolver o slug:** funciona, mas a tabela não tem dado sensível. `SELECT` direto é mais simples de testar. *(2026-11-20: continua valendo para o slug. Para o token de acompanhamento, a leitura aberta foi trocada pela função `resolve_tracking_token` na revisão. Ver o item 5.)*
- **reCAPTCHA do Google:** equivalente, mas o Cloudflare já está no projeto (R2) e o Turnstile não exige interação na maior parte dos casos.
- **Recusar o pedido quando o Turnstile falha:** bloqueia venda por falha de terceiro.
- **WAF/Front Door já na Fase 1:** custo fixo desproporcional ao piloto (mesmo argumento do ADR-0041).

## Consequências

- `ITenantContext` e o interceptor mudam (nova propriedade `Source`). O código que hoje lê `ITenantContext` não precisa mudar.
- Duas tabelas de plataforma novas (`store_slugs`, `order_tracking_tokens`), com grants e políticas próprios no `05-ddl.md` (migrations 0004 e 0007), e a tabela de tenant `web_order_intakes`, com o hash do IP.
- Os limites são por réplica (mesma ressalva do ADR-0041). Com mais réplicas ou com abuso real, migrar para WAF.
- O segredo do Turnstile e a `X-Web-Renderer-Key` ficam no Key Vault (ADR-0037), junto de `PublicPage:IpHashSecret` e `PublicPage:TrackingTokenSecret`.

## Implementação (2026-11-20)

- **Tenant das rotas públicas:** o `PublicTenantMiddleware` roda antes da autenticação. O JwtBearer devolve `NoResult` em `/api/v1/public`, então um `Authorization` recebido é ignorado (item 2). A resolução não usa `IgnoreQueryFilters`. Ela lê `store_slugs` por SQL, com a leitura aberta, e resolve o token pela função.
- **Idempotência** (item 10): a implementação seguiu o item. Os acréscimos estão no ADR-0033, itens 7 a 9: corpo guardado sem o `trackingToken`, preparação antes da transação e `endpoint` gravado como o modelo da rota.
- **Limites de pendentes** (item 13): `pg_advisory_xact_lock` por telefone e por hash do IP, sempre nessa ordem, antes da contagem. Assim, dois envios simultâneos do mesmo cliente não passam juntos.
- **Assinatura sem escrita** (item 16): as rotas `/public/*` ficam fora do `SubscriptionWriteGuardFilter` (`402`). O próprio handler responde `409 StoreUnavailable`, sem o motivo.
- **CORS** (item 15): política `PublicPage` própria (`Cors:PublicPageOrigins`). Ela não tem credenciais, aceita `GET` e `POST` e os headers `Content-Type`, `Idempotency-Key`, `X-Turnstile-Token` e `If-None-Match`, e é separada da política do app do restaurante (ADR-0053 §4.6).
