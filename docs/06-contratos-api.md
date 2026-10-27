# 06. Contratos da API (exemplos JSON literais)

> **Fonte de verdade de payload.** Implementa `04-modelo-dados.md`, `05-ddl.md` e os ADRs. Backend e mobile **não inventam** campo, tipo, `errorCode` ou status HTTP fora deste documento. Se faltar algo, pare e pergunte, e atualize aqui antes de implementar. Onde houver diferença com o §7 do épico, vale este documento (mapeamento em §6.17).

## 6.0 Convenções

| Item | Regra |
|---|---|
| Base | `https://{host}/api/v1` (ADR-0035). Todos os caminhos abaixo são relativos a ela |
| Autenticação | `Authorization: Bearer {accessToken}` em tudo, exceto onde marcado **público** |
| Tenant | Vem só do token (ADR-0026). Nenhum endpoint recebe `tenantId`, exceto os de dispositivo compartilhado e de admin |
| Versão do app | `X-App-Version: 1.4.0` e `X-App-Platform: ANDROID`/`IOS` em toda requisição (ADR-0035) |
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
| 402 | `SubscriptionInactive` | escrita com assinatura `SUSPENDED`/`CANCELED` (ADR-0017) |
| 403 | `PermissionDenied` | falta permissão (a extensão `permission` diz qual) |
| 403 | `NoActiveMembership` | usuário sem vínculo ativo em nenhum estabelecimento |
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
| 426 | `AppVersionUnsupported` | versão do app abaixo da mínima (extensões `minVersion`, `storeUrl`) |
| 428 | `IdempotencyKeyRequired` / `IfMatchRequired` | header obrigatório ausente |
| 429 | `TooManyRequests` | limite dos endpoints públicos (ADR-0041); header `Retry-After` |

---

## 6.2 Permissões e perfis (ADR-0028)

Cada endpoint indica a permissão exigida. Matriz em código (`Permissions.cs` / `RolePermissions.cs`):

| Permissão | OWNER | MANAGER | ATTENDANT | KITCHEN | CASHIER |
|---|:-:|:-:|:-:|:-:|:-:|
| `orders.view` | ✓ | ✓ | ✓ | ✓ | ✓ |
| `orders.create`, `orders.edit` | ✓ | ✓ | ✓ | | |
| `orders.cancel` | ✓ | ✓ | | | |
| `orders.delivery-fee.edit` | ✓ | ✓ | | | |
| `production.view`, `production.update` | ✓ | ✓ | | ✓ | |
| `payments.register` | ✓ | ✓ | | | ✓ |
| `payments.reverse` | ✓ | ✓ | | | |
| `cash.open`, `cash.movement`, `closing.view`, `closing.finalize` | ✓ | ✓ | | | ✓ |
| `closing.adjust` | ✓ | ✓ | | | |
| `menu.manage`, `catalog.manage` | ✓ | ✓ | | | |
| `customers.manage` | ✓ | ✓ | ✓ | | ✓ |
| `employees.manage` | ✓ | | | | |
| `devices.manage`, `settings.manage`, `audit.view` | ✓ | ✓ | | | |
| `subscription.view` | ✓ | | | | |

Leitura de produtos, tamanhos, cardápios e clientes exige só `orders.view`.

`GET /roles` (autenticado, qualquer perfil) → `200`:

```json
{
  "items": [
    { "role": "OWNER", "name": "Proprietário", "permissions": ["orders.view", "orders.create", "orders.edit", "orders.cancel", "orders.delivery-fee.edit", "production.view", "production.update", "payments.register", "payments.reverse", "cash.open", "cash.movement", "closing.view", "closing.finalize", "closing.adjust", "menu.manage", "catalog.manage", "customers.manage", "employees.manage", "devices.manage", "settings.manage", "audit.view", "subscription.view"] },
    { "role": "KITCHEN", "name": "Cozinha", "permissions": ["orders.view", "production.view", "production.update"] }
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

`Me` (também é a resposta de `GET /auth/me`):

```json
{
  "user": { "id": "0192d6a0-0000-7000-8000-000000000011", "fullName": "Ana Souza", "email": "ana@example.com", "phoneNumber": "+5534999990001" },
  "tenant": { "id": "0192d6a0-0000-7000-8000-000000000001", "name": "Marmitaria da Ana", "timeZone": "America/Sao_Paulo" },
  "employee": { "id": "0192d6a0-0000-7000-8000-000000000301", "displayName": "Ana", "roles": ["OWNER"], "hasPin": true },
  "permissions": ["orders.view", "orders.create", "orders.edit", "orders.cancel", "orders.delivery-fee.edit", "production.view", "production.update", "payments.register", "payments.reverse", "cash.open", "cash.movement", "closing.view", "closing.finalize", "closing.adjust", "menu.manage", "catalog.manage", "customers.manage", "employees.manage", "devices.manage", "settings.manage", "audit.view", "subscription.view"],
  "memberships": [
    { "tenantId": "0192d6a0-0000-7000-8000-000000000001", "tenantName": "Marmitaria da Ana", "employeeId": "0192d6a0-0000-7000-8000-000000000301", "roles": ["OWNER"] }
  ],
  "subscription": { "status": "TRIALING", "trialEndsAt": "2026-11-03T15:04:05Z", "daysLeft": 7, "canWrite": true },
  "sharedDevice": null,
  "serverTime": "2026-10-27T15:04:05Z"
}
```

Para quem não tem `subscription.view`, os campos de `subscription` vêm `null`, exceto `canWrite`, que todos recebem para saber se o app está em modo somente leitura: `{ "status": null, "trialEndsAt": null, "daysLeft": null, "canWrite": true }`.

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
    "timeZone": "America/Sao_Paulo"
  },
  "acceptedTermsVersion": "2026-10",
  "installationId": "inst_8f3a2c1e9b7d4f60"
}
```

Exatamente um entre `newAccount` e `existingAccount` (`{ "login": "ana@example.com", "password": "..." }`). → `201`, `Location: /api/v1/tenant`:

```json
{ "tokens": { "...": "Tokens" }, "me": { "...": "Me" } }
```

Erros: `409 EmailAlreadyRegistered`, `409 PhoneAlreadyRegistered`, `409 TrialAlreadyClaimed`, `401 InvalidCredentials` (existingAccount), `422 ValidationFailed`.

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

→ `200` `Tokens`, com o refresh **rotacionado** (ADR-0027). Reusar um refresh já trocado → `401 InvalidRefreshToken` e revogação da família inteira.

### `POST /auth/logout`

```json
{ "refreshToken": "rt_7Qm2xN0pLw8vB4kZ1sYdF6hJ3cT9eR5u" }
```

→ `204`.

### `POST /auth/switch-tenant`

```json
{ "tenantId": "0192d6a0-0000-7000-8000-000000000777" }
```

→ `200` `{ "tokens": {...}, "me": {...} }`. Um tenant em que o usuário não tem vínculo ativo responde `404 NotFound`.

### `GET /auth/me` → `200` `Me`

### `PUT /auth/me/pin` — define ou troca o próprio PIN

```json
{ "pin": "4821", "currentPassword": "S3nha-forte!" }
```

→ `204`. PIN de 4 a 6 dígitos, e sequências triviais (`1234`, `0000`) são recusadas com `422`.

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

`deviceSecret` aparece **só nesta resposta** e é guardado no SecureStore do aparelho. O servidor guarda só o hash.

#### `GET /devices/shared` — `devices.manage`

→ `200` `{ "items": [ { "id": "...c01", "name": "Tablet cozinha", "platform": "ANDROID", "registeredAt": "2026-10-27T10:00:00Z", "registeredBy": {...}, "revokedAt": null } ] }`

#### `POST /devices/shared/{id}/revoke` — `devices.manage`

→ `204`. Revoga também todos os refresh tokens emitidos por PIN nesse aparelho.

#### `GET /devices/shared/{installationId}/employees` — público + `X-Device-Secret` + `X-Tenant-Id`

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

→ `200` `{ "tokens": {...}, "me": {...} }` (com `me.sharedDevice` preenchido). Erros: `401 InvalidDeviceSecret`, `401 InvalidCredentials` (extensão `attemptsLeft`), `423 PinLocked` (extensão `lockedUntil`).

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
  "createdAt": "2026-10-27T09:30:00Z"
}
```

### `PATCH /tenant` — `settings.manage` [M]

```json
{ "name": "Marmitaria e Restaurante da Ana", "city": "Uberlândia", "state": "MG", "timeZone": "America/Sao_Paulo" }
```

→ `200` com o tenant. `documentType`/`documentNumber` não são alteráveis pelo app.

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

Campos ausentes não mudam. → `200` com as configurações. Mudar o preço do quilo não afeta pesagens já registradas (RN-006.3).

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

**Sem preço, plano ou link de compra** (ADR-0017).

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
  "createdAt": "2026-10-27T09:40:00Z",
  "updatedAt": "2026-10-27T09:40:00Z"
}
```

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
  "requiresPreparation": false
}
```

`PATCH /products/{id}`:

```json
{ "defaultPrice": "6.50", "isAvailable": false, "imageStorageKey": "t/0192d6a0-0000-7000-8000-000000000001/p/0192d6a0-0000-7000-8000-000000000121-20261027.jpg" }
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

Tipos aceitos: `image/jpeg`, `image/png`, `image/webp`, até 2 MB. Depois do `PUT`, o app grava `imageStorageKey` via `PATCH`.

### Tamanhos de marmita

`MarmitaSize`: `{ "id": "0192d6a0-0000-7000-8000-000000000202", "version": 1, "name": "M", "price": "20.00", "sortOrder": 2, "isActive": true }`

| Método e rota | Permissão | Corpo → resposta |
|---|---|---|
| `GET /marmita-sizes?includeInactive=false` | `orders.view` | → `200 { "items": [MarmitaSize] }` |
| `POST /marmita-sizes` | `catalog.manage` | `{ "name": "G", "price": "25.00", "sortOrder": 3 }` → `201 MarmitaSize` |
| `PATCH /marmita-sizes/{id}` [M] | `catalog.manage` | `{ "price": "26.00", "isActive": true }` → `200 MarmitaSize` |

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
  "fulfillmentType": "DELIVERY",
  "customer": { "id": "0192d6a0-0000-7000-8000-000000000801", "name": "João Silva", "phoneNumber": "+5534999991234" },
  "deliveryAddress": { "street": "Rua das Acácias", "number": "120", "complement": "Ap 201", "neighborhood": "Santa Mônica", "reference": "Em frente à padaria" },
  "tableLabel": null,
  "tabNumber": null,
  "notes": "Entregar até 12h30",
  "deliveryFee": "5.00",
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
- `items` traz só itens `ACTIVE`. As remoções aparecem em `GET /orders/{id}/history`.
- `pendingAlertCount` é **por funcionário** (o de quem chama).
- `cancellation` (quando cancelado): `{ "canceledAt": "...", "canceledBy": {...}, "reason": "Cliente desistiu" }`.
- `customer` existe quando há cliente cadastrado **ou** nome avulso. Com nome avulso (ex.: `"Mesa 3"` num pedido por quilo), `customer.id` é `null`. Sem nenhum dos dois, `customer` é `null`.
- `items` e `addons` saem na ordem de inclusão.

Pedido `PF`: `fulfillmentType: "DINE_IN"`, `tableLabel: "5"`, itens `itemKind: "PF"` com `productId` do prato e `unitPrice` = `pfPrice` do cardápio.

Pedido `KILO`: `productionStatus: null`, e `weighings`:

```json
[
  { "id": "0192d6a0-0000-7000-8000-000000000611", "weightGrams": 452, "pricePerKg": "59.90", "amount": "27.07", "createdAt": "2026-10-27T12:05:00Z", "createdBy": { "id": "0192d6a0-0000-7000-8000-000000000303", "displayName": "Carla" } },
  { "id": "0192d6a0-0000-7000-8000-000000000612", "weightGrams": 318, "pricePerKg": "59.90", "amount": "19.05", "createdAt": "2026-10-27T12:06:00Z", "createdBy": { "id": "0192d6a0-0000-7000-8000-000000000303", "displayName": "Carla" } }
]
```

### `GET /orders` — `orders.view`

Filtros:
- `businessDayId` (padrão: o expediente aberto);
- `allDays=true` (ignora o expediente; útil para pendências);
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
      "itemCount": 3,
      "pendingAlertCount": 1,
      "createdAt": "2026-10-27T11:30:00Z"
    }
  ],
  "nextCursor": "eyJjIjoiMjAyNi0xMC0yN1QxMToyOTowMFoiLCJpIjoiLi4uIn0"
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

Erros: `409 BusinessDayRequired`, `404 MenuNotFound`, `422 ProductUnavailable`, `422 KiloPriceNotConfigured`, `422 ValidationFailed` (pedido vazio, `itemKind` incompatível com `orderType`, endereço faltando em entrega).

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

Campos ausentes não mudam. Em pedido pago, só `notes`, `tableLabel` e `tabNumber` podem mudar. Os demais campos respondem `409 OrderAlreadyPaid`. → `200 Order`.

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

→ `200 Order`. O efeito colateral (RN-007.10): se algum item exigir preparo e o pedido estiver `COMPLETED`, ele volta para `IN_PROGRESS`. Gera `OrderChangeEvent`.

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

→ `200 Order`. Erros: `409 OrderHasPayments`, `409 OrderCanceled`.

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

Do mais recente para o mais antigo. Inclui itens removidos, pagamentos, estornos, status e cancelamento.

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

Painel da cozinha (RN-007.4/7.5): só `MARMITA` e `PF` ativos do expediente aberto, agrupados e do mais antigo para o mais novo. Sem expediente aberto → `409 BusinessDayRequired`.

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
        "fulfillmentType": "DINE_IN",
        "label": "Mesa 5",
        "notes": null,
        "isPaid": false,
        "createdAt": "2026-10-27T11:50:00Z",
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
- `adjustments`: no resumo parcial, os ajustes **registrados** neste expediente; no `DailyClosing`, os que **corrigem** aquele fechamento.
- Toda movimentação de caixa sobe a `version` do expediente (é ela que dá o `409` da finalização). Pagamentos não sobem: a finalização trava o expediente e calcula com o que já foi confirmado.

`byOrderType[].received` agrupa os pagamentos recebidos **neste** expediente pelo tipo do pedido. Por isso o "recebido" de marmita pode incluir fiado de dias anteriores (`receivedFromPreviousDays`). Para o exemplo fechar com o épico, os R$ 35 de fiado estão contidos nos R$ 1.100 de marmita.

### `POST /closing/finalize` — `closing.finalize` [I][M]

`If-Match` = versão do `BusinessDay`.

```json
{ "businessDayId": "0192d6a0-0000-7000-8000-000000000401", "countedCash": "740.00", "justification": "Troco dado a mais em uma entrega; conferido com o entregador." }
```

→ `201`, `Location: /api/v1/closing/{id}`, corpo `DailyClosing`: o mesmo formato do summary, com `isFinal: true`, `dailyClosingId`, `cash.countedCash: "740.00"`, `cash.cashDifference: "-10.00"`, `cash.justification`, `closedAt` e `closedBy`. O expediente passa a `CLOSED`.

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
| `TENANT_SETTINGS` | `GET /tenant/settings` |
| `EMPLOYEE` | `GET /auth/me` (se for o próprio) / `GET /employees/{id}` |
| `PRODUCT` | `GET /products/{id}` |
| `MARMITA_SIZE` | `GET /marmita-sizes` |
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
| `OrderCreated` | orders, production (exceto KILO) | `orderNumber`, `orderType`, `label` |
| `OrderUpdated` | orders, production | `orderNumber`, `orderType`, `productionStatus`, `isPaid` |
| `OrderStatusChanged` | orders, production | `orderNumber`, `from`, `to` |
| `OrderCanceled` | orders, production | `orderNumber` |
| `OrderPaymentRegistered` | orders, closing | `orderNumber`, `paidAmount`, `balance` |
| `OrderPaymentConfirmed` | orders, production, closing | `orderNumber` (quitou) |
| `OrderPaymentReversed` | orders, production, closing | `orderNumber`, `isPaid` |
| `OrderAlertCreated` | orders, production | `orderNumber`, `changeKind`, `affectsProduction` |
| `OrderAlertAcknowledged` | `employee:{id}` | `eventIds` |
| `MenuUpdated` | catalog | `menuType`, `date` |
| `CatalogUpdated` | catalog | `entityType` (`PRODUCT`/`MARMITA_SIZE`/`TENANT_SETTINGS`) |
| `BusinessDayOpened` | todos do tenant | `businessDate` |
| `ClosingUpdated` | closing | — (recarregar o summary; agrupado no servidor, no máximo 1 a cada 5 s) |
| `ClosingFinalized` | todos do tenant | `businessDate`, `dailyClosingId` |
| `AccessRevoked` | `employee:{id}` | `reason` (`DEACTIVATED`/`ROLES_CHANGED`/`DEVICE_REVOKED`): o app renova o token ou sai |
| `SubscriptionStatusChanged` | todos do tenant | `status`, `canWrite` |

O evento **não** é estado. O app busca o recurso pela API (§6.12). Quando `actorEmployeeId` é o próprio usuário, o app ignora o alerta (RN-008.6). Um salto de `seq` dispara `GET /sync/changes`.

---

## 6.15 Administração e webhooks (ADR-0017, ADR-0025)

### `/admin/*` — só `IsPlatformAdmin` (token de usuário de plataforma, sem tenant)

| Método e rota | Corpo → resposta |
|---|---|
| `GET /admin/tenants?status=PAST_DUE&search=&limit=50&cursor=` | → `200 { "items": [ { "id", "name", "documentNumber", "createdAt", "subscription": { "status", "trialEndsAt", "currentPeriodEndsAt" }, "activeEmployeeCount", "lastOrderAt" } ], "nextCursor": null }` |
| `GET /admin/tenants/{id}` | → `200` (mesmo item, com `ordersLast30Days` e `openBusinessDay`, **sem** dados de pedidos ou clientes) |
| `POST /admin/tenants/{id}/extend-trial` | `{ "days": 7, "reason": "Instalação atrasada no piloto" }` → `200 Subscription` |
| `POST /admin/tenants/{id}/suspend` | `{ "reason": "Fraude confirmada" }` → `200 Subscription` |
| `POST /admin/tenants/{id}/reactivate` | `{ "reason": "Pagamento confirmado manualmente" }` → `200 Subscription` |
| `POST /admin/billing-webhooks/{id}/reprocess` | → `202` |

Toda ação gera auditoria com `origin = PLATFORM_ADMIN`.

### `POST /webhooks/asaas` — público + header `asaas-access-token`

O corpo é o do Asaas, repassado como veio. O servidor valida o token, grava `billing_webhook_events` (único por `gateway_event_id`) e responde `200` **antes** de processar: o processamento é assíncrono e idempotente. Os eventos tratados no MVP são `PAYMENT_CONFIRMED`, `PAYMENT_RECEIVED`, `PAYMENT_OVERDUE`, `SUBSCRIPTION_DELETED` e `SUBSCRIPTION_INACTIVATED`. Os demais são gravados e ignorados.

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
| — | novos: `/business-days`, `/customers`, `/marmita-sizes`, `/production/orders`, `/devices/shared`, `/subscription`, `/roles`, `/audit-logs`, `/admin/*`, `/webhooks/asaas` |
