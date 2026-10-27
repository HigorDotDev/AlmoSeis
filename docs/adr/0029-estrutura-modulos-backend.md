# ADR-0029 — Estrutura concreta dos módulos do backend

- **Status:** Aceito (recomendação aceita pelo usuário em 2026-10-27)
- **Data:** 2026-10-27
- **Origem:** Inconsistência entre EP-001 §5.1 ("monólito modular") e §5.3 (estrutura só por camadas)
- **Relacionados:** ADR-0002, ADR-0031

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
