# 07. Backend .NET — arquitetura classe por classe

> Implementa o ADR-0029 (estrutura), sobre `04-modelo-dados.md`, `05-ddl.md` e `06-contratos-api.md`. Diz **onde cada coisa mora e como as peças conversam**. Os payloads estão no `06`, o schema no `05` e as decisões nos ADRs. Uma classe nova que não se encaixe aqui precisa de uma atualização deste documento antes.

## 7.1 Solução e dependências

```text
backend/
  AlmoSeis.sln
  Directory.Build.props          # net10.0, Nullable, ImplicitUsings, TreatWarningsAsErrors, AnalysisLevel latest
  Directory.Packages.props       # Central Package Management: versões fixadas num só lugar
  src/
    AlmoSeis.SharedKernel/
    AlmoSeis.Infrastructure/
    AlmoSeis.Modules.Identity/
    AlmoSeis.Modules.Billing/
    AlmoSeis.Modules.Catalog/
    AlmoSeis.Modules.Menu/
    AlmoSeis.Modules.Ordering/
    AlmoSeis.Modules.Payments/
    AlmoSeis.Modules.Closing/
    AlmoSeis.Modules.Audit/
    AlmoSeis.Modules.Sync/
    AlmoSeis.Api/
  tests/
    AlmoSeis.UnitTests/
    AlmoSeis.IntegrationTests/
    AlmoSeis.ArchitectureTests/
```

**Referências permitidas** (as setas apontam para o projeto referenciado):

```text
Api ──► Modules.* ──► Infrastructure ──► SharedKernel
 │          │
 │          └──► Contracts de outros módulos (mesmo projeto referenciado; só tipos públicos)
 └──► Infrastructure
```

| Projeto | Conteúdo | Pode referenciar |
|---|---|---|
| `SharedKernel` | Primitivas de domínio, interfaces de marcação, erros, abstrações de handler | nada (só BCL) |
| `Infrastructure` | `AppDbContext`, `UnitOfWork`, interceptores, tenant/RLS, idempotência, feed, outbox, auditoria, storage, e-mail, tokens | `SharedKernel`, EF Core, Npgsql, Azure SDKs |
| `Modules.X` | `Domain/`, `Application/`, `Infrastructure/`, `Contracts/`, `XModule.cs` | `SharedKernel`, `Infrastructure`, outros `Modules.*` (**só** o namespace `Contracts`) |
| `Api` | Host, controllers, hub, middlewares, filtros, composição | todos |

**Visibilidade:** em cada módulo, tudo é `internal`, exceto `Contracts/*` e a classe `XModule` (registro). Assim um módulo **não consegue** usar o domínio de outro, mesmo referenciando o projeto. `InternalsVisibleTo` só para `AlmoSeis.UnitTests` e `AlmoSeis.IntegrationTests`.

### Pacotes (versões em `Directory.Packages.props`, sempre a estável alinhada ao .NET 10)

| Uso | Pacote |
|---|---|
| EF + PostgreSQL | `Npgsql.EntityFrameworkCore.PostgreSQL`, `EFCore.NamingConventions` |
| Validação | `FluentValidation`, `FluentValidation.DependencyInjectionExtensions` |
| DI por convenção / decorators | `Scrutor` |
| Identidade (hash de senha, tokens de reset) | `Microsoft.Extensions.Identity.Core` (sem `IdentityDbContext`) |
| JWT | `Microsoft.AspNetCore.Authentication.JwtBearer` |
| OpenAPI | `Microsoft.AspNetCore.OpenApi` (UI `Scalar.AspNetCore` só em homolog) |
| Azure | `Azure.Identity`, `Azure.Extensions.AspNetCore.Configuration.Secrets`, `Azure.Communication.Email`, `Azure.Monitor.OpenTelemetry.AspNetCore` |
| R2 (S3) | `AWSSDK.S3` |
| Observabilidade | `OpenTelemetry.Extensions.Hosting`, `Npgsql.OpenTelemetry` |
| Testes | `xunit.v3`, `Shouldly`, `NSubstitute`, `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.PostgreSql`, `NetArchTest.Rules` |

**Fora (decisão):** MediatR (ADR-0029), FluentAssertions (licença comercial desde a v8), AutoMapper (mapeamento é explícito, em métodos `ToDto()`), Serilog (logs via `ILogger` + exportador OpenTelemetry, ADR-0037).

---

## 7.2 SharedKernel

```csharp
namespace AlmoSeis.SharedKernel.Domain;

public abstract class Entity
{
    public Guid Id { get; protected init; }
}

public abstract class AggregateRoot : Entity, IVersioned
{
    private readonly List<IDomainEvent> _events = [];
    public int Version { get; private set; } = 1;
    public IReadOnlyList<IDomainEvent> DomainEvents => _events;

    /// Toda mutação do agregado (inclusive dos filhos) chama Touch() uma vez. O EF usa o Version original no WHERE (ADR-0034).
    protected void Touch(IDomainEvent? @event = null) { ... }
    public void EnsureVersion(int expected) { if (expected != Version) throw new VersionConflictException(expected, Version); }
    public void ClearDomainEvents() => _events.Clear();
}

public interface ITenantOwned  { Guid TenantId { get; } }
public interface IVersioned    { int Version { get; } }
public interface ISyncTracked  { string SyncEntityType { get; } }   // "ORDER", "PRODUCT"... (06 §6.12)
public interface IDomainEvent  { DateTimeOffset OccurredAt { get; } }
```

| Tipo | Responsabilidade |
|---|---|
| `Money` (`readonly record struct`) | `decimal` com 2 casas, `Of(decimal)` rejeita mais de 2 casas, operadores `+ - *`, `Zero`. Não existe conversão implícita a partir de `double` (ADR-0030) |
| `Grams` (`readonly record struct`) | `int` 1–50.000 |
| `KiloPricing.Amount(Grams, Money pricePerKg)` | `Math.Round(g * p / 1000m, 2, MidpointRounding.AwayFromZero)`: **a única** implementação da regra de pesagem |
| `ErrorCode` | `sealed record ErrorCode(string Code, int HttpStatus, string Title)`. Os valores estão em `ErrorCodes` (espelho exato de 06 §6.1) |
| `BusinessException` | `(ErrorCode code, string? detail = null, IReadOnlyDictionary<string, object?>? extensions = null)` |
| `NotFoundException`, `VersionConflictException(entityType, entityId, expected, current)` | Especializações de `BusinessException`. `entityType`/`entityId` dizem ao Api qual recurso recarregar para o `current` do `409` |
| `ICommandHandler<TCommand, TResult>` / `IQueryHandler<TQuery, TResult>` | `Task<TResult> HandleAsync(T request, CancellationToken ct)` |
| `IIdGenerator` | `Guid New()` → `Guid.CreateVersion7(timeProvider.GetUtcNow())` |
| `Permissions` | Constantes de string (`Permissions.Orders.Create = "orders.create"`…) |
| `Roles` + `RolePermissions` | Matriz de 06 §6.2, em código, coberta por teste |

**Erros:** regras de negócio lançam `BusinessException` com um `ErrorCode`. Não há tipo `Result<T>`: o fluxo feliz fica linear e o mapeamento para HTTP acontece num lugar só (§7.4). Validação de formato fica nos `AbstractValidator<T>` (FluentValidation) e vira `422 ValidationFailed`.

**Tempo:** sempre `TimeProvider` (BCL), injetado. Nunca `DateTime.Now`/`UtcNow` (regra de arquitetura, §7.12).

---

## 7.3 Infrastructure

### Persistência

| Classe | Responsabilidade |
|---|---|
| `AppDbContext : DbContext` | Sem `DbSet` públicos: módulos usam `Set<T>()`. `OnModelCreating` aplica `UseSnakeCaseNamingConvention`, todas as `IModuleModelConfiguration` recebidas por DI e o **query filter de tenant** em toda entidade `ITenantOwned`: `e => _tenant.BypassFilter \|\| e.TenantId == _tenant.TenantId` |
| `PlatformDbContext : AppDbContext` | Connection string do papel `almoseis_platform`, `BypassFilter = true`. Usada **só** por admin, webhooks, dispatcher e jobs (ADR-0025) |
| `IModuleModelConfiguration` | `void Configure(ModelBuilder b)`: cada módulo implementa uma e registra suas `IEntityTypeConfiguration<T>` |
| `TenantSessionConnectionInterceptor : DbConnectionInterceptor` | Em `ConnectionOpened(Async)`: `SELECT set_config('app.tenant_id', @t, false), set_config('app.user_id', @u, false)`. O Npgsql executa `DISCARD ALL` ao devolver a conexão ao pool, o que limpa as variáveis |
| `TenantGuardSaveChangesInterceptor` | Em `SavingChanges`: preenche `TenantId` de entidades `Added` com `_tenant.TenantId` e **lança** se alguma entidade `ITenantOwned` tiver outro tenant |
| `ISessionVariables` | `SetAsync(name, value)` sobre a conexão corrente. Uso: `app.invitation_code` no aceite de convite (05 §5.12) |
| `UnitOfWork` (scoped) | Ver "ciclo de uma escrita" abaixo |
| `MoneyConverter`, `GramsConverter` | Value converters do EF (`numeric(12,2)` ↔ `Money`, `integer` ↔ `Grams`) |
| `Persistence/Migrations/` | **SQL-first (ADR-0042):** cada migration é uma classe mínima que executa um script embutido (`Sql/NNNN_*.sql`), gerado de `05-ddl.md` por `backend/tools/Sync-DdlMigration.ps1`. Não se usa `dotnet ef migrations add` |

### Ciclo de uma escrita (sem MediatR)

`TransactionalCommandHandlerDecorator<TCommand, TResult>` envolve **todo** `ICommandHandler` (registrado via `Scrutor.Decorate`):

```text
1. Se não há transação ambiente → BEGIN (READ COMMITTED). Se o IdempotencyFilter já abriu, reutiliza a dele.
2. handler.HandleAsync(...)          // carrega agregados, chama métodos de domínio, IAuditWriter.Add(...)
3. unitOfWork.CommitAsync():
   a. coleta DomainEvents dos agregados rastreados
   b. ChangeFeedWriter: para cada entidade ISyncTracked Added/Modified → UPDATE tenant_sync_counters ... RETURNING → ChangeFeedEntry (ADR-0032)
   c. OutboxWriter: cada evento de domínio → IIntegrationEventMapper<TEvent> → OutboxMessage(s) com o seq da entidade (ADR-0031)
   d. SaveChangesAsync()             // interceptores: guarda de tenant; o token de concorrência Version vai no WHERE
   e. limpa os eventos dos agregados
4. Se a transação é dele → COMMIT. Em exceção → ROLLBACK e propaga.
```

`DbUpdateConcurrencyException` vira `VersionConflictException`. O filtro de erro recarrega o recurso para preencher `current` (06 §6.1).

### Serviços transversais

| Classe | Responsabilidade |
|---|---|
| `ITenantContext` / `TenantContext` (scoped, imutável após `Initialize`) | `TenantId`, `UserId`, `EmployeeId`, `SharedDeviceId`, `BypassFilter`. Preenchido pelo `TenantContextMiddleware` a partir das claims. Vazio em rotas públicas |
| `IAuditWriter` / `AuditWriter` | `Add(operationType, entityType, entityId, orderId?, before?, after?)` adiciona `AuditLog` ao contexto (mesma transação). Preenche origem, ator, dispositivo e `correlationId` = `Activity.Current.TraceId`. `before`/`after` são **objetos anônimos escolhidos pelo handler**, nunca a entidade inteira (ADR-0024, ADR-0031) |
| `ChangeFeedWriter` | Etapa 3b |
| `OutboxWriter` + `IIntegrationEventMapper<TEvent>` | Etapa 3c. Os mappers ficam nos módulos e produzem o envelope de 06 §6.14 |
| `IdempotencyStore` | `TryBeginAsync(key, userId, endpoint, requestHash)` → `Started` \| `Replay(stored)` \| `KeyReused`. `CompleteAsync(status, body, resourceId)`. Usa a mesma transação (§7.4) |
| `IObjectStorage` / `R2ObjectStorage` | `CreateUploadUrlAsync(key, contentType, sizeBytes)` (URL pré-assinada PUT de 15 min), `PublicUrl(key)` |
| `IEmailSender` / `AcsEmailSender` | ADR-0040 |
| `IJwtTokenIssuer` / `JwtTokenIssuer` | Access token RS256 de 15 min. Chave privada no Key Vault (local: `user-secrets`). Claims: `sub`, `tenant_id`, `employee_id`, `device_id`, `security_stamp`, `jti` (ADR-0027) |
| `IRefreshTokenService` | Emite, rotaciona (família), revoga. Grava só SHA-256 |
| `ISecretHasher` | SHA-256 hex para refresh token, `deviceSecret`, `ClaimKey` de teste grátis e hash de request da idempotência |
| `IAdvisoryLock` | `pg_try_advisory_lock(hash(jobName))`: garante uma execução por job quando houver mais de uma réplica |

---

## 7.4 Api — pipeline HTTP

Ordem em `Program.cs`:

```text
UseForwardedHeaders            // IP real do Container Apps (ADR-0041)
UseExceptionHandler            // ProblemDetailsExceptionHandler
UseHsts / UseHttpsRedirection  // só fora de Development
AppVersionMiddleware           // X-App-Version < MinSupportedAppVersion → 426 (ADR-0035); ignora /health, /webhooks
UseRateLimiter                 // políticas auth, pin, invitation, webhook (ADR-0041)
UseAuthentication              // JwtBearer + OnTokenValidated → EmployeeAccessValidator
TenantContextMiddleware        // claims → ITenantContext; `activity.SetTag("tenant.id", ...)`
UseAuthorization               // PermissionPolicyProvider
MapControllers                 // filtros globais abaixo
MapHub<RealtimeHub>("/hubs/realtime")
MapHealthChecks("/health/live" | "/health/ready")
```

**Filtros de MVC** (ordem de execução):

| Filtro | Escopo | Faz |
|---|---|---|
| `SubscriptionWriteGuardFilter` | global, métodos ≠ GET | `ISubscriptionStatusCache.CanWrite(tenant)` (cache de 60 s, invalidado por webhook ou admin) → `402 SubscriptionInactive`. Ignora `/auth/*`, `/admin/*`, `/webhooks/*` |
| `[Idempotent]` → `IdempotencyFilter` | ações marcadas (lista do ADR-0033) | Sem header → `428 IdempotencyKeyRequired`. Abre a transação, `TryBeginAsync` (o INSERT do registro segura o índice único; um duplicado concorrente **espera**, e com `lock_timeout` de 5 s responde `409 OperationInProgress`). `Replay` → devolve o corpo guardado + `Idempotent-Replayed: true`. `KeyReused` → `422`. Depois da ação **2xx**: `CompleteAsync` + COMMIT. Erro → ROLLBACK (nada é guardado, então o reenvio executa de novo) |
| `[RequiresIfMatch]` → `IfMatchFilter` | ações marcadas [M] em 06 | Sem `If-Match` → `428 IfMatchRequired`. Parse de `"N"` → `IConcurrencyContext.ExpectedVersion` |
| `ETagResultFilter` | global | Resposta com `IVersionedDto` → header `ETag: "{version}"` |
| `ValidationFilter` | global | Executa o `IValidator<TRequest>` do corpo → `422 ValidationFailed` com `errors` |

**`ProblemDetailsExceptionHandler`** mapeia exceções para HTTP:
- `BusinessException` → `ErrorCode.HttpStatus`, com `errorCode`, `traceId` e as extensões;
- `VersionConflictException` → `409` + `current`. O `ICurrentResourceLoader` usa o `entityType` da exceção para escolher a query de leitura registrada (`ORDER` → `GetOrderQuery`, `MENU` → `GetMenuQuery`, `BUSINESS_DAY` → `GetCurrentBusinessDayQuery`…), executa com `entityId` e serializa o mesmo DTO do `GET`;
- `ValidationException` → `422`;
- `BadHttpRequestException`/`JsonException` → `400 MalformedRequest`;
- qualquer outra → `500` genérico, sem detalhe e com log de erro.

**Autenticação e autorização** (ADR-0027, ADR-0028):

| Classe | Responsabilidade |
|---|---|
| `EmployeeAccessValidator` (`JwtBearerEvents.OnTokenValidated`) | Lê `IEmployeeAccessCache.GetAsync(employeeId)` (`IMemoryCache`, 30 s). `Status != ACTIVE` ou `SecurityStamp` diferente da claim → `Fail` → `401 Unauthenticated` |
| `IEmployeeAccessCache` | `{ Status, SecurityStamp, Roles, Permissions }`. `Invalidate(employeeId)` é chamado pelos handlers que mudam perfis ou status |
| `PermissionPolicyProvider : IAuthorizationPolicyProvider` | Política `"perm:{nome}"` gerada sob demanda |
| `PermissionAuthorizationHandler` | Confere a permissão em `IEmployeeAccessCache`. Falha → `403 PermissionDenied` com a extensão `permission` |
| `[HasPermission(Permissions.Orders.Create)]` | Atributo usado nos controllers |
| `IPermissionChecker` (scoped) | `bool Has(string permission)` sobre o mesmo cache. Usado em handlers quando a permissão depende do **valor** enviado (ex.: `orders.delivery-fee.edit` só se a taxa difere da padrão) |
| `DeviceSecretAuthenticationHandler` (esquema `DeviceSecret`) | Só para `GET /devices/shared/{installationId}/employees` e `POST /auth/pin-login`: valida `X-Device-Secret` + `X-Tenant-Id` |
| `PlatformAdminRequirement` | Política `PlatformAdmin`: `users.is_platform_admin`, token sem tenant |

**JSON** (`AddControllers().AddJsonOptions`):
- `camelCase`;
- `JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper)`;
- `DefaultIgnoreCondition = Never` (nulos sempre presentes);
- `MoneyJsonConverter` (`"35.50"`; leitura rejeita número JSON e mais de 2 casas);
- `UtcDateTimeOffsetConverter` (sempre `Z`);
- `DateOnly` ISO.

**Controllers:** finos e sem regra. Recebem DTO, montam o comando e chamam `ICommandHandler<,>`/`IQueryHandler<,>` dos `Contracts`. Um controller por recurso de 06: `AuthController`, `DevicesController`, `TenantController`, `SubscriptionController`, `EmployeesController`, `InvitationsController`, `RolesController`, `ProductsController`, `MarmitaSizesController`, `BusinessDaysController`, `MenusController`, `CustomersController`, `OrdersController`, `OrderItemsController`, `OrderAlertsController`, `ProductionController`, `PaymentsController`, `ClosingController`, `SyncController`, `OperationsController`, `AuditLogsController`, `AdminTenantsController`, `AsaasWebhookController`.

---

## 7.5 Anatomia de um módulo

```text
AlmoSeis.Modules.Ordering/
  Contracts/                      ← público
    Commands/CreateOrderCommand.cs, AddOrderItemsCommand.cs, ...
    Queries/GetOrderQuery.cs, ListOrdersQuery.cs, ...
    Dtos/OrderDto.cs, OrderSummaryDto.cs, ...        (formato de 06, IVersionedDto)
    Ports/IOrderPaymentPort.cs, IOrderReadPort.cs     (o que outros módulos podem pedir)
  Domain/                         ← internal; sem EF/ASP.NET
    Order.cs, OrderItem.cs, OrderItemComponent.cs, WeighingEntry.cs, Customer.cs, CustomerAddress.cs,
    OrderChangeEvent.cs, AlertAcknowledgement.cs, Events/*.cs, Enums/*.cs
  Application/                    ← internal
    CreateOrder/CreateOrderHandler.cs, CreateOrderValidator.cs
    ... um diretório por caso de uso
  Infrastructure/                 ← internal
    Configurations/*.cs (IEntityTypeConfiguration), OrderingModelConfiguration.cs,
    OrderPaymentPort.cs (implementação do port), Mappers/*IntegrationEventMapper.cs
  OrderingModule.cs               ← público: static IServiceCollection AddOrderingModule(this IServiceCollection)
```

`XModule.AddXModule()` registra handlers (Scrutor por convenção), validators, ports, mappers e a `IModuleModelConfiguration`. `Program.cs` chama `AddIdentityModule()`, `AddCatalogModule()` etc.

**Leitura:** query handlers usam `AsNoTracking()` com projeção direta para DTO (`Select(...)`), sem carregar agregados.

---

## 7.6 Módulo Identity

| Classe | Tipo | Notas |
|---|---|---|
| `User` | entidade (não `ITenantOwned`) | `PasswordHash`, `SecurityStamp`, lockout. `SetPassword(IPasswordHasher<User>, string)` |
| `Tenant` | agregado | `Rename(...)`, `AcceptTerms(version, userId, at)` |
| `TenantSettings` | agregado [S] | `Update(kiloPrice?, deliveryFee?, methods?, idleSeconds?)`. Valida "pelo menos 1 método" |
| `TenantEmployee` | agregado [S] | `AssignRoles(set)`, `Deactivate(by)`, `Reactivate()`, `SetPin(hash)`, `RegisterPinFailure(now)` (5 → `PinLockedUntil = now + 15 min`), `RotateSecurityStamp()` (chamado por `AssignRoles`/`Deactivate`) |
| `EmployeeInvitation` | agregado | `CreateInvite(roles, by, now)`, `CreatePasswordReset(target, by, now)`, `Accept(userId, now)`, `Revoke()`, `IsUsable(now)` |
| `SharedDevice` | agregado | `Register(installationId, name, platform, secretHash, by)`, `Revoke(now)` |
| `RefreshToken` | entidade | ver `IRefreshTokenService` |
| `InvitationCodeGenerator` | serviço de domínio | 6 caracteres do alfabeto `[A-HJ-NP-Z2-9]`; em colisão com o índice único, nova tentativa (até 5) |
| `PinPolicy` | serviço de domínio | 4–6 dígitos; recusa sequências e repetições |

| Handler (Application) | Endpoint (06) | Pontos de atenção |
|---|---|---|
| `SignUpHandler` | `POST /auth/signup` | Rota pública: o handler gera o `TenantId` e chama `ITenantContext.InitializeForSignup(tenantId, userId)` **antes** de abrir a conexão, para a RLS aceitar os inserts. Cria `User`, `Tenant`, `TenantSettings`, `TenantSyncCounter`, `Subscription(TRIALING)` (via `IBillingPort`), `TenantEmployee(OWNER)` e `TrialClaim` (hash) |
| `LoginHandler` | `POST /auth/login` | Sem tenant: define só `app.user_id` → lista vínculos (05 §5.12 grupo 2). Lockout de senha do Identity |
| `RefreshTokenHandler`, `LogoutHandler`, `SwitchTenantHandler` | `/auth/refresh`, `/logout`, `/switch-tenant` | Reuso de refresh revogado → revoga a família |
| `GetMeQuery` | `GET /auth/me` | Monta o `Me` com `IBillingPort.GetStatus` |
| `SetOwnPinHandler` | `PUT /auth/me/pin` | Confere a senha atual |
| `PinLoginHandler` | `POST /auth/pin-login` | Esquema `DeviceSecret`, tenant do header → `PinPolicy`/bloqueio → tokens com `device_id` |
| `ForgotPasswordHandler`, `ResetPasswordHandler` | `/auth/password/*` | Token do Identity (`DataProtectorTokenProvider`); código de reset via `EmployeeInvitation` |
| `RegisterSharedDeviceHandler`, `RevokeSharedDeviceHandler`, `ListSharedDevicesQuery`, `ListPinEmployeesQuery` | `/devices/shared/*` | `deviceSecret` = 32 bytes aleatórios, base64url, prefixo `ds_` |
| `UpdateTenantHandler`, `UpdateTenantSettingsHandler` | `/tenant`, `/tenant/settings` | [M] |
| `ListEmployeesQuery`, `UpdateEmployeeHandler`, `DeactivateEmployeeHandler`, `ReactivateEmployeeHandler`, `IssuePasswordResetCodeHandler` | `/employees/*` | Regra `LastOwner`. Invalidam o `IEmployeeAccessCache` e publicam `AccessRevoked` |
| `CreateInvitationHandler`, `ListInvitationsQuery`, `RevokeInvitationHandler` | `/employees/invitations*` | |
| `GetInvitationByCodeQuery`, `AcceptInvitationHandler` | `/invitations/{code}*` | `ISessionVariables.SetAsync("app.invitation_code", code)` → lê o convite → `InitializeForTenant(invitation.TenantId, userId)` → grava |
| `ListRolesQuery` | `GET /roles` | Lê `RolePermissions` |

**Contracts/Ports públicos:** `IEmployeeDirectory` (`GetDisplayNamesAsync(ids)`, usado por todos os DTOs com `{ id, displayName }`) e `ITenantSettingsReader` (`GetAsync()`: preço do quilo, taxa padrão, métodos habilitados).

---

## 7.7 Módulos Billing, Catalog e Menu

### Billing (ADR-0017)

| Classe | Responsabilidade |
|---|---|
| `Subscription` (agregado) | Máquina de estados: `StartTrial(now)`, `Activate(periodEnd)`, `MarkPastDue(now)`, `Suspend(now)`, `Cancel(now)`, `ExtendTrial(days)`. Transições inválidas lançam exceção. Calcula `CanWrite` (falso em `SUSPENDED`/`CANCELED`) e `GraceEndsAt` (`PastDueSince + GracePeriod`) |
| `BillingOptions` | `GracePeriodDays = 5`, `SuspendedToCanceledDays = 30`, `PlanCode`, `MonthlyPrice` |
| `ISubscriptionBillingGateway` / `AsaasGateway` | `CreateCustomerAndSubscriptionAsync(tenant, owner)`: chamado ao fim do teste (job) ou por admin. O link de pagamento é enviado **pelo Asaas** |
| `AsaasWebhookController` (Api) + `ReceiveWebhookHandler` | Valida `asaas-access-token` (comparação em tempo constante) → grava `BillingWebhookEvent` (`ON CONFLICT DO NOTHING`) via **`PlatformDbContext`**, porque o papel `almoseis_app` não tem acesso a essa tabela (05 §5.13) → `200` |
| `BillingWebhookProcessor` (`BackgroundService`, `PlatformDbContext`) | Processa os eventos pendentes por ordem de recebimento: `PAYMENT_CONFIRMED/RECEIVED` → `Activate`; `PAYMENT_OVERDUE` → `MarkPastDue`; `SUBSCRIPTION_DELETED/INACTIVATED` → `Cancel`. Auditoria `BILLING_WEBHOOK` e evento `SubscriptionStatusChanged` |
| `SubscriptionLifecycleJob` (de hora em hora, advisory lock) | Teste vencido → `Suspend`; carência vencida → `Suspend`; suspenso há 30 dias → `Cancel` |
| `ISubscriptionStatusCache` | Usado pelo `SubscriptionWriteGuardFilter` |
| Contracts: `IBillingPort` | `StartTrialAsync(tenantId)`, `GetStatusAsync()` |

### Catalog

`Product` (agregado [S]):
- `Create(kind, name, …)`;
- `Update(patch)`, que rejeita mudar `Kind` se `HasBeenSold` (consulta via `IOrderReadPort.ProductWasSoldAsync`);
- `SetImage(key)`, `Deactivate()`, `SetAvailability(bool)`.

`MarmitaSize` (agregado [S]). Handlers: `CreateProduct`, `UpdateProduct`, `CreateProductImageUpload`, `ListProducts`, `GetProduct`, `CreateMarmitaSize`, `UpdateMarmitaSize`, `ListMarmitaSizes`.

Contracts: `ICatalogReader`:
- `GetProductsAsync(ids)` → `{ Id, Kind, Name, DefaultPrice, RequiresPreparation, IsActive, IsAvailable }`;
- `GetMarmitaSizeAsync(id)`.

### Menu (ADR-0038)

`Menu` (agregado [S]):
- `CreateDaily(type, date, items)`, `CreateWeeklyTemplate(type, dayOfWeek, items)`;
- `ReplaceItems(items)`, `UpdateItem(itemId, isAvailable?, pfPrice?)`;
- `CloneAs(kind, date?, dayOfWeek?)` → novo `Menu` com `SourceMenuId`.

Regras: `section = product.kind` (recebe os produtos via `ICatalogReader`), `pfPrice` obrigatório só em `PF` + `MAIN_DISH`, bebida e adicional proibidos.

`MenuMaterializer` (serviço de domínio) aplica o ADR-0038 dentro da transação de abertura do expediente: `MaterializeForAsync(businessDate)`.

Handlers: `CreateMenu`, `ReplaceMenuItems`, `UpdateMenuItem`, `CloneMenu`, `GetMenu`, `ListMenus`, `GetCurrentMenu` (monta `MenuNotFound.sources`).

Contracts: `IMenuReader.GetDailyAsync(menuType, date)` → itens disponíveis com `pfPrice`, e `IMenuMaterializer`.

---

## 7.8 Módulo Ordering (núcleo)

### `Order` (agregado [V][S])

```csharp
internal sealed class Order : AggregateRoot, ITenantOwned, ISyncTracked
{
    // Estado conforme 04 §4.7; setters privados; coleções privadas expostas como IReadOnlyList.
    public static Order Create(NewOrderData data, BusinessDayRef day, int orderNumber, EmployeeRef by, DateTimeOffset now);

    public void UpdateDetails(OrderDetailsPatch patch, bool canEditDeliveryFee, EmployeeRef by, DateTimeOffset now);
    public IReadOnlyList<OrderItem> AddItems(IReadOnlyList<NewItem> items, EmployeeRef by, DateTimeOffset now);
    public void ChangeItem(Guid itemId, int? quantity, string? notes, EmployeeRef by, DateTimeOffset now);
    public void RemoveItem(Guid itemId, EmployeeRef by, DateTimeOffset now);
    public WeighingEntry AddWeighing(Grams grams, Money pricePerKg, EmployeeRef by, DateTimeOffset now);
    public void RemoveWeighing(Guid weighingId, EmployeeRef by, DateTimeOffset now);
    public void ChangeProductionStatus(ProductionStatus to, DateTimeOffset now);
    public void Cancel(string reason, EmployeeRef by, DateTimeOffset now);

    // Chamados só pelo módulo Payments, via IOrderPaymentPort:
    internal void ApplyPayment(Money amount);      // PaidAmount += amount; recalcula IsPaid
    internal void UndoPayment(Money amount);       // estorno; IsPaid volta a false se sobrar saldo
}
```

Toda mutação de itens, pesagens ou taxa passa por um único método privado:

```text
EnsureEditable()          → CANCELED: OrderCanceled. IsPaid: OrderAlreadyPaid (exceto notes/tableLabel/tabNumber)
… aplica a mudança …
RecalculateTotals()       → ItemsTotal, WeighingsTotal, TotalAmount; TotalAmount < PaidAmount → TotalBelowPaidAmount
EnsureNotEmpty()          → pelo menos 1 item ou pesagem ativa
ReopenProductionIfNeeded()→ COMPLETED + item com preparo incluído → IN_PROGRESS (RN-007.10)
RecordChange(kind, details, affectsProduction) → OrderChangeEvent + Touch(OrderUpdated)
```

`OrderItem.Create...` monta os snapshots (`NameSnapshot`, `CompositionSnapshot`, `UnitPrice`) a partir de um `ItemPricingInput` **resolvido pelo handler**, nunca do corpo da requisição:
- MARMITA: preço do tamanho; componentes do cardápio;
- PF: `pfPrice` do item de cardápio;
- BEVERAGE/ADDON: `DefaultPrice` do produto.

`LineTotal` do adicional = `UnitPrice × Quantity × parent.Quantity`, e é recalculado quando a quantidade do pai muda.

### `Customer` (agregado [V][S])

`Create`, `Update`, `AddAddress`, `UpdateAddress`, `RemoveAddress` (garante um único padrão) e `Anonymize(now)` (ADR-0024). O `AnonymizeCustomerHandler` também anonimiza os snapshots dos pedidos do cliente, via `ExecuteUpdateAsync`.

### Handlers

| Handler | Endpoint | Colaboradores |
|---|---|---|
| `CreateOrderHandler` [I] | `POST /orders` | `IBusinessDayReader.GetOpenAsync()` (`BusinessDayRequired`), `IMenuReader`, `ICatalogReader`, `ITenantSettingsReader`, `IOrderNumberGenerator`, `CustomerResolver` (`saveCustomer`), `IAuditWriter` |
| `UpdateOrderDetailsHandler` [M] | `PATCH /orders/{id}` | permissão `orders.delivery-fee.edit` checada no handler (via `IPermissionChecker`), por ser condicional ao valor |
| `AddOrderItemsHandler` [I][M], `ChangeOrderItemHandler` [M], `RemoveOrderItemHandler` [M] | `/orders/{id}/items*` | `IMenuReader`, `ICatalogReader` |
| `AddWeighingsHandler` [I][M], `RemoveWeighingHandler` [M] | `/orders/{id}/weighings*` | `ITenantSettingsReader` (`KiloPriceNotConfigured`) |
| `ChangeProductionStatusHandler` [M] | `PATCH /orders/{id}/status` | |
| `CancelOrderHandler` [M] | `POST /orders/{id}/cancel` | `OrderHasPayments` vem do agregado (`PaidAmount > 0`) |
| `AcknowledgeAlertsHandler` | `POST /orders/{id}/alerts/acknowledge` | `INSERT ... ON CONFLICT DO NOTHING` |
| `GetOrderQuery`, `ListOrdersQuery`, `GetOrderHistoryQuery`, `GetOrderAlertsQuery`, `GetProductionBoardQuery` | `GET`s | projeções. `pendingAlertCount` é calculado por subconsulta (04 §4.7) |
| Clientes: `CreateCustomer`, `UpdateCustomer`, `Add/Update/RemoveAddress`, `AnonymizeCustomer`, `SearchCustomers`, `GetCustomer`, `GetCustomerPendingOrders` | `/customers/*` | |

| Classe auxiliar | Responsabilidade |
|---|---|
| `IOrderNumberGenerator` / `OrderNumberGenerator` | SQL de 05 §5.14 (`business_date_counters` + `ON CONFLICT ... RETURNING`) |
| `CustomerResolver` | `customerId`, ou `saveCustomer` → busca por telefone → cria ou reaproveita. Resolve `customerAddressId` → snapshot do endereço |
| `AlertRecipientsPolicy` | Regras de quem vê o alerta (04 §4.7): o autor é excluído; permissão de produção ou de pedidos |

**Contracts/Ports públicos:**
- `IOrderPaymentPort` (para Payments):
  - `LoadForPaymentAsync(orderId, expectedVersion)` → snapshot (total, pago, saldo, tipo, cancelado);
  - `ApplyPaymentAsync(orderId, amount)`, `UndoPaymentAsync(orderId, amount)`.
  
  A implementação carrega o agregado **no mesmo `AppDbContext` e transação** e chama `ApplyPayment`/`UndoPayment` + `Touch`.
- `IOrderReadPort`: `ProductWasSoldAsync`, consultas de agregação para o Closing.

---

## 7.9 Módulos Payments e Closing

### Payments

`Payment` (agregado [S], sem `[V]`: imutável exceto `Reverse`):
- `Register(orderSnapshot, amount, method, cashReceived?, label, businessDay, by, now)` aplica as regras de troco (ADR-0021) e `PaymentExceedsBalance`;
- `Reverse(reason, businessDay, by, now)` → `PaymentReversal`. Pagamento já estornado → `PaymentAlreadyReversed`.

| Handler | Fluxo |
|---|---|
| `RegisterPaymentHandler` [I][M] | `IBusinessDayReader.GetOpenAsync()` → `IOrderPaymentPort.LoadForPaymentAsync(orderId, ifMatch)` → valida o método habilitado (`ITenantSettingsReader`) → `Payment.Register` → `IOrderPaymentPort.ApplyPaymentAsync` → auditoria `PaymentRegistered` → eventos `OrderPaymentRegistered` e, se quitou, `OrderPaymentConfirmed`. Retorna `{ payment, order }` |
| `ReversePaymentHandler` [I][M] | Carrega o pagamento → versão do pedido via port → `Reverse` → `UndoPaymentAsync` → auditoria `PaymentReversed` → `OrderPaymentReversed` |
| `ListOrderPaymentsQuery`, `GetPaymentQuery` | Projeções com `reversal` |

### Closing

| Classe | Responsabilidade |
|---|---|
| `BusinessDay` (agregado [V][S]) | `Open(date, openingBalance, by, now)`, `Close(by, now)` |
| `CashMovement` (agregado [S]) | Imutável |
| `DailyClosing` (agregado [S]) | `Finalize(ClosingFigures figures, countedCash, justification, by, now)`. Valida `JustificationRequired`. Gera `DailyClosingLine`s. Imutável depois de criado |
| `ClosingAdjustment` (agregado [S]) | Imutável |
| **`ClosingCalculator`** (serviço de domínio puro) | `ClosingFigures Compute(ClosingInputs inputs)`. Recebe os agregados já somados pelo banco (por tipo de pedido e por forma de pagamento; pedidos ativos, cancelados, pendentes; recebidos de dias anteriores; movimentações; estornos) e devolve **todos** os números de 04 §4.9. É a **única** implementação das fórmulas, usada pelo resumo parcial **e** pela finalização |
| `ClosingInputsQuery` | Uma consulta SQL por bloco (`GROUP BY order_type`, `GROUP BY payment_method`…), no padrão de 05 §5.14, com `businessDayId` |
| `IBusinessDayReader` (Contracts) | `GetOpenAsync()` → `BusinessDayRef { Id, BusinessDate, Version }` ou `null` |

| Handler | Endpoint |
|---|---|
| `OpenBusinessDayHandler` [I] | `POST /business-days`: `BusinessDayAlreadyOpen`/`BusinessDayClosedToday` → `BusinessDay.Open` → `IMenuMaterializer.MaterializeForAsync(date)` → evento `BusinessDayOpened` |
| `GetCurrentBusinessDayQuery`, `ListBusinessDaysQuery` | `GET /business-days*` |
| `RegisterCashMovementHandler` [I], `ListCashMovementsQuery` | `/closing/cash-movements` |
| `GetClosingSummaryQuery` | `GET /closing/summary`: expediente aberto → `ClosingCalculator` ao vivo; fechado → snapshot |
| `FinalizeClosingHandler` [I][M] | `SELECT ... FOR UPDATE` no `business_day` → confere `If-Match` → `ClosingInputsQuery` → `ClosingCalculator` → `DailyClosing.Finalize` → `BusinessDay.Close` → evento `ClosingFinalized` |
| `CreateClosingAdjustmentHandler` [I] | `/closing/{id}/adjustments` |
| `GetClosingQuery`, `ListClosingHistoryQuery`, `GetMonthlyClosingQuery` | `GET /closing/*` |

O `FOR UPDATE` no expediente durante a finalização faz pagamentos e movimentações concorrentes esperarem. Toda escrita que exige expediente aberto lê o `business_day` com `FOR SHARE`, então não há venda "entre" o cálculo e o fechamento.

`ClosingUpdated` é emitido por pagamentos, estornos e movimentações, e agrupado no dispatcher em no máximo 1 a cada 5 s por tenant (06 §6.14).

---

## 7.10 Módulos Audit e Sync; tempo real

### Audit

O `IAuditWriter` vive em Infrastructure (§7.3). O módulo Audit tem só leitura: `ListAuditLogsQuery` (`GET /audit-logs`) e `OrderHistoryProjection`, que é usada pelo `GetOrderHistoryQuery` do Ordering via `IAuditReader` (Contracts).

**Catálogo de `operationType`** (constantes em `AuditOperations`):
- `OrderCreated`, `OrderDetailsUpdated`, `OrderItemAdded`, `OrderItemChanged`, `OrderItemRemoved`, `WeighingAdded`, `WeighingRemoved`, `ProductionStatusChanged`, `OrderCanceled`;
- `PaymentRegistered`, `PaymentReversed`;
- `BusinessDayOpened`, `CashMovementRegistered`, `ClosingFinalized`, `ClosingAdjustmentCreated`;
- `MenuCreated`, `MenuItemsReplaced`, `MenuItemUpdated`, `MenuMaterialized`;
- `ProductCreated`, `ProductUpdated`, `MarmitaSizeChanged`;
- `EmployeeRolesChanged`, `EmployeeDeactivated`, `EmployeeReactivated`;
- `InvitationCreated`, `InvitationAccepted`, `InvitationRevoked`;
- `SharedDeviceRegistered`, `SharedDeviceRevoked`;
- `TenantSettingsUpdated`, `CustomerAnonymized`;
- `SubscriptionStatusChanged`, `TrialExtended`.

### Sync

| Classe | Responsabilidade |
|---|---|
| `GetSyncChangesQuery` | SQL de 05 §5.14 (`DISTINCT ON`). Filtra por permissão: `PAYMENT*`/`CASH_MOVEMENT`/`DAILY_CLOSING`/`CLOSING_ADJUSTMENT` exigem `closing.view` ou `orders.view` conforme 06 §6.12. Cursor mais antigo que o primeiro `seq` retido → `410 SyncCursorExpired`, com a extensão `currentSeq` = `tenant_sync_counters.last_seq` |
| `GetOperationQuery` | `GET /operations/{key}`, filtrado por `user_id`. Linha não visível → `NOT_FOUND` |
| `SyncRetentionJob` (diário, `PlatformDbContext`, advisory lock) | Apaga `change_feed_entries` com mais de 7 dias, `idempotency_records` expirados e `outbox_messages` processados há mais de 7 dias |

### Tempo real

| Classe | Responsabilidade |
|---|---|
| `RealtimeHub : Hub` (Api) | `[Authorize]`. Em `OnConnectedAsync`, adiciona aos grupos de 06 §6.14 conforme as permissões. Valida o `appVersion` da query. Sem métodos de cliente |
| `OutboxDispatcher` (`BackgroundService`, `PlatformDbContext`) | A cada 250 ms (ou `LISTEN outbox` via `NOTIFY` no commit, otimização futura): `SELECT ... FOR UPDATE SKIP LOCKED LIMIT 100` → `IRealtimePublisher.PublishAsync(envelope)` → `processed_at`. Em falha, incrementa `attempts` e grava `last_error`. Com `attempts > 10`, registra log de erro e para de tentar a mensagem |
| `IRealtimePublisher` / `SignalRRealtimePublisher` | `IHubContext<RealtimeHub>`. Resolve os grupos pelo `eventType` (tabela de 06 §6.14). `ClosingUpdated` é agrupado por tenant |
| `EmployeeAccessNotifier` | Publica `AccessRevoked` no grupo `employee:{id}` direto (sem outbox: não é estado, e o cache já foi invalidado) |

---

## 7.11 Jobs e configuração

| Job (`BackgroundService` + `PeriodicTimer`) | Periodicidade | Trava |
|---|---|---|
| `OutboxDispatcher` | contínuo (250 ms) | `SKIP LOCKED` (seguro com N réplicas) |
| `BillingWebhookProcessor` | contínuo (5 s) | `SKIP LOCKED` |
| `SubscriptionLifecycleJob` | 1 h | advisory lock |
| `InvitationExpiryJob` | 15 min | advisory lock |
| `SyncRetentionJob`, `RefreshTokenCleanupJob` | diário, 04:00 UTC | advisory lock |
| `TenantRetentionJob` (ADR-0024: 90 dias após `CANCELED`) | diário | advisory lock |

**Configuração** (`IOptions<T>` validadas com `ValidateOnStart`):

| Seção | Classe | Segredo? |
|---|---|---|
| `ConnectionStrings:App` / `:Platform` | — | sim (Key Vault) |
| `Jwt` (`Issuer`, `Audience`, `SigningKeyPem`) | `JwtOptions` | chave sim |
| `Billing` (`Asaas:BaseUrl`, `Asaas:ApiKey`, `Asaas:WebhookToken`, `GracePeriodDays`…) | `BillingOptions` | chaves sim |
| `Storage:R2` (`AccountId`, `Bucket`, `AccessKeyId`, `SecretAccessKey`, `PublicBaseUrl`) | `R2Options` | chaves sim |
| `Email` (`ConnectionString`, `Sender`) | `EmailOptions` | sim |
| `App` (`MinSupportedAppVersion:Android`, `:Ios`, `StoreUrl:*`) | `AppVersionOptions` | não |
| `Idempotency` (`TtlHours = 72`, `LockTimeoutSeconds = 5`) | `IdempotencyOptions` | não |

O `appsettings.json` versionado só tem placeholders (`docs/02-padroes.md` §2.5). A imagem Docker usa a base `aspnet` **não chiseled**, que traz `tzdata`, necessário para `TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")` no cálculo da `BusinessDate`.

---

## 7.12 Testes

| Projeto | O que cobre | Como |
|---|---|---|
| `UnitTests` | Agregados (`Order`, `Payment`, `Subscription`, `DailyClosing`, `Menu`, `TenantEmployee`), `ClosingCalculator`, `KiloPricing`, `Money`, `RolePermissions` | Sem banco. **Exemplos numéricos** do épico e do 06 viram casos de teste (ex.: o fechamento de R$ 3.650 / R$ 750 / −R$ 10) |
| `IntegrationTests` | Cada endpoint de 06 | `WebApplicationFactory` + **um** container Testcontainers PostgreSQL 16 por execução, migrations aplicadas e papéis reais (`almoseis_app`). Respawn entre testes |
| ↳ `TenantIsolationTests` | **Todo** endpoint autenticado | Teoria sobre o catálogo de rotas: dados no tenant A, chamada como B → `404`/lista vazia, nunca dados de A |
| ↳ `IdempotencyTests` | Endpoints [I] | Reenvio com mesmo corpo → mesma resposta + `Idempotent-Replayed`; corpo diferente → `422`; dois em paralelo → um registro só |
| ↳ `ConcurrencyTests` | Endpoints [M] | `If-Match` velho → `409` com `current`; dois pagamentos concorrentes no mesmo saldo → um falha |
| ↳ `DdlMigrationSyncTests` (unitário) + `SchemaTests` | Migrations × `05-ddl.md` × modelo EF | O script embutido é idêntico aos blocos do `05`. A migration aplicada pelo login de migration passa no `05-ddl-smoke-test.sql` (21 checagens), e toda entidade EF encontra suas colunas no banco (ADR-0042) |
| `ArchitectureTests` | ADR-0029 | regras abaixo |

**Regras de arquitetura (NetArchTest):**
1. Tipos em `*.Domain` não dependem de `Microsoft.EntityFrameworkCore`, `Microsoft.AspNetCore` nem `AlmoSeis.Infrastructure`.
2. Um módulo só usa de outro módulo tipos no namespace `*.Contracts`.
3. Controllers não dependem de `AppDbContext` nem de tipos `*.Domain`.
4. Nenhum tipo usa `DateTime.Now`/`UtcNow`/`DateTimeOffset.Now`/`UtcNow` (checagem por análise IL do teste).
5. Nenhum tipo fora de `Infrastructure`/`Admin`/`Billing` usa `PlatformDbContext` ou `IgnoreQueryFilters`.
6. Toda entidade com propriedade `TenantId` implementa `ITenantOwned`.
7. Toda ação de controller que não é GET tem `[HasPermission]` ou `[AllowAnonymous]` explícito.

---

## 7.13 Notas de implementação (aprendidas no código)

| Tema | Como ficou | Por quê |
|---|---|---|
| Inicialização do tenant | Feita no `JwtBearerEvents.OnTokenValidated` (`EmployeeAccessValidator`), e não num `TenantContextMiddleware` separado | É ali que as claims estão validadas, e a checagem de funcionário ativo/stamp já precisa da RLS do tenant |
| Tenant definido no meio da transação | `ITenantSession.ApplyAsync()` reaplica `app.tenant_id`/`app.user_id` na conexão já aberta (autocadastro, login, refresh, troca de tenant) | O decorator abre a conexão antes de o handler saber o tenant |
| Vínculos do usuário | `AuthSessionIssuer.ListMembershipsAsync` usa `IgnoreQueryFilters()` + `Where(UserId == ...)`. É a **única** exceção ao filtro de tenant fora de admin e plataforma | A RLS libera por `app.user_id` (05 §5.12, grupo 2); o filtro do EF esconderia os outros tenants |
| Leitura antes do commit | O `Me` do autocadastro e o `IBillingPort.GetSummaryAsync` consultam `.Local` antes do banco | O `UnitOfWork` só grava no commit, e fazer flush no handler apagaria as entradas do feed |
| Ordem dos INSERTs | Relações EF "de ordenação" (`HasOne<User>()`, auto-relação de `RefreshToken`), mesmo onde a FK real é composta | O EF só ordena comandos por relações que conhece |
| `tenant_settings` | O `Id` do agregado é mapeado para a coluna `tenant_id`, e o filtro de tenant só vale para entidades com `TenantId` mapeado | A chave da tabela é o próprio tenant |
| Falhas que gravam | Login e refresh devolvem `AuthOutcome<T>`, e o controller lança o erro **depois** do commit | O contador de tentativas e a revogação da família precisam persistir |
| Connection strings | Lidas na resolução do `DbContext`, não no registro | Key Vault e `WebApplicationFactory` aplicam a configuração depois do `Program` registrar os serviços |
| Globalização | Sem `InvariantGlobalization` | Fusos IANA (`America/Sao_Paulo`) no Windows dependem do ICU |
| Diagnóstico de testes | `ApiFactory` captura os logs de erro do servidor e os anexa à falha | O 500 genérico da API não mostra a causa no teste |

## 7.14 Exemplo de ponta a ponta: `POST /orders/{id}/payments`

```text
PaymentsController.Register(orderId, RegisterPaymentRequest body)
  [HasPermission(Payments.Register)] [Idempotent] [RequiresIfMatch]
  ── IdempotencyFilter: BEGIN; INSERT idempotency_records (IN_PROGRESS)
  ── IfMatchFilter: ExpectedVersion = 4
  ── ValidationFilter: RegisterPaymentValidator (amount > 0, 2 casas, cashReceived só em CASH)
  → ICommandHandler<RegisterPaymentCommand, PaymentResultDto>  (decorator: reutiliza a transação aberta)
      RegisterPaymentHandler
        day   = IBusinessDayReader.GetOpenAsync()                         // FOR SHARE; null → BusinessDayRequired
        order = IOrderPaymentPort.LoadForPaymentAsync(orderId, 4)         // VersionConflict / OrderCanceled
        ITenantSettingsReader → PaymentMethodDisabled?
        payment = Payment.Register(order, 35.00, CASH, 50.00, …)          // PaymentExceedsBalance; troco 15.00
        IOrderPaymentPort.ApplyPaymentAsync(orderId, 35.00)               // Order.ApplyPayment + Touch → version 5, IsPaid
        IAuditWriter.Add("PaymentRegistered", "PAYMENT", payment.Id, orderId, before, after)
      UnitOfWork.CommitAsync()  → change_feed (ORDER v5, PAYMENT v1), outbox (OrderPaymentRegistered, OrderPaymentConfirmed), SaveChanges
  ── IdempotencyFilter: UPDATE idempotency_records (COMPLETED, 201, body); COMMIT
  ← 201 Created, Location, ETag: "5", { payment, order }
OutboxDispatcher (assíncrono, ≤ 250 ms) → SignalR: grupos orders/production/closing
```
