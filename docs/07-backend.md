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
    AlmoSeis.Modules.Storefront/   # página de pedidos: perfil público, slug, estado, cardápio da página, taxa por distância e provedores de mapas (EP-002)
    AlmoSeis.Modules.Notifications/ # push dos pedidos do site pela Expo: registro do token, envio pelo outbox, lembrete, recibos (EP-002, ADR-0049)
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

**Ordem entre módulos** (referências de projeto não podem formar ciclo): `Identity`, `Catalog` ← `Menu` ← `Closing` ← `Storefront` ← `Ordering` ← `Payments`; `Ordering` ← `Notifications` (lê o estado do aceite pela porta `IWebOrderAcceptanceReader`; o Identity o chama pela porta `IDeviceAccessListener`, declarada no Identity e implementada pelo Notifications). O `Closing` fica **abaixo** do `Ordering` porque todos dependem do expediente (`IBusinessDayReader`); por isso o fechamento lê pedidos e pagamentos com SQL (`ClosingInputsQuery`), sem referenciar esses módulos. O `Storefront` (EP-002) lê catálogo, cardápio, expediente e assinatura, e o `Ordering` o usa para o envio e o acompanhamento pela página (`IStorefrontGate`) e para conferir a cotação de entrega (`IDeliveryQuoteRedeemer`, ADR-0048). O autocadastro (Identity, abaixo de todos) prepara a página pela porta `ITenantCreatedListener`, declarada no Identity e implementada pelo Storefront.

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
| `EstablishmentBehavior`, `TenantModule`, `TenantModuleMap` (`Security/TenantModules.cs`) | Comportamentos (`RESTAURANT`, `SNACK_BAR`) e módulos do ADR-0054, com o mapa fixo comportamento → módulos (item 12). `TenantModuleMap.For(behaviors)` = união, em ordem do enum; conjunto vazio vale `RESTAURANT` (janela do deploy da 0008, §7.13). Ficam no SharedKernel, como as permissões, porque Catalog, Menu e Ordering perguntam pelos módulos e o Catalog não referencia o Identity |
| `ITenantModules` | `GetAsync()`, `HasAsync(module)` e `EnsureAsync(module)` (sem o módulo → `403 ModuleNotEnabled` com a extensão `module`). Implementado pelo Identity (`TenantModules`, §7.6). Toda checagem de regra pergunta pelo módulo, **nunca** pelo comportamento ou pelo rótulo (ADR-0054 item 13, regra 10 de §7.12) |

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

Um envelope pode ter `AvailableAt` (`IntegrationEnvelope(..., AvailableAt)` → `outbox_messages.available_at`): o dispatcher só o entrega a partir desse instante. É assim que o lembrete do push aos 3 min nasce na mesma transação do pedido do site (ADR-0046 item 15), sem tabela de agendamento.

### Serviços transversais

| Classe | Responsabilidade |
|---|---|
| `ITenantContext` / `TenantContext` (scoped, imutável após `Initialize`) | `TenantId`, `UserId`, `EmployeeId`, `SharedDeviceId`, `BypassFilter` e `Source` (`JWT`, `PUBLIC_SLUG`, `TRACKING_TOKEN`, `GATEWAY_WEBHOOK`, `SYSTEM`; ADR-0045 item 3). Preenchido a partir das claims nas rotas autenticadas. `InitializeForSystem(tenantId)`: só o tenant, sem usuário nem funcionário, para jobs que gravam no tenant (expiração e retenção do pedido do site). `InitializeForPublic(tenantId, source)`: rotas `/public/*`, tenant resolvido pelo slug ou pelo token (`IsAnonymous`); auditoria sem ator e idempotência sem usuário. `InitializeForPlatformAdmin(tenantId, adminUserId)` (`Source = PLATFORM_ADMIN`): rota de `/admin` que grava **dentro** de um tenant com o papel da aplicação (acréscimo de comportamento, ADR-0054 item 5): RLS, feed e outbox normais, sem funcionário; o administrador é o `UserId` (ator da auditoria), mas `SessionUserId` (o que vai para `app.user_id`) fica nulo nessa origem, para as políticas por usuário de 05 §5.12 (grupo 2) não abrirem os vínculos dele em outros tenants (ADR-0025, complemento de 2026-11-23). O `TenantSessionConnectionInterceptor` e o `ITenantSession` gravam o `SessionUserId` |
| `IAuditWriter` / `AuditWriter` | `Add(operationType, entityType, entityId, orderId?, before?, after?)` adiciona `AuditLog` ao contexto (mesma transação). Preenche origem, ator, dispositivo e `correlationId` = `Activity.Current.TraceId`. `before`/`after` são **objetos anônimos escolhidos pelo handler**, nunca a entidade inteira (ADR-0024, ADR-0031) |
| `ChangeFeedWriter` | Etapa 3b |
| `OutboxWriter` + `IIntegrationEventMapper<TEvent>` | Etapa 3c. Os mappers ficam nos módulos e produzem o envelope de 06 §6.14 |
| `IdempotencyStore` | `TryBeginAsync(key, userId, endpoint, requestHash)` → `Started` \| `Replay(stored)` \| `KeyReused`. `CompleteAsync(status, body, resourceId)`. Usa a mesma transação (§7.4). `FindCompletedAsync(key, endpoint, requestHash)`: só leitura, sem transação, para o reenvio das rotas com `Preparation` responder antes do passo externo. Nas rotas públicas (`ITenantContext.IsAnonymous`), `user_id` nulo e escopo `(tenant, key)` (ADR-0045 item 10). `StoredResponse` leva o `resource_id` |
| `IReplaySecretSource` (Idempotency) | Campo de resposta com segredo do cliente que **não** fica no `response_body` (ADR-0047). `Field` (`trackingToken`) e `Recreate(tenantId, resourceId)`. Implementação: `TrackingTokenReplaySource` (Ordering), pelo `ITrackingTokenIssuer` |
| `ITurnstileVerifier` / `TurnstileVerifier` (`Security/PublicPageProtection.cs`) | `siteverify` do Cloudflare (cliente HTTP tipado, 3 s): `Passed` (ou `Turnstile:Enabled = false`), `Failed` (sem token, inválido, repetido) ou `Unavailable` (erro, tempo esgotado, `internal-error`). O token nunca vai para log. `TurnstileOptionsValidator`: `Enabled` sem chave, ou com uma chave de teste do Cloudflare (`1x0000…`, `2x0000…`, `3x0000…`) fora de Development → a API não sobe |
| `IClientIpHasher` | IP → HMAC-SHA-256 com sal **diário** derivado de `PublicPage:IpHashSecret`. O IP em claro nunca é gravado (ADR-0045 item 14). Segredo vazio só em Development (aleatório por processo); `PublicPageOptionsValidator` exige `IpHashSecret` e `TrackingTokenSecret` (≥ 32 caracteres, diferentes) nos outros ambientes |
| `ITrackingTokenIssuer` / `TrackingTokenIssuer` | Token de acompanhamento = `base64url(HMAC-SHA-256(PublicPage:TrackingTokenSecret, "tracking-token:v1:{tenant}:{pedido}")[..16])`: 128 bits imprevisíveis sem o segredo e o **mesmo** para o mesmo pedido, então o reenvio idempotente o recalcula e ele não fica guardado em claro em lugar nenhum (só o SHA-256, em `order_tracking_tokens`). Trocar o segredo não invalida os tokens já emitidos (o banco guarda o hash deles); só um reenvio idempotente feito depois da troca devolveria outro token |
| `TrackingTokens` | `LooksValid(token)` (22 caracteres base64url) evita consultar o banco com lixo; `New()` só para testes (um token que não existe). O hash é o `ISecretHasher` (SHA-256) |
| `IObjectStorage` / `R2ObjectStorage` | `CreateUploadUrl(key, contentType)` (URL pré-assinada PUT de 15 min, assinada localmente), `GetAsync(key)` (HEAD do objeto: tamanho e tipo), `PublicUrl(key)`, `IsConfigured`. `AWSSDK.S3` apontado para `https://{AccountId}.r2.cloudflarestorage.com`, com a mesma configuração validada contra o R2 no UrbanCarePlatform: `ForcePathStyle` e checksum `WHEN_REQUIRED` |
| `IEmailSender` / `AcsEmailSender` | ADR-0040 |
| `IJwtTokenIssuer` / `JwtTokenIssuer` | Access token RS256 de 15 min. Chave privada no Key Vault (local: `user-secrets`). Claims: `sub`, `tenant_id`, `employee_id`, `device_id`, `security_stamp`, `jti` (ADR-0027) |
| `IRefreshTokenService` | Emite, rotaciona (família), revoga. Grava só SHA-256 |
| `ISecretHasher` | SHA-256 hex para refresh token, `deviceSecret`, `ClaimKey` de teste grátis e hash de request da idempotência |
| `IAdvisoryLock` | `pg_try_advisory_lock(hash(jobName))`: garante uma execução por job quando houver mais de uma réplica |

---

## 7.4 Api — pipeline HTTP

Ordem em `Program.cs`:

```text
UseForwardedHeaders            // X-Forwarded-For/Proto só de ForwardedHeaders:KnownNetworks/KnownProxies, ForwardLimit = 1 (ADR-0041)
UseExceptionHandler            // ProblemDetailsExceptionHandler
UseHsts / UseHttpsRedirection  // só fora de Development
UseCors("PublicPage")          // só /api/v1/public: Cors:PublicPageOrigins, sem credenciais (ADR-0053 §4.6, ADR-0045)
UseCors("RestaurantApp")       // fora de /api/v1/public: Cors:RestaurantAppOrigins com credenciais (ADR-0053 §4). As duas por UseWhen
HubOriginMiddleware            // /hubs: Origin presente, fora da lista e diferente do host da API → 403 OriginNotAllowed
AppVersionMiddleware           // X-App-Version < MinSupportedAppVersion → 426 (ADR-0035); ignora /health, /webhooks
UseRateLimiter                 // políticas auth, pin, invitation, webhook (ADR-0041) e public-read/quote/order/tracking por IP (ADR-0045)
TrackingHeadersMiddleware      // /public/tracking: no-store, no-referrer, noindex em toda resposta (OnStarting, inclusive erros)
PublicTenantMiddleware         // /public/stores/{slug} e /public/tracking/{token} → ITenantContext.InitializeForPublic (ADR-0045)
PublicStoreQuotaMiddleware     // cotações: 300/dia por **tenant** resolvido (não pelo texto do slug), RateLimiting:PublicQuotesPerStorePerDay
UseAuthentication              // JwtBearer + OnTokenValidated → EmployeeAccessValidator; /api/v1/public → NoResult (Authorization ignorado)
TenantContextMiddleware        // claims → ITenantContext; `activity.SetTag("tenant.id", ...)`
UseAuthorization               // PermissionPolicyProvider
MapControllers                 // filtros globais abaixo
MapHub<RealtimeHub>("/hubs/realtime")
MapHealthChecks("/health/live" | "/health/ready")
```

**Filtros de MVC** (ordem de execução):

| Filtro | Escopo | Faz |
|---|---|---|
| `SubscriptionWriteGuardFilter` | global, métodos ≠ GET | `ISubscriptionStatusCache.CanWrite(tenant)` (cache de 60 s, invalidado por webhook ou admin) → `402 SubscriptionInactive`. Ignora `/auth/*`, `/admin/*`, `/webhooks/*`, `/devices/push-token` e `/public/*` (a página responde `409 StoreUnavailable` sem o motivo, no handler), e as leituras por `POST` (`ReadOnlyPosts`: `POST /customers/lookup`, que leva o telefone no corpo) |
| `[Idempotent]` → `IdempotencyFilter` | ações marcadas (lista do ADR-0033) | Sem header → `428 IdempotencyKeyRequired`. Abre a transação, `TryBeginAsync` (o INSERT do registro segura o índice único; um duplicado concorrente **espera**, e com `lock_timeout` de 5 s responde `409 OperationInProgress`). `Replay` → devolve o corpo guardado + `Idempotent-Replayed: true`. `KeyReused` → `422`. Depois da ação **2xx**: `CompleteAsync` + COMMIT. Erro → ROLLBACK (nada é guardado, então o reenvio executa de novo). `[Idempotent(Public = true)]` (envio pela página): o `endpoint` gravado é o modelo da rota (`POST /api/v1/public/stores/{slug}/orders`), para o reenvio depois de um `308` de slug continuar sendo a mesma operação. `ReplaySecret = "trackingToken"`: o campo sai do corpo antes de `CompleteAsync` e, no reenvio, é recomposto pelo `IReplaySecretSource` a partir do `resource_id` (o banco nunca guarda o token em claro, ADR-0047). `Preparation = typeof(PlaceWebOrderPreparation)`: **antes** de abrir a transação, `FindCompletedAsync` (reenvio concluído responde já, sem repetir o Turnstile de uso único) e então o `IIdempotentPreparation` (Turnstile e conferência da cotação, que pode chamar o provedor de mapas), cujo resultado vai para a ação em `HttpContext.Items`. Assim nenhuma chamada HTTP externa acontece com a transação aberta, com o `IN_PROGRESS` da chave ou com os advisory locks de telefone e IP |
| `[RequiresIfMatch]` → `IfMatchFilter` | ações marcadas [M] em 06 | Sem `If-Match` → `428 IfMatchRequired`. Parse de `"N"` → `IConcurrencyContext.ExpectedVersion` |
| `ETagResultFilter` | global | Resposta com `IVersionedDto` → header `ETag: "{version}"` |
| `ValidationFilter` | global | Executa o `IValidator<TRequest>` do corpo → `422 ValidationFailed` com `errors` |
| `[RequiresModule(TenantModule.X, …)]` → `ModuleFilter` (`ModuleGuard.cs`) | com mais de um módulo, basta um (o `403` leva o primeiro). Escritas de cadastro de módulo: `POST`/`PATCH /products` e `POST /products/{id}/image-upload` (algum de `MARMITA`, `PF`, `KILO`, `DAILY_MENU`; o `403` leva `MARMITA`), `POST`/`PATCH /marmita-sizes` (`MARMITA`), `POST /menus`, `PUT …/items`, `PATCH …/items/{itemId}`, `POST …/clone` (`DAILY_MENU`); toda rota `/fixed-menu/*` exceto o `GET /fixed-menu` (`FIXED_MENU`, inclusive as leituras por id) | `ITenantModules.GetAsync` (algum dos módulos do atributo) → `403 ModuleNotEnabled` (ADR-0054 item 14). `IOrderedFilter` com `Order = -1000`: roda antes dos filtros globais (assinatura, validação) e da idempotência/`If-Match`, então a resposta não depende do corpo. Quando o módulo depende do corpo (`orderType` de `POST /orders`) ou do valor (preço do quilo em `PATCH /tenant/settings`), a checagem fica no handler, pelo mesmo `ITenantModules` |
| `[WebOriginCheck]` (`IResourceFilter`) | `refresh`, `logout`, `pin-login`, `GET /auth/shared-device/employees`, `DELETE /auth/shared-device` | Anti-CSRF (ADR-0053 §3.4): `WebSession.EnsureAllowedOrigin` antes do model binding, para o `403 OriginNotAllowed` vir antes de um `422`. Não é a única barreira: a leitura do cookie confere o `Origin` de novo (`WebSession.ReadCookie`, regra 8 de §7.12) |
| `WebSessionCookieFilter` (`IResultFilter`) | global | Só com `X-App-Platform: WEB`: `AuthResultDto`/`TokensDto` → refresh no cookie `__Secure-almoseis-rt` e `refreshToken: null`; `RegisteredSharedDeviceDto` → segredo no cookie `__Secure-almoseis-ds` e `deviceSecret: null` (06 §6.3 "App no navegador") |

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
| `DeviceSecretAuthenticationHandler` (esquema `DeviceSecret`) | Só para `GET /devices/shared/{installationId}/employees`, `GET /auth/shared-device/employees` (web) e `POST /auth/pin-login`: valida `X-Device-Secret` (na web, só o cookie; o header é ignorado) + `X-Tenant-Id`. Implementado sem esquema próprio (§7.13, "Login por PIN") |
| `WebSession` (estático) | Transporte da sessão na web (ADR-0053): `IsWeb(request)` (`X-App-Platform: WEB`); `RefreshToken(request, corpo, required)` (web: cookie, e refresh no corpo → `422`; mobile: corpo); `DeviceSecret(request, header)` (mobile: header; web: só o cookie, header ignorado); `EnsureAllowedOrigin(request)` (com `WEB`, `Origin` fora da lista → `403 OriginNotAllowed`); `ReadCookie` (privado, o **único** ponto da API que lê cookie da requisição, e confere o `Origin` antes); emissão e remoção dos cookies (`HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth`, sem `Domain`). Fora da web, cookies são ignorados |
| `PublicPageCorsOptions` / `AddPublicPageCors` (`PublicPage.cs`) | `Cors:PublicPageOrigins`, mesma validação de formato. Política `PublicPage`: sem credenciais, `GET`/`POST`, headers `Content-Type`, `Idempotency-Key`, `X-Turnstile-Token`, `If-None-Match`; expostos `ETag`, `Location`, `Retry-After`, `Idempotent-Replayed`; `Max-Age` 600 |
| `PublicTenantMiddleware` (`PublicPage.cs`) | Slug → `IStoreSlugResolver` (inexistente → `404 StoreNotFound`; antigo ou com maiúsculas → `301` em GET, `308` nos demais, para o atual). Token → `ITrackingTokenResolver` (inexistente → `404 NotFound`; vencido → `410 TrackingExpired`). Depois, `InitializeForPublic`: a conexão seguinte já abre com `app.tenant_id` do tenant resolvido |
| `PublicRoutes.IsRenderer` | `X-Web-Renderer-Key` = `PublicPage:RendererKey` (comparação em tempo constante): leitura do servidor da página fora do limite por IP (ADR-0051) |
| `RestaurantAppCorsOptions` / `AddRestaurantAppCors` | Seção `Cors` (`RestaurantAppOrigins`), validada no início (`RestaurantAppCors.IsValidOrigin`: origem completa `https://`, ou `http://` só em `localhost`/`127.0.0.1`; sem `*`, caminho, query nem credenciais). Política `RestaurantApp`: essas origens, `AllowCredentials`, métodos `GET/POST/PUT/PATCH/DELETE`, headers de 06 §6.3 "App no navegador" (`Authorization`, `Content-Type`, `Idempotency-Key`, `If-Match`, `X-App-Version`, `X-App-Platform`, `X-Tenant-Id`), expostos `ETag`, `Location`, `Retry-After`, `Idempotent-Replayed`, `Max-Age` 600. Lista vazia → nenhuma origem |
| `PlatformAdminRequirement` / `[PlatformAdmin]` | Política `PlatformAdmin` (ADR-0043): só tokens de `POST /auth/admin/login`. O `EmployeeAccessValidator` reconhece o token sem `tenant_id` com `platform_admin`, aceita-o **só** em `/api/v1/admin` e confere, a cada requisição, `is_platform_admin` e o `security_stamp` do usuário (`IPlatformAdminCheck`). Token de estabelecimento em `/admin/*` → `403`; token de admin fora de `/admin` → `401` |

**JSON** (`AddControllers().AddJsonOptions`):
- `camelCase`;
- `JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper)`;
- `DefaultIgnoreCondition = Never` (nulos sempre presentes);
- `MoneyJsonConverter` (`"35.50"`; leitura rejeita número JSON e mais de 2 casas);
- `UtcDateTimeOffsetConverter` (sempre `Z`);
- `DateOnly` ISO.

**Controllers públicos** (`AlmoSeis.Api.Controllers.Public`): `PublicStoresController` (`/public/stores/{slug}`, `/orders`, `/delivery-quotes`) e `PublicTrackingController` (`/public/tracking/{token}`, `/withdraw`). `[AllowAnonymous]`, só chamam handlers `IPublicHandler` (regra 9) e só usam DTOs públicos dos `Contracts`. O `304` do acompanhamento (comparação do `If-None-Match` com o `ETag` do DTO, `"{versão do pedido}-{status calculado}-{versão de storefront_settings}-{slug}"`, que o handler monta e não vai no JSON) é transporte e fica no controller; a desistência devolve o mesmo `ETag`. `PublicStoresController.PrepareOrderAsync` (`[NonAction]`) é o passo de preparação do envio, chamado pelo `PlaceWebOrderPreparation` com o `IQueryHandler<PrepareWebOrderQuery>` (`IPublicHandler`). O IP de quem conecta (já corrigido pelo `ForwardedHeaders`) vai no comando só para o Turnstile e para o hash.

**Controllers:** finos e sem regra. A origem do refresh e do segredo do aparelho (corpo/header × cookie) é transporte e fica no `WebSession`, chamado pelo `AuthController`. Recebem DTO, montam o comando e chamam `ICommandHandler<,>`/`IQueryHandler<,>` dos `Contracts`. Um controller por recurso de 06: `AuthController`, `DevicesController`, `TenantController`, `SubscriptionController`, `EmployeesController`, `InvitationsController`, `RolesController`, `ProductsController`, `MarmitaSizesController`, `FixedMenuController` (`/fixed-menu`, EP-003), `BusinessDaysController`, `MenusController`, `CustomersController`, `OrdersController`, `OrderItemsController`, `OrderAlertsController`, `ProductionController`, `PaymentsController`, `ClosingController`, `SyncController`, `OperationsController`, `AuditLogsController`, `AdminTenantsController`, `AsaasWebhookController`, `StorefrontController` (`/storefront`), `PushTokenController` (`/devices/push-token`, só `[Authorize]`: qualquer perfil registra; quem recebe é decidido no envio).

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
| `Tenant` | agregado [V][S] (`TENANT`) | `Create(..., behavior, labels, now)` (autocadastro: exatamente um comportamento `SIGNUP` e pelo menos um rótulo dele), `Update(name, city, state, timeZone)`, `ReplaceLabels(labels, now)` (troca o conjunto; vazio ou rótulo de comportamento não habilitado → `422 labels`; mesmo conjunto não sobe a versão), `AddBehavior(behavior, adminUserId, now)` (`PLATFORM_ADMIN`; já habilitado → nada; levanta `TenantModulesChanged`). `EffectiveBehaviors`/`EffectiveLabels`/`Modules`: sem linha de comportamento (janela do deploy), `RESTAURANT` + rótulo `RESTAURANT`; a primeira escrita grava essas linhas com `SYSTEM` (`BackfillDeployWindow`), porque a FK de `tenant_labels` exige o comportamento. `ITenantOwned` com `TenantId => Id` (sem coluna, sem filtro do EF: só a RLS) e `ISyncTracked`, para a mudança ir ao feed |
| `TenantBehavior`, `TenantLabel` | filhos do `Tenant` (05 §5.17 0008) | Chaves compostas `(TenantId, Behavior)`/`(TenantId, Label)`, sem `id` nem versão própria (vale `tenants.version`). `TenantBehavior.Create` confere `SYSTEM` ⇔ sem autor (`ck_tenant_behaviors_enabled_by`). O `Behavior` do rótulo é derivado de `EstablishmentLabels` (Contracts), com a lista fixa e o texto pt-BR do ADR-0054 item 8. Relações EF de ordenação: `TenantBehavior → User` (`enabled_by_user_id`) e `TenantLabel → TenantBehavior` (FK composta) |
| `TenantSettings` | agregado [S] | `Update(kiloPrice?, deliveryFee?, methods?, idleSeconds?)`. Valida "pelo menos 1 método" |
| `TenantEmployee` | agregado [S] | `AssignRoles(set)`, `Deactivate(by)`, `Reactivate()`, `SetPin(hash)`, `RegisterPinFailure(now)` (5 → `PinLockedUntil = now + 15 min`), `RotateSecurityStamp()` (chamado por `AssignRoles`/`Deactivate`) |
| `EmployeeInvitation` | agregado | `CreateInvite(roles, by, now)`, `CreatePasswordReset(target, by, now)`, `Accept(userId, now)`, `Revoke()`, `IsUsable(now)` |
| `SharedDevice` | agregado | `Register(installationId, name, platform, secretHash, by)`, `Revoke(now)` |
| `RefreshToken` | entidade | ver `IRefreshTokenService` |
| `InvitationCodeGenerator` | serviço de domínio | 6 caracteres do alfabeto `[A-HJ-NP-Z2-9]`; em colisão com o índice único, nova tentativa (até 5) |
| `PinPolicy` | serviço de domínio | 4–6 dígitos; recusa sequências e repetições |

| Handler (Application) | Endpoint (06) | Pontos de atenção |
|---|---|---|
| `SignUpHandler` | `POST /auth/signup` | Rota pública: o handler gera o `TenantId` e chama `ITenantContext.InitializeForSignup(tenantId, userId)` **antes** de abrir a conexão, para a RLS aceitar os inserts. Cria `User`, `Tenant` (com o comportamento e os rótulos, gravados no primeiro `SaveChanges`, antes do `Me`), `TenantSettings`, `TenantSyncCounter`, `Subscription(TRIALING)` (via `IBillingPort`), `TenantEmployee(OWNER)` e `TrialClaim` (hash). O `SignUpCommandValidator` recusa antes de gravar: `tenant.behavior` ausente, `tenant.labels` vazio ou com rótulo de outro comportamento (ADR-0054 itens 3 e 9) |
| `LoginHandler` | `POST /auth/login` | Sem tenant: define só `app.user_id` → lista vínculos (05 §5.12 grupo 2). Lockout de senha do Identity |
| `RefreshTokenHandler`, `LogoutHandler`, `SwitchTenantHandler` | `/auth/refresh`, `/logout`, `/switch-tenant` | Reuso de refresh revogado → revoga a família. O handler não sabe de cookie: recebe o refresh já resolvido pela Api (`WebSession.RefreshToken`); refresh vazio → `InvalidRefreshToken` (refresh) ou nada a revogar (logout) |
| `GetMeQuery` | `GET /auth/me` | Monta o `Me` com `IBillingPort.GetStatus` |
| `SetOwnPinHandler` | `PUT /auth/me/pin` | Confere a senha atual |
| `ChangeOwnPasswordHandler` | `PUT /auth/me/password` | Confere a senha atual (`422`, sem contar no bloqueio, como o PIN). `User.SetPasswordHash` troca o stamp; revoga por `ExecuteUpdate` os refresh tokens do usuário com `installation_id` diferente do token. Auditoria `PasswordChanged` |
| `PinLoginHandler` | `POST /auth/pin-login` | Esquema `DeviceSecret`, tenant do header → `PinPolicy`/bloqueio → tokens com `device_id` |
| `ForgotPasswordHandler`, `ResetPasswordHandler` | `/auth/password/*` | Token do Identity (`DataProtectorTokenProvider`); código de reset via `EmployeeInvitation` |
| `RegisterSharedDeviceHandler`, `RevokeSharedDeviceHandler`, `ListSharedDevicesQuery`, `ListPinEmployeesQuery` | `/devices/shared/*`, `GET /auth/shared-device/employees` | `deviceSecret` = 32 bytes aleatórios, base64url, prefixo `ds_`. `platform` `ANDROID`/`IOS`/`WEB`; o `DevicesController` exige `WEB` ⇔ `X-App-Platform: WEB` (`422 errors.platform`) |
| `UpdateTenantHandler`, `UpdateTenantSettingsHandler` | `/tenant`, `/tenant/settings` | [M]. `labels` → `Tenant.ReplaceLabels` + auditoria `TenantLabelsChanged` (só os códigos); `kiloPricePerKg` diferente do atual exige `KILO` (`ITenantModules`) |
| `AddTenantBehaviorHandler` | `POST /admin/tenants/{id}/behaviors` | Rota de plataforma, mas **dentro** do decorator transacional: valida `behavior`/`reason` → `InitializeForPlatformAdmin(tenant da rota, admin)` + `ITenantSession.ApplyAsync` → carrega o `Tenant` (RLS: outro ou inexistente → `404`) → `AddBehavior` → auditoria `TenantBehaviorAdded` (`origin: PLATFORM_ADMIN`, ator = o administrador). A sessão vai com `app.user_id` vazio (`SessionUserId`), e a resposta só com `TenantBehaviors`. O commit grava o feed `TENANT` e o outbox `TenantModulesChanged` (`TenantModulesChangedMapper`, sem ator). Não usa o `PlatformDbContext`: o papel de plataforma não tem `UPDATE` em `tenant_sync_counters`, e o acréscimo precisa entrar no feed (Q-324) |
| `TenantModules` : `ITenantModules` | — | Lê `tenant_behaviors` do tenant da sessão (filtro + RLS) uma vez por escopo e aplica o `TenantModuleMap`. Para habilitar módulo por tenant no futuro, só esta classe muda |
| `ListEmployeesQuery`, `UpdateEmployeeHandler`, `DeactivateEmployeeHandler`, `ReactivateEmployeeHandler`, `IssuePasswordResetCodeHandler` | `/employees/*` | Regra `LastOwner`. Invalidam o `IEmployeeAccessCache` e publicam `AccessRevoked` |
| `CreateInvitationHandler`, `ListInvitationsQuery`, `RevokeInvitationHandler` | `/employees/invitations*` | |
| `GetInvitationByCodeQuery`, `AcceptInvitationHandler` | `/invitations/{code}*` | `ISessionVariables.SetAsync("app.invitation_code", code)` → lê o convite → `InitializeForTenant(invitation.TenantId, userId)` → grava |
| `ListRolesQuery` | `GET /roles` | Lê `RolePermissions` |

**Contracts/Ports públicos:** `IEmployeeDirectory` (`GetDisplayNamesAsync(ids)`, usado por todos os DTOs com `{ id, displayName }`; `CountActiveMembershipsAsync(userId)` por SQL, pela RLS de vínculo, para o título do push), `IDeviceAccessListener` (implementado pelo Notifications: o `LogoutHandler`, o `SwitchTenantHandler`, o `EmployeeAccessRevoker` e o registro/revogação do aparelho compartilhado o chamam na mesma transação; o logout anônimo assume a sessão do tenant do refresh antes), `ITenantCalendar` (`TodayAsync()`: data local no fuso do tenant, usada na abertura do expediente) e `ITenantSettingsReader` (`GetAsync()`: preço do quilo, taxa padrão, métodos habilitados). `EstablishmentLabel` + `EstablishmentLabels` (lista fixa do ADR-0054 item 8, comportamento e texto de cada rótulo) ficam nos Contracts do Identity: só ele lê rótulo (regra 10 de §7.12). `GetMeQuery`/`AuthSessionIssuer.BuildMeAsync` monta `tenant.behaviors`, `tenant.labels` e `tenant.modules` (06 §6.3).

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

**Implementado:**
- Transições do `Subscription` devolvem se mudou e levantam `SubscriptionStatusChanged`; transição inválida (ex.: ativar cancelada) é `InvalidOperationException`, que o processador registra em log e ignora.
- Webhook e job rodam no `PlatformDbContext`, sem `UnitOfWork`: o `SubscriptionChangeRecorder` grava auditoria (`BILLING_WEBHOOK`/`SYSTEM`), o `SubscriptionStatusChanged` no outbox **sem `seq`** (a assinatura não entra no feed) e invalida o cache.
- `PAYMENT_CONFIRMED`/`RECEIVED`: `currentPeriodEndsAt` = `payment.dueDate` + 1 mês. A assinatura é achada por `gateway_subscription_id` (`payment.subscription` ou `subscription.id`).
- O job cria a assinatura no gateway quando faltam `TrialEndingNoticeDays` (2) para o fim do teste e o gateway está configurado; lê nome, documento e contato do proprietário por SQL (job de plataforma).
- `ISubscriptionAccess` (cache de 60 s por tenant, invalidado no mesmo processo) alimenta o `SubscriptionWriteGuardFilter`.
- Rotas `/admin/*` (ADR-0043): `IPlatformAdminService` no `PlatformDbContext`, **fora** do decorator transacional (não é `ICommandHandler`: não há tenant). Consultas por SQL com só cadastro, assinatura e contagens. Ações: `Subscription.ExtendTrial` (teste: soma dias; suspensa sem período pago: volta ao teste), `Suspend`, `Activate(agora + 1 mês)`; cada uma grava uma auditoria semântica (`TrialExtended`, `SubscriptionSuspendedByAdmin`, `SubscriptionReactivatedByAdmin`) com `actor_user_id` e, quando o estado muda, o `SubscriptionChangeRecorder` grava `SubscriptionStatusChanged` e o evento de tempo real. Reprocessar webhook = `processed_at = null`.
- `POST /admin/tenants/{id}/behaviors` (EP-003, ADR-0054 item 5) **não** é do `IPlatformAdminService`: é o `AddTenantBehaviorHandler` do Identity (§7.6), chamado pelo `AdminController`, que grava no tenant da rota com o papel da aplicação.

### Catalog

`Product` (agregado [S]):
- `Create(kind, name, …)`;
- `Update(patch)`, que rejeita mudar `Kind` se `HasBeenSold` (consulta via `IOrderReadPort.ProductWasSoldAsync`);
- `SetImage(key)`, `Deactivate()`, `SetAvailability(bool)`.

`MarmitaSize` (agregado [S]). Handlers: `CreateProduct`, `UpdateProduct`, `CreateProductImageUpload`, `ListProducts`, `GetProduct`, `CreateMarmitaSize`, `UpdateMarmitaSize`, `ListMarmitaSizes`. Escritas de produto (`POST`/`PATCH /products`, `image-upload`) exigem algum de `MARMITA`, `PF`, `KILO` ou `DAILY_MENU` (a lanchonete pura cadastra pelo cardápio fixo); escritas de tamanho exigem o módulo `MARMITA` (`[RequiresModule]` no controller); sem ele, `ListMarmitaSizes` devolve lista vazia (ADR-0054 item 14). O `ICatalogReader.ListActiveMarmitaSizesAsync` (página de pedidos) não é filtrado: sem o módulo não há tamanho cadastrável.

Contracts: `ICatalogReader`:
- `GetProductsAsync(ids)` → `{ Id, Kind, Name, Description, DefaultPrice, RequiresPreparation, ImageUrl, IsActive, IsAvailable }`;
- `GetMarmitaSizeAsync(id)`.

`IProductUsageCheck` (Contracts): implementado por Menu e Ordering; o `UpdateProduct` recusa trocar `kind` se algum disser que o produto está em uso.

**Preço de site** (EP-003, ADR-0057): `MarmitaSize` ganha `SellsOnSite`/`SitePrice` (`SitePricing.Resolve`: ligada sem preço → o de balcão); `Product` ganha `SitePrice` só em `BEVERAGE` (`ApplySitePrice`: bebida visível **sempre** grava o preço de site, com o `DefaultPrice` quando não vem, **também `0.00`** desde a 0012 (I6, ADR-0057 item 2 complementado: preço de site da bebida `>= 0`; a de `0.00` não sai mais do site, e mostrá-la de novo sem preço não é mais `422`); `sitePrice: null` com a bebida visível e preço negativo → `422`; adicional com preço de site → `422`. Os validadores de `/products` aceitam `sitePrice >= 0`; os do tamanho de marmita e do cardápio fixo continuam `> 0`. Tamanho de marmita criado sem `sellsOnSite` continua `false`). `CatalogMarmitaSize.PriceFor(PriceList)` e `CatalogProduct.PriceFor(PriceList)` são a regra única de "qual preço o pedido cobra" (`SITE` + vende no site → preço de site; senão balcão; bebida visível ainda sem preço de site → balcão). `PriceList` (`COUNTER`/`SITE`) fica nos Contracts do Catalog, porque o Ordering e o Storefront o usam e o Catalog fica abaixo dos dois.

### Catalog: cardápio fixo (EP-003 fatia 2; ADR-0056, ADR-0057)

Decisão: fica no **Catalog** (ADR-0056 item 4: cadastro permanente; o Menu é o cardápio datado), em arquivos próprios: `Domain/FixedMenu.cs`, `Domain/FixedMenuAvailability.cs`, `Application/FixedMenuHandlers.cs`, `FixedMenuSupport.cs`, `FixedMenuValidators.cs`, `Contracts/FixedMenuContracts.cs`, `Infrastructure/FixedMenuInfrastructure.cs`. Um módulo novo exigiria outro elo na ordem entre módulos sem ganho: o pedido à la carte (Ordering) já referencia o Catalog.

| Classe | Responsabilidade |
|---|---|
| `DishCategory` [V][S] (`DISH_CATEGORY`) | `Create`, `Apply(name?, sortOrder?, isActive?)`. Reordenar = `PATCH` do `sortOrder` |
| `Dish` [V][S] (`DISH`) + `DishSize` | `Create(DishData)`, `Apply(DishChanges)` (lista de tamanhos substitui: com id atualiza e reativa, sem id cria, ausente inativa; "Tamanho?" ligado exige tamanho ativo e limpa o preço único; desligado inativa os tamanhos e exige `Price`; espelha `ck_dishes_price`), `SetAvailability(isPaused?, sizes)`. Imagem trocada → `FixedMenuImageDiscarded` |
| `BeverageType` [V][S] (`BEVERAGE_TYPE`) + `BeverageSize` + `BeverageFlavor` | Três níveis; o sabor é do tamanho (Q-305). `Apply` substitui tamanhos e, em cada um, os sabores; `SetAvailability(sizes, flavors)` |
| `Combo` [V][S] (`COMBO`) + `ComboComponent` | `Create(ComboData)` (pelo menos um componente), `Apply` (componentes trocados por inteiro, só se diferentes: a mesma lista não sobe a versão), `SetPaused` |
| `AddonGroup` [V][S] (`ADDON_GROUP`) + `AddonOption` + `AddonGroupLink` | Opções substituídas como os tamanhos; vínculos com categoria **ou** prato, apagados quando saem (únicos filhos com `DELETE`, como os componentes) |
| `FixedMenuAvailability` (Domain, puro) | **Única** implementação da disponibilidade derivada (ADR-0056 item 23): prato, tamanho de prato, tamanho de bebida, sabor, opção, componente (com o nome "Batata frita P") e combo. Nada é gravado: a pausa de um componente tira o combo de venda sem tocar nele |
| `FixedMenuLoader` | Carrega o cardápio inteiro do tenant sem rastreamento (cinco consultas com `AsSplitQuery`); sem `FIXED_MENU`, vazio (ADR-0054 item 14) |
| `FixedMenuMapping` | DTOs de 06 §6.5 "Cardápio fixo" (`includeInactive` filtra filhos) e o `FixedMenuSnapshot` da porta |
| `FixedMenuRules` | Nome único entre ativos por raiz; categoria ativa; `ResolveComponents` (prato ativo com o tamanho **dele**, obrigatório se tem tamanho, proibido se não tem; tamanho de bebida ativo de tipo ativo; pausado pode); `EnsureNotUsedByActiveCombosAsync` (`409 UsedInActiveCombos` com `combos`, item 26: prato, tamanho, tipo ou tamanho de bebida inativado, ou prato que passa a ter tamanho); vínculos com alvo ativo; chave de imagem `t/{tenant}/d/{id}-*` e `t/{tenant}/c/{id}-*`; vistas da auditoria |
| Handlers | `Create/UpdateDishCategory`, `Create/UpdateDish`, `SetDishAvailability`, `Create/UpdateBeverageType`, `SetBeverageAvailability`, `Create/UpdateCombo`, `SetComboAvailability`, `Create/UpdateAddonGroup`, `SetAddonAvailability`, `RequestFixedMenuImageUpload`; `GetFixedMenuQuery` e um `Get*Query` por raiz; um `ICurrentResourceReader` por raiz (`current` do `409`). Auditoria `FixedMenuItemCreated`/`FixedMenuItemUpdated`/`FixedMenuAvailabilityChanged`; `CatalogChanged` → `CatalogUpdated` com o `entityType` da raiz; feed pelas raízes `ISyncTracked` |
| `FixedMenuImageDiscardedMapper` + `FixedMenuImageCleanup` (`IOutboxConsumer`) | Como a imagem do produto: o objeto antigo sai do R2 depois do commit, só com o prefixo do próprio item |

Permissões: cadastro `catalog.manage`; rotas `…/availability` `catalog.availability` (OWNER, MANAGER, ATTENDANT, KITCHEN; Q-306). O cadastro não tem `[Idempotent]` (como `/products`, fora da lista do ADR-0033).

Contracts: **`IFixedMenuReader.LoadAsync()`** → `FixedMenuSnapshot` (pratos com tamanhos e `AddonOptionIds` efetivos, tamanhos de bebida com sabores, combos com componentes (com o `Id` do componente, que o pedido usa em `parts[].comboComponentId`) e opções de adicional, só ativos, com `IsSellable`) e `FixedMenuPrice.For(PriceList)`. É a porta que o resolvedor do pedido à la carte (`FixedMenuItems`, fatia 3, ADR-0058, §7.8) usa e que a página da lanchonete (fatia 5) vai usar, com a mesma disponibilidade do `GET /fixed-menu`.

`POST /products/{id}/image-upload` entra junto do `R2ObjectStorage` (URL pré-assinada); por ora `IObjectStorage` só deriva a URL pública (`ObjectStorage:PublicBaseUrl`).

### Menu (ADR-0038)

`Menu` (agregado [S]):
- `CreateDaily(type, date, items)`, `CreateWeeklyTemplate(type, dayOfWeek, items)`;
- `ReplaceItems(items)`, `UpdateItem(itemId, isAvailable?, pfPrice?)`;
- `CloneAs(kind, date?, dayOfWeek?)` → novo `Menu` com `SourceMenuId`.

Regras: `section = product.kind` (recebe os produtos via `ICatalogReader`), `pfPrice` obrigatório só em `PF` + `MAIN_DISH`, bebida e adicional proibidos.

`MenuMaterializer` (serviço de domínio) aplica o ADR-0038 dentro da transação de abertura do expediente: `MaterializeForAsync(businessDate)`. Só com o módulo `DAILY_MENU` (ADR-0054 item 18): sem ele devolve `[]`, sem ler os modelos. As escritas de cardápio exigem `DAILY_MENU` (`[RequiresModule]`), e `ListMenus` sem o módulo devolve lista vazia.

Handlers: `CreateMenu`, `ReplaceMenuItems`, `UpdateMenuItem`, `CloneMenu`, `GetMenu`, `ListMenus`, `GetCurrentMenu` (monta `MenuNotFound.sources`: DAILY de ontem, DAILY de 7 dias atrás e o modelo do dia da semana). O Menu declara a porta `IOpenBusinessDate` (data do expediente aberto) e o Closing a implementa, porque o Closing já depende do Menu para materializar.

`ReplaceItems` reaproveita o id do item quando o produto continua na lista. `CloneMenu` não copia produtos desativados depois do original.

Contracts: `IMenuReader.GetDailyAsync(menuType, date)` → itens disponíveis com `pfPrice` (entra com o Ordering), `IMenuMaterializer` (→ `MaterializedMenuDto[]`, o `materializedMenus` de 06 §6.6) e `IOpenBusinessDate`.

---

### Storefront (EP-002 fatias 2 e 4; ADR-0044, ADR-0045, ADR-0048)

| Classe | Responsabilidade |
|---|---|
| `StorefrontSettings` (agregado [V], chave = tenant) | Perfil, chave da página, pausas, modos, endereço, regra da taxa e ponto do restaurante (`Latitude`/`Longitude`, `LocationConfirmedAt`). `CreateDefault` (inativa). `Update(changes, slugChanged, hasActiveMarmitaSize, now)`: ativar exige endereço completo e um tamanho ativo; mudar o endereço apaga o ponto (e descarta o `Location` enviado junto); `Location` nulo mantém o ponto (compatível com o app da fatia 2); versão sobe também quando só o slug mudou. `SuggestLocation` (ponto geocodificado, sem confirmação, só num ponto vazio). `MissingForPageDelivery(mapsConfigured)` → `PageDeliveryRequirement` (`DELIVERY_OFF`, `PRICE_PER_KM`, `CONFIRMED_LOCATION`, `MAPS_PROVIDER`); `IsDeliveryAvailableOnPage` = lista vazia. Não é [S]: o `entityType` do `409` é `STOREFRONTSETTINGS` |
| `DeliveryFeeRule` (Domain) | `Km(metros)` (1 casa, `AwayFromZero`), `Fee(km, preçoKm, mínimo)` = `max(round(km × preço, 2), mínimo)`, `WithinRadius` (km arredondado) |
| `DeliveryAddressKey` (Domain) | Endereço do cliente normalizado (sem caixa, acento e pontuação; "R." → "rua"; cidade nula = a do restaurante; UF do restaurante; sem complemento e referência) → `Hash` (HMAC-SHA-256 com `Maps:AddressHashSecret` sobre tenant + texto; 05 §5.17 0007) e `Display`. `DeliveryAddressHashKey` (singleton) entrega o segredo (vazio só em Development: aleatório por processo; `MapsOptionsValidator` exige ≥ 32 caracteres nos outros ambientes) |
| `DeliveryQuote` [T], `GeocodeCacheEntry` [T] (Domain) | Cotação de 15 min (`ForDistance`, `ForFixed`, `ToDto`, `ToRedeemed`) e cache por tenant (hash → ponto com **4 casas**, `GeocodeCacheEntry.Coarse`; metros enquanto o ponto de partida, com 6 casas, for o mesmo, `CachedMeters`). A cotação corrente usa o ponto exato do geocodificador; uma cotação servida pelo cache usa o ponto de 4 casas (cerca de 11 m) |
| `StoreSlug` | Linha de `store_slugs` (só leitura no EF; escrita por SQL) |
| `SlugRules` | `Slugify`, `Problem(slug)` (formato, só números, reservados, palavras ofensivas por trecho), `Candidates(name, city)` (nome → nome-cidade → -2, -3…). Lista de reservados igual à da migration 0004, coberta por teste |
| `SlugWriter` | `ReserveFirstFreeAsync` (`INSERT … ON CONFLICT (slug) DO NOTHING`, próximo candidato) e `ChangeAsync` (outro tenant → `422`; antigo do próprio → reativa; máximo de 5 antigos; o atual sai antes de o novo entrar, por causa de `ux_store_slugs_current`) |
| `StorefrontProvisioner` : `ITenantCreatedListener` | Autocadastro: settings inativa + slug, na mesma transação |
| `StorefrontStatusCalculator` | `UNAVAILABLE` (inativa ou `ISubscriptionAccess.CanWrite = false`) → `CLOSED` (`IOpenBusinessDate` nulo) → `PAUSED` → `OPEN` |
| `PublicMenuBuilder` | `DAILY` de `MARMITA` da data (`IMenuReader`) + `ICatalogReader.ListActiveMarmitaSizesAsync` + `ListActiveAsync([ADDON, BEVERAGE])` com `IsVisibleOnPage` |
| `PublicFixedMenuBuilder` (EP-003 fatia 5) | Cardápio fixo da página da lanchonete: `IFixedMenuReader.LoadMenuAsync` (o `FixedMenuDto` do `GET /fixed-menu?includeInactive=false`, nunca devolvido direto) → `PublicFixedMenuDto` (DTOs próprios, ADR-0045 item 7): categorias com pratos e tamanhos `IsSellable && SellsOnSite`, bebidas tipo → tamanho → sabores não pausados, combos `IsSellable && SellsOnSite` com componentes (grupos de adicionais do prato, sabores da bebida) e só os grupos usados, com opções não pausadas. Preço = `sitePrice` |
| `StoreSections` (Contracts) | Seções da página pelos módulos (ADR-0054 item 13): `MARMITA` (módulo `MARMITA`) e `A_LA_CARTE` (`FIXED_MENU` + `DELIVERY`). Usado pelo perfil público, pelo `StorefrontGate` (`OfferedSections`, para o envio) e pela ativação |
| `StorefrontActivation` | `PUT /storefront` com `isPageActive`: alguma seção com o que vender no site (tamanho de marmita com `SellsOnSite`; prato ativo sem tamanho com `SellsOnSite` ou com um tamanho ativo que vende no site; combo ativo com `SellsOnSite`). A pausa não conta. Devolve a mensagem do `422` conforme as seções; o `StorefrontSettings.Update(…, hasSellableItem, now, missingItemMessage)` lança |
| `StoreSlugResolver` : `IStoreSlugResolver` | SQL direto em `store_slugs` (leitura aberta, sem o filtro de tenant), antes de o tenant existir |
| `StorefrontGate` : `IStorefrontGate` | Para o Ordering: estado, slug, nome, contato, endereço, modos, data do expediente, a versão de `storefront_settings` (`SettingsVersion`, para o `ETag` do acompanhamento) e as seções (`Sections`/`OfferedSections`, EP-003 fatia 5); cardápio de marmita da página |
| Handlers | `GetStorefrontQuery`, `UpdateStorefrontHandler` [M] (`StorefrontUpdated` sem telefone, endereço nem coordenadas; ponto vazio + endereço completo → `DeliveryQuoteCalculator.GeocodeStoreAsync` → `SuggestLocation`, na mesma transação, 3 s no máximo; falha → sem ponto), `GetPublicStoreQueryHandler` (`deliveryAvailable`, `deliveryRule`, `MAPS` em `thirdParties` com provedor externo) e `DeliveryQuoteHandler` (`IPublicHandler`: `OPEN` → Turnstile → `DeliveryQuoteCalculator` → grava a cotação; `DeliveryQuoteRequestValidator`) |
| `IGeocoder`, `IRouteDistanceProvider`, `IMapsAvailability` (Application/Maps.cs) | Portas do ADR-0048 item 15, sem tipos do provedor: `GeocodeQuery` → `GeocodeResult(GeoPoint, Exact\|Approximate, formattedAddress)` ou nulo; (origem, destino) → metros ou nulo (sem rota). Falha/tempo/cota → `MapsUnavailableException` (sem endereço na mensagem) |
| Provedores (`Infrastructure/Maps`) | `MapsOptions` (`Maps:*`, `MapsOptionsValidator`: Google sem chave, `Fake` em Production, URL inválida ou e-mail no User-Agent → a API não sobe). `FakeMaps` (hash → ponto perto do centro; número com dígito = exato; "inexistente" = não encontrado; Haversine × 1,3), `NoMaps` (`None`), `GoogleGeocoder` (Geocoding: `ROOFTOP`/`RANGE_INTERPOLATED` em `street_address`/`premise`/`subpremise` = exato; `ZERO_RESULTS` = nulo; outro status = indisponível), `GoogleRouteDistance` (Compute Route Matrix, `DRIVE`, `TRAFFIC_UNAWARE`, chave em `X-Goog-Api-Key`, `X-Goog-FieldMask`), `NominatimGeocoder` (busca estruturada; `house_number` = exato) + `NominatimThrottle` (singleton, 1 req/s por réplica), `OsrmRouteDistance` (`/route/v1/driving`, `NoRoute` = nulo). `MapsHttp`: tempo de `Maps:TimeoutSeconds`; clientes HTTP com `RemoveAllLoggers()` (a URL leva o endereço e, no Geocoding, a chave). Seleção por `Maps:Provider` na fábrica do DI |
| `DeliveryQuoteCalculator` | Disponibilidade → `DeliveryAddressKey` → `FIXED` (taxa padrão) ou cache (`GeocodeCacheEntry` válido; endereço mostrado = o digitado) / geocodificador (nulo → `422` em `{campo}.street`; aproximado → `422` em `{campo}.number`) → metros do cache (mesmo ponto de partida, `RouteCacheDays`) ou do provedor (nulo → `OutOfDeliveryArea`) → `DeliveryQuote.ForDistance` → raio (`OutOfDeliveryArea` com `distanceKm`/`maxDistanceKm`). Falha do provedor → `DeliveryUnavailable` com o texto do ADR, log de aviso (só provedor, operação e motivo) e o contador `almoseis.maps.failures` (`Meter` `AlmoSeis.Maps`, para o alerta do item 14) |
| `GeocodeCacheWriter`, `DetachedTenantWork` | O cache é gravado **fora** da transação da requisição (`INSERT … ON CONFLICT DO UPDATE` num escopo novo com `InitializeForPublic(tenant)`): uma cotação que termina em `OutOfDeliveryArea` não paga o provedor de novo. Só com `GeocodeCacheDays > 0` (padrão 7); metros só com `RouteCacheDays > 0` (padrão 7). Grava as coordenadas do cliente arredondadas a 4 casas |
| `DeliveryQuoteRedeemer` : `IDeliveryQuoteRedeemer` | Para o Ordering, no envio (ADR-0048 item 10): sem id → `422`; inexistente/de outro tenant (query filter + RLS) → `422` em `deliveryQuoteId`; válida + mesmo hash + mesma versão da configuração → `RedeemedDeliveryQuote`; senão recalcula: mesma taxa segue, outra → grava a cotação nova por `DetachedTenantWork` (sobrevive ao rollback) e `409 DeliveryQuoteChanged` com `quote` |
| `DeliveryQuoteRetentionJob` (`Storefront.Jobs`) | Ver §7.11 |

### Notifications (EP-002 fatia 5; ADR-0049, ADR-0046 item 15)

| Classe | Responsabilidade |
|---|---|
| `PushRegistration` [T] (Domain) | Token do Expo por instalação e tenant (05 §5.17 0006): funcionário da sessão, aparelho compartilhado no login por PIN, plataforma, `WebOrdersEnabled`, `ShowTenantName`. `IsExpoToken`, `Create`, `Refresh` (sem a opção = mantém), `Mask` (para log: `ExponentPushToken[abcd…]`) |
| `PushDelivery` [T] (Domain) | Um push por mensagem do outbox e registro: deduplicação e ticket para o recibo |
| `WebOrderPushContent` (Domain) | Título e corpo do item 2 (sem dado pessoal nem valor) e `MinutesLeft` (⌈s/60⌉) |
| `RegisterPushTokenHandler` / `RegisterPushTokenValidator` | `PUT /devices/push-token`: `installationId` = o do token, `WEB` recusado, `pg_advisory_xact_lock` pelo token, `SELECT release_push_token(token, installationId)` (função `SECURITY DEFINER` da 0007: libera o registro do mesmo token em outro tenant **só se for desta instalação**, sem a sessão ver a linha; `-1` = outra instalação → `409 PushTokenInUse`), apaga o mesmo token em outra instalação **deste** tenant (filtro + RLS), upsert por instalação, `ShowTenantName` por `IEmployeeDirectory.CountActiveMembershipsAsync` (falso no aparelho compartilhado). Sem auditoria |
| `RemovePushTokenHandler` | `DELETE /devices/push-token` (idempotente) |
| `PushRegistrationCleaner` : `IDeviceAccessListener` | Logout (fora do aparelho compartilhado), troca de tenant, `AccessRevoked` (desativação, perfis) e revogação do aparelho apagam o registro, na transação de quem chamou |
| `WebOrderPushMapper` (Ordering) | Do `OrderAwaitingAcceptance`: a mensagem interna `WebOrderPushRequested` na hora e a do lembrete com `AvailableAt` = envio + `WebOrders:ReminderMinutes` (só se antes do prazo). `data`: `kind`, `fulfillmentType`, `marmitaCount` |
| `WebOrderPushConsumer` : `IOutboxConsumer` | `WebOrderPushRequested` → escopo com `InitializeForSystem(tenant)` → `WebOrderPushService`. `MaxAttempts` = `Push:MaxAttempts` (3), `RetryDelay` = 30 s |
| `WebOrderPushService` | Pedido ainda `AWAITING_ACCEPTANCE` e no prazo (`IWebOrderAcceptanceReader`) → registros com a opção ligada, menos os que já têm entrega desta mensagem → funcionário ativo com `orders.accept` agora (`IEmployeeAccessReader`) → lotes de 100 → `IPushSender` → cada lote numa transação: `PushDelivery` + `DeviceNotRegistered` apaga o registro |
| `PushReceiptChecker` | Recibos (`getReceipts`) no tenant: `DeviceNotRegistered` apaga; ausente há 24 h → `ReceiptUnavailable` |
| `IPushSender`: `ExpoPushSender` / `FakePushSender` / `NoPushSender` | `Push:Provider`. Expo: `POST` JSON com `Accept`, `Authorization: Bearer` só com `Push:Expo:AccessToken`, `gzip`, tempo de `Push:Expo:TimeoutSeconds`, cliente HTTP sem log (`RemoveAllLoggers`); 429, 5xx, rede, tempo e erro do lote inteiro → `PushUnavailableException` (mensagem sem token). Fake: guarda as mensagens (testes), token com `DeviceNotRegistered` simula o aparelho desinstalado, `FailFor(token, n)` simula a Expo fora do ar, `BeforeSend` simula a Expo lenta |
| `PushOptions` / `PushOptionsValidator` | Seção `Push` (§7.11). `Fake` em Production, URL não https ou valores fora da faixa → a API não sobe |
| `PushMaintenanceJob` (`Notifications.Jobs`) | Ver §7.11 |

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

    // Pedido da página e atendimento (ADR-0046):
    public static Order SubmitFromWeb(NewOrder data, BusinessDayRef day, DateTimeOffset deadline, DateTimeOffset now); // sem número/autor
    public void EnsureAwaitingAcceptance(DateTimeOffset now);      // WebOrderExpired / OrderNotAwaitingAcceptance
    public void Accept(int orderNumber, EmployeeRef? by, Guid? customerId, int? estimatedReadyMinutes, DateTimeOffset now);
    public void Reject(RejectionReason reason, string? message, EmployeeRef by, DateTimeOffset now);
    public void RejectBecauseBusinessDayClosed(DateTimeOffset now);  // finalização do fechamento
    public bool Expire(DateTimeOffset now);                          // job; false se não venceu
    public void Withdraw(DateTimeOffset now);                        // cliente pela página
    public void ChangeFulfillment(FulfillmentStatus to, DateTimeOffset now);

    // Chamados só pelo módulo Payments, via IOrderPaymentPort:
    internal void ApplyPayment(Money amount);      // PaidAmount += amount; recalcula IsPaid
    internal void UndoPayment(Money amount);       // estorno; IsPaid volta a false se sobrar saldo
}
```

Toda mutação de itens, pesagens ou taxa passa por um único método privado:

```text
EnsureEditable()          → não aceito: OrderNotAccepted (ADR-0046). CANCELED: OrderCanceled. IsPaid: OrderAlreadyPaid (exceto notes/tableLabel/tabNumber)
… aplica a mudança …
RecalculateTotals()       → ItemsTotal, WeighingsTotal, TotalAmount; TotalAmount < PaidAmount → TotalBelowPaidAmount
EnsureNotEmpty()          → pelo menos 1 item ou pesagem ativa
ReopenProductionIfNeeded()→ COMPLETED + item com preparo incluído → IN_PROGRESS (RN-007.10)
RecordChange(kind, details, affectsProduction) → OrderChangeEvent + Touch(OrderUpdated)
```

**Taxa de entrega (ADR-0048 item 20):** `NewOrder.Pricing` (`DeliveryPricing(source, metros?, preçoKm?, mínimo?)`) grava `DeliveryFeeSource` e o snapshot da distância, só em entrega (sem `Pricing` → `FIXED_DEFAULT`). O `CreateOrderHandler` passa `FIXED_DEFAULT` (taxa = padrão) ou `MANUAL`; o envio da página, `DISTANCE` com o snapshot da cotação. `UpdateDetails` que muda taxa ou endereço recalcula a origem (`FIXED_DEFAULT` se igual à padrão, senão `MANUAL`), mantendo o snapshot como histórico; saindo de entrega, tudo vira nulo. `DeliveryDistanceKm` ("4.5") é derivado e só existe com `DISTANCE`.

`OrderItem.Create...` monta os snapshots (`NameSnapshot`, `CompositionSnapshot`, `UnitPrice`) a partir de um `ItemPricingInput` **resolvido pelo handler**, nunca do corpo da requisição:
- MARMITA: preço do tamanho na tabela do pedido (`CatalogMarmitaSize.PriceFor`); componentes do cardápio;
- PF: `pfPrice` do item de cardápio;
- BEVERAGE/ADDON: `CatalogProduct.PriceFor` (bebida no site: `SitePrice`; adicional: `DefaultPrice`).

**Tabela de preço** (EP-003, ADR-0057 item 7): `Order.PriceList` (`COUNTER` em `Create`, `SITE` em `SubmitFromWeb`; imutável; vai no `OrderDto`). `OrderItemResolver.ResolveAsync(itens, data, priceList)`: `CreateOrderHandler` passa `COUNTER`, `WebOrderSubmitter` `SITE` e `AddOrderItemsHandler` a do pedido (Q-309: item acrescentado no app a um pedido do site usa o preço de site de quem vende no site). O `PublicMenuBuilder` (Storefront) mostra só tamanhos com `SellsOnSite` e os preços `PriceFor(SITE)`, então o `WebOrderCart` e o resolvedor continuam batendo (`CartChanged` do passo 10a). A janela do deploy da 0009 está no 05 §5.17: esta API sempre grava `price_list` e o `site_price` da bebida visível.

**Pedido do site da lanchonete** (EP-003 fatia 5, ADR-0058 item 14): `Order.SubmitFromWeb` aceita `MARMITA` e `A_LA_CARTE`, sempre `DELIVERY` ou `PICKUP` (`422` em `fulfillmentType` no salão). `OrderAwaitingAcceptance` leva `MarmitaCount` e `ItemCount` (soma das quantidades do primeiro nível); o `OrderAwaitingAcceptanceMapper` e o `WebOrderPushMapper` gravam `itemCount` no `data`, e o `WebOrderPushConsumer` lê o campo se existir (mensagem antiga sem ele → texto das marmitas). `WebOrderPushContent.For(…, itemCount)`: `marmitaCount` 0 → "3 itens". Aceite, recusa, expiração, desistência, cozinha e atendimento não dependem do tipo. O acompanhamento (`WebOrderTracking`) monta a árvore: `COMBO` com `parts` (as `COMBO_PART`, com os `GROUP_ADDON` em `addons`), os demais com `addons`; o nome é o `DisplayName` do snapshot.

`LineTotal` do filho = `UnitPrice × Quantity × quantidade de cada antepassado` (pai e, no combo, avô), recalculado em toda a subárvore quando a quantidade de um antepassado muda (`AncestorQuantity`, `RepriceDescendants`).

**Pedido à la carte** (EP-003 fatia 3, ADR-0058; ADR-0058 e ADR-0056 estão `Proposto`):

- `OrderType.ALaCarte` aceita `DINE_IN`, `DELIVERY` e `PICKUP` (`EnsureFulfillment`). Produção como o PF (`OPEN` na criação), atendimento como a marmita fora do salão. `OrderRules.ModuleOf(orderType, fulfillmentType)`: `A_LA_CARTE` em `DINE_IN` → módulo `A_LA_CARTE`; em entrega/retirada → `DELIVERY`.
- **Um tipo por pedido** (Q-317): `Order.KindFits(orderType, kind)` é a tabela única. `MARMITA`/`PF` só na modalidade homônima; `BEVERAGE`/`ADDON` do catálogo fora do `A_LA_CARTE`; `FIXED_DISH`/`FIXED_BEVERAGE`/`COMBO` só no `A_LA_CARTE`; `COMBO_PART`/`GROUP_ADDON` nunca no primeiro nível. O `OrderItemResolver` confere antes de ler cardápio (sem `MenuNotFound` por engano) e o agregado confere de novo (`EnsureTopLevelKind`). `ChildFits(pai, tipo)` repete o `ck_order_items_parent`: `ADDON` sob `MARMITA`/`PF`, `COMBO_PART` sob `COMBO`, `GROUP_ADDON` sob `FIXED_DISH` ou `COMBO_PART` com prato.
- **Árvore:** `NewItem.Addons` são os filhos (no combo, as partes, cada uma com os seus `GROUP_ADDON`); `AddTree` é recursivo e cada `OrderItem` copia do pai `ParentItemKind` (sempre, também no `ADDON` da marmita: a API nova sempre grava, 05 §5.17 0010) e, só no `GROUP_ADDON`, `ParentDishId`. `NewItem.Fixed` (`FixedMenuRefs`) traz as referências do cardápio fixo e os snapshots `SizeName`/`FlavorName`; `OrderItem.DisplayName` monta "nome tamanho · sabor" para o DTO, a cozinha, os alertas e a auditoria.
- **Edição do combo** (item 10): `ChangeItem` recusa quantidade de `COMBO_PART` e observação no `COMBO` (`422`); `RemoveItem` recusa `COMBO_PART` (`422`) e, no `COMBO`, remove a subárvore inteira (`DescendantsOf`). `AffectsProduction` do alerta considera os descendentes (o combo não vai à cozinha, as partes vão).
- **`FixedMenuItems`** (`Application/FixedMenuItems.cs`): o ramo do cardápio fixo do `OrderItemResolver`, sobre o `FixedMenuSnapshot` do `IFixedMenuReader` (uma leitura por requisição). Valida disponibilidade derivada (`422 ProductUnavailable`), tamanho obrigatório/proibido, sabor obrigatório/proibido, componente do combo (`parts[].comboComponentId` = `FixedMenuComboComponent.Id`), adicional dos grupos do prato (`FixedMenuDish.AddonOptionIds`, senão `422`) e o pai do `GROUP_ADDON` incluído depois (`ItemResolution.Existing`: prato avulso ou parte de prato ativa). Preço por `FixedMenuPrice.For(priceList)`; adicional e acréscimo de sabor sem tabela (Q-308); `RequiresPreparation` do prato, do pai no adicional e `false` no combo e nas bebidas (`ck_order_items_preparation`).
- `OrderItemResolver.ResolveAsync(itens, ItemResolution(data, priceList, orderType, existentes?))`.

**Delivery por telefone** (EP-003 fatia 4, ADR-0059; ADR-0059 está `Proposto`):

- `NewOrder` ganha `ExpectedPaymentMethod` e `ChangeFor` (opcionais, no fim). `Order.Create` confere, nesta ordem: `EnsureFulfillment`, `EnsureCustomerContact` (tabela `Order.ContactRequired(tipo, atendimento)`: marmita → nome, e telefone só na entrega, Q-312; à la carte fora do salão → nome e telefone), `EnsureDelivery`, `EnsureExpectedPayment` (obrigatória em `DELIVERY`, proibida em `DINE_IN`, Q-316) e, depois das linhas, `EnsureChangeFor` (o mesmo do pedido do site: só `CASH`, positivo e ≥ total; a sobrecarga estática `EnsureChangeFor(método, troco, total, checkMinimum)` é a mesma regra do `PATCH`). Tudo `422 ValidationFailed` por campo. A forma prevista não cria `Payment` nem mexe em `PaidAmount`.
- `UpdateDetails`: troca de atendimento confere nome e telefone; sem troca, só o campo que mudou (pedido antigo sem telefone continua editável). Indo para `DINE_IN`, `ExpectedPaymentMethod`/`ChangeFor` viram nulos (`ck_orders_expected_payment_fulfillment`).
- **Forma prevista e "troco para" no `PATCH`** (revisão de 2026-11-23, I1, ADR-0059 complementado): `UpdateOrderRequest` e `OrderDetailsChanges` ganham `Optional<ExpectedPaymentMethod?> ExpectedPaymentMethod` e `Optional<Money?> ChangeFor` (no fim, com `default`). No `UpdateDetails`, os dois entram no grupo "financeiro" (quitado → `409 OrderAlreadyPaid`, ADR-0007) e passam por `ResolveExpectedPayment(changes, atendimento, total)`, chamado depois do `EnsureDelivery`, com o total resultante (`ItemsTotal + WeighingsTotal + taxa nova`): no salão, forma ou troco no corpo → `422` e os dois viram nulos; fora dele, `EnsureExpectedPayment` roda se o atendimento mudou **ou** a forma veio no corpo (entrega antiga sem forma continua editável nos outros campos; retirada que vira entrega exige a forma, Q-316); a forma que deixa de ser `CASH` sem `changeFor` no corpo leva o troco gravado; `EnsureChangeFor(..., checkMinimum: changeFor mudou)`. O `UpdateOrderHandler` confere `PaymentMethodDisabled` só quando a forma muda (depois do `If-Match`), e a auditoria `OrderDetailsUpdated` ganha `expectedPaymentMethod` e `changeFor` no antes/depois. O `UpdateOrderRequestValidator` confere o enum e `changeFor > 0`.
- **"Troco para" abaixo do total** (I2): `Order.ChangeForBelowTotal` (derivado, não mapeado) e `Order.ChangeForBelowTotalRule(changeFor, total)` (usado pela projeção do `ListOrdersQueryHandler`) → `OrderDto.ChangeForBelowTotal` e `OrderSummaryDto.ChangeForBelowTotal`, logo depois do `ChangeFor`. `AddItems`, `ChangeItem` e a taxa nunca conferem o "troco para".
- A "forma habilitada" depende de `ITenantSettingsReader` e fica no `CreateOrderHandler` (`PaymentMethodDisabled`).
- Aba Delivery: `ListOrdersQuery` ganha `FulfillmentTypes` (lista, `?fulfillmentType=` repetido; o `UpperSnakeEnumModelBinderProvider` liga cada elemento) e `FulfillmentStatus` (`PENDING` inclui o nulo da versão anterior). `OrderSummaryDto` ganha `ExpectedPaymentMethod` e `ChangeFor` (e `ChangeForBelowTotal`, I2).

### `Customer` (agregado [V][S])

`Create`, `Update`, `AddAddress`, `UpdateAddress`, `RemoveAddress` (garante um único padrão) e `Anonymize(now)` (ADR-0024). O `AnonymizeCustomerHandler` também anonimiza os snapshots dos pedidos do cliente, via `ExecuteUpdateAsync`.

### Handlers

| Handler | Endpoint | Colaboradores |
|---|---|---|
| `CreateOrderHandler` [I] | `POST /orders` | `ITenantModules.EnsureAsync(OrderRules.ModuleOf(orderType, fulfillmentType))` primeiro (`MARMITA`/`PF`/`KILO` → módulo homônimo; `A_LA_CARTE` → `A_LA_CARTE` no salão e `DELIVERY` fora; `403 ModuleNotEnabled`, ADR-0054 item 14, ADR-0058 item 1), `IBusinessDayReader.GetOpenAsync()` (`BusinessDayRequired`), `IMenuReader`, `ICatalogReader`, `ITenantSettingsReader`, `IOrderNumberGenerator`, `CustomerResolver` (`customerId`, `saveCustomer` ou o telefone), `IAuditWriter`. Sem checagem de `orders.delivery-fee.edit`: a taxa digitada na criação vale (Q-315) e vira `MANUAL`; a auditoria `OrderCreated` leva `deliveryFee`, `deliveryFeeSource`, `defaultDeliveryFee` (só em `MANUAL`), `expectedPaymentMethod` e `changeFor`. Forma prevista fora de `EnabledPaymentMethods` → `PaymentMethodDisabled` |
| `UpdateOrderDetailsHandler` [M] | `PATCH /orders/{id}` | permissão `orders.delivery-fee.edit` checada no handler (via `IPermissionChecker`), por ser condicional ao valor (taxa mudada e diferente da padrão; vale também para quem digitou a taxa na criação, Q-315) |
| `AddOrderItemsHandler` [I][M], `ChangeOrderItemHandler` [M], `RemoveOrderItemHandler` [M] | `/orders/{id}/items*` | `IMenuReader`, `ICatalogReader`, `IFixedMenuReader` (à la carte; o resolvedor recebe os itens do pedido para o `GROUP_ADDON` incluído depois) |
| `AddWeighingsHandler` [I][M], `RemoveWeighingHandler` [M] | `/orders/{id}/weighings*` | `ITenantSettingsReader` (`KiloPriceNotConfigured`) |
| `ChangeProductionStatusHandler` [M] | `PATCH /orders/{id}/status` | |
| `CancelOrderHandler` [M] | `POST /orders/{id}/cancel` | `OrderHasPayments` vem do agregado (`PaidAmount > 0`) |
| `AcknowledgeAlertsHandler` | `POST /orders/{id}/alerts/acknowledge` | `INSERT ... ON CONFLICT DO NOTHING` |
| `AcceptOrderHandler` [I][M] | `POST /orders/{id}/accept` | `WebOrderRules.LoadForDecisionAsync` → `WebOrderCustomerLinker` → `OrderNumberGenerator` → `Order.Accept` → auditoria `OrderAccepted` |
| `RejectOrderHandler` [M] | `POST /orders/{id}/reject` | `WebOrderRules.LoadForDecisionAsync` → `Order.Reject` → auditoria `OrderRejected` só com `reasonCode` (o texto livre fica no pedido, nunca na auditoria) |
| `ChangeFulfillmentHandler` [M] | `PATCH /orders/{id}/fulfillment` | `completeProduction` → `IPermissionChecker` (`production.update`) + `ChangeProductionStatus(COMPLETED)`; depois `ChangeFulfillment`. Auditoria `ProductionStatusChanged` e `OrderFulfillmentChanged` |
| `ExpireWebOrderHandler` (comando interno) | — (job) | Um pedido por transação, no contexto do tenant (`Origin = SYSTEM`) |
| `WithdrawWebOrderHandler`, `SubmitWebOrderHandler` (comandos internos) | — | Os mesmos casos de uso das rotas públicas (`WebOrderWithdrawal`, `WebOrderSubmitter`), usados pelos testes |
| `PrepareWebOrderQueryHandler` (`IPublicHandler`) | passo `Preparation` de `POST /public/stores/{slug}/orders`, **fora** da transação | Campo-isca ou página fora de `OPEN` → nada; senão `ITurnstileVerifier` e, na entrega, `IDeliveryQuoteRedeemer` (pode recalcular no provedor de mapas). Devolve `WebOrderPreparation(Captcha, Quote, QuoteError)`: o erro de negócio da cotação fica guardado para o handler lançar na posição dele |
| `PlaceWebOrderHandler` [I] (`IPublicHandler`) | `POST /public/stores/{slug}/orders` | Campo-isca → `IStorefrontGate` (`StoreUnavailable`) → Turnstile (o da preparação; sem ela, `ITurnstileVerifier`) → modalidade (`DeliveryUnavailable`) → seção: `orderType` (ausente = `MARMITA`) nas `OfferedSections` (senão `422` em `orderType`) → forma habilitada → `BrazilianPhone.Normalize` → `pg_advisory_xact_lock` por telefone e por hash do IP + contagem dos aguardando (`TooManyPendingOrders`) → cardápio (só `MARMITA`: `MenuNotFound`) → entrega: a cotação da preparação (ou `IDeliveryQuoteRedeemer`; taxa + `DeliveryPricing(DISTANCE, metros, preço/km, mínimo)`) → `WebOrderCart.Price` (marmita, contra o cardápio público) ou `WebOrderCart.PriceFixedMenu` (à la carte, contra o `IFixedMenuReader.LoadAsync`), com a taxa (`CartChanged`) → `WebOrderSubmitter` (com o `OrderType`) → **total, itens e taxa do pedido gravado = os do carrinho validado** (senão `CartChanged` e rollback: o carrinho da página e o `OrderItemResolver` são cálculos separados) → `OrderTrackingToken` (hash do token de `ITrackingTokenIssuer`) + `WebOrderIntake` (hash do IP) |
| `GetWebOrderTrackingQueryHandler`, `WithdrawWebOrderByTrackingHandler` (`IPublicHandler`) | `/public/tracking/{token}`, `/withdraw` | `TrackedOrderLoader` (hash → pedido, no tenant; `404`/`410`) → `WebOrderTracking.BuildAsync` (status do ADR-0047 item 7, linha do tempo, primeiro nome, só rua e número, `changedByStore` = existe `OrderChangeEvent`, `ETag` com versão, status calculado, versão da configuração e slug). A desistência é idempotente pelo estado: já `WITHDRAWN` → o acompanhamento atual, sem auditoria nem evento |
| `AnonymizeWebOrderHandler` (comando interno) | — (job) | `Order.AnonymizeNotAccepted` + auditoria `WebOrderAnonymized` (`SYSTEM`) |
| `GetOrderQuery`, `ListOrdersQuery`, `GetOrderHistoryQuery`, `GetOrderAlertsQuery`, `GetProductionBoardQuery` | `GET`s | projeções. `pendingAlertCount` é calculado por subconsulta (04 §4.7). O `OrderDtoBuilder` monta a árvore (`addons` = `ADDON`/`GROUP_ADDON`; `parts` = `COMBO_PART`, no mesmo formato). No painel, o `A_LA_CARTE` sai achatado (`KitchenLines`): pratos avulsos e partes de prato com preparo, quantidade da parte × combo, sem `COMBO` nem bebidas (ADR-0058 itens 11 e 12) |
| Clientes: `CreateCustomer`, `UpdateCustomer`, `Add/Update/RemoveAddress`, `AnonymizeCustomer`, `SearchCustomers`, `GetCustomer`, `GetCustomerPendingOrders`, `LookupCustomerByPhone` | `/customers/*` | `LookupCustomerByPhoneQueryHandler` (`GET /customers/by-phone`, EP-003 fatia 4): telefone exato em E.164 (`422` fora do formato), cliente não anonimizado + `lastDeliveryAddress` (pedido mais recente do cliente com endereço, `ix_orders_customer_recent`); sem cadastro, `customer: null` |

| Classe auxiliar | Responsabilidade |
|---|---|
| `IOrderNumberGenerator` / `OrderNumberGenerator` | SQL de 05 §5.14 (`business_date_counters` + `ON CONFLICT ... RETURNING`) |
| `CustomerResolver` | `customerId`; ou `saveCustomer` (com o endereço em `CustomerAddress`, Q-313); ou, fora do salão, o telefone (vínculo automático, sem endereço, ADR-0059 item 14). Resolve `customerAddressId` → snapshot do endereço. Telefone → `FindOrCreateByPhoneAsync`: `pg_advisory_xact_lock` por telefone (a mesma chave do `WebOrderCustomerLinker`) + `INSERT … ON CONFLICT DO NOTHING RETURNING id` de 05 §5.14 e, sem linha, `SELECT`. O cadastro existente nunca muda (Q-314). Inserido por SQL, o agregado é anexado com `Name` marcado como alterado (o `UPDATE` sem mudança mantém a versão 1) para o `UnitOfWork` gravar o feed `CUSTOMER` e o `CustomerChanged`; endereços do "salvar cliente" entram como `Added`. Auditoria `CustomerCreated` (nome e contagem de endereços, sem telefone) |
| `AlertRecipientsPolicy` | Regras de quem vê o alerta (04 §4.7): o autor é excluído; permissão de produção ou de pedidos |
| `WebOrderRules.LoadForDecisionAsync` | Aceite e recusa: lê o expediente aberto (`FOR SHARE`); prazo vencido → `WebOrderExpired` **antes** do `If-Match` (o job muda a versão); depois `EnsureVersion` (o segundo aceite recebe `VersionConflict` com `current`) e `EnsureAwaitingAcceptance`; expediente do pedido diferente do aberto → `BusinessDayRequired` |
| `WebOrderCustomerLinker` | Vínculo com `Customer` no aceite (ADR-0044 item 20): `pg_advisory_xact_lock` por telefone, cliente existente sem alterar, ou novo com nome e telefone |
| `AwaitingWebOrdersRejector` | Implementa `IAwaitingWebOrdersRejector` (Contracts do Closing): na finalização, `RejectBecauseBusinessDayClosed` nos `AWAITING_ACCEPTANCE` do expediente + auditoria `WebOrderAutoRejected` (`SYSTEM`) |
| `WebOrderExpiryJob`, `WebOrderRetentionJob` (namespace `Jobs`) | Ver §7.11 |
| `WebOrderCart` | Preço do carrinho da página contra o `PublicMenuDto` (tamanho + adicionais × quantidade + bebidas); linha com item fora do cardápio, indisponível ou escondido → `available: false`. O `OrderItemResolver` refaz o preço ao gravar, com as mesmas fontes. `PriceFixedMenu` (lanchonete, EP-003 fatia 5): linha a linha pelo `FixedMenuItems(snapshot, SITE, siteOnly: true).ResolveLine`; `ProductUnavailable` → `available: false` e os ids fora de venda em `unavailableProductIds`; `ValidationFailed` (sabor faltando, tamanho incoerente, componente repetido) sobe como `422`; total da linha por `FixedMenuItems.LineTotal` (pai × filho × neto) |
| `FixedMenuItems` (`siteOnly`) | No modo página, prato, tamanho de prato, tamanho de bebida e combo **pedidos** precisam de `SellsOnSite` (senão `ProductUnavailable`, como a pausa); componentes de combo não (ADR-0057 item 4). Fora da página (`WebOrderSubmitter`, `AddOrderItemsHandler`), `FixedMenuPrice.For(SITE)` cai no balcão para o que não vende no site (Q-309) |
| `BrazilianPhone` | Telefone digitado → E.164 (`+55`), celular (9 + 8 dígitos) ou fixo (2–5 + 7) |
| `TrackingTokenResolver` : `ITrackingTokenResolver` | `SELECT … FROM resolve_tracking_token(hash)` (função `SECURITY DEFINER` da 0007, só tenant e prazo de um hash), antes do tenant (para o middleware). A tabela só é lida no tenant da sessão |
| `TrackingTokenReplaySource` : `IReplaySecretSource` | `trackingToken` do reenvio idempotente, recalculado pelo `ITrackingTokenIssuer` a partir do pedido (`resource_id`) |

**Contracts/Ports públicos:**
- `IOrderPaymentPort` (para Payments):
  - `LoadForPaymentAsync(orderId, expectedVersion)` → snapshot (total, pago, saldo, tipo, cancelado);
  - `ApplyPaymentAsync(orderId, amount)`, `UndoPaymentAsync(orderId, amount)`;
  - `ExistsAsync(orderId)`: o `GET /orders/{id}/payments` de pedido inexistente ou de outro tenant responde `404`, não lista vazia.
  
  A implementação carrega o agregado **no mesmo `AppDbContext` e transação** e chama `ApplyPayment`/`UndoPayment` + `Touch`.
- `IOrderPaymentsReader`: os `payments` do DTO do pedido. Declarada aqui e implementada pelo Payments; até lá, `NoOrderPayments` (lista vazia, registrada com `TryAdd`).
- O "produto vendido não muda de `kind`" é o `OrderProductUsageCheck` (`IProductUsageCheck` do Catalog). Não há `IOrderReadPort`: o Closing lê pedidos por SQL (§7.1).

**Aceite e atendimento (ADR-0046):** listas (`GET /orders`, padrão `acceptanceStatus=ACCEPTED`), painel da cozinha (só aceitos, ordenado por `AcceptedAt`), pendências/fiado (`CustomerDtoBuilder.Pending`) e o `IOrderPaymentPort` (`OrderNotAccepted`) só consideram pedido aceito. Pedido do app nasce `ACCEPTED` com `AcceptedAt = CreatedAt`. O aceite levanta `OrderAccepted` e `OrderCreated` (com `label` nulo: evento sem dado pessoal); recusa, expiração e desistência levantam `OrderRejected`; o envio levanta `OrderAwaitingAcceptance`; o atendimento, `OrderFulfillmentChanged`.

**Implementado:** `OrderChangeEvent` é filho do agregado (coleção nunca carregada; só os novos são inseridos), registrado depois do `Touch`, então `OrderVersion` já é a versão nova. `GET /orders/{id}/history` lê `audit_logs` direto (a entidade `AuditLog` é da Infrastructure); o módulo Audit assume quando `GET /audit-logs` for implementado.

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

**Implementado:** `Payment` e `PaymentReversal` são `Entity` + `ISyncTracked`, não `AggregateRoot`: as tabelas não têm `version`, e a concorrência é a do pedido (`If-Match` = versão do pedido, checada em `IOrderPaymentPort.LoadForPaymentAsync`). Dois estornos simultâneos do mesmo pagamento: o segundo cai no token de versão do pedido (`409 VersionConflict`). A porta usa `FindAsync`, então `Load` e `Apply`/`Undo` mexem na mesma instância rastreada, sem carregar itens. Os eventos `OrderPaymentRegistered`/`Confirmed`/`Reversed` nascem no agregado `Order` (entidade `ORDER` no envelope). `ClosingUpdated` ainda não é emitido: entra com o dispatcher, que agrupa por tenant.

### Closing

| Classe | Responsabilidade |
|---|---|
| `BusinessDay` (agregado [V][S]) | `Open(date, openingBalance, by, now)`, `Close(by, now)` |
| `CashMovement` (agregado [S]) | Imutável |
| `DailyClosing` (agregado [S]) | `Finalize(ClosingFigures figures, countedCash, justification, by, now)`. Valida `JustificationRequired`. Gera `DailyClosingLine`s (quatro `ORDER_TYPE` desde a 0010: `MARMITA`, `PF`, `KILO`, `A_LA_CARTE`, uma linha só para o à la carte, Q-311) e grava `SiteSoldTotal` e `SiteDeliveryFeeTotal` (coluna da 0012, M1). Imutável depois de criado. O `ClosingDtoBuilder` devolve a linha `A_LA_CARTE` zerada para fechamento anterior à 0010 |
| `ClosingAdjustment` (agregado [S]) | Imutável |
| **`ClosingCalculator`** (serviço de domínio puro) | `ClosingFigures Compute(ClosingInputs inputs)`. Recebe os agregados já somados pelo banco (por tipo de pedido e por forma de pagamento; pedidos ativos, cancelados, pendentes; recebidos de dias anteriores; movimentações; estornos) e devolve **todos** os números de 04 §4.9, mais o `SiteSoldTotal` (pedidos ativos `SITE`, **sem a taxa de entrega**) e o `SiteDeliveryFeeTotal` (a taxa deles), revisão de 2026-11-23, M1, ADR-0057 item 12 complementado. `OrderTypes` = `MARMITA`, `PF`, `KILO`, `A_LA_CARTE` (sempre as quatro). É a **única** implementação das fórmulas, usada pelo resumo parcial **e** pela finalização |
| `ClosingInputsQuery` | Uma consulta SQL por bloco (`GROUP BY order_type`, `GROUP BY payment_method`…), no padrão de 05 §5.14, com `businessDayId`. Pedidos: só `acceptance_status = 'ACCEPTED'` (ADR-0046 item 4), com a parte `price_list = 'SITE'` de cada grupo: `SiteSold` = Σ (`total_amount − delivery_fee`) e `SiteDeliveryFees` = Σ `delivery_fee` (`OrderTotalsRow`, para o "Vendido pelo site" e a "Taxa de entrega do site", ADR-0057 item 12, M1). Mais uma consulta de contagens informativas (`OrderStatusCounts`: aguardando aceite, não aceitos, entregas sem `DELIVERED`) |
| `IAwaitingWebOrdersRejector` (Contracts) | Declarada pelo Closing e implementada pelo Ordering (que fica acima): recusa os pedidos do site ainda aguardando na finalização |
| `IBusinessDayReader` (Contracts) | `GetOpenAsync()` → `BusinessDayRef { Id, BusinessDate, Version }` ou `null` |

| Handler | Endpoint |
|---|---|
| `OpenBusinessDayHandler` [I] | `POST /business-days`: `BusinessDayAlreadyOpen`/`BusinessDayClosedToday` → `BusinessDay.Open` → `IMenuMaterializer.MaterializeForAsync(date)` → evento `BusinessDayOpened` |
| `GetCurrentBusinessDayQuery`, `ListBusinessDaysQuery` | `GET /business-days*` |
| `RegisterCashMovementHandler` [I], `ListCashMovementsQuery` | `/closing/cash-movements` |
| `GetClosingSummaryQuery` | `GET /closing/summary`: expediente aberto → `ClosingCalculator` ao vivo; fechado → snapshot |
| `FinalizeClosingHandler` [I][M] | `SELECT ... FOR UPDATE` no `business_day` → confere `If-Match` → `ClosingInputsQuery` → `ClosingCalculator` → `IAwaitingWebOrdersRejector.RejectAwaitingAsync` (ADR-0046 item 13, mesma transação) → `DailyClosing.Finalize` (com quantos recusou) → `BusinessDay.Close` → evento `ClosingFinalized` |
| `CreateClosingAdjustmentHandler` [I] | `/closing/{id}/adjustments` |
| `GetClosingQuery`, `ListClosingHistoryQuery`, `GetMonthlyClosingQuery` | `GET /closing/*`. O mensal soma os snapshots, inclusive `SiteSoldTotal` e `SiteDeliveryFeeTotal` (M1), como gravados |

O `FOR UPDATE` no expediente durante a finalização faz pagamentos e movimentações concorrentes esperarem. Toda escrita que exige expediente aberto lê o `business_day` com `FOR SHARE`, então não há venda "entre" o cálculo e o fechamento.

**Implementado:** `CashMovement`, `DailyClosing` (+ linhas) e `ClosingAdjustment` são `Entity` + `ISyncTracked` imutáveis; só o `BusinessDay` é agregado versionado. A movimentação sobe a versão do expediente com `UPDATE ... SET version = version + 1 RETURNING id` atômico (duas sangrias simultâneas não conflitam; só a finalização com o resumo antigo leva `409`). A finalização faz `SELECT ... FOR UPDATE` por SQL antes de carregar o agregado. `ClosingInputsQuery` usa linhas com `decimal` e converte para `Money` no código, sem depender de conversor em tipo não mapeado.

`ClosingUpdated` é emitido por pagamentos, estornos e movimentações, e agrupado no dispatcher em no máximo 1 a cada 5 s por tenant (06 §6.14).

---

## 7.10 Módulos Audit e Sync; tempo real

### Audit

O `IAuditWriter` vive em Infrastructure (§7.3). O módulo Audit tem só leitura: `ListAuditLogsQuery` (`GET /audit-logs`) e `OrderHistoryProjection`, que é usada pelo `GetOrderHistoryQuery` do Ordering via `IAuditReader` (Contracts).

**Catálogo de `operationType`** (constantes em `AuditOperations`):
- `OrderCreated`, `OrderDetailsUpdated`, `OrderItemAdded`, `OrderItemChanged`, `OrderItemRemoved`, `WeighingAdded`, `WeighingRemoved`, `ProductionStatusChanged`, `OrderCanceled`;
- pedido da página (ADR-0046 item 24): `WebOrderSubmitted` e `WebOrderWithdrawn` (`WEB`, sem ator), `OrderAccepted`, `OrderRejected`, `WebOrderExpired`, `WebOrderAutoRejected` e `WebOrderAnonymized` (`SYSTEM`), `OrderFulfillmentChanged`;
- página de pedidos: `StorefrontUpdated` (sem telefone de contato nem endereço);
- `PaymentRegistered`, `PaymentReversed`;
- `BusinessDayOpened`, `CashMovementRegistered`, `ClosingFinalized`, `ClosingAdjustmentCreated`;
- `MenuCreated`, `MenuItemsReplaced`, `MenuItemUpdated`, `MenuMaterialized`;
- `ProductCreated`, `ProductUpdated`, `MarmitaSizeCreated`, `MarmitaSizeUpdated`;
- cardápio fixo (EP-003): `FixedMenuItemCreated`, `FixedMenuItemUpdated`, `FixedMenuAvailabilityChanged` (o `entityType` diz a raiz);
- `EmployeeRolesChanged`, `EmployeeDeactivated`, `EmployeeReactivated`;
- `InvitationCreated`, `InvitationAccepted`, `InvitationRevoked`;
- `SharedDeviceRegistered`, `SharedDeviceRevoked`;
- `TenantSettingsUpdated`, `CustomerAnonymized`;
- estabelecimento (EP-003): `TenantSignedUp` (com `behaviors` e `labels`), `TenantUpdated`, `TenantLabelsChanged` (só os códigos), `TenantBehaviorAdded` (`PLATFORM_ADMIN`, com `reason`);
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
| `RealtimeHub : Hub` (Api) | `[Authorize]`. Em `OnConnectedAsync`, adiciona aos grupos de 06 §6.14 conforme as permissões. Valida o `appVersion` da query. Sem métodos de cliente. O `negotiate` passa pelo CORS; o `HubOriginMiddleware` confere o `Origin`, porque o WebSocket não passa pelo CORS (ADR-0053 §4.2) |
| `OutboxDispatcher` (`BackgroundService`, `PlatformDbContext`) | **Dois laços independentes**, cada um a cada 250 ms (ou `LISTEN outbox` via `NOTIFY` no commit, otimização futura), com `WHERE processed_at IS NULL AND (available_at IS NULL OR available_at <= agora) FOR UPDATE SKIP LOCKED LIMIT 100`. **Tempo real** (`event_type` sem `IOutboxConsumer`): `IRealtimePublisher.PublishAsync(envelope)` → `processed_at`, numa transação curta (a publicação é em memória). **Consumidores internos** (push pela Expo, imagem antiga no R2): reserva o lote numa transação curta (`attempts + 1`, `available_at = agora + Outbox:ConsumerLeaseSeconds`, 120 s) e confirma; chama cada consumidor **fora** de qualquer transação; grava o resultado de cada mensagem num `UPDATE … WHERE processed_at IS NULL` próprio (sucesso → `processed_at`; falha → `last_error` e `available_at = agora + RetryDelay`). Uma chamada HTTP lenta não segura os `FOR UPDATE` nem atrasa os eventos de tempo real; se a réplica cair no meio, outra pega a mensagem quando a reserva vence (a deduplicação de `push_deliveries` evita o push repetido). Com `attempts ≥ Outbox:MaxAttempts` (10), log de erro e a mensagem para. Um consumidor pode ter `MaxAttempts` próprio (ao desistir, a mensagem é marcada processada com o `last_error` e log de erro). `DispatchBatchAsync` (testes) roda os dois laços em sequência |
| `IRealtimePublisher` / `SignalRRealtimePublisher` | `IHubContext<RealtimeHub>`. Resolve os grupos pelo `eventType` (tabela de 06 §6.14; `TenantModulesChanged` vai para todos do tenant). `ClosingUpdated` é agrupado por tenant |
| `EmployeeAccessNotifier` | Publica `AccessRevoked` no grupo `employee:{id}` direto (sem outbox: não é estado, e o cache já foi invalidado) |

---

## 7.11 Jobs e configuração

| Job (`BackgroundService` + `PeriodicTimer`) | Periodicidade | Trava |
|---|---|---|
| `OutboxDispatcher` | contínuo (250 ms) | `SKIP LOCKED` (seguro com N réplicas) |
| `BillingWebhookProcessor` | contínuo (5 s) | `SKIP LOCKED` |
| `SubscriptionLifecycleJob` | 1 h | advisory lock |
| `InvitationExpiryJob` | 15 min | advisory lock |
| `WebOrderRetentionJob` (Ordering, ADR-0052 itens 3 e 5, ADR-0047 item 4) | 60 min (`WebOrders:RetentionIntervalMinutes`) | advisory lock. Como o de expiração: o `PlatformDbContext` acha os pedidos `WEB` `REJECTED`/`EXPIRED`/`WITHDRAWN` há 7 dias e ainda não anonimizados (`ix_orders_web_retention`), e cada um é anonimizado por `ICommandHandler<AnonymizeWebOrderCommand>` no contexto do tenant. Depois apaga, no papel de plataforma, os `order_tracking_tokens` vencidos e os `web_order_intakes` com mais de 7 dias |
| `WebOrderExpiryJob` (Ordering, ADR-0046 item 14) | 30 s (`WebOrders:ExpiryIntervalSeconds`) | advisory lock. O `PlatformDbContext` só acha os vencidos de todos os tenants (`ix_orders_acceptance_deadline`, lote de 500); cada pedido é expirado por `ICommandHandler<ExpireWebOrderCommand>` num escopo com `InitializeForSystem(tenant)`: RLS, auditoria `SYSTEM`, feed e outbox normais, um pedido por transação. `VersionConflict` (o aceite gravou antes) é registrado em log e ignorado |
| `DeliveryQuoteRetentionJob` (Storefront, ADR-0052 item 5) | 60 min (`Maps:RetentionIntervalMinutes`) | advisory lock. No papel de plataforma: apaga `delivery_quotes` vencidas há 24 h e `geocode_cache_entries` vencidas (7 dias por padrão). Só dado técnico, nada gravado no tenant |
| `PushMaintenanceJob` (Notifications, ADR-0049 itens 4 e 8) | 5 min (`Push:MaintenanceIntervalMinutes`) | advisory lock. O `PlatformDbContext` só acha os tickets enviados há `Push:ReceiptDelayMinutes` (15) e ainda não conferidos (`ix_push_deliveries_receipt`, lote de 1000); a conferência é no contexto de cada tenant (`PushReceiptChecker`; Expo fora do ar → fica para a próxima rodada). Depois, no papel de plataforma, apaga só dado técnico vencido: registros sem uso há 30 dias e entregas com mais de 7 |
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
| `App` (`MinSupportedAppVersion:ANDROID`, `:IOS`, `:WEB`, `StoreUrl:ANDROID`, `:IOS`; sem `StoreUrl:WEB`, então o `426` da web leva `storeUrl: null`) | `AppVersionOptions` | não |
| `Cors` (`RestaurantAppOrigins`: Development `http://localhost:8082`; homolog `https://app-homolog.almoseis.app`; produção `https://app.almoseis.app`) | `RestaurantAppCorsOptions` | não |
| `Idempotency` (`TtlHours = 72`, `LockTimeoutSeconds = 5`) | `IdempotencyOptions` | não |
| `WebOrders` (`AcceptanceMinutes = 10`, `ExpiryJobEnabled = true`, `ExpiryIntervalSeconds = 30`, `RetentionJobEnabled = true`, `RetentionIntervalMinutes = 60`, `ReminderMinutes = 3`) | `WebOrderOptions` | não |
| `Cors:PublicPageOrigins` (Development `http://localhost:3000`) | `PublicPageCorsOptions` | não |
| `Turnstile` (`Enabled = true`, `SecretKey`, `TimeoutSeconds = 3`). Development: a chave secreta de teste oficial `1x0000000000000000000000000000000AA`; testes: `Enabled = false`. `Enabled` sem chave, ou com chave de teste (`1x0000…`/`2x0000…`/`3x0000…`) fora de Development, impede a API de subir | `TurnstileOptions` (`TurnstileOptionsValidator`) | chave sim |
| `PublicPage` (`IpHashSecret`, `TrackingTokenSecret`, `RendererKey`). Os dois primeiros obrigatórios fora de Development (≥ 32 caracteres, diferentes entre si); vazios em Development → aleatórios por processo | `PublicPageOptions` (`PublicPageOptionsValidator`) | sim |
| `Outbox` (`Enabled`, `PollMilliseconds = 250`, `BatchSize = 100`, `MaxAttempts = 10`, `ConsumerLeaseSeconds = 120`) | `OutboxOptions` | não |
| `RateLimiting` (`Enabled`, `PublicQuotesPerStorePerDay = 300`) | `RateLimitSettings` | não |
| `Push` (`Provider` = `Expo`\|`Fake`\|`None`, `ChannelId` = `web-orders`, `Sound` = `web_order.wav`, `MaxAttempts` = 3, `RetryDelaySeconds` = 30, `ReceiptDelayMinutes` = 15, `MaintenanceJobEnabled`, `MaintenanceIntervalMinutes` = 5, `RegistrationRetentionDays` = 30, `DeliveryRetentionDays` = 7, `Expo:AccessToken`/`SendUrl`/`ReceiptsUrl`/`TimeoutSeconds` = 5). `appsettings.json`: `Expo`; Development e testes: `Fake`; chave ausente: `Fake`. Token da Expo: `dotnet user-secrets set Push:Expo:AccessToken ...` (Key Vault em homolog/produção); sem ele, o envio funciona sem a "enhanced push security" | `PushOptions` (`PushOptionsValidator`, `ValidateOnStart`) | token da Expo sim |
| `Maps` (`Provider` = `None`\|`Fake`\|`Osm`\|`Google`, `TimeoutSeconds = 3`, `GeocodeCacheDays = 7`, `RouteCacheDays = 7`, `AddressHashSecret` (obrigatório fora de Development, ≥ 32 caracteres), `RetentionJobEnabled`, `RetentionIntervalMinutes = 60`, `Google:ApiKey`/`GeocodingUrl`/`RouteMatrixUrl`, `Osm:NominatimUrl`/`OsrmUrl`/`UserAgent`/`NominatimMinIntervalMilliseconds = 1000`/`AcceptStreetLevel`, `Fake:CenterLatitude`/`CenterLongitude`/`RadiusKm`/`RouteFactor`). `appsettings.json`: `None`; Development e testes: `Fake`; chave ausente da configuração: `Fake`. Trocar: `dotnet user-secrets set Maps:Provider Osm` (instâncias públicas do Nominatim e do OSRM, só desenvolvimento) ou `Maps:Provider Google` + `Maps:Google:ApiKey` (Key Vault em homolog/produção) | `MapsOptions` (`MapsOptionsValidator`, `ValidateOnStart`) | chave do Google e `AddressHashSecret` sim |
| `ForwardedHeaders` (`KnownNetworks`: CIDRs; `KnownProxies`: IPs). Vazio → só loopback (padrão do ASP.NET Core; Development fica assim). Homolog/produção: a sub-rede de onde o ingress do Container Apps conecta (`09-devops.md`) | `ForwardedHeadersSettings` → `ForwardedHeadersOptions` (`XForwardedFor \| XForwardedProto`, `ForwardLimit = 1`) | não |

O `appsettings.json` versionado só tem placeholders (`docs/02-padroes.md` §2.5). A imagem Docker usa a base `aspnet` **não chiseled**, que traz `tzdata`, necessário para `TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")` no cálculo da `BusinessDate`.

---

## 7.12 Testes

| Projeto | O que cobre | Como |
|---|---|---|
| `UnitTests` | Agregados (`Order`, `Payment`, `Subscription`, `DailyClosing`, `Menu`, `TenantEmployee`), `ClosingCalculator`, `KiloPricing`, `Money`, `RolePermissions`, taxa por distância (`DeliveryFeeRule` com a tabela do ADR-0048, `DeliveryAddressKey`, ponto do restaurante, snapshot no pedido) e os provedores de mapas (`FakeMaps`; Google e OSM contra um `HttpMessageHandler` simulado; validação de `Maps:*`); push (`PushNotificationTests`: título e corpo, minutos restantes, formato e máscara do token, mensagens do outbox com o lembrete agendado e sem o total, `ExpoPushSender` contra um `HttpMessageHandler` simulado, validação de `Push:*`); URL pré-assinada do R2 com o relógio da aplicação atrasado (`X-Amz-Expires=900`); pedido à la carte (`ALaCarteOrderTests`: 2 combos com bacon e Coca Zero = 73,00, quantidade do combo reprecifica netos (109,50), parte não sai nem muda sozinha, remover o combo leva a árvore, açaí 500 ml + granola e Suco 9,00 + 1,00, tamanho/sabor/componente/adicional conferidos, pausa → `ProductUnavailable`, tabela `COUNTER`×`SITE`, `KindFits` de cada modalidade, à la carte nos três atendimentos, adicional depois numa parte reabre a produção, quitado bloqueia; `ClosingCalculatorTests` com `A_LA_CARTE`, `SiteSoldTotal` sem a taxa e `SiteDeliveryFeeTotal` (M1)); cardápio fixo (`FixedMenuDomainTests`: "Tamanho?", substituição de tamanhos sem apagar e mantendo a pausa, PATCH sem mudança, Suco 500 ml 9,00 + 1,00 = 10,00, pausa derivada dos combos, todos os sabores pausados, combo 32,00 + 4,00 + 0,50 = 36,50, vínculos de grupo, `PriceFor`/`FixedMenuPrice.For` por tabela, preço de site do tamanho de marmita e da bebida, bebida de 0,00 no site com preço de site 0,00, cafezinho 3,00 grátis no site, negativo e adicional `422` (I6)); delivery por telefone (`PhoneDeliveryOrderTests`: tabela `ContactRequired` da Q-312, entrega de marmita sem telefone `422`, balcão sem telefone, à la carte de entrega e retirada sem nome ou telefone `422`, forma prevista obrigatória na entrega e proibida no salão, RF-312 57,00 com troco para 100,00 sem pagamento, troco ≥ total (47,00: 50,00 e 47,00 passam, 46,99 e 40,00 não), troco só em dinheiro, taxa digitada `MANUAL`, à la carte para o salão limpa a forma, `PATCH` sem telefone ou de comanda sem nome para retirada `422`; I1: `PATCH` da forma e do troco (dinheiro → Pix apaga o troco, troco abaixo do total, troco no cartão e forma nula na entrega `422`, sem mudança não sobe a versão), retirada que vira entrega exige a forma, comanda com forma ou troco `422`, quitado `OrderAlreadyPaid`; I2: taxa de 5,00 para 12,00 passa o troco para 30,00 (total 32,00) e marca `ChangeForBelowTotal`, editar o troco abaixo do total `422`); comportamentos e módulos (`EstablishmentBehaviorTests`: mapa do ADR-0054, tenant sem linha = restaurante, códigos iguais aos `CHECK`s da 0008, lista fixa de rótulos com os textos, `Tenant.Create`/`ReplaceLabels`/`AddBehavior`, `ModuleOf` por `OrderType`, `403 ModuleNotEnabled` com `module`, grupo do `TenantModulesChanged`) | Sem banco. **Exemplos numéricos** do épico e do 06 viram casos de teste (ex.: o fechamento de R$ 3.650 / R$ 750 / −R$ 10) |
| `IntegrationTests` | Cada endpoint de 06 | `WebApplicationFactory` + **um** container Testcontainers PostgreSQL 16 por execução, migrations aplicadas e papéis reais (`almoseis_app`). Respawn entre testes |
| ↳ `TenantIsolationTests` | Rotas autenticadas com id de recurso em `/fixed-menu/*`, `/orders/*` (inclusive `/orders/{orderId}/payments`), `/payments/*` e `/customers/*` | Um `[Fact]` sobre o catálogo de rotas: lê o `EndpointDataSource` da API (rotas com parâmetro nesses prefixos), exige um caso para cada uma (rota nova sem caso, ou caso sem rota, falha o teste) e chama cada rota como o tenant B com os ids do tenant A (raízes do cardápio fixo, pedido com item, pagamento, cliente com endereço), com corpo válido, `Idempotency-Key` e `If-Match`. Toda resposta é `404 NotFound`, e as versões e contagens de A não mudam. Os dois tenants têm os dois comportamentos e o expediente aberto, para o `404` não ser escondido por `403 ModuleNotEnabled` ou `409 BusinessDayRequired`. As outras raízes (`/menus`, `/marmita-sizes`, `/products`, `/business-days`, `/closing`…) têm o isolamento coberto nos testes de fluxo de cada módulo |
| ↳ `IdempotencyTests` | Endpoints [I] | Reenvio com mesmo corpo → mesma resposta + `Idempotent-Replayed`; corpo diferente → `422`; dois em paralelo → um registro só |
| ↳ `ConcurrencyTests` | Endpoints [M] | `If-Match` velho → `409` com `current`; dois pagamentos concorrentes no mesmo saldo → um falha |
| ↳ `WebSessionFlowTests` | App no navegador (ADR-0053) | Login web: refresh só no cookie `HttpOnly`; refresh pelo cookie com rotação; reuso revoga a família e apaga o cookie; `Origin` ausente ou estranho → `403`; refresh no corpo vindo da web → `422`; mobile sem `Set-Cookie` e sem ler cookie; logout apaga; aparelho compartilhado web (lista e PIN pelo cookie, revogação apaga o cookie; `X-Device-Secret` ignorado na web; cookie do tenant A com `X-Tenant-Id`/`tenantId` do B → `401 InvalidDeviceSecret`); cookie emitido por `signup`, aceite de convite e `switch-tenant`; `switch-tenant` revoga o refresh anterior da instalação (web e mobile); erros de `/auth/*` levam CORS; `DELETE /auth/shared-device` sem `WEB` sem `Set-Cookie`; preflight de origem permitida e negada e sem headers fora do contrato; `Origin` no hub; `426` da web sem `storeUrl`. Cliente sem `CookieContainer`: os cookies vão no header `Cookie`, como no navegador |
| ↳ `WebOrderFlowTests` | ADR-0046 | Pedido do app nasce aceito com número; pedido do site aguarda fora das listas, da cozinha e do fechamento e recusa toda escrita (`OrderNotAccepted`); aceite (número seguinte da data, cliente pelo telefone, `OrderCreated` sem rótulo, reenvio idempotente, `VersionConflict` com `current`); dois aceites simultâneos → um aceite e um número; recusa (permissão, motivo, texto); job de expiração e aceite tardio (`WebOrderExpired`); **corrida aceite × expiração** (o pedido é travado por uma conexão externa até os dois UPDATEs entrarem na fila; só o primeiro grava); **teste obrigatório do fechamento** (recusado, expirado, desistência e aguardando nunca entram no vendido; a finalização recusa os aguardando); desistência; transições de atendimento, "Concluir e marcar", `OrderAlreadyDispatched`; isolamento entre tenants. O envio usa o comando interno `SubmitWebOrderCommand` (a rota pública é da fatia 2); o job desligado por configuração e chamado por `RunOnceAsync` |
| ↳ `PublicStorefrontFlowTests` | EP-002 fatia 2 (+ preço de site do EP-003 fatia 2: página com o preço de site e sem o tamanho que não vende no site, `CartChanged` com o preço de balcão, pedido `SITE` × app `COUNTER`, item acrescentado no app pelo preço de site ou de balcão, ativação exige tamanho no site; bebida de 0,00 e cafezinho com preço de site 0,00 na página a 0,00, mostrada de novo sem preço sem `422`, negativo, adicional e tamanho de marmita com 0,00 `422`, I6) | Página nasce inativa com slug; ativação exige endereço; perfil e cardápio (indisponível desabilitado, `isVisibleOnPage`); `CLOSED`/`PAUSED`/`UNAVAILABLE` (assinatura suspensa) recusam com `StoreUnavailable`; `storefront.manage`; slug antigo `301`/`308`, inexistente `404`, reservado/de outro tenant `422`, volta ao antigo; **isolamento**: slug de A não lê nem grava dados de B (produtos de B → `CartChanged`; `Authorization` de B ignorado); preço do servidor (`CartChanged` com o carrinho); pedido `AWAITING_ACCEPTANCE` no app e aceite pela fatia 1 refletido no acompanhamento; idempotência anônima (replay, `KeyReused`, `428`); 21 marmitas, só bebidas, PF, telefone, troco em Pix, entrega (`DeliveryUnavailable`), campo-isca; 2 pendentes por telefone e 3 por IP; acompanhamento (só primeiro nome, `304`, headers, token inventado `404`, vencido `410`); desistência; Turnstile ligado (`siteverify` simulado: sem token/falha → `403`, fora do ar → `CAPTCHA_UNAVAILABLE`); rate limit `public-order`; CORS da página sem credenciais e separado do app; retenção (anonimização, tokens e IP) |
| ↳ `SnackBarStorefrontFlowTests` | EP-003 fatia 5 (página da lanchonete) | `sections`; `fixedMenu` só com o que vende no site, pelo preço de site (aba sem prato some, tamanho/bebida/combo fora do site somem); pausa de tamanho, opção e sabor tira da página, e o combo com componente pausado some; `CLOSED`/`UNAVAILABLE` sem cardápio; pedido `A_LA_CARTE` com combo (69,80), faixa "Novos do site", `itemCount` 3/`marmitaCount` 0 no evento e no push, aceite, cozinha sem a linha do combo, acompanhamento com `parts`, Q-309 no add-items (site 6,50 × balcão 18,00); entrega com taxa `FIXED` e recusa; `CartChanged` (total, fora do site, pausado, com `unavailableProductIds`); um tipo por pedido, só bebidas, 21 pratos e combos, 21 por linha, adicional 11, sabor faltando, marmita numa lanchonete; ativação exige prato ou combo no site (bebida não basta; pausado basta); restaurante com os dois comportamentos (duas seções, um pedido de cada, mistura → `422`); **isolamento** entre duas lanchonetes |
| ↳ `DeliveryQuoteFlowTests` | EP-002 fatia 4 (ADR-0048) | Provedor roteirizado em memória (`ScriptedMaps` no DI): ponto do restaurante geocodificado sem confirmação, confirmado, mantido com `location` ausente ou nulo e zerado com endereço novo; perfil com `deliveryAvailable`/`deliveryRule`; 4,5 km → 3,60, 2,1 km → mínimo 3,00, 6,0 km → 4,80, 6,1 km e sem rota → `OutOfDeliveryArea`, aproximado/inexistente → `422` no campo; cache por tenant (segunda cotação não chama o provedor; só o hash, sem texto); envio com a taxa, snapshot `DISTANCE`, aceite e acompanhamento com `4.5`; preço por km mudado → `409 DeliveryQuoteChanged` com cotação nova gravada e reenvio aceito; vencida com a mesma taxa → aceita; endereço trocado → `409`, só complemento → aceita; `CartChanged` com a taxa; provedor fora do ar → `DeliveryUnavailable`, perfil ainda com entrega e retirada funcionando; **isolamento** (cotação de A recusada em B, cache não compartilhado); retenção |
| ↳ `PushFlowTests` | EP-002 fatia 5 (ADR-0049) | `FakePushSender` e o dispatcher chamado pelo teste (`Outbox:Enabled = false`): registro, atualização e remoção (`installationId` de outro aparelho, `WEB` e token inválido → `422`); destinatários por permissão (`orders.accept`) e pela opção; conteúdo sem dado pessoal nem valor, título com o estabelecimento para quem tem dois vínculos; pedidos do app sem push; reenvio da mesma mensagem do outbox sem push repetido; Expo fora do ar → nova tentativa em 30 s e desistência na terceira; lembrete agendado aos 3 min, enviado uma vez e não enviado para pedido já aceito; `DeviceNotRegistered` no ticket e no recibo; limpeza de 30/7 dias; **isolamento** (push de A não vai para B; o token registrado em B sai de A); logout, troca de tenant, desativação, troca de perfis e revogação do aparelho apagam o registro, e o logout do aparelho compartilhado não |
| ↳ `EstablishmentBehaviorFlowTests` | EP-003 fatia 1 (ADR-0054) | Cadastro como restaurante e como lanchonete (`me.tenant.behaviors`/`labels`/`modules`, linha `SIGNUP` com o proprietário, auditoria); `422` sem comportamento, sem rótulo e com rótulo de outro comportamento, `400` com código fora da lista; `PATCH /tenant` com rótulos (`428` sem `If-Match`, mesma lista sem nova versão, `422` em `labels`, `409` com `current`, auditoria, feed `TENANT`, `settings.manage`); lanchonete: `POST /orders` `PF`/`MARMITA`/`KILO`, tamanhos, cardápios, preço do quilo e escritas de `/products` (`module: MARMITA`) → `403 ModuleNotEnabled` (antes da validação do corpo), listas vazias e abertura do expediente sem clonar o modelo semanal; admin acrescenta `SNACK_BAR` (`422`, `404`, token de estabelecimento `403`, repetição sem efeito, linha `PLATFORM_ADMIN`, auditoria com o administrador, outbox `TenantModulesChanged` com `seq`, feed visível à cozinha, rótulo da lanchonete aceito depois); a sessão do admin num tenant tem `app.user_id` vazio e não vê o tenant do qual ele é proprietário (a sessão JWT do mesmo usuário vê os dois); **isolamento** (rótulos, módulos e linhas por tenant, também pela RLS com o login da aplicação); tenant sem linha (janela do deploy) como restaurante e gravado na primeira escrita |
| ↳ `FixedMenuFlowTests` | EP-003 fatia 2 (ADR-0056) | Cadastro com `ETag`/`Location` e leitura numa chamada; reordenar por `sortOrder`; "Tamanho?" (`422` sem tamanho, sem preço, com os dois); preço de site preenchido com o de balcão; **pausa derivada** (Batata P pausa os dois combos que a usam e não o terceiro, sem mudar a versão do combo; reativar devolve; um sabor pausado mantém a lata, os dois a tiram e os combos com ela); pausa do combo; a porta `IFixedMenuReader` vê o mesmo; a pausa sobrevive à abertura do expediente; **KITCHEN pausa mas não edita**, ATTENDANT pausa, CASHIER não; `409 UsedInActiveCombos` (prato, tamanho fora da lista, tipo) e inativação depois de inativar os combos; componentes conferidos; adicionais = categoria ∪ prato; `428`/`409` com `current`; PATCH sem mudança sem versão nova; auditoria, feed e outbox; restaurante → `403 ModuleNotEnabled` e listas vazias; **isolamento** (leitura, pausa, combo e prato com itens de outro tenant; RLS com o login da aplicação) |
| ↳ `ALaCarteOrderFlowTests` | EP-003 fatia 3 (ADR-0058) | Comanda de mesa com açaí 500 ml + granola, lata com sabor e 2 combos com bacon e Coca Zero (99,00; o combo 73,00), árvore e snapshots na resposta, `parent_item_kind`/`parent_dish_id` no banco, reenvio idempotente; **cozinha** com 3 itens (açaí, X-Burguer ×2 com bacon, Batata P ×2), sem combo nem bebida; tamanho/sabor faltando e adicional fora do prato → `422`; pausa da Batata P (combo) e da Coca Zero → `422 ProductUnavailable`; **um tipo por pedido** (bebida do catálogo e PF na comanda, prato fixo no PF, também num tenant com os dois comportamentos e na inclusão); inclusão depois com `If-Match` (adicional numa parte de prato reabre a produção, segundo funcionário `409` com `current`, adicional na bebida do combo `422`), quantidade do combo reprecifica, parte não sai nem muda de quantidade sozinha, remover o combo leva a árvore; pagamento parcial e troco, quitado bloqueia, cancelar exige estornar; fechamento com a linha `A_LA_CARTE` e `siteSoldTotal`/`siteDeliveryFeeTotal` no resumo e no snapshot; `siteSoldTotal` só com o pedido `SITE` aceito e **sem a taxa** (retirada 40,00 + entrega 45,00 com 5,00 de taxa → 80,00 e 5,00, no resumo, no snapshot, no banco e no mensal, M1); entrega/retirada com os campos de hoje e comanda "para viagem" com `tabNumber`; restaurante → `403 ModuleNotEnabled` (`A_LA_CARTE` no salão, `DELIVERY` fora); `PATCH` que troca o atendimento confere o módulo do novo (`DELIVERY` na comanda que vira retirada, `A_LA_CARTE` na retirada que vira comanda, antes da versão; sem trocar, edita); **isolamento** (prato, combo e adicional de outro tenant → `ProductUnavailable`; pedido e painel de outro tenant) |
| ↳ `PhoneDeliveryFlowTests` (+ `ALaCarteOrderFlowTests.Phone_delivery_is_born_accepted_in_the_kitchen_with_change_and_contact_rules`, `OrderFlowTests.Delivery_fee_typed_at_creation_needs_no_permission_but_later_changes_do`) | EP-003 fatia 4 (ADR-0059) | Telefone novo cria o cliente uma vez, sem `customer_addresses`, com feed `CUSTOMER` versão 1 e `CustomerCreated` sem telefone; forma prevista Pix sem pagamento; `POST /customers/lookup` (sem cadastro → `customer: null`; nome, pendência e `lastDeliveryAddress`; formato `422`; a rota antiga `GET /customers/by-phone` não existe; KITCHEN e CASHIER sem `orders.create` → `403`, ATTENDANT encontra); **mesmo telefone em cinco formatos (`(34) 99111-0020`, `+55 34 …`, `034…`, `34…`, E.164) → um cliente, gravado em E.164, e a busca o acha em qualquer formato; `PATCH` normaliza**; outro nome no mesmo telefone fica só no pedido (Q-314); **4 pedidos simultâneos com o mesmo telefone novo → 1 cliente**; reenvio idempotente sem duplicar pedido nem cliente; marmita: entrega sem telefone ou sem forma `422`, troco abaixo do total ou em Pix `422`, troco igual ao total aceito, balcão sem telefone e sem cliente, PF com forma `422`, forma desabilitada `PaymentMethodDisabled`; **Q-315: ATTENDANT digita 7,00 (padrão 5,00) → `MANUAL` e auditoria com o valor e a padrão; `PATCH` da taxa pelo ATTENDANT → `403 PermissionDenied` (`orders.delivery-fee.edit`), pelo dono → 200**; aba Delivery: telefone e site aceito na mesma lista com `fulfillmentType` repetido, `expectedPaymentMethod`/`changeFor` no resumo, filtro `fulfillmentStatus`, valor desconhecido `400`; à la carte: 57,00 com taxa `MANUAL` e troco para 100,00, número e cozinha na hora, troco para 50,00, entrega sem forma, retirada sem telefone ou sem nome e salão com forma → `422`; **I1: `PATCH` da forma e do troco** (dinheiro → Pix apaga o troco, auditoria antes/depois sem telefone, troco abaixo do total `422`, forma nula na entrega `422`, forma desabilitada `PaymentMethodDisabled`, versão velha `409 VersionConflict`, quitado `409 OrderAlreadyPaid`, outro tenant `404`); retirada que vira entrega sem forma `422`, PF com forma `422`; **I2**: item incluído e taxa que passam o troco são aceitos com `changeForBelowTotal: true` no pedido e no resumo, editar o troco abaixo do total `422`; **isolamento** (mesmo telefone em dois tenants, busca e lista de B sem os dados de A) |
| ↳ `ProtectionFlowTests` | ADR-0035 e ADR-0041 | `426`; limites por login, por IP e por instalação (inclusive `GET /auth/shared-device/employees`, que divide a partição com a rota do mobile); `X-Forwarded-For` de proxy confiável separa os clientes, de endereço não confiável é ignorado, e só a entrada da direita conta (`ForwardLimit = 1`). Um `IStartupFilter` do teste simula o IP de quem conecta, porque o TestServer não tem um |
| ↳ `DdlMigrationSyncTests` (unitário) + `SchemaTests` | Migrations × `05-ddl.md` × modelo EF | O script embutido é idêntico aos blocos do `05` (a inicial = blocos antes de 05 §5.17; cada alteração = os blocos da sua subseção em §5.17). A migration aplicada pelo login de migration passa no `05-ddl-smoke-test.sql` (45 checagens), e toda entidade EF encontra suas colunas no banco (ADR-0042), com as `IModuleModelConfiguration` de **todos** os módulos registradas pela API (desde a 0009) |
| `ArchitectureTests` | ADR-0029 | regras abaixo |

**Regras de arquitetura (NetArchTest):**
1. Tipos em `*.Domain` não dependem de `Microsoft.EntityFrameworkCore`, `Microsoft.AspNetCore` nem `AlmoSeis.Infrastructure`.
2. Um módulo só usa de outro módulo tipos no namespace `*.Contracts`.
3. Controllers não dependem de `AppDbContext` nem de tipos `*.Domain`.
4. Nenhum tipo usa `DateTime.Now`/`UtcNow`/`DateTimeOffset.Now`/`UtcNow` (checagem por análise IL do teste).
5. Nenhum tipo fora de `Infrastructure`/`Admin`/`Billing`/`Sync` usa `PlatformDbContext` ou `IgnoreQueryFilters`. Exceção: jobs de plataforma de um módulo, no namespace `AlmoSeis.Modules.X.Jobs`, que só leem candidatos entre os tenants e gravam no contexto de cada tenant (`WebOrderExpiryJob`), ou só apagam dado técnico vencido (`DeliveryQuoteRetentionJob`; o `PushMaintenanceJob` faz os dois).
6. Toda entidade com propriedade `TenantId` implementa `ITenantOwned`.
7. Toda ação de controller que não é GET tem `[HasPermission]` ou `[AllowAnonymous]` explícito.
8. Cookie da requisição só é lido por `WebSession.ReadCookie`, que confere o `Origin` antes (ADR-0053 §3.4). Análise de fonte: `.Cookies` (exceto `Append`/`Delete` da resposta), o header `Cookie` e `IRequestCookieCollection` fora do `ReadCookie` falham. Os testes de fonte (4 e 8) acham `backend/src` subindo a partir da saída do build ou, com `--artifacts-path`, a partir do próprio arquivo do teste.
9. Controllers públicos (`AlmoSeis.Api.Controllers.Public`) só recebem handlers cuja implementação é `IPublicHandler`, e todo handler `IPublicHandler` é usado por eles (ADR-0045 item 7).
10. Regra de negócio pergunta pelo **módulo** (ADR-0054 itens 7 e 13): fora do Identity (inclusive na Api), nenhum tipo depende de `EstablishmentBehavior` nem de `EstablishmentLabel`; no Identity, rótulo só nos Contracts, no agregado `Tenant`/`TenantLabel`, no cadastro, no `PATCH /tenant`, no `Me` e no mapeamento EF.

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
| Chaves `Guid` | O `AppDbContext` marca toda PK `Guid` como `ValueGenerated.Never` | Os ids são UUID v7 da aplicação; sem isso, um filho novo com id preenchido (ex.: `MenuItem`) descoberto pela navegação vira UPDATE e falha como conflito de concorrência |
| Enums no banco | Convenção global: todo enum vira texto `UPPER_SNAKE` (`UpperSnakeEnumConverter<T>`), igual ao JSON e aos CHECKs do 05 | Um só formato em banco, API e auditoria; enums ficam nos `Contracts` do módulo |
| Enums em query | `UpperSnakeEnumModelBinderProvider` (`?kind=MAIN_DISH`); valor desconhecido → `400 MalformedRequest` | O binder padrão só aceita o nome C# (`MainDish`) |
| Model state inválido | `InvalidModelStateResponseFactory` lança `BusinessException(MalformedRequest)` com `errors` | O 400 padrão do `[ApiController]` não traz `errorCode` |
| PATCH parcial | Corpo com `Optional<T>` (SharedKernel) + `OptionalJsonConverterFactory`: ausente = sem valor, `null` = limpar; `null` em campo não anulável → `400` | Distinguir "não mexe" de "limpa" sem `JsonElement` nos handlers |
| Versão por mutação | `AggregateRoot.Raise(evento)` na criação (nasce na versão 1); `Touch` só quando algo mudou de fato. Um PATCH sem mudança real devolve a mesma versão | Evita feed e eventos de tempo real sem mudança |
| `current` do 409 | Cada módulo registra um `ICurrentResourceReader` por `entityType`; o `ProblemDetailsExceptionHandler` o chama depois do rollback | O exception handler não conhece os módulos |
| JSON do ProblemDetails | `ConfigureHttpJsonOptions` recebe as mesmas convenções do MVC | O writer de ProblemDetails usa as opções HTTP, e o `current` sairia com dinheiro como objeto |
| Envelope de tempo real | `seq` e `entityVersion` vêm da entrada do feed da mesma transação (o mapper só informa tipo, entidade, ator e `data`) | O evento é montado antes do incremento de versão |
| Regras por campo no domínio | `BusinessException.Validation(campo, mensagem)` devolve o mesmo `422 { errors }` do FluentValidation, com o caminho da API (`items[1].pfPrice`) | Regras que dependem de estado (preço × tipo, produto do cardápio) não cabem no validator |
| Login de admin | `AdminLoginHandler` emite só access token (sem refresh, porque `refresh_tokens` exige tenant) com `security_stamp` do **usuário**. Tokens de estabelecimento não levam mais `platform_admin` | ADR-0043 |
| Colunas com número | A convenção snake_case do EF gera `orders_last30days` para `OrdersLast30Days` (sem `_` antes do número); aliases de `SqlQuery` seguem isso | Pego pelo teste do detalhe de tenant no admin |
| Imagem de produto | `POST /products/{id}/image-upload` só assina (chave nova a cada pedido: `t/{tenant}/p/{produto}-{aaaaMMddHHmmss}-{6 hex}.{ext}`). O `PATCH` com `imageStorageKey` nova confere o objeto no bucket (existe, até 2 MB, JPEG/PNG/WebP) antes de aceitar. Sem `Storage:R2` configurado (desenvolvimento e testes), a conferência é pulada e o pedido de URL falha com 500 | A URL pré-assinada de PUT não limita o tamanho; a conferência no PATCH é a barreira. Credenciais locais: `dotnet user-secrets set "Storage:R2:AccessKeyId" ...` (e `SecretAccessKey`, `AccountId`, `Bucket`) em `backend/src/AlmoSeis.Api` |
| Endpoint do R2 | `R2Options.Endpoint` usa só protocolo e host do `ServiceUrl`; `PublicBaseUrl` sem o bucket (`https://pub-<hash>.r2.dev` ou domínio próprio) | Validado contra o bucket real em 2026-10-27: com o bucket no caminho do `ServiceUrl`, o SDK em path-style grava a chave como `<bucket>/t/...` e a URL pública "precisa" do bucket. A observação do UrbanCarePlatform de pôr o bucket no `PublicBaseUrl` vem desse mesmo efeito. O r2.dev recusa o user-agent padrão do Python (`403`) |
| Imagem anterior do produto | `Product.Apply` levanta `ProductImageDiscarded` com a chave antiga quando a imagem é trocada ou removida. O mapper gera a mensagem interna `ProductImageDiscarded` no outbox (mesma transação), e o `OutboxDispatcher` a entrega a um `IOutboxConsumer` (`ProductImageCleanup`) em vez do SignalR. O consumidor só apaga chaves `t/{tenant}/p/{produto}-*` do próprio envelope | Apagar só depois do commit, com novas tentativas em falha e sem tabela nova. Validado contra o bucket real em 2026-10-27 |
| Consumidores internos do outbox | `IOutboxConsumer` por `eventType`: o dispatcher entrega ao consumidor quando existe um; senão, ao `IRealtimePublisher`. Falha conta em `attempts` como qualquer mensagem | Reaproveita a entrega pelo menos uma vez do outbox para efeitos colaterais fora do banco |
| Produto em uso | `IProductUsageCheck` (Contracts do Catalog) implementado por Menu (e, depois, Ordering): produto em cardápio ou vendido não muda de `kind` | Catalog não pode depender de quem o referencia |
| Versão mínima do app | `AppVersionMiddleware`: compara `X-App-Version` (ou `appVersion` na query do hub) com `App:MinSupportedAppVersion:{plataforma}`. **Sem o header, passa** | Ferramentas, webhooks e testes não mandam o header; o app sempre manda (ADR-0035) |
| Rate limiting | `GlobalLimiter` com `PartitionedRateLimiter.CreateChained`: um elo por limite do ADR-0041, cada um devolve "sem limite" fora das suas rotas. `RateLimitKeyMiddleware` lê `login`/`installationId` do corpo de `/auth/login` e `/auth/pin-login` antes do limiter. `RateLimiting:Enabled = false` nos testes, exceto no teste dedicado | O particionamento nativo descarta partições ociosas; por réplica e em memória, como o ADR decide |
| `GET /audit-logs` | Módulo Audit, filtro de tenant explícito (audit_logs tem tenant anulável). Nome do aparelho por `IEmployeeDirectory.GetDeviceNamesAsync` | O Audit só lê; quem escreve é o `IAuditWriter` da Infrastructure |
| Aparelho compartilhado | Só se registra a partir dele mesmo (`installationId` = o do token). Registrar de novo a mesma instalação revoga o registro anterior por `ExecuteUpdate` antes do INSERT e gera segredo novo | Tablet reinstalado volta sem suporte; o índice único de instalação ativa vale por comando |
| Login por PIN | Sem esquema de autenticação próprio: o handler confere `X-Device-Secret` (hash em tempo constante) no tenant do corpo/header. A sessão do banco usa só o tenant do aparelho (`app.user_id` vazio), então `me.memberships` lista só esse estabelecimento. PIN com hash do `IPasswordHasher` | Mais simples que um `AuthenticationHandler`; o aparelho é de um estabelecimento só |
| "Esqueci a senha" | Token do Data Protection (`ITimeLimitedDataProtector`, 30 min) com id e security stamp do usuário: uso único porque a senha nova muda o stamp. Sem auditoria (não há tenant). `IEmailSender` padrão só registra em log | Não precisa de tabela. **Pendências:** provedor de e-mail do ADR-0040 e chave do Data Protection persistida e compartilhada entre réplicas (Key Vault/Blob) antes de produção |
| Convite pelo código | Leitura com `IgnoreQueryFilters()` sob a política `invitation_by_code` (`app.invitation_code` na sessão); em seguida `InitializeForTenant` + `ApplyAsync` para ler o nome do estabelecimento e gravar no tenant do convite. A consulta pública abre a conexão uma vez para as variáveis de sessão valerem | É a segunda exceção ao filtro de tenant do EF, além dos vínculos do usuário |
| Código de convite único | `INSERT ... ON CONFLICT (code) WHERE status = 'PENDING' DO NOTHING`, até 5 tentativas | O índice é global, mas a RLS esconde os convites dos outros tenants; uma violação de unicidade abortaria a transação |
| Validade dos códigos | Convite 3 dias; redefinição de senha 24 h. Vencido e ainda `PENDING` é tratado como `EXPIRED` na leitura (sem job por enquanto) | 06 §6.4 mostra 72 h para o convite; a redefinição é mais sensível |
| Revogação de acesso | Troca de perfis e desativação geram novo `security_stamp`, invalidam o `IEmployeeAccessCache` (porta `IEmployeeAccessInvalidator`) e enfileiram `AccessRevoked` para `employee:{id}`; a desativação também revoga os refresh tokens | ADR-0027: o token antigo para na próxima requisição desta réplica e em até 30 s nas outras |
| Redefinição de senha com login errado | Mesmo `409 InvitationNotPending` do código inválido | Não confirma a quem o código pertence |
| Eventos sem agregado | `IUnitOfWork.Enqueue(envelope, occurredAt)`: o envelope vai para o outbox no commit, com o `seq` da entidade (que precisa estar no feed da transação). Usado pelo `ClosingUpdated` de movimentações e ajustes | Essas entidades são imutáveis e não são `AggregateRoot`, então não têm eventos de domínio |
| Hub SignalR | `RealtimeHub` lê tenant, funcionário e permissões do `EmployeeAccess` guardado no `HttpContext` do handshake | O hub roda num escopo próprio do SignalR, fora do escopo da requisição, então o `ITenantContext` não está inicializado |
| `ClosingUpdated` | Agrupado no `SignalRRealtimePublisher` (singleton, por réplica): o primeiro sai na hora; os seguintes dentro de 5 s viram um só no fim da janela, com `seq`/entidade do último evento e `data: null`. Pagamentos e estornos também agendam um | 06 §6.14. Com várias réplicas a janela é por réplica; o app trata o aviso como "recarregue o resumo" |
| Dispatcher | `OutboxDispatcher` (`PlatformDbContext`, `FOR UPDATE SKIP LOCKED`, 250 ms, lote de 100, desiste após 10 tentativas com log de erro). `OrderAlertAcknowledged` ainda não é publicado | Seguro com várias réplicas; entrega pelo menos uma vez (ADR-0031) |
| Feed por permissão | Mapa fixo `entityType` → permissão de leitura no `GetSyncChangesQueryHandler`: `orders.view` para catálogo, cardápio, cliente, pedido, pagamento, estorno, expediente e configurações; `closing.view` para movimentação, fechamento e ajuste; `employees.manage` para funcionário (o próprio sempre) | Mesmo critério dos `GET` correspondentes de 06 §6.12 |
| `lastSeq` do feed | Com `hasMore`, é o último `seq` da página; sem, é o contador do tenant | Entradas filtradas por permissão não são varridas de novo a cada chamada |
| Cursor expirado | `since > 0` e o primeiro `seq` retido do tenant > `since + 1` → `410` com `currentSeq`. `since = 0` nunca expira | Os `seq` de um tenant são contíguos, então um buraco no começo só pode ser a limpeza de 7 dias |
| `IN_PROGRESS` em `/operations` | O registro em curso só existe na transação dela. Um INSERT de sondagem com a mesma chave, `lock_timeout` de 100 ms, numa transação sempre desfeita: esperou → `IN_PROGRESS`. `completedAt` = `createdAt` | Não há coluna `completed_at`: a operação conclui na mesma transação em que o registro nasce |
| Ids monotônicos | `IdGenerator` gera UUID v7 com contador de 12 bits em `rand_a` dentro do mesmo milissegundo (RFC 9562 §6.2) | `Guid.CreateVersion7` sorteia esses bits: itens criados na mesma requisição saíam em ordem aleatória, e os cursores por id deixavam de seguir a criação |
| `SqlQuery<T>` com tipo próprio | As colunas do resultado seguem a convenção snake_case (`business_date`), sem alias em PascalCase | A convenção de nomes vale também para o tipo não mapeado |
| Endereço padrão do cliente | `ux_customer_addresses_default` virou `EXCLUDE ... DEFERRABLE INITIALLY DEFERRED` (05 §5.7) | Trocar o padrão desmarca um e marca outro na mesma transação, e o EF não garante a ordem dos UPDATEs. Índice único parcial não pode ser adiável |
| Validar antes de mutar | Regras do agregado (ex.: não esvaziar o pedido) são checadas antes de alterar o estado | O rollback esconde o problema na API, mas o agregado nunca deve ficar inválido em memória |
| Alerta pendente | Literal de 04 §4.7: evento com `affectsProduction` exige `production.view`; os demais, `orders.view`. `ORDER_NOTES_CHANGED` conta como produção quando o pedido tem produção (não é KILO) | A cozinha lê a observação do pedido no painel |
| Ciência de alerta | `INSERT ... ON CONFLICT DO NOTHING` por SQL; ainda **não** publica `OrderAlertAcknowledged` | A ciência não entra no feed (não há `entityType` para ela em 06 §6.12), e o outbox exige o `seq` de uma entidade da transação |
| Anonimização de cliente | Os pedidos do cliente são carregados e anonimizados pelo agregado (`Order.AnonymizeCustomer`): nome → "Cliente anonimizado", telefone nulo, endereço de entrega → marcador `"Endereço anonimizado"`. Vale para pedido pago ou cancelado, sobe a versão e grava o feed | `ck_orders_delivery` exige endereço em entrega; o feed leva a mudança aos aparelhos (ADR-0032). Um `UPDATE` em SQL não passaria pelo `UnitOfWork` |
| Fiado | `pendingAmount`/`pendingOrderCount` e `/pending-orders` = pedidos ativos não quitados, de qualquer data | ADR-0021: fiado é saldo, não entidade |
| Idempotência: INSERT do registro | `INSERT ... ON CONFLICT (tenant_id, key) DO NOTHING` com `lock_timeout` local só nesse comando; 0 linhas → lê o registro já confirmado (replay ou `422`). `55P03` → `409 OperationInProgress` | Um INSERT simples aborta a transação na violação de unicidade; o `ON CONFLICT` espera o concorrente e continua utilizável |
| Idempotência: hash do pedido | SHA-256 dos argumentos `[FromBody]` já desserializados e reserializados com o JSON da API, mais `método + caminho` gravado em `endpoint` | Espaços e ordem das chaves não mudam o hash; a mesma chave em outra rota ou de outro usuário é `422` |
| Idempotência: replay | `ContentResult` com status e corpo guardados + `Idempotent-Replayed: true`. O corpo sai do `jsonb`, então a ordem das chaves pode mudar; `Location` e `ETag` não são repetidos | Mesmos campos e valores; o app não depende da ordem |
| Idempotência: `resource_id` | DTO de resposta implementa `IHasResourceId` de forma explícita (não aparece no JSON) | `GET /operations/{key}` devolve o recurso sem conhecer cada DTO |
| Abertura concorrente | `pg_advisory_xact_lock(hashtextextended('business-day-open:{tenant}', 0))` antes de checar o expediente aberto | A segunda abertura espera e responde `409 BusinessDayAlreadyOpen` com `current`, em vez de estourar `ux_business_days_open` no commit |
| Cursor de `GET /business-days` | Base64url do último id da página; ordem por `id` decrescente (UUID v7 = ordem de abertura) | Opaco, estável e sem `OFFSET` |
| `dailyClosingId` | Sempre `null` até a finalização do fechamento ser implementada | `DailyClosing` ainda não é mapeado |
| Cookies de sessão na web (ADR-0053) | Emitidos por um filtro de resultado global (`WebSessionCookieFilter`), não pelos handlers: o módulo Identity não conhece HTTP nem plataforma. Na falha (`401 InvalidRefreshToken`/`InvalidDeviceSecret`), o cookie é apagado por `Response.OnStarting` | O tratador de exceções limpa os headers da resposta antes de escrever o `ProblemDetails`; os callbacks de `OnStarting` rodam depois disso |
| Plataforma `WEB` só pelo header | Cookies só são lidos com `X-App-Platform: WEB`; sem ele, a requisição é tratada como mobile (refresh no corpo obrigatório) | ADR-0053 §3.1: o mobile ignora cookies. O header customizado força o *preflight*, então uma página de outra origem não consegue usar o cookie |
| `Secure` em `http://localhost` | O ASP.NET emite `Secure` e o prefixo `__Secure-` também em HTTP (não confere o esquema). O Chrome e o Edge aceitam esses cookies vindos de `http://localhost` | Desenvolvimento com o app em `localhost:8082` e a API em `localhost:5080` (mesmo *site*) |
| `Origin` no hub | Rejeita só `Origin` presente, fora da lista e diferente do host da própria API | O WebSocket do React Native envia `Origin` = o host do servidor; clientes nativos sem `Origin` também passam. O hub autentica por token na query, sem cookie, então a checagem é defesa em profundidade |
| Header `Cookie` fora do log | Não há `UseHttpLogging` nem registro de headers; a instrumentação OpenTelemetry do ASP.NET Core não grava headers por padrão. Quem ligar log de headers precisa excluir `Cookie`, `Set-Cookie`, `Authorization` e `X-Device-Secret` | ADR-0053 / `02-padroes.md`: token nunca em log |
| IP do cliente atrás do ingress | `UseForwardedHeaders` antes de tudo, aceitando `X-Forwarded-For`/`X-Forwarded-Proto` só de `ForwardedHeaders:KnownNetworks`/`KnownProxies`, com `ForwardLimit = 1` | Sem isso, todo cliente caía na partição de IP do ingress e o limite de `/auth/*` (10/min por IP, ADR-0041) virava global. Com `ForwardLimit = 1`, entradas que o cliente forja à esquerda são ignoradas |
| Troca de estabelecimento | `SwitchTenantHandler` revoga (`ExecuteUpdate`, na transação) os refresh tokens não revogados do usuário com o `installationId` do access token, depois de confirmar o vínculo, e emite uma família nova | O app guarda uma sessão por instalação; o refresh anterior ficaria válido 30 dias sem dono (web e mobile) |
| Hub e expiração do token | `MapHub(..., o => o.CloseOnAuthenticationExpiration = true)` | O servidor fecha a conexão quando o JWT vence, para uma aba do desktop não ficar conectada com token vencido (ADR-0053). O app renova o token no `accessTokenFactory` (`refreshIfExpiring`, margem de 60 s) antes de reconectar e, se a reconexão automática desistir, agenda novas tentativas (08 §8.6) |
| Push e o aparelho que trocou de tenant | Um token do Expo tem um registro só (`ux_push_registrations_token`). O `PUT` chama `release_push_token(token, installationId)` (05 §5.17 0007, `SECURITY DEFINER`): apaga o registro do mesmo token em outro tenant **só se for da mesma instalação**, sem a sessão ver a linha; outra instalação → `409 PushTokenInUse` | A RLS esconde o registro antigo do outro tenant, e o logout pode não ter chegado ao servidor (sem rede). A política por posse do token da 0006 deixava quem conhecesse só a string do token de outro aparelho apagar o registro dele (o restaurante parava de receber o aviso) e ler as colunas |
| Lembrete do push sem tabela | Segunda mensagem do outbox com `available_at`, gravada junto do pedido. No envio, o pedido precisa ainda aguardar aceite | Um job que varresse pedidos precisaria marcar o lembrete no pedido, e isso subiria a versão (o `If-Match` do aceite daria `409`) |
| Push pelo menos uma vez | `push_deliveries` (PK mensagem + registro) consultada antes e gravada por lote de 100 na mesma transação que apaga os `DeviceNotRegistered` | Uma falha no segundo lote, ou o dispatcher morrendo antes de marcar a mensagem, não repete o aviso para quem já recebeu. Ainda pode repetir se o processo cair entre a resposta da Expo e o commit do lote |
| Expiração da URL pré-assinada do R2 | O cliente S3 é criado antes de ler o relógio, e o `Expires` vai com +0,5 s | O SDK calcula `X-Amz-Expires` = round(`Expires` − relógio dele). A criação preguiçosa do cliente na primeira chamada passava de 0,5 s e saía `899` (`ProductImageFlowTests` instável) |
| Alterações de schema | 05 §5.17: uma subseção `### NNNN_Nome` por migration, com o seu bloco `sql`; `Sync-DdlMigration.ps1 -Target NNNN_Nome.sql` gera o script (só ASCII no `.ps1`; o cabeçalho com acento sai por `[char]`, porque o Windows PowerShell 5.1 lê o script sem BOM como ANSI), e a classe de migration tem 10 linhas. A primeira é a `0002_SharedDeviceWebPlatform` (`platform` com `WEB`) | ADR-0042 §3: a `0001` não muda depois de aplicada |
| Aceite × expiração (ADR-0046 item 16) | Disputa pela `version`, sem trava pessimista: o primeiro UPDATE grava, o outro cai no token de concorrência. Aceite e recusa leem o expediente com `FOR SHARE` (como toda escrita de venda), e o job também: a finalização do fechamento não roda no meio | O número do dia só é consumido depois das validações; se o aceite perde no UPDATE, o rollback devolve o número ao `business_date_counters` (sequência sem buracos) |
| Ordem dos erros no aceite | Prazo vencido → `WebOrderExpired`, depois `If-Match` → `VersionConflict`, depois o estado → `OrderNotAwaitingAcceptance` | O job muda a versão ao expirar: sem isso, o aceite tardio veria só um `VersionConflict`. Já o "Já aceito por Ana" (item 9) precisa do `current` do 409 |
| Cliente do pedido do site | `pg_advisory_xact_lock` pela chave `customer-phone:{tenant}:{telefone}` antes de procurar o cliente | Dois aceites com o mesmo telefone novo estourariam `ux_customers_phone` (500); assim o segundo espera e acha o cadastro do primeiro |
| Envio pela página | Rota pública desde a fatia 2 (`PlaceWebOrderHandler`), sobre o mesmo `WebOrderSubmitter` do comando interno. Pedido `WEB` só `MARMITA` (RF-202/ADR-0044) | O modelo, o aceite e os jobs já dependiam dele |
| Tenant das rotas públicas | `PublicTenantMiddleware` antes da autenticação; o JwtBearer devolve `NoResult` em `/api/v1/public`. A resolução lê `store_slugs` por SQL (leitura aberta pela RLS da 0004) e o token pela função `resolve_tracking_token` (0007), sem `IgnoreQueryFilters` | O tenant ainda não existe na sessão, e o filtro do EF esconderia tudo. O slug é público; o token, não: a tabela de tokens deixou de ter leitura aberta. A escrita nessas tabelas continua presa ao tenant (`WITH CHECK`) |
| Chamadas externas fora da transação | Outbox: o laço dos consumidores reserva com lease e chama a Expo/R2 sem transação. Envio pela página: Turnstile e mapas no `Preparation` da idempotência, antes da transação e dos advisory locks. A cotação (`POST …/delivery-quotes`) continua com o Turnstile e o mapa dentro da transação do decorator: ela não tem idempotência nem advisory lock, e a única linha gravada é a da cotação | Uma chamada HTTP lenta com a transação aberta segura conexão, `IN_PROGRESS`, `FOR UPDATE` e locks de outras requisições |
| Token do Turnstile fora do corpo | Header `X-Turnstile-Token` | O token é de uso único: dentro do corpo, um reenvio por resposta perdida com token novo daria `IdempotencyKeyReused` |
| Carrinho conferido duas vezes | `WebOrderCart` (contra o cardápio público) antes de gravar; `OrderItemResolver` ao gravar | O primeiro devolve o `CartChanged` com o carrinho inteiro recalculado (e barra o que está escondido da página); o segundo é o mesmo caminho do app |
| Limites de pendentes | `pg_advisory_xact_lock` por `web-order-phone:{tenant}:{telefone}` e `web-order-ip:{tenant}:{hash}`, sempre nessa ordem | Dois envios simultâneos do mesmo cliente passariam juntos pela contagem |
| Redirecionamento do slug no POST | `308` (não `301`) | O `301` vira GET no navegador e perderia o corpo |
| Janela do deploy da 0008 (ADR-0054) | Tenant sem nenhuma linha em `tenant_behaviors` é tratado como `RESTAURANT` (decisão do usuário em 2026-11-22): `TenantModuleMap.For([])`, `Tenant.EffectiveBehaviors`/`EffectiveLabels` (rótulo `RESTAURANT`, como a correção da migration) e `ITenantModules`. A primeira escrita no agregado (`PATCH /tenant` com rótulos, acréscimo pelo admin) grava essas linhas com `origin = SYSTEM` e sem autor | A API anterior não conhece as tabelas: um tenant criado por ela entre a migration e o deploy fica sem comportamento (05 §5.17 0008). Sem o fallback, ele perderia as abas e as rotas do restaurante; sem gravar na escrita, a FK de `tenant_labels` recusaria o rótulo, e o acréscimo de `SNACK_BAR` o deixaria sem `RESTAURANT` |
| Tenant no feed | `Tenant` é `ISyncTracked` (`TENANT`, `orders.view`): rename, rótulos e comportamento novo geram a entrada, e o app recarrega o `/auth/me`. O autocadastro grava o tenant no primeiro `SaveChanges` do handler, então ele não entra no feed do cadastro | Q-324: o evento é só aviso, e o aparelho que estava desconectado descobre a mudança pelo `/sync/changes` |
| Admin gravando no tenant | O acréscimo de comportamento é `ICommandHandler` (decorator, `UnitOfWork`) com `InitializeForPlatformAdmin`, e não um método do `IPlatformAdminService` no `PlatformDbContext` | O papel `almoseis_platform` não pode subir `tenant_sync_counters` (05 §5.13), então não grava feed; a escrita com o papel da aplicação e a RLS do tenant da rota mantém auditoria, feed e outbox na mesma transação. A sessão leva `app.user_id` vazio (`SessionUserId`): com o id do administrador, as políticas por usuário de `tenants`/`tenant_employees`/`tenant_employee_roles` abririam os estabelecimentos em que ele seja funcionário (ADR-0025, complemento de 2026-11-23) |
| Ordem do `ModuleNotEnabled` | Depois de autenticação e permissão (`[HasPermission]` é filtro de autorização), antes de tudo o mais: filtro com `Order = -1000` nas rotas de cadastro e primeira linha do `CreateOrderHandler` | A resposta da lanchonete não depende do corpo, do expediente nem da assinatura; quem não tem permissão continua vendo `PermissionDenied` |
| Disponibilidade do cardápio fixo | Derivada a cada leitura (`FixedMenuAvailability`), sem coluna nem evento no combo. O app recarrega o `GET /fixed-menu` a cada `CatalogUpdated`/entrada de feed de qualquer raiz do cardápio fixo | ADR-0056 item 23: gravar "pausado por componente" no combo exigiria escrever em N combos a cada pausa e conflitaria com o `If-Match` deles |
| Preço de site na janela do deploy da 0009 | O banco aceita bebida visível sem `site_price` e pedido `WEB` com `COUNTER` (a API anterior grava assim). Esta API sempre grava os dois; na leitura, bebida visível sem preço de site cobra e mostra o `DefaultPrice`, e a próxima edição grava o preço de site | 09 §9.4; a contração (apertar os `CHECK`s) fica para a release seguinte (05 §5.17 0009) |
| Expand/contract da 0003 | O banco aceita pedido aceito sem `accepted_at` e entrega/retirada sem `fulfillment_status` (a API anterior grava assim durante o deploy). O código trata o nulo como "aceito na criação" (`AcceptedAt ?? CreatedAt` no painel) e `PENDING` (atendimento, "Pronto para retirar", `IS DISTINCT FROM` no fechamento). A contração (preencher e apertar os `CHECK`s) fica para a release seguinte (05 §5.17) | 09 §9.4: a migration roda antes do deploy e precisa ser compatível com a versão anterior. O backfill dos pedidos existentes tira o `FORCE` da RLS de `orders` só durante o `UPDATE`, porque o papel de migration não é membro de `almoseis_platform` |

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
