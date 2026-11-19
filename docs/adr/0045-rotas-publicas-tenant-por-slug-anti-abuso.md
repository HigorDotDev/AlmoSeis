# ADR-0045 — Rotas públicas: tenant resolvido pelo slug, RLS e anti-abuso

- **Status:** Proposto
- **Data:** 2026-11-19
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

3. `ITenantContext` ganha a origem da resolução: `Source = JWT | PUBLIC_SLUG | TRACKING_TOKEN | GATEWAY_WEBHOOK`. Continua imutável durante a requisição.
4. Um middleware das rotas públicas resolve:
   - `{slug}` → `tenant_id` pela tabela de plataforma `store_slug` (`slug` único, `tenant_id`, `is_current`, `created_at`, `blocked_until`). Slug antigo → `301` para o atual (a página segue o redirecionamento). Slug inexistente → `404 StoreNotFound`;
   - `{trackingToken}` → `tenant_id` e `order_id` pela tabela de plataforma `order_tracking_token` (guarda só o **SHA-256** do token, ADR-0047).
5. Essas duas tabelas **não têm RLS de tenant**. O papel da aplicação tem só `SELECT` nelas (e `INSERT`/`UPDATE` pelos handlers do módulo dono). Elas não guardam dado pessoal: o slug já é público e o token está em hash.
6. Depois de resolvido, o mesmo `DbConnectionInterceptor` do ADR-0026 executa `SET app.tenant_id`. **Toda leitura e gravação seguinte passa pelo query filter e pelo RLS** como numa rota autenticada. `app.user_id` fica vazio.
7. Só handlers marcados como públicos (`IPublicHandler`) podem ser chamados pelos controllers públicos. Um teste de arquitetura garante isso. Os DTOs públicos são próprios (nunca reaproveitam os DTOs do app) e não carregam ids de funcionário, custos nem dados de outros clientes.
8. Teste de integração obrigatório: com dois tenants, o slug de A nunca lê nem grava dados de B, e um token de acompanhamento de A nunca abre pedido de B (critério do RF-001 estendido).

**Auditoria e idempotência sem usuário**

9. `AuditLog.Origin` ganha `WEB`. Pedido web gravado com `ActorUserId` e `ActorEmployeeId` nulos. O `CorrelationId` continua. Telefone, endereço e IP **nunca** vão para a auditoria (ADR-0024).
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
    | `public-quote` | `delivery-quotes` | 10/min e 60/dia por IP; 300/dia por tenant |
    | `public-order` | `POST .../orders`, `POST .../withdraw` | 5/min e 20/h por IP |
    | `public-tracking` | `GET /public/tracking/*` | 60/min por IP |
    | `public-render` | leituras feitas pelo servidor da página (ADR-0051) | isentas do limite por IP, autenticadas por `X-Web-Renderer-Key` |

12. **Desafio anti-robô: Cloudflare Turnstile** (modo gerenciado, quase sempre invisível) no envio do pedido e na cotação de entrega. O servidor valida o token no `siteverify` antes de gravar. Se o Cloudflare não responder em 3 s, o servidor **aceita** e marca o pedido como `RiskFlag = CAPTCHA_UNAVAILABLE`. Isso é seguro porque o pedido "pagar na entrega" sempre passa pelo aceite humano (ADR-0046) e o pedido online só vai para a cozinha depois de pago.
13. **Limites de negócio:**
    - no máximo **2 pedidos aguardando aceite** por telefone por tenant e **3 por IP** por tenant (`429 TooManyPendingOrders`);
    - tamanho do pedido do ADR-0044 (20 marmitas, 30 linhas), observações até 200 caracteres, corpo até 32 KB;
    - campo-isca (honeypot) escondido no formulário: preenchido → `400` sem detalhes.
14. O IP é guardado **só como hash com sal rotativo** no registro de entrada do pedido, por 7 dias, para os limites e para a investigação de trote (ADR-0052).
15. **CORS:** as rotas públicas aceitam só a origem da página (domínio a definir). As demais rotas da API não aceitam CORS de navegador.
16. Assinatura do tenant sem escrita (`SUSPENDED`/`CANCELED`, ADR-0017): leitura pública responde `UNAVAILABLE` e gravação responde `409 StoreUnavailable`. O motivo nunca é exposto.

## Alternativas consideradas

- **Tenant no header ou na query, com o mesmo pipeline das rotas autenticadas:** reabre exatamente o que o ADR-0026 fechou.
- **JWT anônimo emitido para a página (token de "visitante" com `tenant_id`):** permite reaproveitar o pipeline, mas cria um token de longa vida no navegador e não reduz o risco real, que é a resolução do slug.
- **Função `SECURITY DEFINER` para resolver o slug:** funciona, mas a tabela não tem dado sensível. `SELECT` direto é mais simples de testar.
- **reCAPTCHA do Google:** equivalente, mas o Cloudflare já está no projeto (R2) e o Turnstile não exige interação na maior parte dos casos.
- **Recusar o pedido quando o Turnstile falha:** bloqueia venda por falha de terceiro.
- **WAF/Front Door já na Fase 1:** custo fixo desproporcional ao piloto (mesmo argumento do ADR-0041).

## Consequências

- `ITenantContext` e o interceptor mudam (nova propriedade `Source`). O código que hoje lê `ITenantContext` não precisa mudar.
- Duas tabelas de plataforma novas (`store_slug`, `order_tracking_token`), com grants próprios no `05-ddl.md`.
- Os limites são por réplica (mesma ressalva do ADR-0041). Com mais réplicas ou com abuso real, migrar para WAF.
- O segredo do Turnstile e a `X-Web-Renderer-Key` ficam no Key Vault (ADR-0037).
