# ADR-0029 — Estrutura concreta dos módulos do backend

- **Status:** Aceito (recomendação aceita pelo usuário em 2026-10-27)
- **Data:** 2026-10-27
- **Origem:** Inconsistência entre EP-001 §5.1 ("monólito modular") e §5.3 (estrutura só por camadas)
- **Relacionados:** ADR-0002, ADR-0031
- **Complementado em 2026-11-20** (implementação do EP-002, fatias 1 a 5; ADR-0044 a ADR-0049, Propostos): módulos Storefront e Notifications, portas entre módulos e peças transversais novas. Ver a seção "Complemento (2026-11-20)".

## Contexto

A estrutura do §5.3 separa o código **por camada** (`Restaurant.Application/Orders`, `Restaurant.Domain/Entities`…). Isso não cria fronteiras entre módulos, e nada impede `Payments` de acessar entidades internas de `Ordering`. Também não está decidido se o projeto usa MediatR, que agora tem licença comercial.

## Decisão

1. **.NET 10 (LTS)**, ASP.NET Core com Controllers.
2. Solution:

   ```text
   src/
     AlmoSeis.Api/                      # host: controllers, hubs, middlewares, DI, Swagger
     AlmoSeis.SharedKernel/             # Entity, AggregateRoot, DomainEvent, Result, Money, ITimeProvider
     AlmoSeis.Infrastructure/           # AppDbContext, interceptors (tenant/audit/outbox/version), storage, gateway
     AlmoSeis.Modules.Identity/         # Users, Tenants, Employees, Devices, Permissions
     AlmoSeis.Modules.Catalog/
     AlmoSeis.Modules.Menu/
     AlmoSeis.Modules.Ordering/         # Order, OrderItem, WeighingEntry, Customer, alertas
     AlmoSeis.Modules.Payments/
     AlmoSeis.Modules.Closing/          # BusinessDay, CashMovement, DailyClosing, ClosingAdjustment
     AlmoSeis.Modules.Audit/
     AlmoSeis.Modules.Billing/          # Subscription, webhooks
     AlmoSeis.Modules.Sync/             # change feed, idempotência, operações
   tests/
     AlmoSeis.UnitTests/
     AlmoSeis.IntegrationTests/         # Testcontainers PostgreSQL
     AlmoSeis.ArchitectureTests/        # NetArchTest
   ```

   Cada módulo tem internamente `Domain/`, `Application/` e `Infrastructure/` (configurações EF do módulo) e expõe só `Contracts/` (interfaces e DTOs públicos).
3. **Um único `AppDbContext`** que aplica as configurações de todos os módulos. Assim, pedido + pagamento + feed + auditoria + outbox gravam numa **mesma transação** sem coordenação distribuída. As tabelas não têm schema por módulo no MVP.
4. **Sem MediatR.** Casos de uso são classes `XxxHandler` com interfaces simples (`ICommandHandler<TCommand, TResult>`), registradas por convenção (Scrutor). Validação com **FluentValidation**. Eventos de domínio são despachados em processo no `SaveChanges`.
5. Um módulo só usa outro pelo `Contracts/` desse outro módulo. Testes de arquitetura garantem isso e que nenhum `Domain/` depende de EF ou ASP.NET.
6. Fluxo: `Controller → Handler (Application) → Domain (regras no agregado) → Repository/DbContext`. Controllers não têm regra de negócio.

## Alternativas consideradas

- **Camadas puras (§5.3 do épico):** menos projetos, sem fronteiras.
- **DbContext por módulo:** isolamento maior, mas transações entre módulos (pagamento ↔ pedido ↔ caixa) passam a exigir conexão compartilhada ou consistência eventual.
- **MediatR:** licença comercial e indireção que o projeto não precisa.

## Consequências

- Substitui a estrutura do §5.3 do épico.
- Nomes `Restaurant.*` do épico passam a ser `AlmoSeis.*`.

## Complemento (2026-10-27, detalhado em `docs/07-backend.md`)

- **Fronteira por visibilidade:** em cada módulo, tudo é `internal`, exceto `Contracts/` e a classe de registro `XModule`. Os controllers ficam em `AlmoSeis.Api` e chamam `ICommandHandler<,>`/`IQueryHandler<,>` com comandos públicos de `Contracts`.
- **`AppDbContext` em `AlmoSeis.Infrastructure`**, sem conhecer os módulos. Cada módulo registra uma `IModuleModelConfiguration` por DI. A transação única do item 3 é coordenada por um decorator transacional + `UnitOfWork`, que também grava o feed (ADR-0032) e o outbox (ADR-0031).
- **Sem `Result<T>`:** erros de negócio são `BusinessException` com um `ErrorCode` que espelha o catálogo de `06-contratos-api.md` §6.1, mapeado para `ProblemDetails` num único handler.
- **Tempo:** `TimeProvider` da BCL, sem abstração própria (`ITimeProvider` sai do SharedKernel).
- **Fora:** FluentAssertions (licença comercial), AutoMapper e Serilog (logs via `ILogger` + OpenTelemetry).

## Complemento (2026-11-20, EP-002, detalhado em `docs/07-backend.md` §7.1, §7.3, §7.4, §7.7 e §7.10)

- **Módulos novos:**
  - `AlmoSeis.Modules.Storefront`: perfil público, slug, estado da página, cardápio da página, taxa por distância e provedores de mapas (ADR-0044, ADR-0048);
  - `AlmoSeis.Modules.Notifications`: push dos pedidos do site pela Expo (ADR-0049).
- **Ordem entre módulos** (sem ciclo): `Identity`, `Catalog` ← `Menu` ← `Closing` ← **`Storefront`** ← `Ordering` ← `Payments`. **`Notifications`** fica acima do `Ordering`. O Storefront lê catálogo, cardápio, expediente e assinatura. O Ordering usa o Storefront pelo `IStorefrontGate` e pelo `IDeliveryQuoteRedeemer`.
- **Portas declaradas no módulo de baixo e implementadas no de cima**, para o de baixo chamar sem referenciar:
  - `ITenantCreatedListener` (Identity → Storefront): o autocadastro cria a página inativa com o slug, na mesma transação;
  - `IDeviceAccessListener` (Identity → Notifications): logout, troca de tenant, `AccessRevoked` e revogação do aparelho apagam o registro de push, na transação de quem chamou;
  - `IWebOrderAcceptanceReader` (Ordering, lido pelo Notifications): antes de enviar, confere se o pedido ainda aguarda aceite e está no prazo.
- **Peças transversais novas** (Infrastructure e Api):
  - `IReplaySecretSource`: campo da resposta com segredo do cliente que não fica no `response_body` da idempotência e é recomposto no reenvio a partir do `resource_id` (ADR-0033 item 7);
  - `IIdempotentPreparation` e `[Idempotent(Public, ReplaySecret, Preparation)]`: passo de preparação antes da transação, depois de um `FindCompletedAsync` só de leitura (ADR-0033 item 8);
  - `ITrackingTokenIssuer`: token de acompanhamento derivado por HMAC (ADR-0047);
  - `PublicTenantMiddleware`: o tenant das rotas `/public/*` vem do slug ou do token (ADR-0045). `PublicStoreQuotaMiddleware`: cota de cotações por **tenant resolvido** (300/dia), contada depois de o slug virar tenant.
- **`OutboxDispatcher` com dois laços independentes**, cada um a cada 250 ms com `FOR UPDATE SKIP LOCKED`:
  - **tempo real** (sem `IOutboxConsumer`): publica no SignalR e marca `processed_at` numa transação curta;
  - **consumidores externos** (push pela Expo, imagem antiga no R2): reserva o lote numa transação curta (`attempts + 1`, `available_at = agora + 120 s`, `Outbox:ConsumerLeaseSeconds`). Depois chama cada consumidor **fora** de qualquer transação e grava o resultado de cada mensagem num `UPDATE` próprio. Se a réplica cair no meio, outra pega a mensagem quando a reserva vence.
  - Uma chamada HTTP lenta não segura `FOR UPDATE` nem atrasa os eventos de tempo real. O envelope ganha `AvailableAt` (`outbox_messages.available_at`) para mensagens agendadas.
- **Regra geral:** nenhuma chamada HTTP externa (Turnstile, mapas, Expo, R2) acontece com a transação da requisição aberta, com o `IN_PROGRESS` da idempotência ou com advisory locks. Exceção registrada: `POST /public/stores/{slug}/delivery-quotes` chama Turnstile e mapas dentro da transação do decorator. Ela não tem idempotência nem advisory lock, e grava só a cotação.
- **Testes de arquitetura novos:** controllers públicos só recebem handlers `IPublicHandler`, e todo `IPublicHandler` é usado por eles. Jobs de plataforma de módulo (`AlmoSeis.Modules.X.Jobs`) podem usar `PlatformDbContext` só para achar candidatos entre tenants (gravando no contexto de cada tenant) ou para apagar dado técnico vencido.
