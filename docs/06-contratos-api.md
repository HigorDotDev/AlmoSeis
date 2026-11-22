# 06. Contratos da API (exemplos JSON literais)

> **Fonte de verdade de payload.** Implementa `04-modelo-dados.md`, `05-ddl.md` e os ADRs. Backend e mobile **não inventam** campo, tipo, `errorCode` ou status HTTP fora deste documento. Se faltar algo, pare e pergunte, e atualize aqui antes de implementar. Onde houver diferença com o §7 do épico, vale este documento (mapeamento em §6.17).

## 6.0 Convenções

| Item | Regra |
|---|---|
| Base | `https://{host}/api/v1` (ADR-0035). Todos os caminhos abaixo são relativos a ela |
| Autenticação | `Authorization: Bearer {accessToken}` em tudo, exceto onde marcado **público**. As rotas `/public/*` da página de pedidos **ignoram** qualquer `Authorization` (ADR-0045) |
| Tenant | Vem só do token (ADR-0026). Nenhum endpoint recebe `tenantId`, exceto os de dispositivo compartilhado e de admin. Nas rotas `/public/*`, vem do `{slug}` ou do `{trackingToken}` resolvido no servidor, com a mesma RLS (ADR-0045, §6.19) |
| Versão do app | `X-App-Version: 1.4.0` e `X-App-Platform: ANDROID`/`IOS`/`WEB` em toda requisição (ADR-0035). `WEB` é o app do restaurante no navegador (ADR-0053) |
| Navegador (`WEB`) | Refresh token e `deviceSecret` em cookies `HttpOnly`, nunca no corpo; conferência de `Origin` nas rotas que leem cookie; CORS só para as origens do ambiente (§6.3, "App no navegador") |
| Idempotência | `Idempotency-Key: {uuid}` nos endpoints marcados **[I]** (ADR-0033) |
| Concorrência | `If-Match: "{version}"` nos endpoints marcados **[M]**. Toda resposta de recurso versionado traz `ETag: "{version}"` e `version` no corpo (ADR-0034) |
| JSON | `camelCase`; enums `UPPER_SNAKE`; campos ausentes = `null` (nunca omitidos) |
| Dinheiro | string com 2 casas: `"35.50"` (ADR-0030) |
| Peso | inteiro em gramas: `"weightGrams": 452` |
| Data operacional | `"2026-10-27"` |
| Instantes | ISO-8601 UTC: `"2026-10-27T15:04:05Z"` |
| Ids | UUID (v7) |
| Paginação | `?limit=50&cursor={opaco}` → `{ "items": [...], "nextCursor": "..." \| null }`; `limit` máx. 100 |
| Autor | sempre `{ "id": "<employeeId>", "displayName": "Ana" }` |
| Escritas | `201 Created` + `Location` ao criar; `200 OK` com o recurso atualizado ao alterar; `204` só quando não há recurso a devolver |

**Ids usados nos exemplos** (todos com prefixo `0192d6a0-0000-7000-8000-`):

| Sufixo | Objeto | Sufixo | Objeto |
|---|---|---|---|
| `000000000001` | tenant "Marmitaria da Ana" | `000000000401` | expediente de 27/10 |
| `000000000011` | usuário Ana (dona) | `000000000501` | pedido #12 (marmita) |
| `000000000301` | funcionária Ana (OWNER) | `000000000502` | pedido #13 (PF, mesa 5) |
| `000000000302` | Bruno (ATTENDANT) | `000000000503` | pedido #14 (quilo) |
| `000000000303` | Carla (CASHIER) | `000000000601`… | itens de pedido |
| `000000000304` | Diego (KITCHEN) | `000000000701`… | pagamentos |
| `000000000101` | prato Frango grelhado | `000000000801` | cliente João Silva |
| `000000000102` | prato Costela | `000000000811` | endereço do João |
| `000000000111`–`113` | Arroz, Feijão, Salada | `000000000901` | cardápio MARMITA 27/10 |
| `000000000121` | Coca-Cola 350ml | `000000000902` | cardápio PF 27/10 |
| `000000000131` | adicional Ovo frito | `000000000a01` | fechamento de 27/10 |
| `000000000201`–`203` | tamanhos P, M, G | `000000000c01` | tablet da cozinha |
| `000000000504` | pedido do site (Carla, ADR-0046) | `000000000802` | cliente Carla Souza |

---

## 6.1 Erros (RFC 7807 `ProblemDetails`)

```json
{
  "type": "urn:almoseis:error:order-already-paid",
  "title": "O pedido já está quitado.",
  "status": 409,
  "detail": "Itens e valores de pedidos quitados não podem ser alterados. Estorne um pagamento para liberar a edição.",
  "instance": "/api/v1/orders/0192d6a0-0000-7000-8000-000000000501/items",
  "errorCode": "OrderAlreadyPaid",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

Validação (`422`) acrescenta `errors` (campo → mensagens em pt-BR):

```json
{
  "type": "urn:almoseis:error:validation-failed",
  "title": "Dados inválidos.",
  "status": 422,
  "errorCode": "ValidationFailed",
  "traceId": "00-1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f-1a2b3c4d5e6f7a8b-01",
  "errors": {
    "items[0].quantity": ["Deve ser entre 1 e 99."],
    "items[1].productId": ["Produto indisponível no cardápio de hoje."]
  }
}
```

Conflito de versão (`409`) traz o estado atual em `current` (ADR-0034):

```json
{
  "type": "urn:almoseis:error:version-conflict",
  "title": "O registro foi alterado por outra pessoa.",
  "status": 409,
  "errorCode": "VersionConflict",
  "traceId": "00-9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d-9a8b7c6d5e4f3a2b-01",
  "expectedVersion": 4,
  "currentVersion": 5,
  "current": { "id": "0192d6a0-0000-7000-8000-000000000501", "version": 5, "...": "recurso completo, como no GET" }
}
```

### Catálogo de `errorCode`

| Status | errorCode | Quando |
|---|---|---|
| 400 | `MalformedRequest` | JSON inválido, tipo errado, enum desconhecido |
| 401 | `Unauthenticated` | token ausente, expirado ou revogado |
| 401 | `InvalidCredentials` | login, senha ou PIN errados |
| 401 | `InvalidRefreshToken` | refresh inválido, expirado ou reutilizado (revoga a família) |
| 401 | `InvalidDeviceSecret` | segredo do dispositivo compartilhado inválido ou revogado |
| 402 | `SubscriptionInactive` | escrita com assinatura `SUSPENDED`/`CANCELED` (ADR-0017); extensão `subscriptionStatus`. Fora do bloqueio: leituras, `/auth`, `/admin`, `/webhooks`, `/devices/push-token` e, com `SUSPENDED`, `POST /closing/finalize` |
| 403 | `PermissionDenied` | falta permissão (a extensão `permission` diz qual) |
| 403 | `NoActiveMembership` | usuário sem vínculo ativo em nenhum estabelecimento |
| 403 | `OriginNotAllowed` | requisição `X-App-Platform: WEB` sem `Origin` ou com `Origin` fora de `Cors:RestaurantAppOrigins`, nas rotas que leem cookie (anti-CSRF, ADR-0053; §6.3 "App no navegador") e na conexão do hub (§6.14) |
| 404 | `NotFound` | recurso inexistente **ou de outro tenant** (nunca 403, para não revelar existência) |
| 404 | `MenuNotFound` | sem cardápio do dia (traz `sources`, §6.7) |
| 404 | `NoOpenBusinessDay` | `GET /business-days/current` sem expediente aberto |
| 409 | `VersionConflict` | `If-Match` desatualizado |
| 409 | `OperationInProgress` | mesma `Idempotency-Key` ainda em execução |
| 409 | `BusinessDayRequired` | escrita que exige expediente aberto (ADR-0019) |
| 409 | `BusinessDayAlreadyOpen` | tentativa de abrir com outro expediente aberto |
| 409 | `BusinessDayClosedToday` | já existe expediente fechado na data e `confirmSameDate` não foi enviado (ADR-0039) |
| 409 | `OrderAlreadyPaid` | edição de itens, valores ou taxa em pedido quitado (ADR-0007) |
| 409 | `OrderCanceled` | qualquer escrita em pedido cancelado |
| 409 | `OrderHasPayments` | cancelamento com pagamentos válidos (ADR-0022) |
| 409 | `OrderNotAccepted` | escrita em pedido do site não aceito (aguardando, recusado, expirado ou desistência): dados, itens, pesagens, produção, pagamento, cancelamento e atendimento (ADR-0046 item 4). Extensão `acceptanceStatus` |
| 409 | `OrderNotAwaitingAcceptance` | aceite, recusa ou desistência de pedido que não aguarda mais aceite (já aceito, recusado ou desistência). Extensão `acceptanceStatus`. Com `If-Match` desatualizado, o `VersionConflict` (com `current`) vem antes |
| 409 | `WebOrderExpired` | aceite, recusa ou desistência depois do prazo de aceite, vencido ou já `EXPIRED` (ADR-0046 item 16). Vem antes do `VersionConflict`: o job de expiração muda a versão |
| 409 | `OrderAlreadyDispatched` | incluir itens, ou trocar entrega ↔ retirada, com o atendimento em `OUT_FOR_DELIVERY`, `DELIVERED` ou `PICKED_UP` (ADR-0046 item 19). O atendente cria outro pedido |
| 409 | `ProductionNotCompleted` | marcar "Saiu para entrega" ou "Retirado" com a produção não concluída e sem `completeProduction` (ADR-0046 item 18) |
| 409 | `PaymentAlreadyReversed` | estorno repetido |
| 409 | `ClosingAlreadyFinalized` | finalizar expediente já fechado |
| 409 | `EmailAlreadyRegistered` / `PhoneAlreadyRegistered` | autocadastro ou aceite com contato já usado (use `existingAccount`); `PhoneAlreadyRegistered` também em `POST`/`PATCH /customers` com telefone de outro cliente, com o cliente existente em `current` |
| 409 | `TrialAlreadyClaimed` | documento ou telefone já usou teste grátis (ADR-0017) |
| 409 | `LastOwner` | remover ou desativar o último `OWNER` ativo |
| 409 | `InvitationNotPending` | convite aceito, revogado ou expirado |
| 409 | `MenuAlreadyExists` | já existe `DAILY` da data, ou modelo do mesmo dia da semana, para o `menuType` (extensão `existingMenuId`) |
| 410 | `SyncCursorExpired` | `since` mais antigo que a retenção do feed (ADR-0032). Extensão `currentSeq`: o app recarrega tudo e continua a partir dela |
| 422 | `ValidationFailed` | regras de formato e de negócio por campo |
| 422 | `IdempotencyKeyReused` | mesma chave com corpo diferente (ADR-0033) |
| 422 | `PaymentExceedsBalance` | valor maior que o saldo (ADR-0021) |
| 422 | `TotalBelowPaidAmount` | edição deixaria o total abaixo do já pago (ADR-0007) |
| 422 | `PaymentMethodDisabled` | forma de pagamento desabilitada no estabelecimento |
| 422 | `ProductUnavailable` | produto inativo, indisponível ou fora do cardápio do dia (o `detail` diz qual: "Costela não está disponível no cardápio de hoje.") |
| 422 | `KiloPriceNotConfigured` | pesagem sem preço do quilo configurado |
| 422 | `JustificationRequired` | fechamento com diferença ≠ 0 sem justificativa |
| 423 | `PinLocked` | PIN bloqueado (extensão `lockedUntil`) |
| 426 | `AppVersionUnsupported` | versão do app abaixo da mínima (extensões `minVersion`, `storeUrl`; em `WEB`, `storeUrl` é sempre `null`) |
| 428 | `IdempotencyKeyRequired` / `IfMatchRequired` | header obrigatório ausente |
| 429 | `TooManyRequests` | limite dos endpoints públicos (ADR-0041, ADR-0045); header `Retry-After` |
| 404 | `StoreNotFound` | `{slug}` da página inexistente (§6.19). Slug antigo não dá `404`: redireciona |
| 409 | `StoreUnavailable` | envio (ou cotação) com a página fora de `OPEN`. Extensão `storeStatus`: `CLOSED`, `PAUSED` ou `UNAVAILABLE`, nunca o motivo (ADR-0045 item 16) |
| 409 | `CartChanged` | envio pela página com item indisponível ou fora do cardápio do dia, ou com `expectedTotal` diferente do total recalculado. Extensão `cart` com o carrinho recalculado (ADR-0044 item 21, §6.19) |
| 422 | `DeliveryUnavailable` | entrega pela página indisponível agora: pausa de entrega, requisitos da entrega por distância ausentes (`pageDelivery.missing`, §6.18) ou provedor de mapas fora do ar, lento ou sem cota (`detail`: "Não conseguimos calcular a entrega agora. Você pode retirar no local.", ADR-0048 item 13). A página oferece retirada |
| 422 | `OutOfDeliveryArea` | cotação (ou recálculo no envio) com o endereço além do raio máximo, ou sem rota de carro até ele (ADR-0048 item 7). Extensões `distanceKm` e `maxDistanceKm` quando é o raio |
| 409 | `DeliveryQuoteChanged` | envio pela página com cotação vencida, de outro endereço ou de outra configuração, e o recálculo deu outra taxa (ADR-0048 item 10). Extensão `quote` com a cotação nova (já gravada); o cliente confirma e reenvia com ela |
| 403 | `CaptchaFailed` | envio pela página sem o token do Turnstile ou com token inválido, vencido ou repetido (ADR-0045 item 12) |
| 429 | `TooManyPendingOrders` | o telefone já tem 2 pedidos aguardando aceite no restaurante, ou o IP já tem 3 (ADR-0045 item 13) |
| 410 | `TrackingExpired` | acompanhamento com mais de 7 dias (ADR-0047 item 4) |
| 409 | `PushTokenInUse` | `PUT /devices/push-token` com um token já registrado por **outra instalação** em outro estabelecimento (05 §5.17 0007, ADR-0049 item 4). O registro do outro aparelho não é tocado. O app não mostra erro: tenta de novo na próxima abertura (o registro antigo some no logout dele ou em 30 dias sem uso) |
| 409 | `UsedInActiveCombos` | inativar prato, tamanho de prato, tipo ou tamanho de bebida do cardápio fixo usado por combo ativo (ADR-0056 item 26). Extensão `combos` (`[{ "id", "name" }]`): o estabelecimento ajusta ou inativa os combos antes (§6.5 "Cardápio fixo") |
| 403 | `ModuleNotEnabled` | operação de um módulo que o estabelecimento não tem (ADR-0054 item 14; mapa em §6.3 "Comportamentos, rótulos e módulos"). Extensão `module` (`MARMITA`, `PF`, `KILO`, `DAILY_MENU`…). Vem depois do `401`/`403 PermissionDenied` e **antes** de qualquer outro erro da rota (validação do corpo, `If-Match`, `Idempotency-Key`, assinatura, expediente). Leitura de cadastro de módulo ausente não dá erro: devolve lista vazia |

---

## 6.2 Permissões e perfis (ADR-0028)

Cada endpoint indica a permissão exigida. Matriz em código (`Permissions.cs` / `RolePermissions.cs`):

| Permissão | OWNER | MANAGER | ATTENDANT | KITCHEN | CASHIER |
|---|:-:|:-:|:-:|:-:|:-:|
| `orders.view` | ✓ | ✓ | ✓ | ✓ | ✓ |
| `orders.create`, `orders.edit` | ✓ | ✓ | ✓ | | |
| `orders.cancel` | ✓ | ✓ | | | |
| `orders.delivery-fee.edit` | ✓ | ✓ | | | |
| `orders.accept` (aceitar e recusar pedido do site, ADR-0046) | ✓ | ✓ | ✓ | | ✓ |
| `orders.fulfillment.update` (saiu para entrega, entregue, retirado, ADR-0046) | ✓ | ✓ | ✓ | ✓ | ✓ |
| `production.view`, `production.update` | ✓ | ✓ | | ✓ | |
| `payments.register` | ✓ | ✓ | | | ✓ |
| `payments.reverse` | ✓ | ✓ | | | |
| `cash.open`, `cash.movement`, `closing.view`, `closing.finalize` | ✓ | ✓ | | | ✓ |
| `closing.adjust` | ✓ | ✓ | | | |
| `menu.manage`, `catalog.manage` | ✓ | ✓ | | | |
| `catalog.availability` (pausar e reativar itens do cardápio fixo; EP-003 Q-306, ADR-0028) | ✓ | ✓ | ✓ | ✓ | |
| `customers.manage` | ✓ | ✓ | ✓ | | ✓ |
| `employees.manage` | ✓ | | | | |
| `devices.manage`, `settings.manage`, `audit.view` | ✓ | ✓ | | | |
| `storefront.manage` (página de pedidos: slug, perfil, pausa, endereço, entrega; ADR-0046 item 22) | ✓ | ✓ | | | |
| `subscription.view` | ✓ | | | | |

Leitura de produtos, tamanhos, cardápios e clientes exige só `orders.view`.

As permissões dizem **quem** pode; os **módulos** do estabelecimento (ADR-0054, §6.3) dizem **o que** existe nele. Uma rota de módulo ausente responde `403 ModuleNotEnabled` mesmo para o `OWNER`. A fatia 2 do EP-003 acrescenta `catalog.availability` (pausa do cardápio fixo, separada da edição com `catalog.manage`). Não há permissão nova na fatia 1 do EP-003: os rótulos são editados com `settings.manage` (`PATCH /tenant`), e o acréscimo de comportamento é rota de plataforma (§6.15).

`GET /roles` (autenticado, qualquer perfil) → `200`:

```json
{
  "items": [
    { "role": "OWNER", "name": "Proprietário", "permissions": ["orders.view", "orders.create", "orders.edit", "orders.cancel", "orders.delivery-fee.edit", "orders.accept", "orders.fulfillment.update", "production.view", "production.update", "payments.register", "payments.reverse", "cash.open", "cash.movement", "closing.view", "closing.finalize", "closing.adjust", "menu.manage", "catalog.manage", "catalog.availability", "customers.manage", "employees.manage", "devices.manage", "settings.manage", "storefront.manage", "audit.view", "subscription.view"] },
    { "role": "KITCHEN", "name": "Cozinha", "permissions": ["orders.view", "production.view", "production.update", "orders.fulfillment.update", "catalog.availability"] }
  ]
}
```

---

## 6.3 Autenticação e sessão (ADR-0018, ADR-0027)

### Objetos comuns

`Tokens`:

```json
{
  "tokenType": "Bearer",
  "accessToken": "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIwMTkyZDZhMC0uLi4ifQ.sig",
  "accessTokenExpiresAt": "2026-10-27T15:19:05Z",
  "refreshToken": "rt_7Qm2xN0pLw8vB4kZ1sYdF6hJ3cT9eR5u",
  "refreshTokenExpiresAt": "2026-11-26T15:04:05Z"
}
```

Com `X-App-Platform: WEB`, `refreshToken` vem `null` e o refresh vai no cookie `__Secure-almoseis-rt` (ver "App no navegador", no fim desta seção). Os demais campos não mudam.

`Me` (também é a resposta de `GET /auth/me`):

```json
{
  "user": { "id": "0192d6a0-0000-7000-8000-000000000011", "fullName": "Ana Souza", "email": "ana@example.com", "phoneNumber": "+5534999990001" },
  "tenant": {
    "id": "0192d6a0-0000-7000-8000-000000000001",
    "name": "Marmitaria da Ana",
    "timeZone": "America/Sao_Paulo",
    "behaviors": ["RESTAURANT"],
    "labels": ["MARMITARIA"],
    "modules": ["MARMITA", "PF", "KILO", "DAILY_MENU"]
  },
  "employee": { "id": "0192d6a0-0000-7000-8000-000000000301", "displayName": "Ana", "roles": ["OWNER"], "hasPin": true },
  "permissions": ["orders.view", "orders.create", "orders.edit", "orders.cancel", "orders.delivery-fee.edit", "orders.accept", "orders.fulfillment.update", "production.view", "production.update", "payments.register", "payments.reverse", "cash.open", "cash.movement", "closing.view", "closing.finalize", "closing.adjust", "menu.manage", "catalog.manage", "catalog.availability", "customers.manage", "employees.manage", "devices.manage", "settings.manage", "audit.view", "subscription.view"],
  "memberships": [
    { "tenantId": "0192d6a0-0000-7000-8000-000000000001", "tenantName": "Marmitaria da Ana", "employeeId": "0192d6a0-0000-7000-8000-000000000301", "roles": ["OWNER"] }
  ],
  "subscription": { "status": "TRIALING", "trialEndsAt": "2026-11-03T15:04:05Z", "daysLeft": 7, "canWrite": true },
  "sharedDevice": null,
  "serverTime": "2026-10-27T15:04:05Z"
}
```

Para quem não tem `subscription.view`, os campos de `subscription` vêm `null`, exceto `canWrite`, que todos recebem para saber se o app está em modo somente leitura: `{ "status": null, "trialEndsAt": null, "daysLeft": null, "canWrite": true }`.

#### Comportamentos, rótulos e módulos (EP-003, ADR-0054, ADR-0055)

`tenant.behaviors`, `tenant.labels` e `tenant.modules` vêm em todo `Me` (autocadastro, login, login por PIN, troca de tenant, aceite de convite e `GET /auth/me`), para qualquer perfil. As três listas vêm sempre na ordem das tabelas abaixo.

- **`behaviors`**: o conjunto de comportamentos habilitados (`RESTAURANT`, `SNACK_BAR`). Pelo menos um. Escolhido no cadastro (exatamente um) e fixo depois; só o administrador da plataforma acrescenta (§6.15). O app nunca oferece troca nem contratação, nem mostra preço (ADR-0017).
- **`modules`**: o que o estabelecimento pode fazer, derivado dos comportamentos pelo mapa fixo abaixo (união dos módulos de cada comportamento). **O app monta as abas e o menu lateral só por `modules` e pelas permissões** (ADR-0055), nunca por `behaviors` ou `labels`. O servidor impõe os mesmos módulos (`403 ModuleNotEnabled`).
- **`labels`**: rótulos só informativos (perfil, app e, no futuro, marketplace). Não mudam nenhuma aba nem regra.

| Módulo | O que libera | `RESTAURANT` | `SNACK_BAR` |
|---|---|:-:|:-:|
| `MARMITA` | pedidos `MARMITA`, tamanhos de marmita (`/marmita-sizes`), entrega da aba Marmitas | ✓ | |
| `PF` | pedidos `PF`, cardápio de PF | ✓ | |
| `KILO` | pedidos `KILO`, preço do quilo (`kiloPricePerKg` de `/tenant/settings`) | ✓ | |
| `DAILY_MENU` | cardápio do dia e modelo semanal (`/menus`), clonagem na abertura do expediente | ✓ | |
| `A_LA_CARTE` | comandas à la carte no salão (fatia 3) | | ✓ |
| `DELIVERY` | aba Delivery (fatia 4) | | ✓ |
| `FIXED_MENU` | cardápio fixo (fatia 2) | | ✓ |

Rótulos (lista fixa, sem texto livre; o texto em pt-BR fica no app, **não há rota de leitura da lista**, ADR-0054 item 8):

| Comportamento | Código | Texto |
|---|---|---|
| `RESTAURANT` | `RESTAURANT` | Restaurante |
| `RESTAURANT` | `MARMITARIA` | Marmitaria |
| `RESTAURANT` | `SELF_SERVICE` | Self-service/quilo |
| `SNACK_BAR` | `BURGER_JOINT` | Hamburgueria |
| `SNACK_BAR` | `PASTEL_SHOP` | Pastelaria |
| `SNACK_BAR` | `ACAI_SHOP` | Açaiteria |
| `SNACK_BAR` | `PIZZERIA` | Pizzaria |
| `SNACK_BAR` | `SNACK_BAR` | Lanchonete |
| `SNACK_BAR` | `ICE_CREAM_SHOP` | Sorveteria |
| `SNACK_BAR` | `CAFE` | Cafeteria |
| `SNACK_BAR` | `BAKERY` | Padaria |
| `SNACK_BAR` | `SKEWER_BAR` | Espetaria |
| `SNACK_BAR` | `SUSHI` | Sushi/temaki |
| `SNACK_BAR` | `SWEETS_SHOP` | Doceria |
| `SNACK_BAR` | `OTHER` | Outro |

Uma lanchonete (hamburgueria e açaiteria):

```json
"tenant": {
  "id": "0192d6a0-0000-7000-8000-000000000777",
  "name": "Lanches do Beto",
  "timeZone": "America/Sao_Paulo",
  "behaviors": ["SNACK_BAR"],
  "labels": ["BURGER_JOINT", "ACAI_SHOP"],
  "modules": ["A_LA_CARTE", "DELIVERY", "FIXED_MENU"]
}
```

Com os dois comportamentos (só depois do acréscimo pelo admin): `"behaviors": ["RESTAURANT", "SNACK_BAR"]`, `"modules": ["MARMITA", "PF", "KILO", "DAILY_MENU", "A_LA_CARTE", "DELIVERY", "FIXED_MENU"]`.

Quando os módulos mudam (acréscimo pelo admin), o app recebe `TenantModulesChanged` (§6.14) e a entrada `TENANT` no feed (§6.12), recarrega o `GET /auth/me` e remonta as abas. Até recarregar, o servidor já impõe os módulos novos.

Tenants que existiam antes do EP-003 são `RESTAURANT` com o rótulo `RESTAURANT` (migração 0008, Q-303). Um tenant sem nenhum comportamento gravado (criado pela API anterior durante o deploy) é tratado e respondido como esse mesmo caso.

### `POST /auth/signup` — público

Cria o usuário (ou usa um existente), o estabelecimento, a assinatura `TRIALING` e o vínculo `OWNER` (ADR-0018).

```json
{
  "newAccount": {
    "fullName": "Ana Souza",
    "email": "ana@example.com",
    "phoneNumber": "+5534999990001",
    "password": "S3nha-forte!"
  },
  "existingAccount": null,
  "tenant": {
    "name": "Marmitaria da Ana",
    "documentType": "CNPJ",
    "documentNumber": "11222333000181",
    "city": "Uberlândia",
    "state": "MG",
    "timeZone": "America/Sao_Paulo",
    "behavior": "RESTAURANT",
    "labels": ["MARMITARIA"]
  },
  "acceptedTermsVersion": "2026-10",
  "installationId": "inst_8f3a2c1e9b7d4f60"
}
```

Exatamente um entre `newAccount` e `existingAccount` (`{ "login": "ana@example.com", "password": "..." }`). → `201`, `Location: /api/v1/tenant`:

```json
{ "tokens": { "...": "Tokens" }, "me": { "...": "Me" } }
```

- `tenant.behavior` (ADR-0054 item 3): **um** comportamento, `RESTAURANT` ou `SNACK_BAR`, obrigatório e sem padrão. A tela de cadastro não pré-marca nenhum.
- `tenant.labels` (itens 8 e 9): um ou mais rótulos da lista fixa (§6.3 "Comportamentos, rótulos e módulos"), **todos do comportamento escolhido**, inclusive no restaurante (Q-302). Repetidos contam uma vez.
- O `me` da resposta já traz `behaviors`, `labels` e `modules`.

Erros: `409 EmailAlreadyRegistered`, `409 PhoneAlreadyRegistered`, `409 TrialAlreadyClaimed`, `401 InvalidCredentials` (existingAccount), `422 ValidationFailed` (inclusive `errors["tenant.behavior"]` ausente e `errors["tenant.labels"]` vazio ou com rótulo de outro comportamento), `400 MalformedRequest` (código de comportamento ou de rótulo fora da lista).

```json
{
  "type": "urn:almoseis:error:validation-failed",
  "title": "Dados inválidos.",
  "status": 422,
  "errorCode": "ValidationFailed",
  "traceId": "00-2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e-2b3c4d5e6f7a8b9c-01",
  "errors": { "tenant.labels": ["Marque só rótulos do tipo de estabelecimento escolhido."] }
}
```

O app anterior ao EP-003 não envia `behavior` e recebe `422`: a versão mínima do app sobe antes do primeiro cadastro de lanchonete (Q-323, ADR-0035).

### `POST /auth/login` — público

```json
{
  "login": "+5534999990001",
  "password": "S3nha-forte!",
  "installationId": "inst_8f3a2c1e9b7d4f60",
  "preferredTenantId": "0192d6a0-0000-7000-8000-000000000001"
}
```

`login` é o e-mail ou o telefone E.164. Os tokens são emitidos para `preferredTenantId`, se for um vínculo ativo. Senão, para o vínculo ativo mais antigo. → `200` `{ "tokens": {...}, "me": {...} }`. Erros: `401 InvalidCredentials` (inclusive com a conta bloqueada por tentativas, quando traz a extensão `lockedUntil`) e `403 NoActiveMembership`.

### `POST /auth/refresh` — público

```json
{ "refreshToken": "rt_7Qm2xN0pLw8vB4kZ1sYdF6hJ3cT9eR5u", "installationId": "inst_8f3a2c1e9b7d4f60" }
```

→ `200` `Tokens`, com o refresh **rotacionado** (ADR-0027). Reusar um refresh já trocado → `401 InvalidRefreshToken` e revogação da família inteira. Na web, o refresh vem do cookie e o corpo traz só `installationId` ("App no navegador").

### `POST /auth/logout`

```json
{ "refreshToken": "rt_7Qm2xN0pLw8vB4kZ1sYdF6hJ3cT9eR5u" }
```

→ `204`. Na web, o corpo é `{}` (ou vazio), o refresh vem do cookie e a resposta apaga o cookie ("App no navegador").

### `POST /auth/switch-tenant`

```json
{ "tenantId": "0192d6a0-0000-7000-8000-000000000777" }
```

→ `200` `{ "tokens": {...}, "me": {...} }`. Um tenant em que o usuário não tem vínculo ativo responde `404 NotFound`.

- A sessão anterior **desta instalação** sai: os refresh tokens do usuário com o `installationId` do access token são revogados antes de emitir os novos (o app guarda uma sessão por instalação). Outras instalações do mesmo usuário não mudam. O access token antigo vale até expirar (15 min). No `404`, nada é revogado.

### `GET /auth/me` → `200` `Me`

### `PUT /auth/me/pin` — define ou troca o próprio PIN

```json
{ "pin": "4821", "currentPassword": "S3nha-forte!" }
```

→ `204`. PIN de 4 a 6 dígitos, e sequências triviais (`1234`, `0000`) são recusadas com `422`.

### `PUT /auth/me/password` — troca a própria senha

```json
{ "currentPassword": "S3nha-forte!", "newPassword": "Outra-s3nha!" }
```

→ `204`. Qualquer perfil, inclusive com a assinatura suspensa (não é escrita do estabelecimento).

- Troca o security stamp do usuário: links de "Esqueci a senha" ainda não usados deixam de valer.
- Revoga todos os refresh tokens do usuário **exceto os da instalação que fez a troca** (`installation_id` do access token). Os outros aparelhos caem no próximo refresh (em até 15 min). Quem trocou continua logado.
- Erros: `422 ValidationFailed` com `errors.currentPassword` quando a senha atual está errada (não é `401`, para o app não tratar como sessão vencida; mesma regra do `PUT /auth/me/pin`); `errors.newPassword` quando a nova tem menos de 8 caracteres ou é igual à atual.
- Auditoria `PasswordChanged` (`USER`). Fora do limite `auth` do ADR-0041, como o `PUT /auth/me/pin`: exige sessão válida.

### `POST /auth/admin/login` — público (ADR-0043)

Só para administradores de plataforma (`users.is_platform_admin`).

```json
{ "login": "ops@almoseis.app", "password": "S3nha-forte!" }
```

→ `200`:

```json
{
  "accessToken": "eyJhbGciOi...",
  "expiresAt": "2026-10-27T15:19:05Z",
  "user": { "id": "0192d6a0-0000-7000-8000-000000000019", "fullName": "Operação AlmoSeis", "email": "ops@almoseis.app" }
}
```

Sem refresh token: a sessão dura o access token (15 min). Usuário que não é administrador, senha errada ou conta bloqueada → `401 InvalidCredentials` (mesma resposta, com `lockedUntil` quando bloqueada). O token não tem `tenant_id`: rotas de estabelecimento respondem `401`; e um token de estabelecimento recebe `403` em `/admin/*`.

### `POST /auth/password/reset` — público

Consome um código de redefinição emitido por quem tem `employees.manage` (§6.4).

```json
{ "code": "K7P3QZ", "login": "+5534999990002", "newPassword": "Outra-s3nha!" }
```

→ `204`. Revoga todos os refresh tokens do usuário. Erros: `409 InvitationNotPending`, `422 ValidationFailed`.

### `POST /auth/password/forgot` — público (só para quem tem e-mail)

```json
{ "email": "ana@example.com" }
```

→ `202`, **sempre**, mesmo que o e-mail não exista. Envia um link de redefinição de uso único, válido por 30 min, via e-mail transacional (ADR-0040). `POST /auth/password/reset` aceita `{ "emailToken": "...", "newPassword": "..." }` no lugar de `code` + `login`.

### Dispositivo compartilhado (ADR-0027)

#### `POST /devices/shared` — `devices.manage`

Registra **a instalação atual** como aparelho compartilhado.

```json
{ "installationId": "inst_c0z1nh4t4bl3t001", "name": "Tablet cozinha", "platform": "ANDROID" }
```

→ `201`:

```json
{
  "id": "0192d6a0-0000-7000-8000-000000000c01",
  "tenantId": "0192d6a0-0000-7000-8000-000000000001",
  "installationId": "inst_c0z1nh4t4bl3t001",
  "name": "Tablet cozinha",
  "platform": "ANDROID",
  "deviceSecret": "ds_Hk29sLq0Pz7mVx3Nc8Bt1Rw6Yf4Ju5Ea",
  "registeredAt": "2026-10-27T10:00:00Z",
  "registeredBy": { "id": "0192d6a0-0000-7000-8000-000000000301", "displayName": "Ana" }
}
```

`deviceSecret` aparece **só nesta resposta** e é guardado no SecureStore do aparelho. O servidor guarda só o hash. `platform`: `ANDROID`, `IOS` ou `WEB`. `WEB` exige `X-App-Platform: WEB` (e vice-versa; senão `422` com `errors.platform`); nesse caso `deviceSecret` vem `null` e o segredo vai no cookie `__Secure-almoseis-ds` ("App no navegador").

#### `GET /devices/shared` — `devices.manage`

→ `200` `{ "items": [ { "id": "...c01", "name": "Tablet cozinha", "platform": "ANDROID", "registeredAt": "2026-10-27T10:00:00Z", "registeredBy": {...}, "revokedAt": null } ] }`

#### `POST /devices/shared/{id}/revoke` — `devices.manage`

→ `204`. Revoga também todos os refresh tokens emitidos por PIN nesse aparelho.

#### `GET /devices/shared/{installationId}/employees` — público + `X-Device-Secret` + `X-Tenant-Id`

Rota do mobile. Na web, a mesma resposta vem de `GET /auth/shared-device/employees` ("App no navegador"), porque o cookie do segredo só vale sob `/api/v1/auth`.

Lista quem pode entrar por PIN (funcionários ativos com PIN definido):

```json
{
  "device": { "id": "0192d6a0-0000-7000-8000-000000000c01", "name": "Tablet cozinha" },
  "tenant": { "id": "0192d6a0-0000-7000-8000-000000000001", "name": "Marmitaria da Ana" },
  "employees": [
    { "id": "0192d6a0-0000-7000-8000-000000000304", "displayName": "Diego", "roles": ["KITCHEN"] },
    { "id": "0192d6a0-0000-7000-8000-000000000302", "displayName": "Bruno", "roles": ["ATTENDANT"] }
  ]
}
```

#### `POST /auth/pin-login` — público + `X-Device-Secret`

```json
{
  "tenantId": "0192d6a0-0000-7000-8000-000000000001",
  "installationId": "inst_c0z1nh4t4bl3t001",
  "employeeId": "0192d6a0-0000-7000-8000-000000000304",
  "pin": "4821"
}
```

→ `200` `{ "tokens": {...}, "me": {...} }` (com `me.sharedDevice` preenchido). Erros: `401 InvalidDeviceSecret`, `401 InvalidCredentials` (extensão `attemptsLeft`), `423 PinLocked` (extensão `lockedUntil`). Na web, o segredo vem **só** do cookie, e `X-Device-Secret` é ignorado ("App no navegador").

### Push dos pedidos do site (ADR-0049, EP-002 F-209)

O app registra o token do Expo da **instalação atual** no estabelecimento da sessão. Quem recebe é decidido **no envio**, pelas permissões atuais do funcionário do registro (`orders.accept`), não no registro. Só o pedido do site aguardando aceite e o lembrete aos 3 min geram push; pedidos do app e alterações continuam só em primeiro plano (ADR-0036).

#### `PUT /devices/push-token` — autenticado (qualquer perfil)

```json
{
  "installationId": "inst_8f3a2c1e9b7d4f60",
  "expoPushToken": "ExponentPushToken[xxxxxxxxxxxxxxxxxxxxxx]",
  "platform": "ANDROID",
  "webOrdersEnabled": true
}
```

→ `200`:

```json
{
  "installationId": "inst_8f3a2c1e9b7d4f60",
  "platform": "ANDROID",
  "webOrdersEnabled": true,
  "receivesWebOrders": true,
  "registeredAt": "2026-11-20T14:58:00Z",
  "updatedAt": "2026-11-20T14:58:00Z"
}
```

- Registra ou atualiza (idempotente; sem `Idempotency-Key` nem `If-Match`: só o próprio aparelho escreve). O app chama ao entrar, a cada abertura e quando o Expo troca o token. `updatedAt` é o "uso": registro sem novo `PUT` há 30 dias é apagado.
- `installationId` precisa ser o do access token (`422` com `errors.installationId`, como em `POST /devices/shared`). `platform`: `ANDROID` ou `IOS`; `WEB` está reservado para o Web Push (Q-26) e hoje responde `422` com `errors.platform`. `expoPushToken` fora do formato `ExponentPushToken[…]` (ou `ExpoPushToken[…]`) → `422` com `errors.expoPushToken`.
- `webOrdersEnabled`: a opção "Avisar pedidos do site com o app fechado" (item 6), ligada no primeiro registro. `null` ou ausente mantém a atual.
- `receivesWebOrders` = a opção está ligada **e** o funcionário da sessão tem `orders.accept` agora. O token nunca volta na resposta.
- Login por PIN (aparelho compartilhado): o registro fica ligado ao aparelho, e vale o último funcionário que entrou por PIN, mesmo depois do tempo ocioso.
- Um token tem um registro só. O mesmo token registrado **por esta instalação** em outro estabelecimento (o aparelho trocou sem logout) sai de lá; registrado por outra instalação deste estabelecimento (app reinstalado), também. Registrado por **outra instalação em outro estabelecimento** → `409 PushTokenInUse`, e o registro de lá continua: quem só conhece a string do token não tira o aviso de outro aparelho. Push nunca vai para um aparelho registrado em outro tenant.
- Fora do bloqueio da assinatura (`402`), como `/auth/*`: é configuração do aparelho.

#### `DELETE /devices/push-token` — autenticado

→ `204`, sempre (idempotente). Apaga o registro desta instalação (a do access token) no estabelecimento da sessão.

**O servidor também apaga o registro** (na mesma transação): no `POST /auth/logout` (exceto do aparelho compartilhado, cujo logout é também o do tempo ocioso), no `POST /auth/switch-tenant` (o do estabelecimento anterior), na desativação e na troca de perfis do funcionário (`AccessRevoked`), na revogação do aparelho compartilhado e quando a Expo responde `DeviceNotRegistered`.

#### A notificação (enviada pela API de push da Expo)

O que o servidor envia para cada aparelho, em lotes de até 100, pelo outbox (pelo menos uma vez, sem repetir no mesmo aparelho; 3 tentativas com 30 s de espera):

```json
{
  "to": "ExponentPushToken[xxxxxxxxxxxxxxxxxxxxxx]",
  "title": "Novo pedido do site",
  "body": "2 marmitas · Entrega · aceite em até 10 min",
  "data": {
    "tenantId": "0192d6a0-0000-7000-8000-000000000001",
    "orderId": "0192d6a0-0000-7000-8000-000000000504",
    "kind": "WEB_ORDER_AWAITING_ACCEPTANCE"
  },
  "sound": "web_order.wav",
  "channelId": "web-orders",
  "priority": "high",
  "ttl": 600
}
```

| `kind` | Quando | `title` | `body` |
|---|---|---|---|
| `WEB_ORDER_AWAITING_ACCEPTANCE` | pedido do site enviado (`OrderAwaitingAcceptance`) | `Novo pedido do site` | `{n} marmita(s) · Entrega\|Retirada · aceite em até {m} min` |
| `WEB_ORDER_REMINDER` | 3 min depois, se ainda aguarda aceite (ADR-0046 item 15) | `Pedido do site esperando` | `Faltam {m} min para expirar` |

- `{m}`: minutos inteiros até o prazo de aceite, arredondados para cima, no momento do envio. `ttl`: segundos até o prazo (depois disso a Expo descarta).
- Quando o usuário tem vínculo com mais de um estabelecimento, o título termina com ` · {nome do estabelecimento}` (ex.: `Novo pedido do site · Marmitaria da Ana`). No aparelho compartilhado, não.
- **Sem nome, telefone, endereço nem valor** (ADR-0049 item 3, ADR-0052 item 7). `data` leva só `tenantId`, `orderId` e `kind`: tocar abre o pedido, e o app busca tudo pela API. Notificação de outro `tenantId` que não o da sessão é ignorada.
- Pedido já aceito, recusado, expirado ou desistido no momento do envio não gera push (nem o lembrete).
- `channelId` e `sound` vêm de `Push:ChannelId` e `Push:Sound`: o app cria o canal Android `web-orders` (importância alta) com esse som, e o mesmo arquivo vai no pacote iOS. Com o app em primeiro plano, o app suprime a notificação (já tratou pelo SignalR, item 9).

#### Configuração (API)

| Chave | Uso | Desenvolvimento / testes |
|---|---|---|
| `Push:Provider` | `Expo` (`appsettings.json`), `Fake` (sem rede; recusado em Production) ou `None` (não envia). Sem a chave: `Fake` | `Fake` |
| `Push:Expo:AccessToken` | "Enhanced push security" da Expo (`Authorization: Bearer`). Opcional; só Key Vault / `user-secrets`, nunca no repositório | vazio |
| `Push:Expo:SendUrl`, `ReceiptsUrl`, `TimeoutSeconds` (5) | `https://exp.host/--/api/v2/push/send` e `/getReceipts` | — |
| `Push:ChannelId` (`web-orders`), `Push:Sound` (`web_order.wav`) | canal Android e som (item 7) | — |
| `Push:MaxAttempts` (3), `RetryDelaySeconds` (30) | tentativas da mensagem do outbox (item 8) | — |
| `Push:ReceiptDelayMinutes` (15), `MaintenanceJobEnabled`, `MaintenanceIntervalMinutes` (5), `RegistrationRetentionDays` (30), `DeliveryRetentionDays` (7) | recibos, registros sem uso e histórico de envios | testes: job desligado, chamado por `RunOnceAsync` |
| `WebOrders:ReminderMinutes` (3) | lembrete depois do envio; `0` desliga | — |

### App no navegador — `X-App-Platform: WEB` (ADR-0053)

O app do restaurante no navegador do desktop usa as mesmas rotas, com três diferenças: o refresh token e o `deviceSecret` ficam em cookies que o JavaScript não lê, as rotas que leem esses cookies conferem o `Origin`, e a API responde CORS para as origens do ambiente. **`ANDROID` e `IOS` não mudam:** refresh e segredo no corpo e nos headers, como acima, sem `Set-Cookie`; a API ignora esses cookies quando a plataforma não é `WEB`.

**Cookies.** Só do host da API (sem `Domain`), `HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth`:

| Cookie | Valor | `Max-Age` | Emitido por (só com `X-App-Platform: WEB`) | Lido por | Apagado por (`Max-Age=0`) |
|---|---|---|---|---|---|
| `__Secure-almoseis-rt` | o refresh token (`rt_…`) | segundos até `refreshTokenExpiresAt` (30 dias, ADR-0027) | `signup`, `login`, `refresh`, `switch-tenant`, `pin-login`, `POST /invitations/{code}/accept` | `refresh`, `logout` | `logout`; `refresh` que falha com `401 InvalidRefreshToken` (inclusive no reuso, que revoga a família) |
| `__Secure-almoseis-ds` | o `deviceSecret` (`ds_…`) | `34560000` (400 dias, o limite do Chrome) | `POST /devices/shared`; reemitido com o mesmo valor a cada `pin-login` bem-sucedido pelo cookie | `GET /auth/shared-device/employees`, `pin-login` | `DELETE /auth/shared-device`; `401 InvalidDeviceSecret` numa das duas rotas, quando o segredo veio do cookie |

Nas respostas com `Tokens`, o campo `refreshToken` vem `null` e os demais campos não mudam. No `POST /devices/shared`, `deviceSecret` vem `null`.

```text
POST /api/v1/auth/login HTTP/1.1
Host: api.almoseis.app
Origin: https://app.almoseis.app
X-App-Version: 1.4.0
X-App-Platform: WEB
Content-Type: application/json

{ "login": "+5534999990001", "password": "S3nha-forte!", "installationId": "3f0c9a6e-1b2d-4c5e-8f7a-9b0c1d2e3f4a", "preferredTenantId": null }

HTTP/1.1 200 OK
Access-Control-Allow-Origin: https://app.almoseis.app
Access-Control-Allow-Credentials: true
Access-Control-Expose-Headers: ETag,Location,Retry-After,Idempotent-Replayed
Vary: Origin
Set-Cookie: __Secure-almoseis-rt=rt_7Qm2xN0pLw8vB4kZ1sYdF6hJ3cT9eR5u; max-age=2592000; path=/api/v1/auth; secure; samesite=strict; httponly
Content-Type: application/json
```

```json
{
  "tokens": {
    "tokenType": "Bearer",
    "accessToken": "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIwMTkyZDZhMC0uLi4ifQ.sig",
    "accessTokenExpiresAt": "2026-10-27T15:19:05Z",
    "refreshToken": null,
    "refreshTokenExpiresAt": "2026-11-26T15:04:05Z"
  },
  "me": { "...": "Me" }
}
```

O `installationId` da web é um UUID gerado pelo navegador e guardado em `localStorage` (não é segredo, ADR-0053 §3.7). O app chama a API com `credentials: 'include'`.

**Anti-CSRF.** Nas rotas que leem cookie (`POST /auth/refresh`, `POST /auth/logout`, `POST /auth/pin-login`, `GET /auth/shared-device/employees`, `DELETE /auth/shared-device`), uma requisição `X-App-Platform: WEB` sem `Origin`, ou com `Origin` fora de `Cors:RestaurantAppOrigins`, recebe `403 OriginNotAllowed` antes de qualquer leitura do cookie (a conferência acontece antes do corpo ser validado e, de novo, no ponto único em que a API lê cookie). Sem `X-App-Platform: WEB`, o cookie não é lido (a requisição segue o fluxo do mobile e precisa do refresh no corpo ou do `X-Device-Secret`), então o header é, na prática, obrigatório para usar o cookie. Como é um header customizado, ele força o *preflight* do CORS, que falha para qualquer origem fora da lista.

```json
{
  "type": "urn:almoseis:error:origin-not-allowed",
  "title": "Origem não permitida.",
  "status": 403,
  "instance": "/api/v1/auth/refresh",
  "errorCode": "OriginNotAllowed",
  "traceId": "00-2b7c9d1e3f4a5b6c7d8e9f0a1b2c3d4e-2b7c9d1e3f4a5b6c-01"
}
```

#### `POST /auth/refresh` na web

```text
POST /api/v1/auth/refresh HTTP/1.1
Origin: https://app.almoseis.app
X-App-Version: 1.4.0
X-App-Platform: WEB
Cookie: __Secure-almoseis-rt=rt_7Qm2xN0pLw8vB4kZ1sYdF6hJ3cT9eR5u
Content-Type: application/json

{ "installationId": "3f0c9a6e-1b2d-4c5e-8f7a-9b0c1d2e3f4a" }

HTTP/1.1 200 OK
Set-Cookie: __Secure-almoseis-rt=rt_Lw0Xk3Vb9QzP2nRt6Yc1Hs8Jd4Fm7Ga5; max-age=2592000; path=/api/v1/auth; secure; samesite=strict; httponly
```

→ `200` `Tokens` com `refreshToken: null` e o cookie **rotacionado**. A rotação, a ligação com o `installationId` e a revogação da família no reuso são as do ADR-0027.

- `refreshToken` no corpo vindo da web → `422 ValidationFailed` com `errors.refreshToken` ("Na web, o refresh token vem do cookie.");
- sem o cookie, ou com refresh inválido, vencido ou reutilizado → `401 InvalidRefreshToken` + `Set-Cookie: __Secure-almoseis-rt=; expires=Thu, 01 Jan 1970 00:00:00 GMT; max-age=0; path=/api/v1/auth; secure; samesite=strict; httponly`.

Duas abas que renovam juntas com o mesmo cookie contam como reuso: a segunda recebe `401` e a família é revogada. O app evita isso com o lock entre abas (ADR-0053 §3.6).

#### `POST /auth/logout` na web

Corpo `{}` ou vazio. → `204` + `Set-Cookie: __Secure-almoseis-rt=; expires=Thu, 01 Jan 1970 00:00:00 GMT; max-age=0; path=/api/v1/auth; secure; samesite=strict; httponly`. Revoga o refresh do cookie, se houver (sem cookie, só apaga). `refreshToken` no corpo vindo da web → `422`.

#### Aparelho compartilhado na web

1. `POST /devices/shared` (`devices.manage`) com `X-App-Platform: WEB` e `"platform": "WEB"` → `201` com `"deviceSecret": null` e `Set-Cookie: __Secure-almoseis-ds=ds_Hk29sLq0Pz7mVx3Nc8Bt1Rw6Yf4Ju5Ea; max-age=34560000; path=/api/v1/auth; secure; samesite=strict; httponly`.
2. `GET /auth/shared-device/employees?installationId={installationId}` — público + `X-Tenant-Id` + `X-App-Platform: WEB` + cookie `__Secure-almoseis-ds`. Mesma resposta, mesmos erros e mesmo limite (ADR-0041, 30/min por instalação) de `GET /devices/shared/{installationId}/employees`. O `installationId` vai na query (não é segredo); na web o segredo vem só do cookie (`X-Device-Secret` é ignorado, e o CORS nem o permite). Sem `X-App-Platform: WEB`, a rota aceita o `X-Device-Secret` como a do mobile. O `X-Tenant-Id` de outro estabelecimento responde `401 InvalidDeviceSecret`, sem listar ninguém. Sem `installationId` → `422` com `errors.installationId`.
3. `POST /auth/pin-login`: mesmo corpo. Na web, o segredo vem só do cookie (`X-Device-Secret` é ignorado). Sucesso pelo cookie reemite o cookie com o mesmo valor (renova os 400 dias).
4. `DELETE /auth/shared-device` — público + `X-App-Platform: WEB` → `204` + `Set-Cookie: __Secure-almoseis-ds=; expires=Thu, 01 Jan 1970 00:00:00 GMT; max-age=0; path=/api/v1/auth; secure; samesite=strict; httponly`. É o "Deixar de usar como compartilhado" do navegador: só apaga o cookie. A revogação no servidor continua sendo `POST /devices/shared/{id}/revoke`. Sem `X-App-Platform: WEB` não há cookie a apagar: `204` sem `Set-Cookie`.

`401 InvalidDeviceSecret` numa rota que leu o segredo do cookie (aparelho revogado ou registrado de novo) também apaga o cookie.

#### CORS

| Item | Valor |
|---|---|
| Origens | `Cors:RestaurantAppOrigins` (lista por ambiente, sem curinga; a barra final é ignorada). Development: `http://localhost:8082`; homolog: `https://app-homolog.almoseis.app`; produção: `https://app.almoseis.app`. Lista vazia ou ausente → nenhuma origem recebe CORS. Cada origem é `https://host[:porta]`; `http://` só para `localhost` e `127.0.0.1` (qualquer porta). `*`, caminho, query ou `http://` em outro host impedem a API de subir |
| Credenciais | `Access-Control-Allow-Credentials: true` |
| Métodos | `GET`, `POST`, `PUT`, `PATCH`, `DELETE` |
| Headers permitidos | `Authorization`, `Content-Type`, `Idempotency-Key`, `If-Match`, `X-App-Version`, `X-App-Platform`, `X-Tenant-Id`. Sem `X-Device-Secret` (na web o segredo vem só do cookie) e sem os headers do *negotiate* do SignalR (`X-Requested-With`, `X-SignalR-User-Agent`): o app web conecta direto por WebSocket (`skipNegotiation`), que não tem *preflight* |
| Headers expostos | `ETag`, `Location`, `Retry-After`, `Idempotent-Replayed` |
| `Access-Control-Max-Age` | `600` |
| Rotas | todas (`/api/v1/*` e `/hubs/realtime`, inclusive o `negotiate`). Erros (`401`, `403`, `426`…) também levam os headers de CORS, para o app ler o `ProblemDetails` |

*Preflight* de origem permitida → `204` com os headers abaixo. Origem fora da lista → `204` **sem** `Access-Control-Allow-Origin`, e o navegador bloqueia a requisição.

```text
OPTIONS /api/v1/auth/refresh HTTP/1.1
Origin: https://app.almoseis.app
Access-Control-Request-Method: POST
Access-Control-Request-Headers: content-type,x-app-platform,x-app-version

HTTP/1.1 204 No Content
Access-Control-Allow-Origin: https://app.almoseis.app
Access-Control-Allow-Credentials: true
Access-Control-Allow-Methods: POST
Access-Control-Allow-Headers: content-type,x-app-platform,x-app-version
Access-Control-Max-Age: 600
Vary: Origin
```

A página pública do EP-002 (`/public/*`) tem **outra** política, sem credenciais e com outra lista (`Cors:PublicPageOrigins`, §6.19). As duas listas não se misturam: a origem do app não recebe CORS em `/public/*`, e a origem da página não recebe CORS fora de `/public/*`.

**Desenvolvimento:** o app web em `http://localhost:8082` e a API em `http://localhost:5080` são o mesmo *site* (a porta não conta), então o `SameSite=Strict` deixa o cookie seguir. O Chrome e o Edge aceitam cookies `Secure` e o prefixo `__Secure-` em `http://localhost`.

**Versão mínima:** `App:MinSupportedAppVersion:WEB` (ADR-0035). O `426 AppVersionUnsupported` da web vem com `storeUrl: null` (não há loja; o app recarrega a página, ADR-0053 §6).

---

## 6.4 Estabelecimento, assinatura e funcionários

### `GET /tenant` — `orders.view`

```json
{
  "id": "0192d6a0-0000-7000-8000-000000000001",
  "version": 3,
  "name": "Marmitaria da Ana",
  "documentType": "CNPJ",
  "documentNumber": "11222333000181",
  "city": "Uberlândia",
  "state": "MG",
  "timeZone": "America/Sao_Paulo",
  "behaviors": ["RESTAURANT"],
  "labels": ["MARMITARIA"],
  "createdAt": "2026-10-27T09:30:00Z"
}
```

`behaviors` e `labels` como no `Me` (§6.3). Os `modules` ficam só no `Me`.

### `PATCH /tenant` — `settings.manage` [M]

```json
{ "name": "Marmitaria e Restaurante da Ana", "city": "Uberlândia", "state": "MG", "timeZone": "America/Sao_Paulo", "labels": ["MARMITARIA", "SELF_SERVICE"] }
```

→ `200` com o tenant. `documentType`/`documentNumber` não são alteráveis pelo app. Campos ausentes não mudam.

- `labels` ("Dados do estabelecimento", ADR-0054 item 11) **troca o conjunto inteiro**: pelo menos um, todos de comportamentos habilitados (`behaviors`). Vazio, `null` ou rótulo de comportamento não habilitado → `422` em `errors.labels`; código fora da lista → `400 MalformedRequest`. O mesmo conjunto não muda a versão.
- O comportamento **não** é alterável pelo app (não existe `behaviors` no corpo; o campo é ignorado).
- Os rótulos são parte do estabelecimento: o `If-Match` é a `version` do tenant, e a troca sobe essa versão, grava a auditoria `TenantLabelsChanged` e a entrada `TENANT` no feed (§6.12). Rótulo não muda módulo nem aba.

### `GET /tenant/settings` — `orders.view`

```json
{
  "version": 5,
  "kiloPricePerKg": "59.90",
  "defaultDeliveryFee": "5.00",
  "enabledPaymentMethods": ["CASH", "PIX", "CARD"],
  "sharedDeviceIdleSeconds": 120
}
```

### `PATCH /tenant/settings` — `settings.manage` [M]

```json
{ "kiloPricePerKg": "62.90", "defaultDeliveryFee": "6.00", "enabledPaymentMethods": ["CASH", "PIX"], "sharedDeviceIdleSeconds": 180 }
```

Campos ausentes não mudam. → `200` com as configurações. Mudar o preço do quilo não afeta pesagens já registradas (RN-006.3). Mudar `kiloPricePerKg` exige o módulo `KILO` (`403 ModuleNotEnabled`); reenviar o valor atual não conta como mudança.

### `GET /subscription` — `subscription.view`

```json
{
  "status": "PAST_DUE",
  "trialEndsAt": "2026-11-03T15:04:05Z",
  "currentPeriodEndsAt": "2026-12-03T15:04:05Z",
  "pastDueSince": "2026-12-04T03:00:00Z",
  "graceEndsAt": "2026-12-09T03:00:00Z",
  "daysLeft": 2,
  "canWrite": true,
  "message": "Pagamento pendente. Verifique o link de pagamento enviado ao seu e-mail ou WhatsApp."
}
```

**Sem preço, plano ou link de compra** (ADR-0017). `message` por estado: teste (dias restantes), atraso (o texto acima), suspensa (só consulta), cancelada (exportação); `null` quando ativa.

### Funcionários

`Employee`:

```json
{
  "id": "0192d6a0-0000-7000-8000-000000000302",
  "version": 2,
  "userId": "0192d6a0-0000-7000-8000-000000000012",
  "displayName": "Bruno",
  "roles": ["ATTENDANT", "CASHIER"],
  "status": "ACTIVE",
  "hasPin": true,
  "contact": { "email": null, "phoneNumber": "+5534999990002" },
  "joinedAt": "2026-10-27T11:00:00Z",
  "deactivatedAt": null,
  "deactivatedBy": null
}
```

| Método e rota | Permissão | Corpo → resposta |
|---|---|---|
| `GET /employees?status=ACTIVE` | `employees.manage` | → `200 { "items": [Employee] }` |
| `GET /employees/{id}` | `employees.manage` | → `200 Employee` |
| `PATCH /employees/{id}` [M] | `employees.manage` | `{ "displayName": "Bruno S.", "roles": ["ATTENDANT"] }` → `200 Employee` |
| `POST /employees/{id}/deactivate` [M] | `employees.manage` | sem corpo → `200 Employee`; revoga o acesso em até 30 s (ADR-0027); `409 LastOwner` |
| `POST /employees/{id}/reactivate` [M] | `employees.manage` | sem corpo → `200 Employee` |
| `POST /employees/{id}/password-reset-code` | `employees.manage` | sem corpo → `201 Invitation` com `purpose = PASSWORD_RESET` |

Os contatos (`contact`) aparecem só para `employees.manage`.

### Convites (ADR-0018)

`Invitation`:

```json
{
  "id": "0192d6a0-0000-7000-8000-000000000d01",
  "purpose": "INVITE",
  "code": "K7P3QZ",
  "link": "https://almoseis.app/convite/K7P3QZ",
  "roles": ["ATTENDANT"],
  "targetEmployee": null,
  "status": "PENDING",
  "expiresAt": "2026-10-30T11:00:00Z",
  "createdAt": "2026-10-27T11:00:00Z",
  "createdBy": { "id": "0192d6a0-0000-7000-8000-000000000301", "displayName": "Ana" },
  "acceptedAt": null
}
```

| Método e rota | Permissão | Corpo → resposta |
|---|---|---|
| `POST /employees/invitations` | `employees.manage` | `{ "roles": ["ATTENDANT"] }` → `201 Invitation` |
| `GET /employees/invitations?status=PENDING` | `employees.manage` | → `200 { "items": [Invitation] }` |
| `POST /employees/invitations/{id}/revoke` | `employees.manage` | → `204`; `409 InvitationNotPending` |

#### `GET /invitations/{code}` — público

Para o convidado conferir antes de aceitar:

```json
{
  "code": "K7P3QZ",
  "purpose": "INVITE",
  "tenantName": "Marmitaria da Ana",
  "roles": ["ATTENDANT"],
  "invitedBy": "Ana",
  "expiresAt": "2026-10-30T11:00:00Z"
}
```

Convite inexistente, expirado ou já usado → `404 NotFound` (não diferencia, para não revelar códigos).

#### `POST /invitations/{code}/accept` — público

```json
{
  "newAccount": { "fullName": "Bruno Lima", "email": null, "phoneNumber": "+5534999990002", "password": "Br-s3nha-123" },
  "existingAccount": null,
  "displayName": "Bruno",
  "installationId": "inst_b7un0c3lul4r0001"
}
```

→ `201` `{ "tokens": {...}, "me": {...} }`, já no tenant do convite. Se o usuário já tinha vínculo **inativo** nesse tenant, o vínculo é reativado com os perfis do convite.

---

## 6.5 Catálogo (ADR-0020)

### Produtos

`Product`:

```json
{
  "id": "0192d6a0-0000-7000-8000-000000000101",
  "version": 1,
  "kind": "MAIN_DISH",
  "name": "Frango grelhado",
  "description": "Peito de frango grelhado com ervas",
  "category": "Carnes",
  "defaultPrice": "22.00",
  "requiresPreparation": true,
  "imageUrl": "https://img.almoseis.app/t/0192d6a0-0000-7000-8000-000000000001/p/0192d6a0-0000-7000-8000-000000000101.jpg",
  "isActive": true,
  "isAvailable": true,
  "isVisibleOnPage": true,
  "sitePrice": null,
  "createdAt": "2026-10-27T09:40:00Z",
  "updatedAt": "2026-10-27T09:40:00Z"
}
```

`isVisibleOnPage` (ADR-0044 item 5): bebidas e adicionais com `false` não aparecem na página de pedidos (§6.19). Nasce `true`; muda pelo `PATCH`. Pratos e acompanhamentos seguem o cardápio do dia.

`sitePrice` (EP-003, ADR-0057 item 5, Q-307): **só em `BEVERAGE`**, `> 0`. Na bebida, `isVisibleOnPage` é o "vende no site", e o pedido do site cobra o `sitePrice` (§6.19); adicionais usam o mesmo preço no site (Q-308) e têm `sitePrice: null` (enviar um valor → `422` em `errors.sitePrice`). A API **sempre** grava o preço de site da bebida visível: sem `sitePrice` no `POST`, ou ao tornar a bebida visível sem ele, vale o `defaultPrice`. Bebida de `"0.00"` não tem preço de site: no `POST` ela nasce com `isVisibleOnPage: false`; no `PATCH`, ficar visível sem `sitePrice` → `422` em `errors.sitePrice`. Bebida criada antes da migração 0009 (ou pela API anterior durante o deploy) pode vir visível com `sitePrice: null`: a página usa o `defaultPrice` até a próxima edição gravar o preço de site.

| Método e rota | Permissão | Corpo → resposta |
|---|---|---|
| `GET /products?kind=BEVERAGE&includeInactive=false` | `orders.view` | → `200 { "items": [Product] }` (sem paginação; catálogo pequeno) |
| `GET /products/{id}` | `orders.view` | → `200 Product` |
| `POST /products` | `catalog.manage` | ver abaixo → `201 Product` |
| `PATCH /products/{id}` [M] | `catalog.manage` | campos parciais → `200 Product` |
| `POST /products/{id}/image-upload` | `catalog.manage` | ver abaixo → `201` |

`POST /products` (o atalho de cadastro rápido do cardápio usa o mesmo endpoint, RN-002.4):

```json
{
  "kind": "BEVERAGE",
  "name": "Coca-Cola 350ml",
  "description": null,
  "category": "Refrigerantes",
  "defaultPrice": "6.00",
  "requiresPreparation": false,
  "sitePrice": "6.50"
}
```

`sitePrice` é opcional (só bebida).

`PATCH /products/{id}`:

```json
{ "defaultPrice": "6.50", "sitePrice": "7.00", "isAvailable": false, "imageStorageKey": "t/0192d6a0-0000-7000-8000-000000000001/p/0192d6a0-0000-7000-8000-000000000121-20261027.jpg" }
```

`kind` não pode mudar depois que o produto foi vendido ou entrou em algum cardápio (`422`, em `errors.kind`). `imageStorageKey` só aceita chaves do próprio produto (`t/{tenantId}/p/{productId}-*`). Para "excluir", use `{ "isActive": false }` (RN-002.6).

`POST /products/{id}/image-upload`:

```json
{ "contentType": "image/jpeg", "sizeBytes": 184320 }
```

→ `201`:

```json
{
  "uploadUrl": "https://<conta>.r2.cloudflarestorage.com/almoseis-images/t/0192d6a0-.../p/0192d6a0-...-20261027.jpg?X-Amz-Signature=...",
  "method": "PUT",
  "headers": { "Content-Type": "image/jpeg" },
  "storageKey": "t/0192d6a0-0000-7000-8000-000000000001/p/0192d6a0-0000-7000-8000-000000000121-20261027.jpg",
  "expiresAt": "2026-10-27T09:55:00Z"
}
```

Tipos aceitos: `image/jpeg`, `image/png`, `image/webp`, até 2 MB. Depois do `PUT`, o app grava `imageStorageKey` via `PATCH`. O `PATCH` confere o objeto no bucket: chave ainda não enviada, maior que 2 MB ou de outro tipo → `422` em `errors.imageStorageKey`. Cada pedido gera uma chave nova; a imagem atual continua até o `PATCH` trocar. **Trocar a imagem** é repetir o fluxo (pedido de URL, `PUT`, `PATCH` com a chave nova); **remover** é `PATCH { "imageStorageKey": null }`. Nos dois casos o arquivo anterior é apagado do bucket só depois que o `PATCH` é confirmado (em segundos, de forma assíncrona); um `PATCH` recusado não apaga nada. Arquivos enviados e nunca gravados num produto não são apagados por esta rota.

### Tamanhos de marmita

`MarmitaSize`: `{ "id": "0192d6a0-0000-7000-8000-000000000202", "version": 1, "name": "M", "price": "20.00", "sortOrder": 2, "isActive": true, "sellsOnSite": true, "sitePrice": "21.90" }`

`sellsOnSite` e `sitePrice` (EP-003, ADR-0057 item 1): o tamanho só aparece na página com `sellsOnSite: true`, e o pedido do site cobra o `sitePrice` (pode ser maior, menor ou igual ao `price`; não é desconto nem taxa, item 11). Ligar sem `sitePrice` (ausente ou `null`) grava o `price` como preço de site (item 2). Desligada, o `sitePrice` fica guardado. No `POST`, `sellsOnSite` ausente = `false`. A migração 0009 ligou a chave em todo tamanho ativo, com `sitePrice = price`.

| Método e rota | Permissão | Corpo → resposta |
|---|---|---|
| `GET /marmita-sizes?includeInactive=false` | `orders.view` | → `200 { "items": [MarmitaSize] }` |
| `POST /marmita-sizes` | `catalog.manage` | `{ "name": "G", "price": "25.00", "sortOrder": 3, "sellsOnSite": true, "sitePrice": "26.90" }` → `201 MarmitaSize` |
| `PATCH /marmita-sizes/{id}` [M] | `catalog.manage` | `{ "price": "26.00", "isActive": true, "sellsOnSite": true, "sitePrice": null }` → `200 MarmitaSize` |

Módulo `MARMITA` (ADR-0054): sem ele, `POST` e `PATCH` → `403 ModuleNotEnabled` (`"module": "MARMITA"`), e o `GET` → `200 { "items": [] }`.

### Cardápio fixo (EP-003 fatia 2; ADR-0056, ADR-0057)

Cardápio permanente da lanchonete, sem data: a edição vale na hora para os pedidos seguintes, e o pedido grava o snapshot (ADR-0020). Cinco recursos raiz, cada um com versão própria (`If-Match`/`ETag`, ADR-0034), auditoria, `CatalogUpdated` e entrada no feed: **categoria** (`DISH_CATEGORY`), **prato** com os tamanhos (`DISH`), **tipo de bebida** com tamanhos e sabores (`BEVERAGE_TYPE`), **combo** com componentes (`COMBO`) e **grupo de adicionais** com opções e vínculos (`ADDON_GROUP`). Os filhos não têm versão: alterar ou pausar um tamanho, sabor ou opção sobe a versão da raiz.

**Módulo `FIXED_MENU`** (ADR-0054): sem ele, toda escrita e toda leitura por id → `403 ModuleNotEnabled` (`"module": "FIXED_MENU"`), antes da validação do corpo; `GET /fixed-menu` → `200` com as cinco listas vazias.

**Permissões:** leitura `orders.view`; cadastro `catalog.manage` (OWNER, MANAGER); **pausar e reativar** `catalog.availability` (OWNER, MANAGER, ATTENDANT, KITCHEN; Q-306), só pelas rotas `…/availability`. Nenhuma rota do cardápio fixo exige `Idempotency-Key` (fora da lista do ADR-0033, como `/products`).

| Método e rota | Permissão | Corpo → resposta |
|---|---|---|
| `GET /fixed-menu?includeInactive=false` | `orders.view` | → `200 FixedMenu` (tudo numa chamada, com a disponibilidade derivada) |
| `GET /fixed-menu/dish-categories/{id}` | `orders.view` | → `200 DishCategory` |
| `POST /fixed-menu/dish-categories` | `catalog.manage` | `{ "name": "Lanches", "sortOrder": 1 }` → `201 DishCategory` |
| `PATCH /fixed-menu/dish-categories/{id}` [M] | `catalog.manage` | `{ "name"?, "sortOrder"?, "isActive"? }` → `200 DishCategory` |
| `GET /fixed-menu/dishes/{id}` | `orders.view` | → `200 Dish` |
| `POST /fixed-menu/dishes` | `catalog.manage` | ver abaixo → `201 Dish` |
| `PATCH /fixed-menu/dishes/{id}` [M] | `catalog.manage` | campos parciais → `200 Dish` |
| `PATCH /fixed-menu/dishes/{id}/availability` [M] | `catalog.availability` | `{ "isPaused": true, "sizes": [{ "id", "isPaused" }] }` → `200 Dish` |
| `POST /fixed-menu/dishes/{id}/image-upload` | `catalog.manage` | como `/products/{id}/image-upload` → `201` |
| `GET /fixed-menu/beverage-types/{id}` | `orders.view` | → `200 BeverageType` |
| `POST /fixed-menu/beverage-types` | `catalog.manage` | ver abaixo → `201 BeverageType` |
| `PATCH /fixed-menu/beverage-types/{id}` [M] | `catalog.manage` | campos parciais → `200 BeverageType` |
| `PATCH /fixed-menu/beverage-types/{id}/availability` [M] | `catalog.availability` | `{ "sizes": [{ "id", "isPaused" }], "flavors": [{ "id", "isPaused" }] }` → `200 BeverageType` |
| `GET /fixed-menu/combos/{id}` | `orders.view` | → `200 Combo` |
| `POST /fixed-menu/combos` | `catalog.manage` | ver abaixo → `201 Combo` |
| `PATCH /fixed-menu/combos/{id}` [M] | `catalog.manage` | campos parciais → `200 Combo` |
| `PATCH /fixed-menu/combos/{id}/availability` [M] | `catalog.availability` | `{ "isPaused": true }` → `200 Combo` |
| `POST /fixed-menu/combos/{id}/image-upload` | `catalog.manage` | como `/products/{id}/image-upload` → `201` |
| `GET /fixed-menu/addon-groups/{id}` | `orders.view` | → `200 AddonGroup` |
| `POST /fixed-menu/addon-groups` | `catalog.manage` | ver abaixo → `201 AddonGroup` |
| `PATCH /fixed-menu/addon-groups/{id}` [M] | `catalog.manage` | campos parciais → `200 AddonGroup` |
| `PATCH /fixed-menu/addon-groups/{id}/availability` [M] | `catalog.availability` | `{ "options": [{ "id", "isPaused" }] }` → `200 AddonGroup` |

#### `GET /fixed-menu` → `200 FixedMenu`

Listas planas, cada uma na ordem de `sortOrder` e nome; o app agrupa os pratos por `dishCategoryId` (abas **Pratos**, **Bebidas** e **Combos**, ADR-0056 item 3). `includeInactive=false` (padrão) tira as raízes inativas e os filhos inativos (tamanhos, sabores, opções); `true` traz tudo, para a tela de cadastro. As leituras por id trazem sempre os filhos inativos também.

```json
{
  "categories": [
    { "id": "0192d6a0-0000-7000-8000-000000001001", "version": 1, "name": "Lanches", "sortOrder": 1, "isActive": true, "createdAt": "2026-11-22T10:00:00Z", "updatedAt": "2026-11-22T10:00:00Z" },
    { "id": "0192d6a0-0000-7000-8000-000000001002", "version": 1, "name": "Porções", "sortOrder": 2, "isActive": true, "createdAt": "2026-11-22T10:00:00Z", "updatedAt": "2026-11-22T10:00:00Z" }
  ],
  "dishes": [
    {
      "id": "0192d6a0-0000-7000-8000-000000001101",
      "version": 1,
      "dishCategoryId": "0192d6a0-0000-7000-8000-000000001001",
      "name": "X-Burguer",
      "description": "Pão, hambúrguer 150 g, queijo",
      "imageUrl": null,
      "requiresPreparation": true,
      "hasSizes": false,
      "price": "22.00",
      "sellsOnSite": true,
      "sitePrice": "23.90",
      "sortOrder": 1,
      "isActive": true,
      "isPaused": false,
      "isSellable": true,
      "sizes": [],
      "addonGroupIds": ["0192d6a0-0000-7000-8000-000000001501"],
      "createdAt": "2026-11-22T10:01:00Z",
      "updatedAt": "2026-11-22T10:01:00Z"
    },
    {
      "id": "0192d6a0-0000-7000-8000-000000001102",
      "version": 3,
      "dishCategoryId": "0192d6a0-0000-7000-8000-000000001002",
      "name": "Batata frita",
      "description": null,
      "imageUrl": null,
      "requiresPreparation": true,
      "hasSizes": true,
      "price": null,
      "sellsOnSite": false,
      "sitePrice": null,
      "sortOrder": 1,
      "isActive": true,
      "isPaused": false,
      "isSellable": true,
      "sizes": [
        { "id": "0192d6a0-0000-7000-8000-000000001201", "name": "P", "price": "12.00", "sellsOnSite": true, "sitePrice": "13.00", "sortOrder": 1, "isActive": true, "isPaused": true, "isSellable": false },
        { "id": "0192d6a0-0000-7000-8000-000000001202", "name": "G", "price": "18.00", "sellsOnSite": true, "sitePrice": "19.50", "sortOrder": 2, "isActive": true, "isPaused": false, "isSellable": true }
      ],
      "addonGroupIds": [],
      "createdAt": "2026-11-22T10:02:00Z",
      "updatedAt": "2026-11-22T11:30:00Z"
    }
  ],
  "beverageTypes": [
    {
      "id": "0192d6a0-0000-7000-8000-000000001301",
      "version": 2,
      "name": "Refrigerante",
      "sortOrder": 1,
      "isActive": true,
      "sizes": [
        {
          "id": "0192d6a0-0000-7000-8000-000000001311",
          "name": "Lata 350 ml",
          "price": "6.00",
          "sellsOnSite": true,
          "sitePrice": "6.50",
          "sortOrder": 1,
          "isActive": true,
          "isPaused": false,
          "isSellable": true,
          "flavors": [
            { "id": "0192d6a0-0000-7000-8000-000000001321", "name": "Coca-Cola", "extraPrice": "0.00", "sortOrder": 1, "isActive": true, "isPaused": true },
            { "id": "0192d6a0-0000-7000-8000-000000001322", "name": "Coca-Cola Zero", "extraPrice": "0.50", "sortOrder": 2, "isActive": true, "isPaused": false }
          ]
        }
      ],
      "createdAt": "2026-11-22T10:03:00Z",
      "updatedAt": "2026-11-22T11:00:00Z"
    }
  ],
  "combos": [
    {
      "id": "0192d6a0-0000-7000-8000-000000001401",
      "version": 1,
      "name": "X-Burguer + batata + refri",
      "description": null,
      "imageUrl": null,
      "price": "32.00",
      "sellsOnSite": true,
      "sitePrice": "34.90",
      "sortOrder": 1,
      "isActive": true,
      "isPaused": false,
      "isSellable": false,
      "components": [
        { "id": "0192d6a0-0000-7000-8000-000000001411", "kind": "DISH", "dishId": "0192d6a0-0000-7000-8000-000000001101", "dishSizeId": null, "beverageSizeId": null, "name": "X-Burguer", "quantity": 1, "sortOrder": 0, "isSellable": true },
        { "id": "0192d6a0-0000-7000-8000-000000001412", "kind": "DISH", "dishId": "0192d6a0-0000-7000-8000-000000001102", "dishSizeId": "0192d6a0-0000-7000-8000-000000001201", "beverageSizeId": null, "name": "Batata frita P", "quantity": 1, "sortOrder": 1, "isSellable": false },
        { "id": "0192d6a0-0000-7000-8000-000000001413", "kind": "BEVERAGE", "dishId": null, "dishSizeId": null, "beverageSizeId": "0192d6a0-0000-7000-8000-000000001311", "name": "Refrigerante Lata 350 ml", "quantity": 1, "sortOrder": 2, "isSellable": true }
      ],
      "createdAt": "2026-11-22T10:05:00Z",
      "updatedAt": "2026-11-22T10:05:00Z"
    }
  ],
  "addonGroups": [
    {
      "id": "0192d6a0-0000-7000-8000-000000001501",
      "version": 1,
      "name": "Adicionais de lanche",
      "sortOrder": 1,
      "isActive": true,
      "options": [
        { "id": "0192d6a0-0000-7000-8000-000000001511", "name": "Bacon", "price": "4.00", "sortOrder": 1, "isActive": true, "isPaused": false },
        { "id": "0192d6a0-0000-7000-8000-000000001512", "name": "Sem cebola", "price": "0.00", "sortOrder": 2, "isActive": true, "isPaused": false }
      ],
      "dishCategoryIds": ["0192d6a0-0000-7000-8000-000000001001"],
      "dishIds": [],
      "createdAt": "2026-11-22T10:06:00Z",
      "updatedAt": "2026-11-22T10:06:00Z"
    }
  ]
}
```

**Disponibilidade derivada** (`isSellable`, calculada a cada leitura, **nunca gravada**, ADR-0056 item 23):

| Onde | `isSellable` |
|---|---|
| prato | ativo, não pausado e, com `hasSizes`, pelo menos um tamanho vendável |
| tamanho de prato | prato ativo e não pausado, tamanho ativo e não pausado |
| tamanho de bebida | tipo e tamanho ativos, tamanho não pausado e, com sabores ativos, pelo menos um não pausado (sem sabor ativo é vendido sem escolha) |
| componente de combo | o prato no tamanho fixo vendável (prato sem tamanho: o prato vendável); o tamanho de bebida vendável |
| combo | ativo, não pausado e **todos** os componentes vendáveis |

Pausar um componente tira de venda os combos que o usam **sem tocar no combo** (versão e `isPaused` do combo não mudam); reativar devolve. O motivo para o app ("Pausado: Batata frita P") é o `name` dos componentes com `isSellable: false`. Por isso, a cada `CatalogUpdated` ou entrada de feed de uma raiz do cardápio fixo, o app recarrega o `GET /fixed-menu` inteiro. A pausa fica até alguém reativar: abrir ou fechar o expediente não mexe nela (item 22).

`addonGroupIds` do prato: os grupos **ativos** ligados à categoria dele ∪ os ligados ao próprio prato, sem repetir, na ordem dos grupos (item 18). O preço do adicional é o mesmo para qualquer tamanho e no site (Q-308).

#### "Vende no site" e preço de site (ADR-0057)

Em prato sem tamanho, cada tamanho de prato, cada tamanho de bebida e combo: `sellsOnSite` (padrão `false`) e `sitePrice`. Com `sellsOnSite: true` e `sitePrice` ausente ou `null`, a API grava o **preço de balcão** como preço de site (item 2); o app mostra o campo preenchido e o estabelecimento ajusta. O preço de site pode ser maior, menor ou igual ao de balcão. Desligada, a unidade não aparece na página, e o `sitePrice` fica guardado (volta se a chave for religada). Prato com tamanho não tem `price`, `sellsOnSite` nem `sitePrice` próprios: ficam em cada tamanho. Acréscimo de sabor e opção de adicional não têm preço de site (Q-308).

#### `POST /fixed-menu/dishes`

```json
{
  "dishCategoryId": "0192d6a0-0000-7000-8000-000000001002",
  "name": "Açaí",
  "description": null,
  "requiresPreparation": true,
  "hasSizes": true,
  "price": null,
  "sellsOnSite": false,
  "sitePrice": null,
  "sortOrder": 1,
  "sizes": [
    { "id": null, "name": "300 ml", "price": "14.00", "sellsOnSite": true, "sitePrice": null, "sortOrder": 1 },
    { "id": null, "name": "500 ml", "price": "18.00", "sellsOnSite": true, "sitePrice": "19.00", "sortOrder": 2 },
    { "id": null, "name": "700 ml", "price": "23.00", "sellsOnSite": false, "sitePrice": null, "sortOrder": 3 }
  ]
}
```

Prato sem tamanho: `"hasSizes": false`, `"price": "22.00"`, `"sizes": []` (ou `null`). `requiresPreparation` ausente ou `null` = `true`.

**`PATCH /fixed-menu/dishes/{id}`**: `dishCategoryId`, `name`, `description`, `requiresPreparation`, `hasSizes`, `price`, `sellsOnSite`, `sitePrice`, `sortOrder`, `isActive`, `imageStorageKey`, `sizes`. Só os presentes mudam. **`sizes` presente substitui a lista**: item com `id` atualiza aquele tamanho (e o reativa, se estava inativo); sem `id` cria; tamanho do prato ausente da lista é **inativado**, nunca apagado. A pausa de cada tamanho não muda pelo `PATCH` (só por `…/availability`). Desligar `hasSizes` inativa todos os tamanhos e exige `price`; ligar exige pelo menos um tamanho ativo e limpa `price`/`sellsOnSite`/`sitePrice`. `imageStorageKey` segue as regras de `/products` com a chave `t/{tenantId}/d/{dishId}-*` (combo: `t/{tenantId}/c/{comboId}-*`).

```json
{ "price": "23.00", "sellsOnSite": true, "sitePrice": "24.90" }
```

#### `POST /fixed-menu/beverage-types`

O sabor é **do tamanho** (Q-305): cada tamanho tem a sua lista. "Copiar sabores de outro tamanho" é do app (envia a mesma lista no outro tamanho).

```json
{
  "name": "Refrigerante",
  "sortOrder": 1,
  "sizes": [
    {
      "id": null, "name": "Lata 350 ml", "price": "6.00", "sellsOnSite": true, "sitePrice": "6.50", "sortOrder": 1,
      "flavors": [
        { "id": null, "name": "Coca-Cola", "extraPrice": "0.00", "sortOrder": 1 },
        { "id": null, "name": "Coca-Cola Zero", "extraPrice": "0.50", "sortOrder": 2 }
      ]
    },
    { "id": null, "name": "2 L", "price": "14.00", "sellsOnSite": false, "sitePrice": null, "sortOrder": 2, "flavors": [] }
  ]
}
```

Preço da bebida = preço do tamanho (ou o de site, no pedido do site) + `extraPrice` do sabor: Suco 500 ml `"9.00"` + "Laranja com acerola" `"1.00"` = `"10.00"`. `extraPrice` ausente = `"0.00"`. **`PATCH /fixed-menu/beverage-types/{id}`**: `name`, `sortOrder`, `isActive`, `sizes`; `sizes` (e o `flavors` de cada tamanho enviado) substitui a lista como em `dishes`: o que fica de fora é inativado.

#### `POST /fixed-menu/combos`

```json
{
  "name": "X-Burguer + batata + refri",
  "description": null,
  "price": "32.00",
  "sellsOnSite": true,
  "sitePrice": "34.90",
  "sortOrder": 1,
  "components": [
    { "kind": "DISH", "dishId": "0192d6a0-0000-7000-8000-000000001101", "dishSizeId": null, "beverageSizeId": null, "quantity": 1 },
    { "kind": "DISH", "dishId": "0192d6a0-0000-7000-8000-000000001102", "dishSizeId": "0192d6a0-0000-7000-8000-000000001201", "beverageSizeId": null, "quantity": 1 },
    { "kind": "BEVERAGE", "dishId": null, "dishSizeId": null, "beverageSizeId": "0192d6a0-0000-7000-8000-000000001311", "quantity": 1 }
  ]
}
```

- `DISH`: prato **ativo**; `dishSizeId` obrigatório se o prato tem tamanho (e precisa ser um tamanho ativo **daquele** prato), `null` se não tem. `BEVERAGE`: `beverageSizeId` de um tamanho ativo de tipo ativo; o sabor é escolhido no pedido (fatia 3). Item pausado pode entrar: o combo só fica fora de venda enquanto durar a pausa.
- `quantity` de 1 a 10; 1 a 10 componentes; a ordem da lista é o `sortOrder` do componente.
- O combo vende no site pela chave **dele**; os componentes não precisam vender no site, só estar vendáveis (ADR-0057 item 4).
- **`PATCH /fixed-menu/combos/{id}`**: `name`, `description`, `price`, `sellsOnSite`, `sitePrice`, `sortOrder`, `isActive`, `imageStorageKey`, `components`. `components` presente **troca** a lista inteira (os componentes não têm valor histórico; a mesma lista não sobe a versão).

Exemplo do pedido (fatia 3, ADR-0058): combo `"32.00"` + bacon `"4.00"` + Coca-Cola Zero (`"0.50"`) = **`"36.50"`**.

#### `POST /fixed-menu/addon-groups`

```json
{
  "name": "Adicionais de lanche",
  "sortOrder": 1,
  "options": [
    { "id": null, "name": "Bacon", "price": "4.00", "sortOrder": 1 },
    { "id": null, "name": "Sem cebola", "price": "0.00", "sortOrder": 2 }
  ],
  "dishCategoryIds": ["0192d6a0-0000-7000-8000-000000001001"],
  "dishIds": []
}
```

Vínculo com categorias e pratos **ativos** do estabelecimento. **`PATCH`**: `name`, `sortOrder`, `isActive`, `options` (substitui a lista; opção ausente é inativada), `dishCategoryIds` e `dishIds` (cada um substitui os vínculos daquele tipo; o outro tipo fica). Sem limite mínimo/máximo nem "grátis até N" nesta fase (item 19).

#### Pausa (`…/availability`, `catalog.availability`) [M]

Só os itens enviados mudam; `id` de outro prato, tipo ou grupo → `422` em `sizes[i].id`/`flavors[i].id`/`options[i].id`. Repetir o mesmo estado não sobe a versão. Auditoria `FixedMenuAvailabilityChanged`.

```json
{ "isPaused": null, "sizes": [{ "id": "0192d6a0-0000-7000-8000-000000001201", "isPaused": true }] }
```

#### Regras e erros

| Situação | Resposta |
|---|---|
| nome vazio ou longo (categoria 40, prato 80, tamanho 30, tipo 40, sabor 40, combo 80, grupo 60, opção 80), descrição > 300, `sortOrder` < 0; preço de prato, tamanho, tamanho de bebida e combo `<= 0`; `sitePrice` `<= 0`; `extraPrice` e preço de opção `< 0`; mais de 20 tamanhos, 50 sabores por tamanho, 10 componentes, 50 opções ou 100 vínculos de cada tipo | `422 ValidationFailed` no campo |
| nome repetido entre os ativos (raiz: no estabelecimento; filho: no mesmo pai) | `422` em `name` / `sizes[i].name` / `sizes[i].flavors[j].name` / `options[i].name` |
| prato com `hasSizes: true` sem tamanho ativo; com `price` ou `sellsOnSite` no prato | `422` em `sizes` / `price` / `sellsOnSite` |
| prato sem tamanho sem `price`, ou com `sizes` | `422` em `price` / `sizes` |
| `dishCategoryId` inexistente, de outro estabelecimento ou inativa | `422` em `dishCategoryId` |
| componente de combo fora das regras acima (inclusive de outro estabelecimento) | `422` em `components[i].dishId` / `.dishSizeId` / `.beverageSizeId`; lista vazia → `components` |
| vínculo com categoria ou prato inexistente/inativo | `422` em `dishCategoryIds[i]` / `dishIds[i]` |
| inativar categoria com prato ativo | `422` em `isActive` |
| **inativar** prato, tamanho de prato (inclusive deixá-lo fora de `sizes` ou desligar `hasSizes`), tipo ou tamanho de bebida usado por **combo ativo**, ou ligar `hasSizes` num prato que um combo ativo usa sem tamanho (item 26) | `409 UsedInActiveCombos` com `combos` |
| recurso de outro estabelecimento ou inexistente | `404 NotFound` |
| `If-Match` ausente / desatualizado | `428 IfMatchRequired` / `409 VersionConflict` com o recurso em `current` |

```json
{
  "type": "urn:almoseis:error:used-in-active-combos",
  "title": "O item é usado em combos ativos.",
  "status": 409,
  "detail": "Ajuste ou inative antes: Batata + refri, X-Burguer + batata + refri.",
  "errorCode": "UsedInActiveCombos",
  "traceId": "00-7a1b2c3d4e5f60718293a4b5c6d7e8f9-7a1b2c3d4e5f6071-01",
  "combos": [
    { "id": "0192d6a0-0000-7000-8000-000000001402", "name": "Batata + refri" },
    { "id": "0192d6a0-0000-7000-8000-000000001401", "name": "X-Burguer + batata + refri" }
  ]
}
```

Auditoria: `FixedMenuItemCreated`, `FixedMenuItemUpdated` (nomes, preços, chaves e pausas; nunca a entidade inteira) e `FixedMenuAvailabilityChanged`, com o `entityType` da raiz.


---

## 6.6 Expediente (ADR-0019, ADR-0038, ADR-0039)

`BusinessDay`:

```json
{
  "id": "0192d6a0-0000-7000-8000-000000000401",
  "version": 1,
  "businessDate": "2026-10-27",
  "status": "OPEN",
  "openingBalance": "100.00",
  "openedAt": "2026-10-27T10:30:00Z",
  "openedBy": { "id": "0192d6a0-0000-7000-8000-000000000303", "displayName": "Carla" },
  "closedAt": null,
  "closedBy": null,
  "dailyClosingId": null
}
```

### `GET /business-days/current` — `orders.view`

→ `200 BusinessDay`, ou `404 NoOpenBusinessDay`.

### `POST /business-days` — `cash.open` [I]

```json
{ "openingBalance": "100.00", "confirmSameDate": false }
```

→ `201`:

```json
{
  "businessDay": { "...": "BusinessDay" },
  "materializedMenus": [
    { "id": "0192d6a0-0000-7000-8000-000000000901", "menuType": "MARMITA", "sourceMenuId": "0192d6a0-0000-7000-8000-000000000911" },
    { "id": "0192d6a0-0000-7000-8000-000000000902", "menuType": "PF", "sourceMenuId": "0192d6a0-0000-7000-8000-000000000912" }
  ]
}
```

`materializedMenus` só existe com o módulo `DAILY_MENU` (ADR-0054 item 18). Numa lanchonete, a abertura não cria cardápio do dia e a lista vem vazia (`[]`). O expediente em si (saldo inicial, caixa único, numeração) é igual para todos os comportamentos.

Erros:
- `409 BusinessDayAlreadyOpen`, com o expediente aberto em `current`;
- `409 BusinessDayClosedToday`, quando já existe expediente fechado na data e `confirmSameDate` é `false`. O app pede confirmação e reenvia com `true` e uma **nova** `Idempotency-Key`, porque o corpo mudou (repetir a chave daria `422 IdempotencyKeyReused`).

### `GET /business-days?from=2026-10-01&to=2026-10-31&limit=50` — `closing.view`

→ `200 { "items": [BusinessDay], "nextCursor": null }`, do mais recente para o mais antigo.

---

## 6.7 Cardápios (RF-003, ADR-0038)

`Menu`:

```json
{
  "id": "0192d6a0-0000-7000-8000-000000000901",
  "version": 2,
  "menuType": "MARMITA",
  "kind": "DAILY",
  "date": "2026-10-27",
  "dayOfWeek": null,
  "sourceMenuId": "0192d6a0-0000-7000-8000-000000000911",
  "items": [
    { "id": "0192d6a0-0000-7000-8000-000000009a01", "productId": "0192d6a0-0000-7000-8000-000000000101", "section": "MAIN_DISH", "name": "Frango grelhado", "description": "Peito de frango grelhado com ervas", "imageUrl": null, "pfPrice": null, "isAvailable": true, "sortOrder": 1 },
    { "id": "0192d6a0-0000-7000-8000-000000009a02", "productId": "0192d6a0-0000-7000-8000-000000000102", "section": "MAIN_DISH", "name": "Costela", "description": null, "imageUrl": null, "pfPrice": null, "isAvailable": false, "sortOrder": 2 },
    { "id": "0192d6a0-0000-7000-8000-000000009a03", "productId": "0192d6a0-0000-7000-8000-000000000111", "section": "SIDE_DISH", "name": "Arroz", "description": null, "imageUrl": null, "pfPrice": null, "isAvailable": true, "sortOrder": 10 },
    { "id": "0192d6a0-0000-7000-8000-000000009a04", "productId": "0192d6a0-0000-7000-8000-000000000112", "section": "SIDE_DISH", "name": "Feijão", "description": null, "imageUrl": null, "pfPrice": null, "isAvailable": true, "sortOrder": 11 }
  ],
  "createdAt": "2026-10-27T10:30:00Z",
  "createdBy": null,
  "updatedAt": "2026-10-27T12:10:00Z"
}
```

`createdBy: null` = materializado pelo sistema. No menu `PF`, os itens `MAIN_DISH` têm `pfPrice` (ex.: `"24.00"`).

### `GET /menus/current?menuType=MARMITA` — `orders.view`

→ `200 Menu` (o `DAILY` da data do expediente aberto). Sem expediente aberto → `409 BusinessDayRequired`. Sem cardápio → `404 MenuNotFound`, já com as opções de reaproveitamento (RN-003.2/3.4):

```json
{
  "type": "urn:almoseis:error:menu-not-found",
  "title": "Ainda não há cardápio de marmitas para hoje.",
  "status": 404,
  "errorCode": "MenuNotFound",
  "traceId": "00-...-01",
  "menuType": "MARMITA",
  "businessDate": "2026-10-27",
  "sources": {
    "yesterdayMenuId": "0192d6a0-0000-7000-8000-000000000903",
    "lastWeekSameDayMenuId": "0192d6a0-0000-7000-8000-000000000904",
    "weeklyTemplateId": null
  }
}
```

### Demais rotas

| Método e rota | Permissão | Corpo → resposta |
|---|---|---|
| `GET /menus?menuType=PF&kind=DAILY&from=2026-10-20&to=2026-10-27` | `orders.view` | → `200 { "items": [MenuSummary] }` (`MenuSummary` = `Menu` sem `items`, com `itemCount`) |
| `GET /menus?menuType=MARMITA&kind=WEEKLY_TEMPLATE` | `orders.view` | → modelos semanais |
| `GET /menus/{id}` | `orders.view` | → `200 Menu` |
| `POST /menus` | `menu.manage` | ver abaixo → `201 Menu` |
| `PUT /menus/{id}/items` [M] | `menu.manage` | substitui a lista inteira → `200 Menu` |
| `PATCH /menus/{id}/items/{itemId}` [M] | `menu.manage` | `{ "isAvailable": false }` ou `{ "pfPrice": "26.00" }` → `200 Menu` ("acabou a costela") |
| `POST /menus/{id}/clone` | `menu.manage` | ver abaixo → `201 Menu` |

`POST /menus`:

```json
{
  "menuType": "PF",
  "kind": "DAILY",
  "date": "2026-10-28",
  "dayOfWeek": null,
  "items": [
    { "productId": "0192d6a0-0000-7000-8000-000000000101", "pfPrice": "24.00", "isAvailable": true, "sortOrder": 1 },
    { "productId": "0192d6a0-0000-7000-8000-000000000111", "pfPrice": null, "isAvailable": true, "sortOrder": 10 }
  ]
}
```

`section` vem do `kind` do produto. Bebidas e adicionais não entram em cardápio (`422`). No `PF`, `pfPrice` nulo em prato principal assume o `defaultPrice` do produto; sem nenhum dos dois → `422`. Fora de `PF` + `MAIN_DISH`, `pfPrice` deve ser `null`. `409 MenuAlreadyExists` (com `existingMenuId`) se já existir um `DAILY` da data, ou um modelo do mesmo dia da semana, para o mesmo `menuType`.

`PUT /menus/{id}/items`: `{ "items": [ { "productId": "...", "pfPrice": null, "isAvailable": true, "sortOrder": 1 } ] }`

`POST /menus/{id}/clone` (cria cópia independente, RN-003.5):

```json
{ "kind": "DAILY", "date": "2026-10-28", "dayOfWeek": null }
```

Para gerar um modelo semanal a partir de um dia: `{ "kind": "WEEKLY_TEMPLATE", "date": null, "dayOfWeek": 3 }`.

Módulo `DAILY_MENU` (ADR-0054): sem ele, `POST /menus`, `PUT /menus/{id}/items`, `PATCH /menus/{id}/items/{itemId}` e `POST /menus/{id}/clone` → `403 ModuleNotEnabled` (`"module": "DAILY_MENU"`), e `GET /menus` → `200 { "items": [] }`.

---

## 6.8 Clientes (ADR-0021, ADR-0024)

`Customer`:

```json
{
  "id": "0192d6a0-0000-7000-8000-000000000801",
  "version": 3,
  "name": "João Silva",
  "phoneNumber": "+5534999991234",
  "notes": "Portão azul, tocar interfone 2",
  "addresses": [
    { "id": "0192d6a0-0000-7000-8000-000000000811", "label": "Casa", "street": "Rua das Acácias", "number": "120", "complement": "Ap 201", "neighborhood": "Santa Mônica", "reference": "Em frente à padaria", "isDefault": true }
  ],
  "pendingAmount": "35.00",
  "pendingOrderCount": 1,
  "anonymizedAt": null,
  "createdAt": "2026-10-20T12:00:00Z"
}
```

| Método e rota | Permissão | Corpo → resposta |
|---|---|---|
| `GET /customers?search=joao&limit=20&cursor=` | `orders.view` | busca por nome ou telefone → `200 { "items": [Customer], "nextCursor": null }` |
| `GET /customers/{id}` | `orders.view` | → `200 Customer` |
| `POST /customers` | `customers.manage` | `{ "name": "João Silva", "phoneNumber": "+5534999991234", "notes": null, "addresses": [ { "label": "Casa", "street": "Rua das Acácias", "number": "120", "complement": "Ap 201", "neighborhood": "Santa Mônica", "reference": null, "isDefault": true } ] }` → `201 Customer` |
| `PATCH /customers/{id}` [M] | `customers.manage` | `{ "name": "João P. Silva", "notes": null }` → `200 Customer` |
| `POST /customers/{id}/addresses` [M] | `customers.manage` | endereço → `201 Customer` |
| `PATCH /customers/{id}/addresses/{addressId}` [M] | `customers.manage` | campos parciais → `200 Customer` |
| `DELETE /customers/{id}/addresses/{addressId}` [M] | `customers.manage` | → `200 Customer` |
| `POST /customers/{id}/anonymize` [M] | `settings.manage` | `{ "reason": "Pedido do titular via WhatsApp" }` → `200 Customer` anonimizado (ADR-0024) |
| `GET /customers/{id}/pending-orders` | `orders.view` | fiado → ver abaixo |

`GET /customers/{id}/pending-orders` → `200`:

```json
{
  "customer": { "id": "0192d6a0-0000-7000-8000-000000000801", "name": "João Silva" },
  "totalPending": "35.00",
  "orders": [
    { "id": "0192d6a0-0000-7000-8000-000000000488", "orderNumber": 31, "businessDate": "2026-10-26", "orderType": "MARMITA", "totalAmount": "35.00", "paidAmount": "0.00", "balance": "35.00", "version": 2 }
  ]
}
```

`409` em `POST /customers` com telefone já cadastrado, trazendo o cliente existente em `current`.

Fiado (`pendingAmount`, `pendingOrderCount` e `pending-orders`) só considera pedidos aceitos; pedido do site só é vinculado ao cliente no aceite (ADR-0044 item 20, ADR-0046 item 4).

---

## 6.9 Pedidos (RF-004 a RF-007, ADR-0005, 0007, 0020–0023)

### Objeto `Order` (detalhe)

```json
{
  "id": "0192d6a0-0000-7000-8000-000000000501",
  "version": 4,
  "orderNumber": 12,
  "businessDayId": "0192d6a0-0000-7000-8000-000000000401",
  "businessDate": "2026-10-27",
  "orderType": "MARMITA",
  "origin": "APP",
  "priceList": "COUNTER",
  "fulfillmentType": "DELIVERY",
  "customer": { "id": "0192d6a0-0000-7000-8000-000000000801", "name": "João Silva", "phoneNumber": "+5534999991234" },
  "deliveryAddress": { "street": "Rua das Acácias", "number": "120", "complement": "Ap 201", "neighborhood": "Santa Mônica", "reference": "Em frente à padaria" },
  "tableLabel": null,
  "tabNumber": null,
  "notes": "Entregar até 12h30",
  "expectedPaymentMethod": null,
  "changeFor": null,
  "riskFlag": null,
  "deliveryFee": "5.00",
  "deliveryFeeSource": "FIXED_DEFAULT",
  "deliveryDistanceKm": null,
  "itemsTotal": "50.00",
  "weighingsTotal": "0.00",
  "totalAmount": "55.00",
  "paidAmount": "20.00",
  "balance": "35.00",
  "isPaid": false,
  "productionStatus": "IN_PROGRESS",
  "productionStatusChangedAt": "2026-10-27T11:42:00Z",
  "cancellationStatus": "ACTIVE",
  "cancellation": null,
  "acceptanceStatus": "ACCEPTED",
  "acceptanceDeadline": null,
  "acceptedAt": "2026-10-27T11:30:00Z",
  "acceptedBy": { "id": "0192d6a0-0000-7000-8000-000000000302", "displayName": "Bruno" },
  "estimatedReadyMinutes": null,
  "rejection": null,
  "expiredAt": null,
  "withdrawnAt": null,
  "fulfillmentStatus": "PENDING",
  "fulfillmentStatusChangedAt": "2026-10-27T11:30:00Z",
  "outForDeliveryAt": null,
  "deliveredAt": null,
  "pickedUpAt": null,
  "isReadyForPickup": false,
  "items": [
    {
      "id": "0192d6a0-0000-7000-8000-000000000601",
      "itemKind": "MARMITA",
      "marmitaSizeId": "0192d6a0-0000-7000-8000-000000000202",
      "productId": null,
      "name": "Marmita M",
      "composition": "Frango grelhado · Arroz, Feijão",
      "components": [
        { "productId": "0192d6a0-0000-7000-8000-000000000101", "role": "MAIN_DISH", "name": "Frango grelhado" },
        { "productId": "0192d6a0-0000-7000-8000-000000000111", "role": "SIDE_DISH", "name": "Arroz" },
        { "productId": "0192d6a0-0000-7000-8000-000000000112", "role": "SIDE_DISH", "name": "Feijão" }
      ],
      "quantity": 2,
      "unitPrice": "20.00",
      "lineTotal": "40.00",
      "requiresPreparation": true,
      "notes": "Sem cebola",
      "addons": [
        {
          "id": "0192d6a0-0000-7000-8000-000000000602",
          "itemKind": "ADDON",
          "productId": "0192d6a0-0000-7000-8000-000000000131",
          "name": "Ovo frito",
          "quantity": 1,
          "unitPrice": "2.00",
          "lineTotal": "4.00",
          "requiresPreparation": true,
          "notes": null,
          "createdAt": "2026-10-27T11:30:00Z",
          "createdBy": { "id": "0192d6a0-0000-7000-8000-000000000302", "displayName": "Bruno" }
        }
      ],
      "createdAt": "2026-10-27T11:30:00Z",
      "createdBy": { "id": "0192d6a0-0000-7000-8000-000000000302", "displayName": "Bruno" }
    },
    {
      "id": "0192d6a0-0000-7000-8000-000000000603",
      "itemKind": "BEVERAGE",
      "marmitaSizeId": null,
      "productId": "0192d6a0-0000-7000-8000-000000000121",
      "name": "Coca-Cola 350ml",
      "composition": null,
      "components": [],
      "quantity": 1,
      "unitPrice": "6.00",
      "lineTotal": "6.00",
      "requiresPreparation": false,
      "notes": null,
      "addons": [],
      "createdAt": "2026-10-27T11:48:00Z",
      "createdBy": { "id": "0192d6a0-0000-7000-8000-000000000302", "displayName": "Bruno" }
    }
  ],
  "weighings": [],
  "payments": [
    { "id": "0192d6a0-0000-7000-8000-000000000701", "amount": "20.00", "paymentMethod": "PIX", "label": null, "status": "CONFIRMED", "paidAt": "2026-10-27T11:31:00Z", "businessDate": "2026-10-27" }
  ],
  "pendingAlertCount": 1,
  "createdAt": "2026-10-27T11:30:00Z",
  "createdBy": { "id": "0192d6a0-0000-7000-8000-000000000302", "displayName": "Bruno" },
  "updatedAt": "2026-10-27T11:48:00Z"
}
```

- `lineTotal` do adicional = `unitPrice × quantity × quantidade do pai` (2,00 × 1 × 2 = 4,00, conforme o §4.7 de `04-modelo-dados.md`).
- `itemsTotal` = 40,00 (2 marmitas M) + 4,00 (ovo × 2) + 6,00 (Coca) = **50,00**. `totalAmount` = 50,00 + 5,00 de entrega = **55,00**.
- `balance` = `totalAmount − paidAmount` = 55,00 − 20,00 = **35,00**, calculado no servidor.
- `deliveryFeeSource` (ADR-0048 item 20, só em entrega): `FIXED_DEFAULT` (a taxa padrão do estabelecimento), `DISTANCE` (calculada pela página, §6.19) ou `MANUAL` (digitada ou editada no app). Editar a taxa ou o endereço de um pedido `DISTANCE` o torna `MANUAL`/`FIXED_DEFAULT`. `null` fora de entrega e nas entregas gravadas antes da migration 0005. `deliveryDistanceKm`: `"4.5"` só enquanto a origem for `DISTANCE`; senão `null`.
- `items` traz só itens `ACTIVE`. As remoções aparecem em `GET /orders/{id}/history`.
- `pendingAlertCount` é **por funcionário** (o de quem chama).
- `cancellation` (quando cancelado): `{ "canceledAt": "...", "canceledBy": {...}, "reason": "Cliente desistiu" }`.
- `customer` existe quando há cliente cadastrado **ou** nome avulso. Com nome avulso (ex.: `"Mesa 3"` num pedido por quilo), `customer.id` é `null`. Sem nenhum dos dois, `customer` é `null`.
- `items` e `addons` saem na ordem de inclusão.
- **Aceite e atendimento (ADR-0046)**, duas dimensões independentes da produção, do financeiro e do cancelamento:
  - `priceList` (EP-003, ADR-0057 item 7): a tabela de preço do pedido, imutável. `APP` → `COUNTER` (preço de balcão); `WEB` → `SITE` (preço de site do tamanho de marmita e da bebida; adicional pelo mesmo preço, Q-308). Item **acrescentado no app** a um pedido `SITE` usa o preço de site de quem vende no site e o de balcão no resto (Q-309); o `unitPrice` de cada linha guarda o que foi cobrado. Pedidos anteriores à migração 0009: `COUNTER`, e os `WEB` já gravados, `SITE`.
  - `origin`: `APP` (criado no app) ou `WEB` (página de pedidos, EP-002). Pedido `WEB` tem `createdBy: null`, e os itens montados pelo cliente também (`items[].createdBy: null`, `addons[].createdBy: null`).
  - `acceptanceStatus`: `AWAITING_ACCEPTANCE`, `ACCEPTED`, `REJECTED`, `EXPIRED`, `WITHDRAWN` (e `AWAITING_PAYMENT`, reservado à Fase 2). **Todo pedido do app nasce `ACCEPTED`**, com `acceptedAt = createdAt` e `acceptedBy = createdBy`. Enquanto não é `ACCEPTED`: `orderNumber`, `productionStatus`, `fulfillmentStatus` e `customer.id` são `null`, e toda escrita responde `409 OrderNotAccepted`. `REJECTED`, `EXPIRED` e `WITHDRAWN` são finais e nunca recebem número.
  - `acceptanceDeadline`: prazo do aceite (envio + 10 min); `null` em pedido do app. `estimatedReadyMinutes`: 15, 30, 45 ou 60, informado no aceite (opcional).
  - `rejection` (quando recusado): `{ "reasonCode": "ITEM_UNAVAILABLE", "message": "A costela acabou.", "rejectedAt": "...", "rejectedBy": {...} }`. Na recusa do sistema ao finalizar o fechamento, `reasonCode: "BUSINESS_DAY_CLOSED"`, `message: null` e `rejectedBy: null`. `expiredAt`/`withdrawnAt` preenchidos nos outros estados finais.
  - `fulfillmentStatus`: só em `DELIVERY`/`PICKUP` aceitos (`null` em `DINE_IN`). Entrega: `PENDING` → `OUT_FOR_DELIVERY` → `DELIVERED`. Retirada: `PENDING` → `PICKED_UP`. `outForDeliveryAt`, `deliveredAt` e `pickedUpAt` são os horários de cada passo; voltar um passo limpa o horário do passo desfeito.
  - `isReadyForPickup` ("Pronto para retirar") é **derivado**, não gravado: `PICKUP` + `productionStatus: "COMPLETED"` + `fulfillmentStatus: "PENDING"`.
  - Mudar o atendimento não mexe no financeiro: pedido entregue e não pago continua com saldo e vira fiado no fechamento.

Pedido `PF`: `fulfillmentType: "DINE_IN"`, `tableLabel: "5"`, itens `itemKind: "PF"` com `productId` do prato e `unitPrice` = `pfPrice` do cardápio.

Pedido `KILO`: `productionStatus: null`, e `weighings`:

```json
[
  { "id": "0192d6a0-0000-7000-8000-000000000611", "weightGrams": 452, "pricePerKg": "59.90", "amount": "27.07", "createdAt": "2026-10-27T12:05:00Z", "createdBy": { "id": "0192d6a0-0000-7000-8000-000000000303", "displayName": "Carla" } },
  { "id": "0192d6a0-0000-7000-8000-000000000612", "weightGrams": 318, "pricePerKg": "59.90", "amount": "19.05", "createdAt": "2026-10-27T12:06:00Z", "createdBy": { "id": "0192d6a0-0000-7000-8000-000000000303", "displayName": "Carla" } }
]
```

### Pedido do site (ADR-0046)

Pedido "pagar na entrega/retirada" enviado pela página, aguardando aceite. A rota pública de envio entra com a página (EP-002 F-204); aqui está o formato que o app do restaurante lê:

```json
{
  "id": "0192d6a0-0000-7000-8000-000000000504",
  "version": 1,
  "orderNumber": null,
  "businessDayId": "0192d6a0-0000-7000-8000-000000000401",
  "businessDate": "2026-10-27",
  "orderType": "MARMITA",
  "origin": "WEB",
  "priceList": "SITE",
  "fulfillmentType": "DELIVERY",
  "customer": { "id": null, "name": "Carla Souza", "phoneNumber": "+5534988887777" },
  "deliveryAddress": { "street": "Rua das Palmeiras", "number": "45", "complement": "Casa 2", "neighborhood": "Centro", "reference": null },
  "tableLabel": null,
  "tabNumber": null,
  "notes": null,
  "expectedPaymentMethod": "CASH",
  "changeFor": "50.00",
  "riskFlag": null,
  "deliveryFee": "3.60",
  "deliveryFeeSource": "DISTANCE",
  "deliveryDistanceKm": "4.5",
  "itemsTotal": "40.00",
  "weighingsTotal": "0.00",
  "totalAmount": "43.60",
  "paidAmount": "0.00",
  "balance": "43.60",
  "isPaid": false,
  "productionStatus": null,
  "productionStatusChangedAt": null,
  "cancellationStatus": "ACTIVE",
  "cancellation": null,
  "acceptanceStatus": "AWAITING_ACCEPTANCE",
  "acceptanceDeadline": "2026-10-27T12:10:00Z",
  "acceptedAt": null,
  "acceptedBy": null,
  "estimatedReadyMinutes": null,
  "rejection": null,
  "expiredAt": null,
  "withdrawnAt": null,
  "fulfillmentStatus": null,
  "fulfillmentStatusChangedAt": null,
  "outForDeliveryAt": null,
  "deliveredAt": null,
  "pickedUpAt": null,
  "isReadyForPickup": false,
  "items": [
    {
      "id": "0192d6a0-0000-7000-8000-000000000641",
      "itemKind": "MARMITA",
      "marmitaSizeId": "0192d6a0-0000-7000-8000-000000000202",
      "productId": null,
      "name": "Marmita M",
      "composition": "Frango grelhado · Arroz, Feijão",
      "components": [
        { "productId": "0192d6a0-0000-7000-8000-000000000101", "role": "MAIN_DISH", "name": "Frango grelhado" },
        { "productId": "0192d6a0-0000-7000-8000-000000000111", "role": "SIDE_DISH", "name": "Arroz" },
        { "productId": "0192d6a0-0000-7000-8000-000000000112", "role": "SIDE_DISH", "name": "Feijão" }
      ],
      "quantity": 2,
      "unitPrice": "20.00",
      "lineTotal": "40.00",
      "requiresPreparation": true,
      "notes": null,
      "addons": [],
      "createdAt": "2026-10-27T12:00:00Z",
      "createdBy": null
    }
  ],
  "weighings": [],
  "payments": [],
  "pendingAlertCount": 0,
  "createdAt": "2026-10-27T12:00:00Z",
  "createdBy": null,
  "updatedAt": "2026-10-27T12:00:00Z"
}
```

- Não aparece na lista padrão, no painel da cozinha, no fechamento nem nas pendências do cliente (RN-206.9). O app lê a faixa "Novos do site" com `GET /orders?acceptanceStatus=AWAITING_ACCEPTANCE` e conta o prazo com `acceptanceDeadline`.
- Depois do aceite é um pedido comum (edita, recebe pagamento manual, pode ser cancelado pelo ADR-0022).
- `customer.phoneNumber` não é verificado (ADR-0044): o app mostra "telefone não verificado" nos pedidos `WEB`.
- Só em pedido `WEB` (`null` nos do app): `expectedPaymentMethod` (`CASH`, `PIX`, `CARD`: a forma que o cliente pretende usar, ADR-0044 item 19; **não** cria pagamento), `changeFor` ("troco para R$ X", só com `CASH`) e `riskFlag` (`CAPTCHA_UNAVAILABLE` quando o anti-robô não respondeu no envio, ADR-0045 item 12: o app pode destacar o pedido).
- Pedidos do site que não viraram venda (`REJECTED`, `EXPIRED`, `WITHDRAWN`) têm nome, telefone, endereço e observações anonimizados 7 dias depois (ADR-0052 item 3): `customer.name = "Cliente anonimizado"`, `phoneNumber: null`, endereço `"Endereço anonimizado"`. Itens, valores e motivo ficam.
- A partir da fatia 2 o envio é a rota pública `POST /public/stores/{slug}/orders` (§6.19).

### `GET /orders` — `orders.view`

Filtros:
- `businessDayId` (padrão: o expediente aberto);
- `allDays=true` (ignora o expediente; útil para pendências);
- `acceptanceStatus` (padrão `ACCEPTED`): a lista comum **só traz pedidos aceitos**; `AWAITING_ACCEPTANCE` é a faixa "Novos do site"; `REJECTED`, `EXPIRED` e `WITHDRAWN` listam os que não viraram venda (ADR-0046);
- `orderType`, `isPaid`, `cancellationStatus`, `productionStatus`, `customerId`;
- `search` (nome do cliente, mesa ou comanda);
- `limit`, `cursor`.

Ordenação: `createdAt` decrescente (na prática, pelo id UUID v7, que segue a ordem de criação). Sem `businessDayId` e sem expediente aberto, a lista vem vazia.

```json
{
  "items": [
    {
      "id": "0192d6a0-0000-7000-8000-000000000501",
      "version": 4,
      "orderNumber": 12,
      "businessDate": "2026-10-27",
      "orderType": "MARMITA",
      "origin": "APP",
      "fulfillmentType": "DELIVERY",
      "customerName": "João Silva",
      "tableLabel": null,
      "tabNumber": null,
      "totalAmount": "55.00",
      "paidAmount": "20.00",
      "balance": "35.00",
      "isPaid": false,
      "productionStatus": "IN_PROGRESS",
      "cancellationStatus": "ACTIVE",
      "acceptanceStatus": "ACCEPTED",
      "acceptanceDeadline": null,
      "fulfillmentStatus": "PENDING",
      "isReadyForPickup": false,
      "itemCount": 3,
      "pendingAlertCount": 1,
      "createdAt": "2026-10-27T11:30:00Z"
    }
  ],
  "nextCursor": "eyJjIjoiMjAyNi0xMC0yN1QxMToyOTowMFoiLCJpIjoiLi4uIn0"
}
```

`GET /orders?acceptanceStatus=AWAITING_ACCEPTANCE` (faixa "Novos do site"):

```json
{
  "items": [
    {
      "id": "0192d6a0-0000-7000-8000-000000000504",
      "version": 1,
      "orderNumber": null,
      "businessDate": "2026-10-27",
      "orderType": "MARMITA",
      "origin": "WEB",
      "fulfillmentType": "DELIVERY",
      "customerName": "Carla Souza",
      "tableLabel": null,
      "tabNumber": null,
      "totalAmount": "45.00",
      "paidAmount": "0.00",
      "balance": "45.00",
      "isPaid": false,
      "productionStatus": null,
      "cancellationStatus": "ACTIVE",
      "acceptanceStatus": "AWAITING_ACCEPTANCE",
      "acceptanceDeadline": "2026-10-27T12:10:00Z",
      "fulfillmentStatus": null,
      "isReadyForPickup": false,
      "itemCount": 1,
      "pendingAlertCount": 0,
      "createdAt": "2026-10-27T12:00:00Z"
    }
  ],
  "nextCursor": null
}
```

### `GET /orders/{id}` — `orders.view`

→ `200 Order` + `ETag: "4"`.

### `POST /orders` — `orders.create` [I]

Marmita para entrega:

```json
{
  "orderType": "MARMITA",
  "fulfillmentType": "DELIVERY",
  "customerId": "0192d6a0-0000-7000-8000-000000000801",
  "customerName": "João Silva",
  "customerPhone": "+5534999991234",
  "saveCustomer": false,
  "customerAddressId": "0192d6a0-0000-7000-8000-000000000811",
  "deliveryAddress": null,
  "deliveryFee": null,
  "tableLabel": null,
  "tabNumber": null,
  "notes": "Entregar até 12h30",
  "items": [
    {
      "itemKind": "MARMITA",
      "marmitaSizeId": "0192d6a0-0000-7000-8000-000000000202",
      "quantity": 2,
      "mainDishProductIds": ["0192d6a0-0000-7000-8000-000000000101"],
      "sideDishProductIds": ["0192d6a0-0000-7000-8000-000000000111", "0192d6a0-0000-7000-8000-000000000112"],
      "notes": "Sem cebola",
      "addons": [ { "productId": "0192d6a0-0000-7000-8000-000000000131", "quantity": 1, "notes": null } ]
    },
    { "itemKind": "BEVERAGE", "productId": "0192d6a0-0000-7000-8000-000000000121", "quantity": 1, "notes": null }
  ],
  "weighings": []
}
```

Regras do corpo:
- **Endereço:** informe `customerAddressId` **ou** `deliveryAddress` (objeto como no `Order`). O servidor grava o snapshot.
- **Taxa de entrega:** `deliveryFee: null` usa a taxa padrão. Um valor diferente do padrão exige `orders.delivery-fee.edit`.
- **Novo cliente:** `saveCustomer: true` sem `customerId` cria o cliente, com o endereço quando houver, na mesma transação. Se o telefone já existir, o pedido é vinculado ao cliente existente.
- **Cardápio do dia:** pratos e acompanhamentos precisam estar disponíveis no cardápio `MARMITA` da data (`422 ProductUnavailable`).

PF:

```json
{
  "orderType": "PF",
  "fulfillmentType": "DINE_IN",
  "customerId": null,
  "customerName": null,
  "customerPhone": null,
  "saveCustomer": false,
  "customerAddressId": null,
  "deliveryAddress": null,
  "deliveryFee": null,
  "tableLabel": "5",
  "tabNumber": null,
  "notes": null,
  "items": [
    { "itemKind": "PF", "productId": "0192d6a0-0000-7000-8000-000000000101", "quantity": 2, "notes": "Um sem salada", "addons": [] },
    { "itemKind": "BEVERAGE", "productId": "0192d6a0-0000-7000-8000-000000000121", "quantity": 2, "notes": null }
  ],
  "weighings": []
}
```

Quilo:

```json
{
  "orderType": "KILO",
  "fulfillmentType": "DINE_IN",
  "customerId": null,
  "customerName": "Mesa 3",
  "customerPhone": null,
  "saveCustomer": false,
  "customerAddressId": null,
  "deliveryAddress": null,
  "deliveryFee": null,
  "tableLabel": "3",
  "tabNumber": "47",
  "notes": null,
  "items": [ { "itemKind": "BEVERAGE", "productId": "0192d6a0-0000-7000-8000-000000000121", "quantity": 1, "notes": null } ],
  "weighings": [ { "weightGrams": 452 } ]
}
```

→ `201`, `Location: /api/v1/orders/{id}`, `ETag: "1"`, corpo `Order`.

Erros: `403 ModuleNotEnabled` (`orderType` de um módulo que o estabelecimento não tem: `MARMITA` → `MARMITA`, `PF` → `PF`, `KILO` → `KILO`; vem antes do expediente), `409 BusinessDayRequired`, `404 MenuNotFound`, `422 ProductUnavailable`, `422 KiloPriceNotConfigured`, `422 ValidationFailed` (pedido vazio, `itemKind` incompatível com `orderType`, endereço faltando em entrega).

```json
{
  "type": "urn:almoseis:error:module-not-enabled",
  "title": "Este recurso não está habilitado no estabelecimento.",
  "status": 403,
  "instance": "/api/v1/orders",
  "errorCode": "ModuleNotEnabled",
  "traceId": "00-3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f-3c4d5e6f7a8b9c0d-01",
  "module": "PF"
}
```

O pedido criado no app nasce `acceptanceStatus: "ACCEPTED"`, com número, `acceptedAt = createdAt` e, em entrega ou retirada, `fulfillmentStatus: "PENDING"` (ADR-0046 item 1).

### `PATCH /orders/{id}` — `orders.edit` [M]

Altera dados do pedido, não itens:

```json
{
  "customerName": "João Silva",
  "customerPhone": "+5534999991234",
  "fulfillmentType": "PICKUP",
  "deliveryAddress": null,
  "deliveryFee": "0.00",
  "tableLabel": null,
  "tabNumber": null,
  "notes": "Cliente vai buscar"
}
```

Campos ausentes não mudam. Em pedido pago, só `notes`, `tableLabel` e `tabNumber` podem mudar. Os demais campos respondem `409 OrderAlreadyPaid`. Trocar `fulfillmentType` com o atendimento em `OUT_FOR_DELIVERY`, `DELIVERED` ou `PICKED_UP` → `409 OrderAlreadyDispatched`. → `200 Order`.

### `POST /orders/{id}/items` — `orders.edit` [I][M]

Inclui um ou mais itens numa só confirmação (RN-012.3):

```json
{
  "items": [
    { "itemKind": "BEVERAGE", "productId": "0192d6a0-0000-7000-8000-000000000121", "quantity": 2, "notes": null },
    { "itemKind": "ADDON", "parentOrderItemId": "0192d6a0-0000-7000-8000-000000000601", "productId": "0192d6a0-0000-7000-8000-000000000131", "quantity": 1, "notes": null }
  ]
}
```

→ `200 Order`. O efeito colateral (RN-007.10): se algum item exigir preparo e o pedido estiver `COMPLETED`, ele volta para `IN_PROGRESS`. Gera `OrderChangeEvent`. Com o atendimento em `OUT_FOR_DELIVERY`, `DELIVERED` ou `PICKED_UP` → `409 OrderAlreadyDispatched` (ADR-0046 item 19): o atendente cria outro pedido. "Voltou sem entregar" (`PENDING`) libera de novo.

### `PATCH /orders/{id}/items/{itemId}` — `orders.edit` [M]

```json
{ "quantity": 3, "notes": "Sem cebola e sem tomate" }
```

→ `200 Order`.

### `DELETE /orders/{id}/items/{itemId}` — `orders.edit` [M]

Remoção lógica, que leva junto os adicionais filhos. → `200 Order`. Não pode deixar o pedido vazio (`422`).

### `POST /orders/{id}/weighings` — `orders.edit` [I][M]

```json
{ "weighings": [ { "weightGrams": 318 } ] }
```

→ `200 Order`. Só em `KILO`. O preço/kg é o vigente no momento (snapshot).

### `DELETE /orders/{id}/weighings/{weighingId}` — `orders.edit` [M]

→ `200 Order`.

### `PATCH /orders/{id}/status` — `production.update` [M]

```json
{ "productionStatus": "COMPLETED" }
```

→ `200 Order`. Qualquer transição entre os três estados é permitida (RN-007.11), inclusive em pedido pago. `KILO` → `422`.

### `POST /orders/{id}/cancel` — `orders.cancel` [M]

```json
{ "reason": "Cliente desistiu do pedido" }
```

→ `200 Order`. Erros: `409 OrderHasPayments`, `409 OrderCanceled`, `409 OrderNotAccepted` (pedido do site não aceito não é cancelado: ele nunca foi venda; o caminho é a recusa).

Em todas as escritas acima (`PATCH /orders/{id}`, itens, pesagens, status, cancelamento), pedido do site ainda não aceito → `409 OrderNotAccepted`.

### `POST /orders/{id}/accept` — `orders.accept` [I][M]

Aceite do pedido do site (ADR-0046 itens 6 a 9). `If-Match` = versão do pedido.

```json
{ "estimatedReadyMinutes": 30 }
```

`estimatedReadyMinutes`: `15`, `30`, `45`, `60` ou `null` (mostrado ao cliente).

Na mesma transação: `acceptanceStatus: "ACCEPTED"`, o próximo `orderNumber` da data operacional (o mesmo contador dos pedidos do app, ADR-0039), `productionStatus: "OPEN"`, `fulfillmentStatus: "PENDING"`, `acceptedAt`/`acceptedBy` e o vínculo com o cliente pelo telefone (ADR-0044 item 20: cliente existente sem alterar o cadastro; sem cadastro, um novo só com nome e telefone). Para a cozinha, o pedido nasce aqui (`OrderCreated`).

→ `200 Order` + `ETag: "2"`:

```json
{
  "id": "0192d6a0-0000-7000-8000-000000000504",
  "version": 2,
  "orderNumber": 15,
  "origin": "WEB",
  "customer": { "id": "0192d6a0-0000-7000-8000-000000000802", "name": "Carla Souza", "phoneNumber": "+5534988887777" },
  "productionStatus": "OPEN",
  "productionStatusChangedAt": "2026-10-27T12:03:00Z",
  "acceptanceStatus": "ACCEPTED",
  "acceptanceDeadline": "2026-10-27T12:10:00Z",
  "acceptedAt": "2026-10-27T12:03:00Z",
  "acceptedBy": { "id": "0192d6a0-0000-7000-8000-000000000302", "displayName": "Bruno" },
  "estimatedReadyMinutes": 30,
  "fulfillmentStatus": "PENDING",
  "fulfillmentStatusChangedAt": "2026-10-27T12:03:00Z",
  "...": "demais campos do Order"
}
```

Erros:
- `409 VersionConflict` com `current`: outra pessoa aceitou ou recusou antes ("Já aceito por Bruno", item 9). Dois aceites simultâneos com a mesma versão resultam em **um** aceite e **um** número;
- `409 WebOrderExpired`: o prazo venceu (mesmo que o job ainda não tenha expirado o pedido);
- `409 OrderNotAwaitingAcceptance` (extensão `acceptanceStatus`): já aceito, recusado ou desistência;
- `409 BusinessDayRequired`: o expediente do pedido não está aberto;
- `422 ValidationFailed` (`estimatedReadyMinutes` fora da lista).

### `POST /orders/{id}/reject` — `orders.accept` [M]

Recusa com motivo da lista (ADR-0046 item 12). O app avisa: "o cliente verá esta mensagem".

```json
{ "reasonCode": "ITEM_UNAVAILABLE", "message": "A costela acabou." }
```

| `reasonCode` | O que o cliente vê |
|---|---|
| `ITEM_UNAVAILABLE` | "Um item do seu pedido acabou." + `message` |
| `OUT_OF_DELIVERY_AREA` | "Não entregamos nesse endereço." + `message` |
| `TOO_BUSY` | "Estamos com muitos pedidos agora." + `message` |
| `CLOSING_SOON` | "Estamos fechando." + `message` |
| `SUSPECTED_PRANK` | "O restaurante não pôde aceitar seu pedido." (`message` **não** é mostrada) |
| `OTHER` | `message` (obrigatória) |

`message`: opcional, 5 a 200 caracteres; obrigatória em `OTHER`. `BUSINESS_DAY_CLOSED` é só do sistema (finalização do fechamento) e responde `422` aqui.

→ `200 Order` com `acceptanceStatus: "REJECTED"`, `orderNumber: null` e:

```json
{
  "rejection": {
    "reasonCode": "ITEM_UNAVAILABLE",
    "message": "A costela acabou.",
    "rejectedAt": "2026-10-27T12:02:00Z",
    "rejectedBy": { "id": "0192d6a0-0000-7000-8000-000000000303", "displayName": "Carla" }
  }
}
```

Erros: os mesmos do aceite, e `422 ValidationFailed` (`message` faltando em `OTHER` ou fora de 5–200; `reasonCode` `BUSINESS_DAY_CLOSED`).

**Prazo e expiração:** o pedido aguarda **10 minutos** a partir do envio (`acceptanceDeadline`). Um job a cada 30 s passa os vencidos para `EXPIRED` (`expiredAt`, auditoria `SYSTEM`). Aceite e expiração disputam pela `version`: só um grava. **Desistência:** enquanto aguarda aceite, o cliente pode desistir pela página de acompanhamento (`WITHDRAWN`, `withdrawnAt`), rota da página (EP-002 F-208). **Fechamento:** ao finalizar, os ainda aguardando viram `REJECTED` com `BUSINESS_DAY_CLOSED` (§6.11).

### `PATCH /orders/{id}/fulfillment` — `orders.fulfillment.update` [M]

Atendimento de entrega e retirada (ADR-0046 item 18), em pedidos do site **e** do app (uso opcional: sem marcação, o pedido fica `PENDING` e nada é bloqueado).

```json
{ "fulfillmentStatus": "OUT_FOR_DELIVERY", "completeProduction": false }
```

| De | Para | Regra |
|---|---|---|
| `PENDING` | `OUT_FOR_DELIVERY` | só `DELIVERY`; exige `productionStatus: "COMPLETED"` |
| `OUT_FOR_DELIVERY` | `DELIVERED` | — |
| `OUT_FOR_DELIVERY` | `PENDING` | "Voltou sem entregar" |
| `DELIVERED` | `OUT_FOR_DELIVERY` | correção de toque errado |
| `PENDING` | `PICKED_UP` | só `PICKUP`; exige `productionStatus: "COMPLETED"` |
| `PICKED_UP` | `PENDING` | correção de toque errado |

- **"Concluir e marcar"**: `completeProduction: true` conclui a produção e marca "Saiu"/"Retirado" na mesma transação (uma versão só). Exige também `production.update` (`403 PermissionDenied` com `permission: "production.update"`). Sem ele, marcar "Saiu"/"Retirado" com a produção aberta → `409 ProductionNotCompleted`.
- Independente do financeiro: vale em pedido pago ou não; entregue e não pago continua com saldo.
- O mesmo status → `200` sem mudança de versão.

→ `200 Order`:

```json
{
  "id": "0192d6a0-0000-7000-8000-000000000501",
  "version": 5,
  "productionStatus": "COMPLETED",
  "fulfillmentStatus": "OUT_FOR_DELIVERY",
  "fulfillmentStatusChangedAt": "2026-10-27T12:15:00Z",
  "outForDeliveryAt": "2026-10-27T12:15:00Z",
  "deliveredAt": null,
  "pickedUpAt": null,
  "isReadyForPickup": false,
  "...": "demais campos do Order"
}
```

Erros: `409 OrderNotAccepted`, `409 OrderCanceled`, `409 ProductionNotCompleted`, `422 ValidationFailed` (transição fora da tabela, ou pedido `DINE_IN`), `403 PermissionDenied`.

### `GET /orders/{id}/history` — `orders.view`

```json
{
  "items": [
    {
      "occurredAt": "2026-10-27T11:48:00Z",
      "operationType": "OrderItemAdded",
      "origin": "APP",
      "actor": { "id": "0192d6a0-0000-7000-8000-000000000302", "displayName": "Bruno" },
      "before": null,
      "after": { "itemId": "0192d6a0-0000-7000-8000-000000000603", "name": "Coca-Cola 350ml", "quantity": 1, "lineTotal": "6.00", "orderTotal": "55.00" }
    },
    {
      "occurredAt": "2026-10-27T11:31:00Z",
      "operationType": "PaymentRegistered",
      "origin": "APP",
      "actor": { "id": "0192d6a0-0000-7000-8000-000000000303", "displayName": "Carla" },
      "before": { "paidAmount": "0.00" },
      "after": { "paymentId": "0192d6a0-0000-7000-8000-000000000701", "amount": "20.00", "paymentMethod": "PIX", "paidAmount": "20.00" }
    }
  ],
  "nextCursor": null
}
```

Do mais recente para o mais antigo. Inclui itens removidos, pagamentos, estornos, status e cancelamento. Em pedido do site: `WebOrderSubmitted` e `WebOrderWithdrawn` (`origin: "WEB"`, `actor: null`), `OrderAccepted`, `OrderRejected`, `WebOrderExpired`, `WebOrderAutoRejected` e `WebOrderAnonymized` (`origin: "SYSTEM"`; a anonimização é a retenção do ADR-0052), e `OrderFulfillmentChanged` (ADR-0046 item 24).

### Alertas (RF-008)

#### `GET /orders/{id}/alerts` — `orders.view`

Alertas pendentes **para quem chama**:

```json
{
  "items": [
    {
      "eventId": "0192d6a0-0000-7000-8000-000000000b01",
      "changeKind": "ITEM_ADDED",
      "affectsProduction": false,
      "orderVersion": 4,
      "occurredAt": "2026-10-27T11:48:00Z",
      "actor": { "id": "0192d6a0-0000-7000-8000-000000000302", "displayName": "Bruno" },
      "summary": "+1 Coca-Cola 350ml",
      "details": { "itemId": "0192d6a0-0000-7000-8000-000000000603", "name": "Coca-Cola 350ml", "quantityBefore": null, "quantityAfter": 1 }
    }
  ]
}
```

#### `POST /orders/{id}/alerts/acknowledge` — `orders.view`

```json
{ "eventIds": ["0192d6a0-0000-7000-8000-000000000b01"] }
```

→ `204`. A ciência vale só para quem chama (RN-008.7), e repetir não dá erro. Ids de outro pedido → `422` em `eventIds`.

O `summary` é montado no servidor a partir de `details`: `+1 Coca-Cola 350ml`, `−1 Coca-Cola 350ml`, `Marmita M: 2 → 3`, `Obs. de PF Frango grelhado: Sem salada`, `Obs. do pedido: ...`, `+ pesagem de 318 g`, `Pesagem de 318 g removida`, `Entrega ou retirada alterada`, `Pedido cancelado: <motivo>`.

### Produção — `GET /production/orders` — `production.view`

Painel da cozinha (RN-007.4/7.5): só `MARMITA` e `PF` **aceitos** e ativos do expediente aberto, agrupados e do mais antigo para o mais novo **pelo aceite** (`acceptedAt`: o pedido do site nasce para a cozinha no aceite; no app, `acceptedAt = createdAt`). Sem expediente aberto → `409 BusinessDayRequired`.

```json
{
  "businessDayId": "0192d6a0-0000-7000-8000-000000000401",
  "groups": {
    "OPEN": [
      {
        "id": "0192d6a0-0000-7000-8000-000000000502",
        "version": 1,
        "orderNumber": 13,
        "orderType": "PF",
        "origin": "APP",
        "fulfillmentType": "DINE_IN",
        "fulfillmentStatus": null,
        "label": "Mesa 5",
        "notes": null,
        "isPaid": false,
        "createdAt": "2026-10-27T11:50:00Z",
        "acceptedAt": "2026-10-27T11:50:00Z",
        "items": [
          { "id": "0192d6a0-0000-7000-8000-000000000621", "name": "PF Frango grelhado", "composition": null, "quantity": 2, "notes": "Um sem salada", "requiresPreparation": true, "addons": [] },
          { "id": "0192d6a0-0000-7000-8000-000000000622", "name": "Coca-Cola 350ml", "composition": null, "quantity": 2, "notes": null, "requiresPreparation": false, "addons": [] }
        ],
        "pendingAlertCount": 0
      }
    ],
    "IN_PROGRESS": [],
    "COMPLETED": []
  }
}
```

`label` = nome do cliente (marmita) ou mesa/comanda (PF).

---

## 6.10 Pagamentos (RF-009, RF-011, ADR-0021, ADR-0022)

`Payment`:

```json
{
  "id": "0192d6a0-0000-7000-8000-000000000702",
  "orderId": "0192d6a0-0000-7000-8000-000000000501",
  "businessDayId": "0192d6a0-0000-7000-8000-000000000401",
  "businessDate": "2026-10-27",
  "amount": "35.00",
  "paymentMethod": "CASH",
  "cashReceived": "50.00",
  "changeGiven": "15.00",
  "label": null,
  "status": "CONFIRMED",
  "paidAt": "2026-10-27T12:40:00Z",
  "createdBy": { "id": "0192d6a0-0000-7000-8000-000000000303", "displayName": "Carla" },
  "reversal": null
}
```

`reversal` (quando estornado):

```json
{ "id": "0192d6a0-0000-7000-8000-000000000791", "businessDate": "2026-10-28", "reason": "Cobrado em duplicidade", "createdAt": "2026-10-28T10:05:00Z", "createdBy": { "id": "0192d6a0-0000-7000-8000-000000000301", "displayName": "Ana" } }
```

### `POST /orders/{id}/payments` — `payments.register` [I][M]

O `If-Match` é a versão do **pedido**: ninguém quita um total que acabou de mudar.

```json
{ "amount": "35.00", "paymentMethod": "CASH", "cashReceived": "50.00", "label": null }
```

Pix ou cartão: `{ "amount": "20.00", "paymentMethod": "PIX", "cashReceived": null, "label": "Pessoa 1" }`. A divisão de conta é uma sequência destas chamadas, cada uma com sua `Idempotency-Key`.

→ `201`, `Location: /api/v1/payments/{id}`:

```json
{
  "payment": { "...": "Payment" },
  "order": { "id": "0192d6a0-0000-7000-8000-000000000501", "version": 5, "totalAmount": "55.00", "paidAmount": "55.00", "balance": "0.00", "isPaid": true }
}
```

Pagamento de pedido de outro expediente (fiado, RF-011) usa a mesma rota. O pagamento cai no expediente **aberto**.

Erros:
- `409 BusinessDayRequired`;
- `409 OrderCanceled`;
- `409 OrderNotAccepted` (pedido do site não aceito não recebe pagamento manual, ADR-0046 item 4);
- `422 PaymentExceedsBalance` (extensão `balance`);
- `422 PaymentMethodDisabled`;
- `422 ValidationFailed`, para `cashReceived < amount` ou `cashReceived` fora de `CASH`.

### `GET /orders/{id}/payments` — `orders.view`

→ `200 { "items": [Payment] }`, incluindo os estornados.

### `GET /payments/{id}` — `orders.view` → `200 Payment`

### `POST /payments/{id}/reverse` — `payments.reverse` [I][M]

`If-Match` = versão do pedido.

```json
{ "reason": "Cobrado em duplicidade" }
```

→ `201`:

```json
{
  "reversal": {
    "id": "0192d6a0-0000-7000-8000-000000000791",
    "paymentId": "0192d6a0-0000-7000-8000-000000000702",
    "orderId": "0192d6a0-0000-7000-8000-000000000501",
    "businessDayId": "0192d6a0-0000-7000-8000-000000000402",
    "businessDate": "2026-10-28",
    "amount": "35.00",
    "paymentMethod": "CASH",
    "reason": "Cobrado em duplicidade",
    "createdAt": "2026-10-28T10:05:00Z",
    "createdBy": { "id": "0192d6a0-0000-7000-8000-000000000301", "displayName": "Ana" }
  },
  "order": { "id": "0192d6a0-0000-7000-8000-000000000501", "version": 6, "totalAmount": "55.00", "paidAmount": "20.00", "balance": "35.00", "isPaid": false }
}
```

Erros: `409 PaymentAlreadyReversed`, `409 BusinessDayRequired`.

---

## 6.11 Caixa e fechamento (RF-010, ADR-0013, ADR-0014, ADR-0019)

### Movimentações de caixa

`CashMovement`:

```json
{
  "id": "0192d6a0-0000-7000-8000-000000000e01",
  "businessDayId": "0192d6a0-0000-7000-8000-000000000401",
  "movementType": "WITHDRAWAL",
  "amount": "200.00",
  "description": "Sangria para depósito",
  "createdAt": "2026-10-27T15:00:00Z",
  "createdBy": { "id": "0192d6a0-0000-7000-8000-000000000303", "displayName": "Carla" }
}
```

| Método e rota | Permissão | Corpo → resposta |
|---|---|---|
| `POST /closing/cash-movements` [I] | `cash.movement` | `{ "movementType": "SUPPLY", "amount": "50.00", "description": "Troco trazido do cofre" }` → `201 CashMovement` (no expediente aberto) |
| `GET /closing/cash-movements?businessDayId=` | `closing.view` | → `200 { "items": [CashMovement] }` (padrão: expediente aberto) |

### `GET /closing/summary?businessDayId=` — `closing.view`

O fechamento **parcial**, calculado na hora (RN-010.1). Padrão: o expediente aberto. Para expediente fechado, devolve o snapshot (`isFinal: true`). Valores do exemplo do épico:

```json
{
  "businessDay": { "id": "0192d6a0-0000-7000-8000-000000000401", "version": 1, "businessDate": "2026-10-27", "status": "OPEN" },
  "isFinal": false,
  "dailyClosingId": null,
  "computedAt": "2026-10-27T21:55:00Z",
  "orderCount": 96,
  "totalSold": "3650.00",
  "deliveryFeesTotal": "85.00",
  "totalReceived": "3430.00",
  "receivedFromPreviousDays": "35.00",
  "totalPending": "220.00",
  "canceled": { "count": 1, "total": "25.00" },
  "reversed": { "count": 0, "total": "0.00" },
  "webOrders": { "awaitingAcceptanceCount": 2, "autoRejectedCount": 0, "notAcceptedCount": 1 },
  "unmarkedDeliveryCount": 4,
  "byOrderType": [
    { "orderType": "MARMITA", "count": 41, "sold": "1250.00", "received": "1100.00", "pending": "150.00", "reversed": "0.00" },
    { "orderType": "PF", "count": 30, "sold": "870.00", "received": "800.00", "pending": "70.00", "reversed": "0.00" },
    { "orderType": "KILO", "count": 25, "sold": "1530.00", "received": "1530.00", "pending": "0.00", "reversed": "0.00" }
  ],
  "byPaymentMethod": [
    { "paymentMethod": "CASH", "count": 38, "received": "850.00", "reversed": "0.00" },
    { "paymentMethod": "PIX", "count": 44, "received": "1680.00", "reversed": "0.00" },
    { "paymentMethod": "CARD", "count": 21, "received": "900.00", "reversed": "0.00" }
  ],
  "cash": {
    "openingBalance": "100.00",
    "cashReceived": "850.00",
    "cashReversed": "0.00",
    "supplies": "0.00",
    "withdrawals": "200.00",
    "expectedCash": "750.00",
    "countedCash": null,
    "cashDifference": null,
    "justification": null
  },
  "adjustments": []
}
```

- Sem `businessDayId` e sem expediente aberto → `404 NoOpenBusinessDay`.
- `closedAt` e `closedBy` existem nos dois formatos e só são preenchidos no fechamento finalizado.
- `byOrderType` e `byPaymentMethod` sempre trazem as três chaves, mesmo zeradas.
- **Só pedidos aceitos** entram em `orderCount`, vendido, pendente, cancelados e `byOrderType` (ADR-0046 item 4, RF-210). Pedido do site aceito entra como qualquer pedido `MARMITA`.
- `webOrders` é informativo, fora do vendido (ADR-0046 item 13, RF-210.2): `awaitingAcceptanceCount` = pedidos do site do expediente ainda aguardando aceite, que a finalização vai recusar ("2 pedidos do site aguardando aceite serão recusados"); `notAcceptedCount` = recusados, expirados e desistências do expediente. No fechamento finalizado, `awaitingAcceptanceCount` é 0, `autoRejectedCount` diz quantos a finalização recusou, e eles já estão somados em `notAcceptedCount`.
- `unmarkedDeliveryCount`: entregas aceitas e ativas sem `fulfillmentStatus: "DELIVERED"` ("N entregas sem marcação de entregue", ADR-0046 item 20). Só informa: a marcação é opcional.
- `adjustments`: no resumo parcial, os ajustes **registrados** neste expediente; no `DailyClosing`, os que **corrigem** aquele fechamento.
- Toda movimentação de caixa sobe a `version` do expediente (é ela que dá o `409` da finalização). Pagamentos não sobem: a finalização trava o expediente e calcula com o que já foi confirmado.

`byOrderType[].received` agrupa os pagamentos recebidos **neste** expediente pelo tipo do pedido. Por isso o "recebido" de marmita pode incluir fiado de dias anteriores (`receivedFromPreviousDays`). Para o exemplo fechar com o épico, os R$ 35 de fiado estão contidos nos R$ 1.100 de marmita.

### `POST /closing/finalize` — `closing.finalize` [I][M]

`If-Match` = versão do `BusinessDay`.

```json
{ "businessDayId": "0192d6a0-0000-7000-8000-000000000401", "countedCash": "740.00", "justification": "Troco dado a mais em uma entrega; conferido com o entregador." }
```

→ `201`, `Location: /api/v1/closing/{id}`, corpo `DailyClosing`: o mesmo formato do summary, com `isFinal: true`, `dailyClosingId`, `cash.countedCash: "740.00"`, `cash.cashDifference: "-10.00"`, `cash.justification`, `closedAt` e `closedBy`. O expediente passa a `CLOSED`.

Na mesma transação, os pedidos do site do expediente ainda `AWAITING_ACCEPTANCE` passam a `REJECTED` com `reasonCode: "BUSINESS_DAY_CLOSED"` (sem funcionário; auditoria `SYSTEM`; evento `OrderRejected`), e `webOrders.autoRejectedCount` diz quantos. O fechamento **não** fica bloqueado por eles (ADR-0046 item 13).

Erros: `422 JustificationRequired`, `409 ClosingAlreadyFinalized`, `409 VersionConflict` (uma movimentação aconteceu enquanto a tela estava aberta, então o app precisa recarregar o resumo e confirmar de novo).

### Histórico

| Método e rota | Permissão | Resposta |
|---|---|---|
| `GET /closing/history?from=2026-10-01&to=2026-10-31&limit=31` | `closing.view` | `200 { "items": [ { "dailyClosingId", "businessDayId", "businessDate", "totalSold", "totalReceived", "totalPending", "cashDifference", "closedAt", "closedBy", "adjustmentCount" } ], "nextCursor": null }` |
| `GET /closing/{id}` | `closing.view` | `200 DailyClosing` + `adjustments` |
| `GET /closing/monthly?month=2026-10` | `closing.view` | ver abaixo |

`GET /closing/monthly?month=2026-10` (soma dos fechamentos finalizados do mês, RN-010 "histórico mensal"):

```json
{
  "month": "2026-10",
  "closingCount": 26,
  "totalSold": "91250.00",
  "totalReceived": "88940.00",
  "totalPendingAtClosings": "4120.00",
  "cashDifferenceTotal": "-37.50",
  "byOrderType": [
    { "orderType": "MARMITA", "sold": "31250.00", "received": "30100.00" },
    { "orderType": "PF", "sold": "21750.00", "received": "21050.00" },
    { "orderType": "KILO", "sold": "38250.00", "received": "37790.00" }
  ],
  "byPaymentMethod": [
    { "paymentMethod": "CASH", "received": "21250.00" },
    { "paymentMethod": "PIX", "received": "45390.00" },
    { "paymentMethod": "CARD", "received": "22300.00" }
  ],
  "days": [
    { "businessDate": "2026-10-27", "dailyClosingIds": ["0192d6a0-0000-7000-8000-000000000a01"], "totalSold": "3650.00", "totalReceived": "3430.00" }
  ]
}
```

### `POST /closing/{id}/adjustments` — `closing.adjust` [I]

```json
{ "adjustmentType": "CASH_COUNT_CORRECTION", "amount": "10.00", "reason": "Nota de R$ 10 encontrada no fundo da gaveta no dia seguinte" }
```

→ `201`:

```json
{
  "id": "0192d6a0-0000-7000-8000-000000000f01",
  "relatedClosingId": "0192d6a0-0000-7000-8000-000000000a01",
  "businessDayId": "0192d6a0-0000-7000-8000-000000000402",
  "adjustmentType": "CASH_COUNT_CORRECTION",
  "amount": "10.00",
  "reason": "Nota de R$ 10 encontrada no fundo da gaveta no dia seguinte",
  "createdAt": "2026-10-28T10:00:00Z",
  "createdBy": { "id": "0192d6a0-0000-7000-8000-000000000301", "displayName": "Ana" }
}
```

O fechamento original **não muda** (ADR-0014). O ajuste aparece em `adjustments` dele.

---

## 6.12 Sincronização e operações (ADR-0032, ADR-0033)

### `GET /sync/changes?since=1040&limit=500` — autenticado

```json
{
  "changes": [
    { "seq": 1041, "entityType": "ORDER", "entityId": "0192d6a0-0000-7000-8000-000000000501", "entityVersion": 5, "changeKind": "UPSERT" },
    { "seq": 1042, "entityType": "PAYMENT", "entityId": "0192d6a0-0000-7000-8000-000000000702", "entityVersion": 1, "changeKind": "UPSERT" },
    { "seq": 1043, "entityType": "MENU", "entityId": "0192d6a0-0000-7000-8000-000000000901", "entityVersion": 3, "changeKind": "UPSERT" }
  ],
  "lastSeq": 1043,
  "hasMore": false
}
```

- Compactado: só a última alteração de cada entidade no intervalo.
- `since=0` inicia a sincronização do zero.
- Cursor com mais de 7 dias → `410 SyncCursorExpired` com a extensão `"currentSeq": 1043`. O app descarta o cache, recarrega o que estiver na tela e passa a usar `since=1043`.
- Só lista o que quem chama pode ler.

| `entityType` | Como o app atualiza |
|---|---|
| `TENANT` | `GET /auth/me` (nome, comportamentos, rótulos e módulos; ADR-0054 item 15) e, na tela "Dados do estabelecimento", `GET /tenant`. Todos os perfis recebem |
| `TENANT_SETTINGS` | `GET /tenant/settings` |
| `EMPLOYEE` | `GET /auth/me` (se for o próprio) / `GET /employees/{id}` |
| `PRODUCT` | `GET /products/{id}` |
| `MARMITA_SIZE` | `GET /marmita-sizes` |
| `DISH_CATEGORY`, `DISH`, `BEVERAGE_TYPE`, `COMBO`, `ADDON_GROUP` | `GET /fixed-menu` (cardápio fixo inteiro: pausar um prato muda a disponibilidade dos combos sem entrada `COMBO`) |
| `MENU` | `GET /menus/{id}` |
| `CUSTOMER` | `GET /customers/{id}` |
| `ORDER` | `GET /orders/{id}` |
| `PAYMENT`, `PAYMENT_REVERSAL` | `GET /payments/{id}` (e o pedido correspondente) |
| `BUSINESS_DAY` | `GET /business-days/current` |
| `CASH_MOVEMENT`, `DAILY_CLOSING`, `CLOSING_ADJUSTMENT` | `GET /closing/summary` |

### `GET /operations/{idempotencyKey}` — autenticado

Resolve operações de resultado desconhecido (RN-014.12):

```json
{
  "key": "5b8f0d6e-2c1a-4f7e-9b3d-8a6c4e2f1d0b",
  "status": "COMPLETED",
  "endpoint": "POST /api/v1/orders",
  "responseStatus": 201,
  "resourceId": "0192d6a0-0000-7000-8000-000000000501",
  "createdAt": "2026-10-27T11:30:00Z",
  "completedAt": "2026-10-27T11:30:01Z"
}
```

`status` pode ser `NOT_FOUND` (nunca chegou, então é seguro reenviar com a mesma chave), `IN_PROGRESS` (consultar de novo em instantes) ou `COMPLETED` (buscar `resourceId`). A resposta é sempre `200`. Só enxerga as chaves do próprio usuário.

### Comportamento do reenvio idempotente

Uma repetição de `POST /orders` com a mesma chave e o mesmo corpo devolve **a resposta original** (`201`, mesmo corpo), com o header `Idempotent-Replayed: true`. "Mesmo corpo" significa mesmos campos e valores: a ordem das chaves pode mudar, e `Location`/`ETag` não são repetidos. A mesma chave enviada por outro usuário ou para outra rota também responde `422 IdempotencyKeyReused`.

---

## 6.13 Auditoria — `GET /audit-logs` — `audit.view`

`?entityType=PAYMENT&entityId=&actorEmployeeId=&from=2026-10-01T00:00:00Z&to=2026-10-31T23:59:59Z&limit=50&cursor=`

```json
{
  "items": [
    {
      "id": "0192d6a0-0000-7000-8000-00000000aa01",
      "occurredAt": "2026-10-28T10:05:00Z",
      "origin": "APP",
      "operationType": "PaymentReversed",
      "entityType": "PAYMENT",
      "entityId": "0192d6a0-0000-7000-8000-000000000702",
      "orderId": "0192d6a0-0000-7000-8000-000000000501",
      "actor": { "id": "0192d6a0-0000-7000-8000-000000000301", "displayName": "Ana" },
      "sharedDevice": null,
      "before": { "status": "CONFIRMED", "amount": "35.00" },
      "after": { "status": "REVERSED", "reason": "Cobrado em duplicidade" },
      "correlationId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
    }
  ],
  "nextCursor": null
}
```

---

## 6.14 Tempo real — SignalR (ADR-0009, ADR-0031)

- **Hub:** `wss://{host}/hubs/realtime?access_token={accessToken}` (o mesmo JWT; o app envia `X-App-Version` como query `appVersion`).
- **Na web** (ADR-0053): o cliente usa `accessTokenFactory` (o token vai na query `access_token`, como no mobile) e envia `appVersion` e `appPlatform=WEB` na query. O `negotiate` passa pelo CORS (§6.3, "App no navegador"). A conexão confere o `Origin`: presente, diferente do próprio host da API e fora de `Cors:RestaurantAppOrigins` → `403 OriginNotAllowed`. Sem `Origin` (clientes nativos) passa.
- **Grupos**, atribuídos pelo servidor na conexão:
  - `tenant:{id}:orders` — `orders.view`;
  - `tenant:{id}:production` — `production.view`;
  - `tenant:{id}:closing` — `closing.view`;
  - `tenant:{id}:catalog` — todos;
  - `employee:{id}` — o próprio funcionário.
- **Método cliente → servidor:** nenhum no MVP. A conexão só recebe.
- **Mensagem servidor → cliente:** o método é sempre `event`, com envelope:

```json
{
  "eventId": "0192d6a0-0000-7000-8000-0000000ee001",
  "eventType": "OrderUpdated",
  "seq": 1041,
  "occurredAt": "2026-10-27T11:48:00Z",
  "entityType": "ORDER",
  "entityId": "0192d6a0-0000-7000-8000-000000000501",
  "entityVersion": 5,
  "actorEmployeeId": "0192d6a0-0000-7000-8000-000000000302",
  "data": { "orderNumber": 12, "orderType": "MARMITA", "productionStatus": "IN_PROGRESS", "isPaid": false }
}
```

| `eventType` | Grupo | `data` |
|---|---|---|
| `OrderCreated` | orders, production (exceto KILO) | `orderNumber`, `orderType`, `label`. Pedido do site: emitido **no aceite**, com `label: null` (evento sem dado pessoal, ADR-0046 item 23) |
| `OrderAwaitingAcceptance` | orders | `fulfillmentType`, `marmitaCount`, `totalAmount`, `acceptanceDeadline` (pedido do site chegou; sem nome, telefone nem endereço) |
| `OrderAccepted` | orders | `orderNumber`, `acceptedBy` (`{ "id", "displayName" }`; `null` no aceite do sistema, Fase 2) |
| `OrderRejected` | orders | `acceptanceStatus` (`REJECTED`/`EXPIRED`/`WITHDRAWN`), `reasonCode` (`null` em `EXPIRED`/`WITHDRAWN`) |
| `OrderFulfillmentChanged` | orders, production | `orderNumber`, `from`, `to` |
| `OrderUpdated` | orders, production | `orderNumber`, `orderType`, `productionStatus`, `isPaid` |
| `OrderStatusChanged` | orders, production | `orderNumber`, `from`, `to` |
| `OrderCanceled` | orders, production | `orderNumber` |
| `OrderPaymentRegistered` | orders, closing | `orderNumber`, `paidAmount`, `balance` |
| `OrderPaymentConfirmed` | orders, production, closing | `orderNumber` (quitou) |
| `OrderPaymentReversed` | orders, production, closing | `orderNumber`, `isPaid` |
| `OrderAlertCreated` | orders, production | `orderNumber`, `changeKind`, `affectsProduction` |
| `OrderAlertAcknowledged` | `employee:{id}` | `eventIds` |
| `MenuUpdated` | catalog | `menuType`, `date` |
| `CatalogUpdated` | catalog | `entityType` (`PRODUCT`/`MARMITA_SIZE`/`TENANT_SETTINGS`; cardápio fixo: `DISH_CATEGORY`/`DISH`/`BEVERAGE_TYPE`/`COMBO`/`ADDON_GROUP`, e o app recarrega o `GET /fixed-menu`) |
| `BusinessDayOpened` | todos do tenant | `businessDate` |
| `ClosingUpdated` | closing | — (recarregar o summary; agrupado no servidor, no máximo 1 a cada 5 s) |
| `ClosingFinalized` | todos do tenant | `businessDate`, `dailyClosingId` |
| `AccessRevoked` | `employee:{id}` | `reason` (`DEACTIVATED`/`ROLES_CHANGED`/`DEVICE_REVOKED`): o app renova o token ou sai |
| `SubscriptionStatusChanged` | todos do tenant | `status`, `canWrite` |
| `TenantModulesChanged` | todos do tenant | `behaviors`, `modules` (o admin acrescentou um comportamento, ADR-0054 item 15). `entityType: "TENANT"`, `actorEmployeeId: null`. O app recarrega o `GET /auth/me` e remonta as abas (ADR-0055) |

O evento **não** é estado. O app busca o recurso pela API (§6.12). Quando `actorEmployeeId` é o próprio usuário, o app ignora o alerta (RN-008.6). Um salto de `seq` dispara `GET /sync/changes`.

O pedido do site também grava no outbox a mensagem interna `WebOrderPushRequested` (o push e o lembrete agendado, §6.3 "Push dos pedidos do site"). Ela **não** vai para o SignalR.

---

## 6.15 Administração e webhooks (ADR-0017, ADR-0025)

### `/admin/*` — só `IsPlatformAdmin` (token de `POST /auth/admin/login`, sem tenant, ADR-0043)

| Método e rota | Corpo → resposta |
|---|---|
| `GET /admin/tenants?status=PAST_DUE&search=&limit=50&cursor=` | → `200 { "items": [ { "id", "name", "documentNumber", "createdAt", "subscription": { "status", "trialEndsAt", "currentPeriodEndsAt" }, "activeEmployeeCount", "lastOrderAt" } ], "nextCursor": null }` |
| `GET /admin/tenants/{id}` | → `200` (mesmo item, com `ordersLast30Days` e `openBusinessDay`, **sem** dados de pedidos ou clientes) |
| `POST /admin/tenants/{id}/extend-trial` | `{ "days": 7, "reason": "Instalação atrasada no piloto" }` → `200 Subscription` |
| `POST /admin/tenants/{id}/suspend` | `{ "reason": "Fraude confirmada" }` → `200 Subscription` |
| `POST /admin/tenants/{id}/reactivate` | `{ "reason": "Pagamento confirmado manualmente" }` → `200 Subscription` |
| `POST /admin/tenants/{id}/behaviors` | `{ "behavior": "SNACK_BAR", "reason": "Contratou a lanchonete à noite" }` → `200 TenantBehaviors` |
| `POST /admin/billing-webhooks/{id}/reprocess` | → `202` |

Toda ação gera auditoria com `origin = PLATFORM_ADMIN`.

- `Subscription` = o mesmo objeto de `GET /subscription` (§6.4).
- `search` procura no nome e no documento; `status` filtra pela situação da assinatura; mais recentes primeiro.
- `openBusinessDay`: `{ "id", "businessDate", "openedAt" }` ou `null`. `lastOrderAt` e `ordersLast30Days` contam todos os pedidos.
- `extend-trial` (`days` de 1 a 60): em `TRIALING`, soma ao fim do teste; em `SUSPENDED` sem nenhum período pago, volta a `TRIALING` por `days` a partir de agora; nos demais estados, `422`.
- `reactivate`: `ACTIVE` até agora + 1 mês (pagamento confirmado fora do gateway); assinatura `CANCELED` → `422`.
- `reason` com pelo menos 5 caracteres. Tenant inexistente → `404`.
- `reprocess`: volta o evento para a fila (`processed_at = null`); o processador o aplica de novo, de forma idempotente.
- `behaviors` (ADR-0054 item 5, EP-003 F-305): acrescenta um comportamento ao tenant, mediante aumento na assinatura combinado fora do app (Q-301). Remover não existe nesta fase. Os rótulos não mudam (o proprietário marca os do comportamento novo em "Dados do estabelecimento"). Comportamento já habilitado → `200` com o estado atual, sem auditoria nem evento. `behavior` ausente ou `reason` com menos de 5 caracteres → `422`; código fora da lista → `400`. Grava `tenant_behaviors` com `origin = PLATFORM_ADMIN` e o administrador em `enabled_by_user_id`, sobe a `version` do tenant, gera a auditoria `TenantBehaviorAdded` (`PLATFORM_ADMIN`, ator = o administrador), a entrada `TENANT` no feed e o evento `TenantModulesChanged` (§6.14), na mesma transação.

`TenantBehaviors`:

```json
{
  "tenantId": "0192d6a0-0000-7000-8000-000000000001",
  "behaviors": ["RESTAURANT", "SNACK_BAR"],
  "labels": ["MARMITARIA"],
  "modules": ["MARMITA", "PF", "KILO", "DAILY_MENU", "A_LA_CARTE", "DELIVERY", "FIXED_MENU"]
}
```

### `POST /webhooks/asaas` — público + header `asaas-access-token`

Token ausente ou errado → `401 Unauthenticated`; corpo sem `id` ou `event` → `400 MalformedRequest`. O corpo é o do Asaas, repassado como veio. O servidor valida o token, grava `billing_webhook_events` (único por `gateway_event_id`) e responde `200` **antes** de processar: o processamento é assíncrono e idempotente. Os eventos tratados no MVP são `PAYMENT_CONFIRMED`, `PAYMENT_RECEIVED`, `PAYMENT_OVERDUE`, `SUBSCRIPTION_DELETED` e `SUBSCRIPTION_INACTIVATED`. Os demais são gravados e ignorados.

### Saúde — públicos

`GET /health/live` → `200 { "status": "Healthy" }`. `GET /health/ready` → `200`/`503` com `{ "status": "Healthy", "checks": { "database": "Healthy", "outbox": "Healthy" } }`. Ficam fora de `/api/v1`.

---

## 6.16 Headers de exemplo (pedido de ponta a ponta)

```text
POST /api/v1/orders/0192d6a0-0000-7000-8000-000000000501/payments HTTP/1.1
Authorization: Bearer eyJhbGciOiJSUzI1NiIs...
X-App-Version: 1.4.0
X-App-Platform: ANDROID
Idempotency-Key: 9e1c2b7a-4d3f-4a8e-b6c5-0f2e1d3c4b5a
If-Match: "4"
Content-Type: application/json

HTTP/1.1 201 Created
Location: /api/v1/payments/0192d6a0-0000-7000-8000-000000000702
ETag: "5"
Content-Type: application/json
```

O `ETag` da resposta de pagamento é a **nova versão do pedido**, para o app usar no próximo `If-Match`.

## 6.17 Mapeamento com o §7 do épico

| Épico | Aqui |
|---|---|
| `POST /auth/login`, `GET /auth/me` | iguais; mais `signup`, `refresh`, `logout`, `switch-tenant`, `pin-login`, `password/*` |
| `POST /employees/invitations`, `PATCH /employees/{id}` | iguais; mais `deactivate`/`reactivate` explícitos |
| `PATCH /menus/{id}` | `PUT /menus/{id}/items` + `PATCH /menus/{id}/items/{itemId}` |
| `POST /menus/{id}/clone` | igual (com `kind`/`date`/`dayOfWeek`) |
| `PATCH /orders/{id}` | igual, só dados do pedido; itens em sub-recursos |
| `POST /orders/{id}/alerts/acknowledge` | igual, com `eventIds` |
| `GET /closing/summary`, `/history`, `/finalize`, `/cash-movements`, `/{id}`, `/{id}/adjustments` | iguais; mais `/closing/monthly` |
| `GET /sync/changes`, `GET /operations/{id}` | iguais; o id da operação é a `Idempotency-Key` |
| — | novos (ADR-0053, web): `GET /auth/shared-device/employees`, `DELETE /auth/shared-device` |
| — | novos (EP-002, ADR-0046): `POST /orders/{id}/accept`, `POST /orders/{id}/reject`, `PATCH /orders/{id}/fulfillment`; filtro `acceptanceStatus` em `GET /orders` |
| — | novos (EP-002 fatia 2, ADR-0044/0045/0047): `GET`/`PUT /storefront` (§6.18); `/public/stores/{slug}`, `/public/stores/{slug}/orders`, `/public/stores/{slug}/delivery-quotes`, `/public/tracking/{trackingToken}`, `/public/tracking/{trackingToken}/withdraw` (§6.19) |
| — | novos: `/business-days`, `/customers`, `/marmita-sizes`, `/production/orders`, `/devices/shared`, `/subscription`, `/roles`, `/audit-logs`, `/admin/*`, `/webhooks/asaas` |

---

## 6.18 Página de pedidos: configuração pelo restaurante (EP-002 F-210; ADR-0044, ADR-0048)

A página nasce **inativa** no autocadastro, com o slug gerado do nome já reservado (ADR-0044 itens 11 e 12). Os tenants anteriores à migration 0004 receberam a mesma coisa (05 §5.17).

### `GET /storefront` — `orders.view`

```json
{
  "version": 3,
  "slug": "marmitaria-da-ana",
  "previousSlugs": ["marmitaria-ana"],
  "publicName": "Marmitaria da Ana",
  "isPageActive": true,
  "isAcceptingOrders": true,
  "isDeliveryEnabled": true,
  "offersDelivery": true,
  "offersPickup": true,
  "contactPhone": "+5534999990000",
  "hoursText": "Seg a sáb, 10h30 às 14h",
  "address": { "street": "Rua das Flores", "number": "123", "complement": null, "neighborhood": "Centro", "city": "Uberlândia", "state": "MG", "zipCode": "38400000" },
  "location": { "latitude": -18.918600, "longitude": -48.277200, "isConfirmed": true },
  "delivery": { "feeMode": "DISTANCE", "pricePerKm": "0.80", "minimumFee": "3.00", "maxDistanceKm": "6.0" },
  "pageDelivery": { "available": true, "missing": [] },
  "status": "OPEN",
  "updatedAt": "2026-10-27T09:30:00Z"
}
```

- `slug`: o atual. `previousSlugs`: os antigos do mesmo tenant, que redirecionam para o atual (máximo 5, ADR-0044 item 15).
- `status`: o estado que a página mostra agora (`OPEN`, `CLOSED`, `PAUSED`, `UNAVAILABLE`, §6.19), para o app exibir "Sua página está fechada agora".
- `location` (ADR-0048 item 3): o ponto do restaurante, latitude/longitude como **número** com 6 casas. Ao salvar um endereço completo, a API o geocodifica **uma vez** e devolve o ponto com `isConfirmed: false`; o app mostra no mapa e o proprietário confirma ou arrasta o marcador (PUT com `isConfirmed: true`). `null` = sem ponto (endereço incompleto, não encontrado, provedor fora do ar ou `Maps:Provider = None`): o app deixa marcar no mapa.
- `delivery.feeMode`: `DISTANCE` (rota × `pricePerKm`, mínimo `minimumFee`, raio opcional `maxDistanceKm`) ou `FIXED` (a `defaultDeliveryFee` do estabelecimento, §6.4, sem mapa). `maxDistanceKm`: string com uma casa (`"6.0"`) ou `null`.
- `pageDelivery`: se a página oferece entrega agora e, se não, o que falta, na ordem: `DELIVERY_OFF` (`offersDelivery` ou `isDeliveryEnabled` falso), `PRICE_PER_KM`, `CONFIRMED_LOCATION`, `MAPS_PROVIDER` (a API está sem provedor de mapas). Os três últimos só no modo `DISTANCE`. Salvar com algo faltando **não** é erro: a entrega só não aparece na página (`deliveryAvailable: false`), e o app explica com esta lista.

Exemplo logo depois de salvar o endereço:

```json
{ "location": { "latitude": -18.918512, "longitude": -48.277003, "isConfirmed": false }, "pageDelivery": { "available": false, "missing": ["CONFIRMED_LOCATION"] } }
```

### `PUT /storefront` — `storefront.manage` [M]

Substitui a configuração inteira (as chaves rápidas "pausar pedidos" e "pausar entrega" mandam o objeto todo com `isAcceptingOrders`/`isDeliveryEnabled` trocados). Resposta: `200` com o mesmo objeto do `GET`.

```json
{
  "slug": "marmitaria-da-ana",
  "publicName": "Marmitaria da Ana",
  "isPageActive": true,
  "isAcceptingOrders": true,
  "isDeliveryEnabled": true,
  "offersDelivery": true,
  "offersPickup": true,
  "contactPhone": "+5534999990000",
  "hoursText": "Seg a sáb, 10h30 às 14h",
  "address": { "street": "Rua das Flores", "number": "123", "complement": null, "neighborhood": "Centro", "city": "Uberlândia", "state": "MG", "zipCode": "38400-000" },
  "delivery": { "feeMode": "DISTANCE", "pricePerKm": "0.80", "minimumFee": "3.00", "maxDistanceKm": "6.0" },
  "location": { "latitude": -18.918600, "longitude": -48.277200, "isConfirmed": true }
}
```

**`location` no PUT** (compatível com o app da fatia 2, que não envia o campo):

| Corpo | Efeito |
|---|---|
| sem `location` (ou `"location": null`) | mantém o ponto salvo e a confirmação. Não existe "apagar o ponto" pelo PUT |
| `location` com `isConfirmed: true` | grava o ponto (arredondado a 6 casas) como confirmado. Reenviar o mesmo ponto confirmado não muda nada (nem a versão) |
| `location` com `isConfirmed: false` | grava o ponto sem confirmação |
| `address` diferente do salvo | **exceção**: o ponto salvo e o `location` enviado são descartados, a API geocodifica o endereço novo e devolve `isConfirmed: false`. Confirme com um segundo PUT |

Regras (`422 ValidationFailed`, com o campo em `errors`):

| Campo | Regra |
|---|---|
| `slug` | `a-z`, `0-9` e `-`; 3 a 40; sem `-` no começo, no fim ou repetido; não só números; fora da lista de reservados (`admin`, `pedidos`, `restaurantes`…, ADR-0044 item 16) e de palavras ofensivas; livre (nem atual nem antigo de outro tenant). Maiúsculas viram minúsculas. Trocar guarda o anterior (redireciona); voltar a um antigo do próprio tenant o reativa; depois de 5 trocas, só pelo suporte |
| `isPageActive: true` | exige `address` com rua, número, bairro, cidade e UF, e pelo menos um tamanho de marmita ativo **com `sellsOnSite: true`** (ADR-0044 item 11, ADR-0057 item 3) |
| `offersDelivery`, `offersPickup` | pelo menos um verdadeiro |
| `publicName` | 2 a 80 |
| `contactPhone` | E.164 ou `null` |
| `address.state` / `address.zipCode` | UF com 2 letras / CEP com 8 dígitos (com ou sem hífen) |
| `delivery.pricePerKm` / `minimumFee` / `maxDistanceKm` | `> 0` ou `null` / `≥ 0` / `"0.1"` a `"999.9"` ou `null` |
| `location.latitude` / `location.longitude` | `-90` a `90` / `-180` a `180` |

Auditoria `StorefrontUpdated` (sem telefone de contato, endereço nem coordenadas; leva `hasLocation` e `locationConfirmed`). `If-Match` antigo → `409 VersionConflict` com o objeto atual em `current`.

---

## 6.19 Página de pedidos: rotas públicas (EP-002 F-201 a F-204, F-208, F-212; ADR-0044, ADR-0045, ADR-0047, ADR-0052)

Rotas **anônimas**, sem `Authorization` (se vier, é ignorado) e sem `X-App-Version`. O tenant é resolvido no servidor pelo `{slug}` ou pelo `{trackingToken}`, e a partir daí valem o query filter e a RLS do tenant (ADR-0045 itens 3 a 6). Dinheiro, enums e datas seguem §6.0.

| Rota | Limite (ADR-0045 item 11) |
|---|---|
| `GET /public/stores/{slug}` | `public-read`: 120/min por IP. O servidor da página (ADR-0051) com `X-Web-Renderer-Key` correto fica fora do limite |
| `POST /public/stores/{slug}/orders` [I] | `public-order`: 5/min e 20/h por IP |
| `POST /public/stores/{slug}/delivery-quotes` | `public-quote`: 10/min e 60/dia por IP; 300/dia por **estabelecimento** (contado depois de o slug virar tenant: slug antigo e maiúsculas contam na mesma cota; `RateLimiting:PublicQuotesPerStorePerDay`) |
| `GET /public/tracking/{trackingToken}` | `public-tracking`: 60/min por IP |
| `POST /public/tracking/{trackingToken}/withdraw` | `public-order` |

Excesso → `429 TooManyRequests` com `Retry-After`.

**Slug:** inexistente → `404 StoreNotFound`. Slug antigo (ou com maiúsculas) → `301` (GET) ou `308` (POST, preserva método e corpo) com `Location` no slug atual, mantendo o resto do caminho e a query:

```text
GET /api/v1/public/stores/marmitaria-ana HTTP/1.1

HTTP/1.1 301 Moved Permanently
Location: /api/v1/public/stores/marmitaria-da-ana
```

**CORS** (`Cors:PublicPageOrigins`, separado do app do restaurante, ADR-0053 §4.6):

| Item | Valor |
|---|---|
| Origens | `Cors:PublicPageOrigins` por ambiente, com as mesmas regras de formato de `RestaurantAppOrigins` (§6.3). Development: `http://localhost:3000`. Homologação/produção: domínio da página (a definir, ADR-0051) |
| Credenciais | **nenhuma** (sem `Access-Control-Allow-Credentials`; a página não tem cookie nem sessão) |
| Métodos | `GET`, `POST` |
| Headers permitidos | `Content-Type`, `Idempotency-Key`, `X-Turnstile-Token`, `If-None-Match` |
| Headers expostos | `ETag`, `Location`, `Retry-After`, `Idempotent-Replayed` |
| `Access-Control-Max-Age` | `600` |
| Rotas | só `/api/v1/public/*`, inclusive nas respostas de erro |

### `GET /public/stores/{slug}` — público

Perfil, estado e cardápio do dia. Estado calculado a cada requisição (ADR-0044 item 6), nesta ordem:

| `status` | Condição | Cardápio |
|---|---|---|
| `UNAVAILABLE` | página inativa, ou assinatura `SUSPENDED`/`CANCELED` (o motivo não aparece) | não; só `slug`, `status`, `name` e `limits` |
| `CLOSED` | sem expediente aberto | não |
| `PAUSED` | `isAcceptingOrders = false` | sim (para consulta; o envio é recusado) |
| `OPEN` | os demais casos | sim |

```json
{
  "slug": "marmitaria-da-ana",
  "status": "OPEN",
  "name": "Marmitaria da Ana",
  "address": { "street": "Rua das Flores", "number": "123", "complement": null, "neighborhood": "Centro", "city": "Uberlândia", "state": "MG" },
  "contactPhone": "+5534999990000",
  "hoursText": "Seg a sáb, 10h30 às 14h",
  "pickupAvailable": true,
  "deliveryAvailable": true,
  "deliveryRule": { "feeMode": "DISTANCE", "pricePerKm": "0.80", "minimumFee": "3.00", "maxDistanceKm": "6.0", "fixedFee": null },
  "privacy": { "controllerName": "Marmitaria da Ana", "controllerPhone": "+5534999990000", "operatorName": "AlmoSeis", "thirdParties": ["ANTI_BOT", "MAPS"] },
  "businessDate": "2026-10-27",
  "menu": {
    "marmitaSizes": [
      { "id": "0192d6a0-0000-7000-8000-000000000201", "name": "P", "price": "16.00" },
      { "id": "0192d6a0-0000-7000-8000-000000000202", "name": "M", "price": "20.00" }
    ],
    "mainDishes": [
      { "productId": "0192d6a0-0000-7000-8000-000000000101", "name": "Frango grelhado", "description": "Peito de frango grelhado com ervas", "imageUrl": null, "isAvailable": true },
      { "productId": "0192d6a0-0000-7000-8000-000000000102", "name": "Costela", "description": null, "imageUrl": null, "isAvailable": false }
    ],
    "sideDishes": [
      { "productId": "0192d6a0-0000-7000-8000-000000000111", "name": "Arroz", "description": null, "imageUrl": null, "isAvailable": true },
      { "productId": "0192d6a0-0000-7000-8000-000000000112", "name": "Feijão", "description": null, "imageUrl": null, "isAvailable": true }
    ],
    "addons": [
      { "productId": "0192d6a0-0000-7000-8000-000000000131", "name": "Ovo frito", "description": null, "imageUrl": null, "price": "2.00", "isAvailable": true }
    ],
    "beverages": [
      { "productId": "0192d6a0-0000-7000-8000-000000000121", "name": "Coca-Cola 350ml", "description": null, "imageUrl": null, "price": "6.00", "isAvailable": true }
    ]
  },
  "limits": { "maxMarmitas": 20, "maxLines": 30, "maxQuantityPerLine": 20, "notesMaxLength": 200 }
}
```

- `menu`: o `DAILY` de `MARMITA` da `businessDate` (ADR-0038) + tamanhos ativos **que vendem no site** + adicionais e bebidas ativos com `isVisibleOnPage`. **Todo preço é o de site** (EP-003, ADR-0057 item 6): `marmitaSizes[].price` = `sitePrice` do tamanho; `beverages[].price` = `sitePrice` da bebida (o `defaultPrice` só para bebida ainda sem preço de site, §6.5); `addons[].price` = `defaultPrice` (Q-308). O envio recalcula com os mesmos preços, e o pedido nasce com `priceList: "SITE"`. `isAvailable: false` → "Acabou" (desabilitado). Sem cardápio publicado → `menu: null`. PF e quilo nunca aparecem.
- `pickupAvailable` / `deliveryAvailable`: o que a página oferece agora. `deliveryAvailable` = entrega ligada (`offersDelivery` e `isDeliveryEnabled`) e, no modo `DISTANCE`, preço por km, ponto do restaurante confirmado e provedor de mapas configurado na API (o mesmo que `pageDelivery.available`, §6.18). Uma falha momentânea do provedor **não** muda este campo: a cotação responde `422 DeliveryUnavailable` e a página oferece retirada. `deliveryRule` (só com `deliveryAvailable: true`): `DISTANCE` → `{ "feeMode": "DISTANCE", "pricePerKm": "0.80", "minimumFee": "3.00", "maxDistanceKm": "6.0", "fixedFee": null }` ("R$ 0,80/km, mínimo R$ 3,00, até 6 km"); `FIXED` → `{ "feeMode": "FIXED", "pricePerKm": null, "minimumFee": null, "maxDistanceKm": null, "fixedFee": "5.00" }`.
- `privacy` (ADR-0052 item 1): dados variáveis do aviso "Seus dados são usados por **{controllerName}** para preparar e entregar o pedido". O texto completo é da página (revisão jurídica). `thirdParties`: `ANTI_BOT` (Cloudflare Turnstile, quando ligado); `MAPS` (entrega por distância disponível e provedor externo, `Osm` ou `Google`; o `Fake` não manda o endereço a ninguém) e `PAYMENTS` com a Fase 2.
- `Cache-Control: no-cache`: o servidor da página revalida a cada uso (pausa e "Acabou" aparecem em até 30 s, RF-202/RF-205).

### `POST /public/stores/{slug}/orders` — público [I]

Headers: `Idempotency-Key: {uuid}` (obrigatório, gerado quando o cliente toca em "Enviar pedido" e reaproveitado em todo reenvio) e `X-Turnstile-Token` (o token do Turnstile; fica **fora** do hash da idempotência porque é de uso único: depois de um **erro**, gere um token novo e reenvie com a mesma chave). Corpo até 32 KB.

```json
{
  "fulfillmentType": "PICKUP",
  "customerName": "Carla Souza",
  "customerPhone": "(34) 98888-7777",
  "deliveryAddress": null,
  "deliveryQuoteId": null,
  "expectedPaymentMethod": "CASH",
  "changeFor": "50.00",
  "notes": "Sem talher",
  "items": [
    {
      "itemKind": "MARMITA",
      "marmitaSizeId": "0192d6a0-0000-7000-8000-000000000202",
      "productId": null,
      "quantity": 2,
      "mainDishProductIds": ["0192d6a0-0000-7000-8000-000000000101"],
      "sideDishProductIds": ["0192d6a0-0000-7000-8000-000000000111", "0192d6a0-0000-7000-8000-000000000112"],
      "addons": [{ "productId": "0192d6a0-0000-7000-8000-000000000131", "quantity": 1 }],
      "notes": "Sem cebola"
    },
    { "itemKind": "BEVERAGE", "marmitaSizeId": null, "productId": "0192d6a0-0000-7000-8000-000000000121", "quantity": 1, "mainDishProductIds": null, "sideDishProductIds": null, "addons": null, "notes": null }
  ],
  "expectedTotal": "50.00",
  "website": null
}
```

→ `201 Created`, `Location: /api/v1/public/tracking/{trackingToken}`:

```json
{
  "trackingToken": "q7Hc2mVxR0aLpT9wZk3s1A",
  "trackingExpiresAt": "2026-11-03T12:00:00Z",
  "acceptanceStatus": "AWAITING_ACCEPTANCE",
  "acceptanceDeadline": "2026-10-27T12:10:00Z",
  "itemsTotal": "50.00",
  "deliveryFee": "0.00",
  "totalAmount": "50.00"
}
```

- **Preço sempre do servidor** (ADR-0044 item 21): `itemsTotal` = Σ (tamanho + adicionais) × quantidade + bebidas = 2 × (20,00 + 2,00) + 6,00 = **50,00**. O corpo não tem preço; campos de preço enviados são ignorados.
- `trackingToken`: 128 bits em base64url (22 caracteres), derivados do pedido com um segredo do servidor (HMAC com `PublicPage:TrackingTokenSecret`): imprevisível para quem não tem o segredo e o mesmo para o mesmo pedido. O servidor guarda só o SHA-256; o token aparece **só aqui** (e na repetição idempotente desta resposta, que o recalcula). A página guarda no `localStorage` e monta `/{slug}/pedido/{trackingToken}` (ADR-0047 itens 1, 2 e 10).
- A resposta não tem nome, telefone nem endereço: o corpo fica guardado no registro de idempotência por 72 h (ADR-0052 item 5), **sem** o `trackingToken` (o reenvio o recalcula a partir do pedido; o token nunca fica em claro no banco).
- O Turnstile e a conferência da cotação de entrega (que pode recalcular no provedor de mapas) rodam **antes** da transação do envio; um reenvio de pedido já gravado responde antes deles (o token do Turnstile, de uso único, não é verificado de novo). A ordem dos erros abaixo não muda.
- O pedido entra no app como pedido do site `AWAITING_ACCEPTANCE` (§6.9 "Pedido do site"), com `customerPhone` normalizado em E.164 e passa pelo aceite (`POST /orders/{id}/accept`).
- `customerPhone` aceita os formatos comuns ("(34) 98888-7777", "34988887777", "+55 34 98888-7777", DDD com zero) e é normalizado: celular com 9 dígitos começando por 9, fixo com 8 começando por 2 a 5.
- Reenvio com a mesma chave e o mesmo corpo → a mesma resposta + `Idempotent-Replayed: true` (escopo `(tenant, key)`, sem usuário). Mesma chave com corpo diferente → `422 IdempotencyKeyReused`. A página **não** usa `GET /operations/{key}`.

Ordem das checagens e erros:

| # | Situação | Resposta |
|---|---|---|
| 1 | formato: nome 2–80; telefone com DDD; `fulfillmentType` `PICKUP`/`DELIVERY`; endereço só (e obrigatório) na entrega; `notes` e `items[].notes` até 200; 1 a 30 linhas; só `MARMITA` e `BEVERAGE`; quantidade 1–20 por linha; marmita com tamanho e pelo menos 1 prato; **pelo menos 1 marmita** (pedido só de bebidas não é aceito); **até 20 marmitas** somadas; `changeFor` só com `CASH` | `422 ValidationFailed` |
| 2 | campo-isca `website` preenchido | `400 MalformedRequest`, sem detalhes |
| 3 | página fora de `OPEN` | `409 StoreUnavailable` (`storeStatus`) |
| 4 | Turnstile ausente ou inválido | `403 CaptchaFailed`. Cloudflare sem resposta em 3 s → o pedido é aceito com `riskFlag: "CAPTCHA_UNAVAILABLE"` |
| 5 | `DELIVERY` com a entrega indisponível (§6.18 `pageDelivery`) | `422 DeliveryUnavailable` |
| 5a | `DELIVERY` sem `deliveryQuoteId` | `422 ValidationFailed` em `deliveryQuoteId` (já na checagem 1) |
| 6 | `PICKUP` com a retirada desligada | `422 ValidationFailed` em `fulfillmentType` |
| 7 | `expectedPaymentMethod` fora das formas habilitadas | `422 PaymentMethodDisabled` |
| 8 | o telefone já tem 2 pedidos aguardando aceite, ou o IP já tem 3 | `429 TooManyPendingOrders` |
| 9 | sem cardápio do dia publicado | `404 MenuNotFound` |
| 9a | `DELIVERY`: cotação inexistente ou de outro restaurante | `422 ValidationFailed` em `deliveryQuoteId` ("Calcule a entrega de novo") |
| 9b | `DELIVERY`: cotação vencida, de outro endereço (rua, número, bairro, cidade ou CEP; complemento e referência não contam) ou feita antes de o restaurante mudar a configuração → **recalcula**. Mesma taxa → segue com ela; outra taxa → | `409 DeliveryQuoteChanged` com a cotação nova em `quote`. O recálculo pode dar os erros da cotação (`OutOfDeliveryArea`, `DeliveryUnavailable`, `422` em `deliveryAddress.street`/`deliveryAddress.number`) |
| 10 | item fora do cardápio, indisponível ou escondido, ou `expectedTotal` diferente do recalculado | `409 CartChanged` |
| 10a | o pedido gravado não bate com o carrinho conferido no passo 10 (total, itens ou taxa; ex.: preço mudou no meio do envio) | `409 CartChanged` com o carrinho recalculado; nada é gravado |
| 11 | `changeFor` menor que o total | `422 ValidationFailed` em `changeFor` |

`409 CartChanged` traz o carrinho recalculado (linhas na ordem de `items`; indisponível → `available: false` e sem valores; o total soma só as disponíveis). A página mostra as diferenças e o cliente confirma de novo:

```json
{
  "type": "urn:almoseis:error:cart-changed",
  "title": "Seu pedido mudou. Confira os itens e os valores.",
  "status": 409,
  "errorCode": "CartChanged",
  "traceId": "00-6f1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e-6f1c2d3e4f5a6b7c-01",
  "cart": {
    "items": [
      { "index": 0, "name": "Marmita M", "available": false, "unitPrice": null, "lineTotal": null },
      { "index": 1, "name": "Coca-Cola 350ml", "available": true, "unitPrice": "6.00", "lineTotal": "6.00" }
    ],
    "unavailableProductIds": ["0192d6a0-0000-7000-8000-000000000102"],
    "itemsTotal": "6.00",
    "deliveryFee": "0.00",
    "totalAmount": "6.00"
  }
}
```

### `POST /public/stores/{slug}/delivery-quotes` — público

Cotação da taxa de entrega (ADR-0048 itens 4 a 13; RF-204). Pedida **quando o cliente confirma o endereço**, nunca a cada tecla. Header `X-Turnstile-Token` como no envio (ADR-0045 item 12: um token novo por chamada; Cloudflare fora do ar → segue). O cálculo é sempre do servidor: o corpo não tem distância nem taxa.

```json
{ "address": { "street": "Rua das Palmeiras", "number": "45", "complement": "Casa 2", "neighborhood": "Centro", "city": "Uberlândia", "zipCode": "38400100", "reference": null } }
```

→ `201 Created` (sem `Location`; a cotação só é usada pelo envio):

```json
{ "quoteId": "0192d6a0-0000-7000-8000-000000000e01", "formattedAddress": "R. das Palmeiras, 45 - Centro, Uberlândia - MG, 38400-100, Brasil", "distanceKm": "4.5", "deliveryFee": "3.60", "expiresAt": "2026-10-27T12:15:00Z" }
```

- **Cálculo** (rota de carro sem trânsito, do ponto **confirmado** do restaurante): `km = round(metros / 1000, 1, AwayFromZero)` e `deliveryFee = max(round(km × pricePerKm, 2, AwayFromZero), minimumFee)`.

  | Rota | `distanceKm` | km × R$ 0,80 | Mínimo R$ 3,00 | `deliveryFee` | Raio `"6.0"` |
  |---|---:|---:|---:|---:|---|
  | 4.480 m | `"4.5"` | 3,60 | — | `"3.60"` | entrega |
  | 2.130 m | `"2.1"` | 1,68 | aplica | `"3.00"` | entrega |
  | 6.040 m | `"6.0"` | 4,80 | — | `"4.80"` | entrega |
  | 6.050 m | `"6.1"` | 4,88 | — | — | `422 OutOfDeliveryArea` |

  Com R$ 0,85/km e 4,5 km: 3,825 → `"3.83"`.
- `formattedAddress`: o endereço como o geocodificador entendeu ("Encontramos: …"); a página mostra e o cliente confirma. Numa cotação servida pelo cache (mesmo endereço normalizado no mesmo restaurante, até 7 dias), é o endereço digitado, porque o cache não guarda texto (ADR-0052 item 5). O cache guarda o hash do endereço com segredo (HMAC com `Maps:AddressHashSecret`) e as coordenadas com 4 casas (cerca de 11 m); a cotação usa o ponto exato do geocodificador quando ele é chamado e o de 4 casas quando vem do cache.
- `expiresAt`: **15 min**. O envio confere a cotação (§ envio, checagens 9a e 9b).
- Modo `FIXED`: `distanceKm: null` e `deliveryFee` = `defaultDeliveryFee`; sem mapa, sem raio e sem conferência de precisão.
- Mesmo endereço normalizado = rua (abreviações comuns como "R." e "Av." contam como "Rua"/"Avenida"), número, bairro, cidade (nula = a do restaurante) e CEP, sem caixa, acento ou pontuação. A UF é sempre a do restaurante. `complement` e `reference` ficam de fora.

Erros, nesta ordem:

| Situação | Resposta |
|---|---|
| formato: `street` obrigatória até 120; `number` até 10; `complement`, `neighborhood` até 60; `city` até 80; `zipCode` com 8 dígitos; `reference` até 120 | `422 ValidationFailed` em `address.*` |
| página fora de `OPEN` | `409 StoreUnavailable` (`storeStatus`) |
| Turnstile ausente ou inválido (com `Turnstile:Enabled`) | `403 CaptchaFailed` |
| entrega indisponível (`deliveryAvailable: false`) | `422 DeliveryUnavailable` |
| endereço não encontrado | `422 ValidationFailed` em `address.street`: "Não encontramos esse endereço. Confira a rua, o número e o bairro." |
| resultado aproximado (só rua, bairro ou cidade; inclui número ausente ou "S/N" não localizado) | `422 ValidationFailed` em `address.number`: "Não achamos esse número nessa rua. Confira o número e o bairro." Resultado aproximado **nunca** vira taxa |
| sem rota de carro, ou `distanceKm` acima de `maxDistanceKm` | `422 OutOfDeliveryArea` ("Não entregamos nesse endereço. Você pode retirar no local."); no raio, com `distanceKm` e `maxDistanceKm` |
| provedor de mapas com erro, sem resposta em 3 s ou sem cota | `422 DeliveryUnavailable` com `detail` "Não conseguimos calcular a entrega agora. Você pode retirar no local." A página oferece retirada; `deliveryAvailable` do perfil continua `true` (a falha é momentânea) e a próxima cotação tenta de novo |

```json
{
  "type": "urn:almoseis:error:out-of-delivery-area",
  "title": "Não entregamos nesse endereço. Você pode retirar no local.",
  "status": 422,
  "errorCode": "OutOfDeliveryArea",
  "traceId": "00-7a1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e-7a1c2d3e4f5a6b7c-01",
  "distanceKm": "6.1",
  "maxDistanceKm": "6.0"
}
```

`409 DeliveryQuoteChanged` do envio (checagem 9b), com a cotação nova já gravada. A página mostra "A taxa de entrega agora é R$ 4,50", o cliente confirma e reenvia com o `quote.quoteId` novo e o `expectedTotal` novo (a mesma `Idempotency-Key` serve: o erro não grava nada do pedido):

```json
{
  "type": "urn:almoseis:error:delivery-quote-changed",
  "title": "A taxa de entrega mudou. Confira o novo valor.",
  "status": 409,
  "detail": "A taxa de entrega agora é R$ 4,50. Confira e envie de novo.",
  "errorCode": "DeliveryQuoteChanged",
  "traceId": "00-8b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e-8b2c3d4e5f6a7b8c-01",
  "quote": { "quoteId": "0192d6a0-0000-7000-8000-000000000e02", "formattedAddress": "Rua das Palmeiras, 45 - Centro, Uberlândia - MG", "distanceKm": "4.5", "deliveryFee": "4.50", "expiresAt": "2026-10-27T12:31:00Z" }
}
```

No envio com entrega, o pedido grava a taxa da cotação e o snapshot (`deliveryFeeSource: "DISTANCE"`, metros, preço por km e mínimo; §6.9); o `itemsTotal + deliveryFee` precisa bater com `expectedTotal` (senão `409 CartChanged`, com a taxa em `cart.deliveryFee`). O endereço do pedido guarda rua, número, complemento, bairro e referência (a cidade não entra no `deliveryAddress` do pedido).

### `GET /public/tracking/{trackingToken}` — público

```json
{
  "version": 3,
  "store": {
    "slug": "marmitaria-da-ana",
    "name": "Marmitaria da Ana",
    "contactPhone": "+5534999990000",
    "address": { "street": "Rua das Flores", "number": "123", "complement": null, "neighborhood": "Centro", "city": "Uberlândia", "state": "MG" }
  },
  "orderNumber": 13,
  "submittedAt": "2026-10-27T12:00:00Z",
  "status": "IN_PREPARATION",
  "acceptanceStatus": "ACCEPTED",
  "acceptanceDeadline": "2026-10-27T12:10:00Z",
  "estimatedReadyMinutes": 30,
  "canWithdraw": false,
  "timeline": [
    { "step": "SUBMITTED", "at": "2026-10-27T12:00:00Z" },
    { "step": "ACCEPTED", "at": "2026-10-27T12:02:00Z" },
    { "step": "IN_PREPARATION", "at": "2026-10-27T12:05:00Z" }
  ],
  "rejection": null,
  "fulfillmentType": "PICKUP",
  "customerFirstName": "Carla",
  "deliveryAddress": null,
  "items": [
    {
      "itemKind": "MARMITA", "name": "Marmita M", "composition": "Frango grelhado · Arroz, Feijão", "quantity": 2,
      "unitPrice": "20.00", "lineTotal": "40.00", "notes": "Sem cebola",
      "addons": [{ "name": "Ovo frito", "quantity": 1, "unitPrice": "2.00", "lineTotal": "4.00" }]
    },
    { "itemKind": "BEVERAGE", "name": "Coca-Cola 350ml", "composition": null, "quantity": 1, "unitPrice": "6.00", "lineTotal": "6.00", "notes": null, "addons": [] }
  ],
  "itemsTotal": "50.00",
  "deliveryFee": "0.00",
  "deliveryDistanceKm": null,
  "totalAmount": "50.00",
  "expectedPaymentMethod": "CASH",
  "changeFor": "50.00",
  "changedByStore": false,
  "trackingExpiresAt": "2026-11-03T12:00:00Z"
}
```

- `status` (ADR-0047 item 7): `AWAITING_PAYMENT` (Fase 2), `AWAITING_ACCEPTANCE`, `REJECTED`, `EXPIRED`, `WITHDRAWN`, `CONFIRMED` (aceito, produção `OPEN`), `IN_PREPARATION`, `READY_FOR_DELIVERY`, `READY_FOR_PICKUP`, `OUT_FOR_DELIVERY`, `DELIVERED`, `PICKED_UP`, `CANCELED`. Um pedido que volta de `COMPLETED` para `IN_PROGRESS` volta a `IN_PREPARATION`. Prazo de aceite vencido antes de o job rodar já aparece como `EXPIRED`.
- `timeline[].step`: `SUBMITTED`, `ACCEPTED`, `IN_PREPARATION`, `READY`, `OUT_FOR_DELIVERY`, `DELIVERED`, `PICKED_UP`, `REJECTED`, `EXPIRED`, `WITHDRAWN`, `CANCELED`, em ordem de horário.
- `rejection`: `{ "reasonCode": "ITEM_UNAVAILABLE", "message": "A costela acabou." }`; em `SUSPECTED_PRANK`, `message: null` (ADR-0046 item 12). `BUSINESS_DAY_CLOSED` na recusa do fechamento.
- `canWithdraw`: `true` só em `AWAITING_ACCEPTANCE`.
- `changedByStore`: o restaurante alterou o pedido depois do aceite ("O restaurante alterou seu pedido", com o total atualizado).
- `deliveryAddress` (entrega): só `{ "street", "number" }`. **Nunca** aparecem telefone, sobrenome, complemento, referência, funcionários, outros pedidos ou saldo de fiado (ADR-0047 itens 5 e 6). `deliveryDistanceKm`: `"4.5"` quando a taxa foi a calculada pela distância; `null` em retirada, no modo `FIXED` ou se o restaurante editou a taxa ou o endereço depois.
- `ETag` opaco (ex.: `"3-IN_PREPARATION-7-marmitaria-da-ana"`): muda com a versão do pedido, com o `status` calculado (prazo de aceite vencido vira `EXPIRED` antes de o job gravar, sem mudar a `version`) e com a configuração da página (bloco `store`). A página não interpreta o valor: consulta a cada 20 s com `If-None-Match`; sem mudança → `304 Not Modified` (ADR-0047 item 8). `version` no corpo continua sendo a do pedido.
- Toda resposta de `/public/tracking/*` (inclusive erros) leva `Cache-Control: private, no-store`, `Referrer-Policy: no-referrer` e `X-Robots-Tag: noindex` (item 9).
- Token inventado, malformado ou de pedido apagado → `404 NotFound`, sem nada do restaurante. Mais de 7 dias → `410 TrackingExpired` (o registro é apagado pelo job de retenção, e depois disso o mesmo token dá `404`).

### `POST /public/tracking/{trackingToken}/withdraw` — público

Sem corpo, sem `Idempotency-Key`: **idempotente pelo estado**. Desistência enquanto `AWAITING_ACCEPTANCE` (ADR-0046 item 17) → `200` com o acompanhamento (`status: "WITHDRAWN"`) e o `ETag`. Repetir com o pedido já `WITHDRAWN` (a resposta se perdeu) → o mesmo `200` com o acompanhamento atual, sem nova auditoria nem evento. Aceito ou recusado → `409 OrderNotAwaitingAcceptance` (extensão `acceptanceStatus`); prazo vencido → `409 WebOrderExpired`. Auditoria `WebOrderWithdrawn` com `origin: "WEB"`, sem ator; o app recebe `OrderRejected` (§6.14) com `acceptanceStatus: "WITHDRAWN"`.

### Configuração (API)

| Chave | Uso | Desenvolvimento / testes |
|---|---|---|
| `Cors:PublicPageOrigins` | origens da página | `["http://localhost:3000"]` |
| `Turnstile:Enabled`, `Turnstile:SecretKey`, `Turnstile:TimeoutSeconds` (3) | Cloudflare Turnstile. A chave secreta vem do Key Vault; `Enabled` sem chave, ou com uma chave de teste do Cloudflare (`1x0000…`, `2x0000…`, `3x0000…`) fora de Development, impede a API de subir | dev: chave secreta de **teste** oficial `1x0000000000000000000000000000000AA` (sempre passa); testes: desligado, e ligado com `siteverify` simulado no teste dedicado |
| `PublicPage:IpHashSecret` | segredo do sal diário do hash do IP (Key Vault). Obrigatório fora de Development (≥ 32 caracteres; a API não sobe sem ele). Vazio em Development → sal aleatório por processo | vazio em Development; testes: valor fixo |
| `PublicPage:TrackingTokenSecret` | segredo do HMAC que deriva o `trackingToken` do pedido (Key Vault). Obrigatório fora de Development (≥ 32 caracteres, diferente do anterior). Vazio em Development → aleatório por processo | vazio em Development; testes: valor fixo |
| `PublicPage:RendererKey` | `X-Web-Renderer-Key` do servidor da página (Key Vault). Vazio → sem isenção | vazio |
| `WebOrders:RetentionJobEnabled`, `RetentionIntervalMinutes` (60) | job de retenção do ADR-0052 | testes: desligado, chamado por `RunOnceAsync` |
| `Maps:Provider` | `None` (sem entrega por distância: `MAPS_PROVIDER` em `pageDelivery.missing`), `Fake`, `Osm` ou `Google` (ADR-0048 item 15). `appsettings.json` versionado: `None` até o usuário escolher. `Fake` é recusado em Production | Development e testes: `Fake` (sem rede). Sem a chave na configuração, o padrão também é `Fake` |
| `Maps:Google:ApiKey` | chave da Geocoding API + Routes API (Compute Route Matrix, Essentials, `TRAFFIC_UNAWARE`). Só Key Vault / `user-secrets`; obrigatória com `Google` (a API não sobe sem ela) | vazio |
| `Maps:Osm:NominatimUrl`, `OsrmUrl`, `UserAgent`, `NominatimMinIntervalMilliseconds` (1000), `AcceptStreetLevel` (false) | Nominatim + OSRM. Padrão: as instâncias públicas (só desenvolvimento; política de 1 req/s, respeitada por réplica). `UserAgent` genérico do produto, sem e-mail | — |
| `Maps:TimeoutSeconds` (3), `GeocodeCacheDays` (7), `RouteCacheDays` (7) | tempo máximo por chamada; validade do cache por tenant das coordenadas (4 casas) e dos metros (0 desliga; ver ADR-0048 item 11 sobre os termos do Google) | — |
| `Maps:AddressHashSecret` | segredo do HMAC do endereço normalizado (cache e conferência da cotação). Key Vault; obrigatório fora de Development (≥ 32 caracteres). Trocar invalida o cache e as cotações em aberto (o envio recalcula) | vazio em Development; testes: valor fixo |
| `Maps:RetentionJobEnabled`, `RetentionIntervalMinutes` (60) | apaga cotações 24 h depois de vencer e o cache vencido (ADR-0052 item 5) | testes: desligado, chamado por `RunOnceAsync` |
