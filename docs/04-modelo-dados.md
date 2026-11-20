# 04. Modelo de Dados (conceitual)

> Consolida o §6 do épico com os ADRs 0001–0039. É o modelo **conceitual**: entidades, campos, relacionamentos, invariantes e cálculos derivados. O modelo **físico** (DDL, tipos PostgreSQL, índices, RLS, papéis) virá em `05-ddl.md` e deve seguir este documento. Se os dois divergirem, corrija os dois antes de implementar.

## 4.0 Convenções

| Item | Regra |
|---|---|
| Identificador | `Id` UUID v7 gerado na aplicação |
| Tenant | Toda entidade marcada **[T]** implementa `ITenantOwned`: tem `TenantId`, query filter do EF e RLS. FKs entre entidades [T] incluem `TenantId` (ADR-0026) |
| Instantes | `DateTimeOffset` em UTC (`...At`) |
| Data operacional | `DateOnly` (`BusinessDate`), na data local do tenant (ADR-0019) |
| Dinheiro | `Money` = `decimal` com 2 casas, BRL (ADR-0030) |
| Peso | `int` em gramas (ADR-0030) |
| Autor da ação | `...ByEmployeeId` → `TenantEmployee.Id`. O vínculo nunca é apagado, então a autoria sobrevive à desativação (RN-001.7) |
| Concorrência | **[V]** = agregado com `Version int`, incrementado em toda alteração do agregado, inclusive dos filhos (ADR-0034) |
| Sincronização | **[S]** = alterações geram entrada no `ChangeFeedEntry` (ADR-0032) |
| Exclusão | Entidades financeiras e históricas **nunca** são apagadas fisicamente. Usam `Status`/`RemovedAt`/`IsActive` |
| Enums | Valores `UPPER_SNAKE`, persistidos como texto |

Tipos usados nas tabelas: `Guid`, `string(n)`, `Money`, `int`, `bool`, `DateOnly`, `DateTimeOffset`, `enum`, `json`. `?` = opcional.

## 4.1 Mapa por módulo (ADR-0029)

| Módulo | Entidades |
|---|---|
| Identity | `User`, `Tenant`, `TenantSettings`, `TenantEmployee`, `TenantEmployeeRole`, `EmployeeInvitation`, `SharedDevice`, `RefreshToken` |
| Billing | `Subscription`, `TrialClaim`, `BillingWebhookEvent` |
| Catalog | `Product`, `MarmitaSize` |
| Menu | `Menu`, `MenuItem` |
| Ordering | `Customer`, `CustomerAddress`, `Order`, `OrderItem`, `OrderItemComponent`, `WeighingEntry`, `OrderChangeEvent`, `AlertAcknowledgement` |
| Payments | `Payment`, `PaymentReversal` |
| Closing | `BusinessDay`, `BusinessDateCounter`, `CashMovement`, `DailyClosing`, `DailyClosingLine`, `ClosingAdjustment` |
| Audit | `AuditLog` |
| Sync | `TenantSyncCounter`, `ChangeFeedEntry`, `IdempotencyRecord`, `OutboxMessage` |
| Notifications (EP-002) | `PushRegistration`, `PushDelivery` (§4.15) |

**Diferenças em relação ao §6.1 do épico:**
- `Role` e `Permission` **não são tabelas**. São constantes em código (ADR-0028), e o vínculo é `TenantEmployeeRole`.
- `DailyClosing` passa a pertencer a um `BusinessDay` (ADR-0019).

## 4.2 Diagrama (núcleo operacional)

```mermaid
erDiagram
  Tenant ||--|| TenantSettings : tem
  Tenant ||--|| Subscription : tem
  User ||--o{ TenantEmployee : "vincula-se"
  Tenant ||--o{ TenantEmployee : emprega
  TenantEmployee ||--o{ TenantEmployeeRole : possui
  Tenant ||--o{ BusinessDay : abre
  BusinessDay ||--o| DailyClosing : "fecha em"
  DailyClosing ||--o{ DailyClosingLine : detalha
  BusinessDay ||--o{ CashMovement : registra
  BusinessDay ||--o{ Order : "vende em"
  BusinessDay ||--o{ Payment : "recebe em"
  BusinessDay ||--o{ PaymentReversal : "estorna em"
  Customer ||--o{ Order : "pede"
  Customer ||--o{ CustomerAddress : tem
  Order ||--o{ OrderItem : contem
  OrderItem ||--o{ OrderItem : "adicionais (parent)"
  OrderItem ||--o{ OrderItemComponent : "composição"
  Order ||--o{ WeighingEntry : "pesagens (KILO)"
  Order ||--o{ Payment : "quitado por"
  Payment ||--o| PaymentReversal : "estornado por"
  Order ||--o{ OrderChangeEvent : "alterações"
  OrderChangeEvent ||--o{ AlertAcknowledgement : "ciência"
  Menu ||--o{ MenuItem : lista
  Product ||--o{ MenuItem : "aparece em"
  MarmitaSize ||--o{ OrderItem : "precifica"
```

---

## 4.3 Identity

### User (global, sem tenant)

Identidade autenticada (ASP.NET Core Identity, ADR-0027).

| Campo | Tipo | Regra |
|---|---|---|
| Id | Guid | |
| FullName | string(120) | |
| Email | string(254)? | único (case-insensitive) quando presente; obrigatório para quem é proprietário |
| PhoneNumber | string(16)? | E.164, único quando presente |
| PasswordHash, SecurityStamp, LockoutEnd, AccessFailedCount | Identity | |
| IsPlatformAdmin | bool | `true` só por script de plataforma (ADR-0025) |
| CreatedAt | DateTimeOffset | |

**Invariante:** `Email` ou `PhoneNumber` precisa estar presente.

### Tenant (é o próprio tenant)

| Campo | Tipo | Regra |
|---|---|---|
| Id | Guid | = `TenantId` |
| Name | string(120) | nome fantasia |
| DocumentType | enum `CPF`, `CNPJ` | |
| DocumentNumber | string(14) | só dígitos |
| City, State | string(80), string(2) | |
| TimeZone | string(64) | IANA, padrão `America/Sao_Paulo` |
| TermsVersion, TermsAcceptedAt, TermsAcceptedByUserId | string(20), DateTimeOffset, Guid | aceite dos Termos e do contrato de tratamento no autocadastro (ADR-0018, ADR-0024) |
| CreatedAt | DateTimeOffset | |
| Version | int | [V] |

### TenantSettings [T][V][S] — 1:1 com Tenant

| Campo | Tipo | Regra |
|---|---|---|
| TenantId | Guid | PK |
| KiloPricePerKg | Money? | obrigatório para registrar pesagem (ADR-0020) |
| DefaultDeliveryFee | Money | ≥ 0, padrão 0 (ADR-0021) |
| EnabledPaymentMethods | enum[] | subconjunto de `CASH`, `PIX`, `CARD`; pelo menos 1 |
| SharedDeviceIdleSeconds | int | padrão 120 (ADR-0027) |

### TenantEmployee [T][V][S] — vínculo usuário ↔ estabelecimento

| Campo | Tipo | Regra |
|---|---|---|
| Id | Guid | é o "funcionário" referenciado em toda autoria |
| UserId | Guid | → User. Único por `(TenantId, UserId)` |
| DisplayName | string(60) | nome exibido no estabelecimento e na seleção do dispositivo compartilhado |
| Status | enum `ACTIVE`, `INACTIVE` | |
| PinHash | string? | PIN de 4–6 dígitos, só para dispositivos compartilhados (ADR-0027) |
| PinFailedAttempts, PinLockedUntil | int, DateTimeOffset? | 5 tentativas → bloqueio de 15 min |
| SecurityStamp | Guid | muda ao desativar ou mudar perfis, o que revoga o acesso (ADR-0027) |
| JoinedAt | DateTimeOffset | |
| DeactivatedAt, DeactivatedByEmployeeId | DateTimeOffset?, Guid? | |

**Invariante:** um tenant precisa ter pelo menos um funcionário `ACTIVE` com o perfil `OWNER`.

### TenantEmployeeRole [T]

| Campo | Tipo | Regra |
|---|---|---|
| TenantEmployeeId | Guid | PK composta |
| Role | enum `OWNER`, `MANAGER`, `ATTENDANT`, `KITCHEN`, `CASHIER` | PK composta |

As permissões efetivas são a união dos mapas perfil → permissões definidos em código (ADR-0028).

### EmployeeInvitation [T]

| Campo | Tipo | Regra |
|---|---|---|
| Purpose | enum `INVITE`, `PASSWORD_RESET` | o reset reaproveita o mecanismo do convite (ADR-0018) |
| Code | string(6) | alfanumérico sem caracteres ambíguos; único entre convites `PENDING` |
| Roles | enum[] | obrigatório para `INVITE` |
| TargetEmployeeId | Guid? | obrigatório para `PASSWORD_RESET` |
| Status | enum `PENDING`, `ACCEPTED`, `REVOKED`, `EXPIRED` | |
| ExpiresAt | DateTimeOffset | criação + 72 h |
| CreatedByEmployeeId, CreatedAt | | |
| AcceptedByUserId, AcceptedAt | Guid?, DateTimeOffset? | |

### SharedDevice [T]

Aparelho registrado como compartilhado (ADR-0027).

| Campo | Tipo | Regra |
|---|---|---|
| InstallationId | string(64) | id da instalação do app, gerado pelo cliente; único por tenant |
| Name | string(40) | ex.: "Tablet cozinha" |
| Platform | enum `ANDROID`, `IOS`, `WEB` | `WEB` = navegador do desktop, com o `deviceSecret` em cookie `HttpOnly` (ADR-0053) |
| SecretHash | string(64) | SHA-256 do `deviceSecret` entregue uma única vez no registro. O aparelho se autentica com ele para listar funcionários e fazer o login por PIN (`06-contratos-api.md` §6.3) |
| RegisteredByEmployeeId, RegisteredAt | | |
| RevokedAt | DateTimeOffset? | |

### RefreshToken (identidade, sem RLS de tenant)

| Campo | Tipo | Regra |
|---|---|---|
| UserId | Guid | |
| TenantId, TenantEmployeeId | Guid | tenant ativo da sessão |
| InstallationId | string(64) | |
| SharedDeviceId | Guid? | quando emitido via PIN |
| TokenHash | string | SHA-256; o token em si nunca é gravado |
| FamilyId | Guid | reuso de token já trocado → revoga a família inteira |
| CreatedAt, ExpiresAt, RevokedAt | | 30 dias |
| ReplacedByTokenId | Guid? | |

---

## 4.4 Billing (ADR-0017)

### Subscription [T][V] — 1:1 com Tenant

| Campo | Tipo | Regra |
|---|---|---|
| Status | enum `TRIALING`, `ACTIVE`, `PAST_DUE`, `SUSPENDED`, `CANCELED` | **só muda por webhook, job ou admin** |
| PlanCode | string(30) | |
| MonthlyPrice | Money | snapshot do preço contratado |
| TrialEndsAt | DateTimeOffset | criação + 7 dias |
| CurrentPeriodEndsAt | DateTimeOffset? | |
| PastDueSince, SuspendedAt, CanceledAt | DateTimeOffset? | |
| GatewayCustomerId, GatewaySubscriptionId | string? | Asaas |

### TrialClaim (plataforma)

`ClaimKey` (único) = **SHA-256** do documento normalizado ou do telefone do proprietário (o valor em claro não é guardado, ADR-0024), mais `TenantId` e `ClaimedAt`. Garante um teste grátis por documento ou telefone.

### BillingWebhookEvent (plataforma)

| Campo | Tipo | Regra |
|---|---|---|
| Gateway | string(20) | `ASAAS` |
| GatewayEventId | string(100) | **único**: processamento idempotente |
| EventType | string(60) | |
| TenantId | Guid? | resolvido pelo `GatewaySubscriptionId` |
| Payload | json | só os campos usados; sem dados de cartão |
| ReceivedAt, ProcessedAt | | |

---

## 4.5 Catalog (ADR-0020)

### Product [T][V][S]

| Campo | Tipo | Regra |
|---|---|---|
| Kind | enum `MAIN_DISH`, `SIDE_DISH`, `BEVERAGE`, `ADDON` | imutável após uso em pedido |
| Name | string(80) | único por `(TenantId, Kind)` entre ativos |
| Description | string(300)? | |
| Category | string(40)? | agrupamento livre para exibição ("Refrigerantes", "Carnes") |
| DefaultPrice | Money? | **obrigatório** para `BEVERAGE`/`ADDON`; para `MAIN_DISH` é o preço padrão de PF; nulo para `SIDE_DISH` |
| RequiresPreparation | bool | padrão: `true` para pratos e adicionais, `false` para bebidas. Decide se a inclusão gera produção ou alerta (RN-007.10) |
| ImageStorageKey | string? | chave no R2 (ADR-0037); a URL é derivada |
| IsActive | bool | desativado = some dos cadastros, mas o histórico permanece (RN-002.6) |
| IsAvailable | bool | falta momentânea de bebida ou adicional (pratos usam a disponibilidade do cardápio) |
| IsVisibleOnPage | bool | padrão `true`. Bebida ou adicional com `false` não aparece na página de pedidos (ADR-0044 item 5) |
| CreatedAt, UpdatedAt | | |

### MarmitaSize [T][V][S]

`Name` string(20), `Price` Money > 0, `SortOrder` int, `IsActive` bool. Único por `(TenantId, Name)` entre ativos.

---

## 4.6 Menu (RF-003, ADR-0038)

### Menu [T][V][S]

| Campo | Tipo | Regra |
|---|---|---|
| MenuType | enum `MARMITA`, `PF` | cardápios independentes (RN-003.1) |
| Kind | enum `DAILY`, `WEEKLY_TEMPLATE` | |
| Date | DateOnly? | obrigatório para `DAILY`; único por `(TenantId, MenuType, Date)` |
| DayOfWeek | int? (1–7, ISO) | obrigatório para `WEEKLY_TEMPLATE`; único por `(TenantId, MenuType, DayOfWeek)` |
| SourceMenuId | Guid? | origem do clone ou da materialização (RN-003.4/3.5); só informativo |
| CreatedByEmployeeId, CreatedAt, UpdatedAt | | |

**Resolução do cardápio do dia** (ADR-0038):
- O cardápio vigente é o `DAILY` da `BusinessDate` do expediente aberto.
- Ao abrir o expediente, cada `WEEKLY_TEMPLATE` do dia da semana sem `DAILY` correspondente é **materializado** como um `DAILY` independente.
- Sem nenhum dos dois, a aba oferece criar o cardápio (RN-003.2).

### MenuItem [T]

| Campo | Tipo | Regra |
|---|---|---|
| MenuId, ProductId | Guid | único por `(MenuId, ProductId)` |
| Section | enum `MAIN_DISH`, `SIDE_DISH` | = `Product.Kind`; bebidas e adicionais não entram em cardápio |
| PfPrice | Money? | só em menu `PF` + `MAIN_DISH`; obrigatório nesse caso; padrão = `Product.DefaultPrice` |
| IsAvailable | bool | RN-003.6 |
| SortOrder | int | |

---

## 4.7 Ordering

### Customer [T][V][S] (ADR-0021, ADR-0024)

| Campo | Tipo | Regra |
|---|---|---|
| Name | string(80) | |
| PhoneNumber | string(16)? | E.164; único por tenant quando presente |
| Notes | string(300)? | |
| AnonymizedAt | DateTimeOffset? | após anonimizar: nome "Cliente anonimizado", telefone, endereços e notas apagados |
| CreatedAt | | |

### CustomerAddress [T]

`CustomerId`, `Label` string(30)?, `Street` string(120), `Number` string(10)?, `Complement` string(60)?, `Neighborhood` string(60)?, `Reference` string(120)?, `IsDefault` bool.

### Order [T][V][S] — agregado raiz (ADR-0005)

| Campo | Tipo | Regra |
|---|---|---|
| BusinessDayId | Guid | expediente aberto na criação (ADR-0019); imutável |
| BusinessDate | DateOnly | cópia de `BusinessDay.BusinessDate` = data da venda (ADR-0015) |
| OrderNumber | int? | sequencial por **data operacional** (ADR-0023, ADR-0039), atribuído **no aceite**: nulo enquanto `AcceptanceStatus ≠ ACCEPTED`; recusados, expirados e desistências nunca recebem (ADR-0046) |
| OrderType | enum `MARMITA`, `PF`, `KILO` | imutável |
| Origin | enum `APP`, `WEB` (futuro: `WHATSAPP`) | `WEB` = página de pedidos (EP-002, ADR-0044) |
| FulfillmentType | enum `DELIVERY`, `PICKUP`, `DINE_IN` | `MARMITA` → `DELIVERY`/`PICKUP`; `PF`/`KILO` → `DINE_IN` |
| CustomerId | Guid? | → Customer. Pedido `WEB`: vinculado só no aceite, pelo telefone (ADR-0044 item 20) |
| CustomerName | string(80)? | snapshot; obrigatório em `MARMITA` |
| CustomerPhone | string(16)? | snapshot |
| DeliveryAddress | json? | snapshot do endereço; obrigatório se `DELIVERY` |
| TableLabel | string(10)? | mesa (PF/KILO) |
| TabNumber | string(10)? | número da comanda física (ADR-0023) |
| Notes | string(300)? | |
| ExpectedPaymentMethod | enum `CASH`, `PIX`, `CARD`? | só `WEB`: forma que o cliente pretende usar (ADR-0044 item 19). Não cria `Payment` |
| ChangeFor | Money? | só `WEB` e `CASH`: "troco para R$ X", ≥ total no envio |
| RiskFlag | enum `CAPTCHA_UNAVAILABLE`? | só `WEB`: o anti-robô não respondeu no envio (ADR-0045 item 12) |
| AnonymizedAt | DateTimeOffset? | pedido `WEB` não aceito anonimizado 7 dias depois (nome, telefone, endereço e observações; ADR-0052 item 3) |
| DeliveryFee | Money | 0 se não for `DELIVERY` |
| DeliveryFeeSource | enum? `FIXED_DEFAULT`, `DISTANCE`, `MANUAL` | só em `DELIVERY` (ADR-0048 item 20): padrão do estabelecimento, calculada pela página, ou digitada/editada no app. Editar taxa ou endereço de um `DISTANCE` → `MANUAL`/`FIXED_DEFAULT`; nulo nas entregas anteriores à 0005 |
| DeliveryDistanceMeters, DeliveryPricePerKm, DeliveryMinimumFee | int?, decimal(10,2)?, Money? | snapshot da regra por distância (os três juntos; obrigatórios em `DISTANCE`; ficam como histórico se a taxa for editada) |
| ItemsTotal | Money | Σ `OrderItem.LineTotal` ativos |
| WeighingsTotal | Money | Σ `WeighingEntry.Amount` ativas |
| TotalAmount | Money | `ItemsTotal + WeighingsTotal + DeliveryFee` |
| PaidAmount | Money | Σ `Payment.Amount` com `Status = CONFIRMED` |
| IsPaid | bool | `TotalAmount > 0 && PaidAmount == TotalAmount` (RN-009.3) |
| ProductionStatus | enum `OPEN`, `IN_PROGRESS`, `COMPLETED`? | **nulo** para `KILO` (RN-006.7) e enquanto o pedido não é aceito (ADR-0046) |
| ProductionStatusChangedAt | DateTimeOffset? | |
| CancellationStatus | enum `ACTIVE`, `CANCELED` | ADR-0022 |
| CanceledAt, CanceledByEmployeeId, CancellationReason | ?, ?, string(300)? | |
| AcceptanceStatus | enum `AWAITING_PAYMENT`, `AWAITING_ACCEPTANCE`, `ACCEPTED`, `REJECTED`, `EXPIRED`, `WITHDRAWN` | entrada do pedido (ADR-0046). Pedido do app nasce `ACCEPTED`; `REJECTED`/`EXPIRED`/`WITHDRAWN` são finais. `AWAITING_PAYMENT` só na Fase 2 (ADR-0050) |
| AcceptanceDeadline | DateTimeOffset? | envio + 10 min (pedido `WEB`); nulo no app |
| AcceptedAt, AcceptedByEmployeeId | ?, Guid? | no app = `CreatedAt`/`CreatedByEmployeeId`; funcionário nulo só no aceite do sistema (Fase 2) |
| EstimatedReadyMinutes | int? | 15, 30, 45 ou 60, informado no aceite |
| RejectionReasonCode | enum `ITEM_UNAVAILABLE`, `OUT_OF_DELIVERY_AREA`, `TOO_BUSY`, `CLOSING_SOON`, `SUSPECTED_PRANK`, `OTHER`, `BUSINESS_DAY_CLOSED`? | só em `REJECTED`. `BUSINESS_DAY_CLOSED` = recusa do sistema na finalização do fechamento, sem funcionário |
| RejectionMessage | string(200)? | 5 a 200; obrigatória em `OTHER`; mostrada ao cliente (exceto `SUSPECTED_PRANK`) |
| RejectedAt, RejectedByEmployeeId | ?, Guid? | |
| ExpiredAt, WithdrawnAt | ?, ? | `EXPIRED` (job de expiração) e `WITHDRAWN` (cliente desistiu) |
| FulfillmentStatus | enum `PENDING`, `OUT_FOR_DELIVERY`, `DELIVERED`, `PICKED_UP`? | atendimento (ADR-0046): só em `DELIVERY`/`PICKUP` aceitos; nulo em `DINE_IN`. "Pronto para retirar" é derivado: `PICKUP` + `COMPLETED` + `PENDING` |
| FulfillmentStatusChangedAt | DateTimeOffset? | |
| OutForDeliveryAt, DeliveredAt, PickedUpAt | ?, ?, ? | horário de cada passo; voltar um passo limpa o do passo desfeito |
| CreatedByEmployeeId | Guid? | nulo só em pedido `WEB` |
| CreatedAt, UpdatedAt | | |

`ItemsTotal`, `WeighingsTotal`, `TotalAmount`, `PaidAmount` e `IsPaid` são **valores derivados e persistidos**. São recalculados pelo agregado na mesma transação de qualquer mudança em itens, pesagens, taxa ou pagamentos, e nunca recebidos do cliente.

**Invariantes do agregado:**
1. É preciso ter pelo menos 1 item ativo ou 1 pesagem ativa (na criação e após remoções).
2. `IsPaid` → itens, pesagens, quantidades e `DeliveryFee` imutáveis (ADR-0007, `409 OrderAlreadyPaid`).
3. `PaidAmount ≤ TotalAmount` sempre. Uma edição que deixaria `TotalAmount < PaidAmount` é rejeitada (`422 TotalBelowPaidAmount`).
4. `CANCELED` → somente leitura. Só pode ser cancelado com `PaidAmount == 0` (ADR-0022).
5. Incluir item com `RequiresPreparation` em pedido `COMPLETED` → `ProductionStatus = IN_PROGRESS` (RN-007.10).
6. `WeighingEntry` só em `KILO`. Itens `MARMITA` só em pedido `MARMITA`, itens `PF` só em pedido `PF`. Bebidas e adicionais avulsos em qualquer modalidade (RN-002.5).
7. Taxa de entrega diferente da padrão exige `orders.delivery-fee.edit`.
8. **Pedido não aceito** (`AcceptanceStatus ≠ ACCEPTED`, ADR-0046 item 4): sem número, sem produção, sem cliente vinculado, sem pagamento; não pode ser editado nem cancelado (`409 OrderNotAccepted`) e não gera `OrderChangeEvent`. **Não entra no fechamento** (vendido, pendente, quantidade), na cozinha nem nas pendências do cliente: toda consulta desses filtra `AcceptanceStatus = ACCEPTED`. Pertence ao expediente aberto no envio.
9. **Aceite** (`orders.accept`): na mesma transação, `OrderNumber` do `BusinessDateCounter`, `ProductionStatus = OPEN`, `FulfillmentStatus = PENDING`, vínculo com `Customer`. Depois do prazo → `409 WebOrderExpired`; aceite e expiração disputam pela `Version`. Ao **finalizar o fechamento**, os `AWAITING_ACCEPTANCE` do expediente viram `REJECTED` (`BUSINESS_DAY_CLOSED`) na mesma transação.
10. **Atendimento** (`orders.fulfillment.update`): `PENDING → OUT_FOR_DELIVERY → DELIVERED` (entrega), `PENDING → PICKED_UP` (retirada), "voltou sem entregar" (`OUT_FOR_DELIVERY → PENDING`) e correções de um passo. Sair ou ser retirado exige `ProductionStatus = COMPLETED` ("Concluir e marcar" conclui na mesma transação, exigindo também `production.update`). Independente do financeiro. Com o pedido fora (`OUT_FOR_DELIVERY`, `DELIVERED`, `PICKED_UP`), incluir item → `409 OrderAlreadyDispatched` (ADR-0046 item 19). Vale também para entrega e retirada criadas no app (uso opcional).

### OrderItem [T] — filho de Order

| Campo | Tipo | Regra |
|---|---|---|
| OrderId | Guid | |
| ParentOrderItemId | Guid? | adicional vinculado a uma marmita ou PF; nulo = item de primeiro nível |
| ItemKind | enum `MARMITA`, `PF`, `BEVERAGE`, `ADDON` | |
| ProductId | Guid? | `PF` → prato; `BEVERAGE`/`ADDON` → produto; nulo em `MARMITA` |
| MarmitaSizeId | Guid? | obrigatório em `MARMITA` |
| Quantity | int | ≥ 1. Em adicional filho, é a quantidade **por unidade do pai** |
| UnitPrice | Money | snapshot: tamanho (MARMITA), `MenuItem.PfPrice` (PF) ou `Product.DefaultPrice` |
| LineTotal | Money | `UnitPrice × Quantity × (Parent?.Quantity ?? 1)` |
| NameSnapshot | string(80) | ex.: "Marmita M", "PF Frango grelhado", "Coca 350ml" |
| CompositionSnapshot | string(500)? | texto legível da composição para a cozinha |
| RequiresPreparation | bool | snapshot de `Product.RequiresPreparation` (MARMITA/PF = `true`) |
| Notes | string(200)? | |
| Status | enum `ACTIVE`, `REMOVED` | remoção é lógica; totais ignoram `REMOVED` |
| CreatedByEmployeeId | Guid? | nulo só nos itens montados pelo cliente na página (pedido `WEB`) |
| CreatedAt, RemovedByEmployeeId, RemovedAt | | |

**Regras:**
- Adicionais só podem ser filhos de `MARMITA`/`PF`. Filho de filho não existe.
- Remover o pai remove os filhos.
- `Quantity` é editável na própria linha enquanto o pedido não está pago, e a mudança gera `OrderChangeEvent` `QUANTITY_CHANGED`. Não é preciso remover a linha e incluir outra.

### OrderItemComponent [T] — composição de marmita

`OrderItemId`, `ProductId`, `Role` enum `MAIN_DISH`/`SIDE_DISH`, `NameSnapshot` string(80). Não tem preço (ADR-0020). Uma marmita precisa de pelo menos 1 `MAIN_DISH`.

### WeighingEntry [T] — filho de Order (KILO)

| Campo | Tipo | Regra |
|---|---|---|
| WeightGrams | int | 1–50.000 |
| PricePerKg | Money | snapshot de `TenantSettings.KiloPricePerKg` (RN-006.3) |
| Amount | Money | `round(WeightGrams × PricePerKg / 1000, 2, AwayFromZero)` (ADR-0030) |
| Status | enum `ACTIVE`, `REMOVED` | |
| CreatedByEmployeeId, CreatedAt, RemovedByEmployeeId, RemovedAt | | |

### OrderChangeEvent [T] (RF-008, ADR-0031)

| Campo | Tipo | Regra |
|---|---|---|
| OrderId | Guid | |
| OrderVersion | int | versão do pedido após a alteração |
| ChangeKind | enum `ITEM_ADDED`, `ITEM_REMOVED`, `QUANTITY_CHANGED`, `ITEM_NOTES_CHANGED`, `ORDER_NOTES_CHANGED`, `WEIGHING_ADDED`, `WEIGHING_REMOVED`, `FULFILLMENT_CHANGED`, `ORDER_CANCELED` | |
| AffectsProduction | bool | `true` se envolve item com `RequiresPreparation`, observação de item ou cancelamento de pedido com produção |
| Details | json | antes/depois: nomes, quantidades, observações; **sem dados de cliente** |
| ActorEmployeeId | Guid | |
| OccurredAt | DateTimeOffset | |

A criação do pedido **não** gera `OrderChangeEvent`. Gera o evento de tempo real `OrderCreated`.

### AlertAcknowledgement [T]

PK `(OrderChangeEventId, TenantEmployeeId)`, `AcknowledgedAt`.

**Alerta pendente** para o funcionário F = `OrderChangeEvent` E tal que:
- `E.ActorEmployeeId ≠ F` (RN-008.6);
- F pode ver aquele pedido (permissão de produção para eventos com `AffectsProduction`; de pedidos para os demais);
- não existe `AlertAcknowledgement (E, F)` (RN-008.7);
- o pedido pertence a um expediente ainda `OPEN`.

---

## 4.8 Payments (ADR-0021, ADR-0022)

### Payment [T][S]

| Campo | Tipo | Regra |
|---|---|---|
| OrderId | Guid | |
| BusinessDayId, BusinessDate | Guid, DateOnly | expediente aberto no recebimento (ADR-0015) |
| Amount | Money | > 0 e ≤ saldo do pedido no momento |
| PaymentMethod | enum `CASH`, `PIX`, `CARD` | precisa estar em `EnabledPaymentMethods` |
| CashReceived | Money? | só `CASH`; ≥ `Amount` |
| ChangeGiven | Money? | `CashReceived − Amount` |
| Label | string(30)? | ex.: "Pessoa 2" (divisão de conta) |
| Status | enum `CONFIRMED`, `REVERSED` | |
| PaidAt | DateTimeOffset | |
| CreatedByEmployeeId | Guid | |

Um pagamento é imutável, exceto a transição `CONFIRMED → REVERSED`. Sempre estorna o valor integral.

### PaymentReversal [T][S]

| Campo | Tipo | Regra |
|---|---|---|
| PaymentId | Guid | **único** (um estorno por pagamento) |
| OrderId | Guid | |
| BusinessDayId, BusinessDate | Guid, DateOnly | expediente aberto **no estorno** (ADR-0014) |
| Amount | Money | = `Payment.Amount` |
| PaymentMethod | enum | = `Payment.PaymentMethod`; estorno `CASH` sai do dinheiro esperado |
| Reason | string(300) | mínimo 5 caracteres |
| CreatedByEmployeeId, CreatedAt | | |

---

## 4.9 Closing (ADR-0013, ADR-0019, ADR-0039)

### BusinessDay [T][V][S] — expediente

| Campo | Tipo | Regra |
|---|---|---|
| BusinessDate | DateOnly | data local na abertura; **não** é única (ADR-0039) |
| Status | enum `OPEN`, `CLOSED` | no máximo 1 `OPEN` por tenant |
| OpeningBalance | Money | ≥ 0 |
| OpenedByEmployeeId, OpenedAt | | |
| ClosedByEmployeeId, ClosedAt | ? | |

### BusinessDateCounter [T]

PK `(TenantId, BusinessDate)`, `LastOrderNumber int`. Fonte do `OrderNumber` (`UPDATE ... RETURNING`). Vários expedientes da mesma data continuam a mesma sequência (ADR-0039).

### CashMovement [T][S]

`BusinessDayId`, `MovementType` enum `SUPPLY`/`WITHDRAWAL`, `Amount` Money > 0, `Description` string(200), `CreatedByEmployeeId`, `CreatedAt`. Imutável. Uma correção é feita com outro movimento.

### DailyClosing [T][S] — snapshot imutável

| Campo | Tipo | Regra |
|---|---|---|
| BusinessDayId | Guid | **único** |
| BusinessDate | DateOnly | |
| OrderCount | int | pedidos `ACTIVE` do expediente. Em todo o fechamento, "pedido" = pedido **aceito** (`AcceptanceStatus = ACCEPTED`, ADR-0046) |
| TotalSold | Money | Σ `TotalAmount` dos pedidos `ACTIVE` do expediente |
| DeliveryFeesTotal | Money | parte de `TotalSold` |
| TotalReceived | Money | Σ pagamentos recebidos neste expediente, de qualquer data de venda. Inclui os que foram estornados depois, porque o estorno é contado à parte em `ReversedTotal` do expediente em que ocorreu |
| ReceivedFromPreviousDays | Money | parte de `TotalReceived` relativa a pedidos de expedientes anteriores (RF-011) |
| TotalPending | Money | Σ `(TotalAmount − PaidAmount)` dos pedidos `ACTIVE` do expediente, no instante do fechamento |
| CanceledCount, CanceledTotal | int, Money | pedidos do expediente cancelados |
| ReversedCount, ReversedTotal | int, Money | estornos registrados neste expediente |
| OpeningBalance | Money | |
| CashReceived | Money | Σ `Amount` de pagamentos `CASH` recebidos aqui |
| CashReversed | Money | Σ estornos `CASH` registrados aqui |
| Supplies, Withdrawals | Money | |
| ExpectedCash | Money | `OpeningBalance + CashReceived + Supplies − Withdrawals − CashReversed` |
| CountedCash | Money | informado |
| CashDifference | Money | `CountedCash − ExpectedCash` |
| Justification | string(500)? | obrigatória se `CashDifference ≠ 0` (RN-010.13) |
| NotAcceptedCount | int | informativo, fora do vendido: pedidos do site do expediente recusados, expirados ou desistências (RF-210.2), incluindo os recusados pela finalização |
| AutoRejectedCount | int | parte de `NotAcceptedCount`: os `AWAITING_ACCEPTANCE` recusados pela finalização (`BUSINESS_DAY_CLOSED`, ADR-0046 item 13) |
| UnmarkedDeliveryCount | int | informativo: entregas aceitas e ativas sem `DELIVERED` (ADR-0046 item 20) |
| ClosedByEmployeeId, ClosedAt | | |

### DailyClosingLine [T] — detalhamento do snapshot

| Campo | Tipo | Regra |
|---|---|---|
| DailyClosingId | Guid | |
| Dimension | enum `ORDER_TYPE`, `PAYMENT_METHOD` | |
| Key | string | `MARMITA`/`PF`/`KILO` ou `CASH`/`PIX`/`CARD` |
| Sold, Received, Pending, Reversed | Money | `Sold`/`Pending` só em `ORDER_TYPE` |
| Count | int | pedidos (ORDER_TYPE) ou pagamentos (PAYMENT_METHOD) |

`ORDER_TYPE.Received` agrupa os pagamentos pelo `OrderType` do pedido pago. A tabela "Fechamento por modalidade" do épico é a projeção dessas linhas. O histórico mensal é a soma dos `DailyClosing` do mês.

### ClosingAdjustment [T][S]

| Campo | Tipo | Regra |
|---|---|---|
| RelatedClosingId | Guid | fechamento que está sendo corrigido |
| BusinessDayId | Guid | expediente aberto em que o ajuste foi registrado |
| AdjustmentType | enum `CASH_COUNT_CORRECTION`, `OTHER` | |
| Amount | Money (com sinal) | |
| Reason | string(500) | |
| CreatedByEmployeeId, CreatedAt | | permissão `closing.adjust` |

O ajuste **não** altera o `DailyClosing` relacionado nem o caixa físico. Aparece no histórico do fechamento original ("corrigido por") e no relatório do expediente em que foi registrado.

---

## 4.10 Audit (ADR-0008, ADR-0031)

### AuditLog — append-only

| Campo | Tipo | Regra |
|---|---|---|
| TenantId | Guid? | nulo só em ações de plataforma sem tenant |
| Origin | enum `APP`, `PLATFORM_ADMIN`, `SYSTEM`, `BILLING_WEBHOOK`, `WEB` | RN-013.4. `WEB` = ação do cliente pela página (envio, desistência), sem ator (ADR-0046) |
| ActorUserId, ActorEmployeeId | Guid? | |
| SharedDeviceId | Guid? | |
| OperationType | string(60) | semântico: `OrderCreated`, `OrderItemAdded`, `PaymentRegistered`, `PaymentReversed`, `ClosingFinalized`, `EmployeeRolesChanged`… |
| EntityType, EntityId | string(40), Guid | |
| OrderId | Guid? | para `GET /orders/{id}/history` |
| Before, After | json? | só os campos relevantes; **sem telefone, endereço nem token** (ADR-0024) |
| CorrelationId | string(64) | `traceId` |
| OccurredAt | DateTimeOffset | |

---

## 4.11 Sync (ADR-0031–0033)

| Entidade | Campos | Regra |
|---|---|---|
| TenantSyncCounter | `TenantId` PK, `LastSeq` bigint | trava de linha por transação de escrita |
| ChangeFeedEntry [T] | PK `(TenantId, Seq)`, `EntityType`, `EntityId`, `EntityVersion`, `ChangeKind` (`UPSERT`/`DELETE`), `OccurredAt` | retenção de 7 dias |
| IdempotencyRecord [T] | `Key`, `UserId`, `Endpoint`, `RequestHash`, `Status` (`IN_PROGRESS`/`COMPLETED`), `ResponseStatus`, `ResponseBody` json, `ResourceId`, `CreatedAt`, `ExpiresAt` | único `(TenantId, Key)`; TTL de 72 h |
| OutboxMessage [T] | `EventType`, `Payload` json (ids, versão, `seq`), `OccurredAt`, `AvailableAt?`, `ProcessedAt?`, `Attempts` | limpeza após 7 dias. `AvailableAt`: entrega agendada (lembrete do push, ADR-0046 item 15) ou espera entre tentativas de um consumidor interno; nulo = na hora |

**Entidades [S]:** `TenantSettings`, `TenantEmployee`, `Product`, `MarmitaSize`, `Menu` (inclui itens), `Customer` (inclui endereços), `Order` (inclui itens, pesagens e eventos), `Payment`, `PaymentReversal`, `BusinessDay`, `CashMovement`, `DailyClosing`, `ClosingAdjustment`.

---

## 4.12 Isolamento por tipo de tabela (ADR-0026)

| Tipo | Tabelas | Política |
|---|---|---|
| Tenant | todas as marcadas [T], mais `Tenant` (por `id`) e `Subscription` | `tenant_id = app.tenant_id` |
| Vínculo | `TenantEmployee`, `TenantEmployeeRole` | `tenant_id = app.tenant_id` **OU** `user_id = app.user_id`. Permite listar os estabelecimentos do próprio usuário no login e na troca de tenant (ADR-0026, item 9) |
| Identidade | `User`, `RefreshToken` | sem RLS de tenant; acesso só pelo módulo Identity |
| Plataforma | `TrialClaim`, `BillingWebhookEvent` | sem RLS; acesso só pelos módulos Billing e Admin com o papel `almoseis_platform` |
| Resolução pública | `StoreSlug`, `OrderTrackingToken` | leitura aberta ao papel da aplicação (o slug é público; o token está em hash), escrita só no tenant da sessão (ADR-0045 itens 4 e 5) |
| Tenant + dono do token | `PushRegistration` | `tenant_id = app.tenant_id`; além disso, `SELECT`/`DELETE` (nunca `INSERT`/`UPDATE`) das linhas com `expo_push_token = app.push_token`, definido só na transação do registro: o aparelho que trocou de estabelecimento sai do anterior (ADR-0049 item 4) |

## 4.14 Página de pedidos (EP-002; ADR-0044, ADR-0045, ADR-0047, ADR-0048, ADR-0052)

### StorefrontSettings [T][V] — 1:1 com Tenant

| Campo | Tipo | Regra |
|---|---|---|
| PublicName | string(80) | padrão: `Tenant.Name` |
| IsPageActive | bool | nasce `false`. Ativar exige endereço completo (rua, número, bairro, cidade, UF) e um `MarmitaSize` ativo |
| IsAcceptingOrders | bool | pausa rápida: `false` → página `PAUSED` |
| IsDeliveryEnabled | bool | pausa da entrega (só retirada) |
| OffersDelivery, OffersPickup | bool | pelo menos um |
| ContactPhone | string(16)? | E.164 |
| HoursText | string(120)? | informativo |
| Address* | Street(120), Number(10), Complement(60), Neighborhood(60), City(80), State(2), ZipCode(8) | endereço do restaurante |
| Latitude, Longitude | decimal(9,6)? | ponto do restaurante: geocodificado ao salvar o endereço e confirmado (ou arrastado) no mapa pelo proprietário; apagado quando o endereço muda |
| LocationConfirmedAt | timestamptz? | confirmação do ponto; exige ponto. Mudar o endereço apaga (ADR-0048 item 3) |
| DeliveryFeeMode | enum `DISTANCE`, `FIXED` | ADR-0048 item 1. `FIXED` = `TenantSettings.DefaultDeliveryFee`, sem mapa |
| PricePerKm | decimal(10,2)? | > 0 |
| MinimumDeliveryFee | Money | ≥ 0 |
| MaxDeliveryDistanceKm | decimal(4,1)? | > 0 |

Estado da página (derivado, ADR-0044 item 6): `UNAVAILABLE` (inativa ou assinatura sem escrita) → `CLOSED` (sem expediente aberto) → `PAUSED` → `OPEN`.

Entrega pela página (derivado, ADR-0048): `OffersDelivery` e `IsDeliveryEnabled`; no modo `DISTANCE`, também `PricePerKm`, ponto confirmado e provedor de mapas configurado. Salvar sem isso não é erro: a página só não oferece entrega.

### DeliveryQuote [T] (ADR-0048 item 8)

`Id`, `AddressHash` (SHA-256 do tenant + endereço normalizado), `FormattedAddress` (200), `Latitude`/`Longitude`, `FeeMode`, `DistanceMeters`, `DistanceKm` (decimal(5,1), uma casa), `PricePerKm`, `MinimumFee`, `DeliveryFee`, `SettingsVersion` (versão do `StorefrontSettings` usada), `CreatedAt`, `ExpiresAt` (+ 15 min). `DISTANCE` exige ponto, metros e regra; `FIXED` não tem nenhum deles. Apagada 24 h depois de vencer.

Cálculos: `km = round(metros / 1000, 1, AwayFromZero)`; `taxa = max(round(km × PricePerKm, 2, AwayFromZero), MinimumDeliveryFee)`; fora da área se `km > MaxDeliveryDistanceKm` (o `km` arredondado) ou sem rota. No envio: válida + mesmo hash + mesma versão → a taxa dela; senão recalcula, e taxa diferente → `409 DeliveryQuoteChanged` com a cotação nova.

### GeocodeCacheEntry [T] (ADR-0048 item 11, ADR-0052 item 5)

PK `(TenantId, AddressHash)`. `Latitude`, `Longitude`, `GeocodedAt`, `ExpiresAt` (+ 30 dias) e, se a rota foi calculada, `OriginLatitude`/`OriginLongitude` (o ponto do restaurante usado), `DistanceMeters` e `RoutedAt`. **Sem o texto do endereço.** Só resultado exato entra. A distância só vale com o mesmo ponto de partida.

### StoreSlug (plataforma)

`Slug` string(40) **único global** (PK), `TenantId`, `IsCurrent` (um por tenant), `CreatedAt`, `ReplacedAt` (nulo ⇔ atual), `BlockedUntil` (quarentena de 12 meses depois da anonimização do tenant). Formato `a-z0-9-`, 3 a 40, sem `-` nas pontas nem repetido, não só números, fora dos reservados. Máximo de 5 antigos por tenant; antigo redireciona.

### OrderTrackingToken (plataforma)

`TokenHash` (SHA-256, PK), `TenantId`, `OrderId` (único), `CreatedAt`, `ExpiresAt` (+ 7 dias). O token em claro (128 bits, base64url) só aparece na resposta do envio. Apagado pelo job de retenção depois de vencer.

### WebOrderIntake [T]

`OrderId` (PK), `IpHash` (HMAC-SHA-256 com sal diário), `CreatedAt`. Limite de 3 pedidos aguardando aceite por IP; apagado em 7 dias.

## 4.15 Notificações push (EP-002; ADR-0049, ADR-0052)

### PushRegistration [T] (ADR-0049 itens 4 a 6)

| Campo | Tipo | Regra |
|---|---|---|
| InstallationId | string(64) | a do access token; único por tenant |
| TenantEmployeeId | Guid | funcionário da sessão que registrou. No aparelho compartilhado, o último que entrou por PIN |
| SharedDeviceId | Guid? | preenchido no login por PIN; a revogação do aparelho apaga o registro |
| Platform | enum `ANDROID`, `IOS`, `WEB` | `WEB` reservado para o Web Push (Q-26); a API recusa por enquanto |
| ExpoPushToken | string(200) | `ExponentPushToken[…]`/`ExpoPushToken[…]`. **Único em todo o sistema**: o aparelho que registra o token num estabelecimento sai dos outros. Nunca vai para log nem auditoria |
| WebOrdersEnabled | bool | "Avisar pedidos do site com o app fechado"; ligada por padrão |
| ShowTenantName | bool | usuário com mais de um vínculo ativo (calculado a cada registro; falso no aparelho compartilhado): o título leva o nome do estabelecimento |
| CreatedAt, UpdatedAt | | `UpdatedAt` = último registro feito pelo app; sem uso há 30 dias → apagado |

Destinatários do push, resolvidos **no envio**: registros do tenant com `WebOrdersEnabled`, plataforma `ANDROID`/`IOS`, funcionário ativo e com `orders.accept` agora (item 5). Apagado no logout fora do aparelho compartilhado, na troca de estabelecimento, na desativação e na troca de perfis, na revogação do aparelho e com `DeviceNotRegistered`. Sem auditoria (configuração técnica do aparelho).

### PushDelivery [T] (ADR-0049 item 8)

PK `(EventId, RegistrationId)`: o `eventId` da mensagem do outbox e o registro. `OrderId`, `Kind` (`WEB_ORDER_AWAITING_ACCEPTANCE`, `WEB_ORDER_REMINDER`), `TicketId?` (sucesso) **ou** `Error?` (erro do ticket, ex. `DeviceNotRegistered`), `SentAt`, `ReceiptCheckedAt?`, `ReceiptError?`. Deduplica o reenvio do outbox (um aparelho não recebe o mesmo aviso duas vezes) e guarda o ticket para a conferência do recibo depois de 15 min. Sem token, sem conteúdo. Apagada em 7 dias.

Conteúdo (derivado, itens 2 e 3): título `Novo pedido do site` / `Pedido do site esperando` (+ ` · {estabelecimento}` com `ShowTenantName`); corpo `{n} marmita(s) · Entrega|Retirada · aceite em até {m} min` / `Faltam {m} min para expirar`, com `m = ⌈segundos até o prazo / 60⌉` no envio. Sem nome, telefone, endereço nem valor.

## 4.13 Mapeamento épico → modelo

| Épico (§6) | Aqui |
|---|---|
| `Order.TableNumber` | `TableLabel` + `TabNumber` |
| `Order.CustomerName/CustomerPhone` | `CustomerId` + snapshots |
| `Order.IsPaid` | derivado e persistido, com `PaidAmount` |
| `Payment.IdempotencyKey` | `IdempotencyRecord` (escopo de qualquer operação crítica) |
| `Payment.Status` | `CONFIRMED`/`REVERSED` + `PaymentReversal` |
| `DailyClosing.BusinessDate` | + `BusinessDayId` e o detalhamento em `DailyClosingLine` |
| `CashMovement.BusinessDate` | `BusinessDayId` (saldo inicial fica em `BusinessDay.OpeningBalance`) |
| `Role`, `Permission` | constantes em código + `TenantEmployeeRole` |
| `AuditLog` | + `Origin`, `OrderId`, `CorrelationId` |
