# 02. Padrões e Convenções (regras permanentes dos agentes)

Estas são as regras "sempre/nunca" que todo agente segue. Cada regra cita o ADR que a justifica. Se uma regra conflitar com uma tarefa, **pare e pergunte**. Não abra exceção silenciosamente.

## 2.1 Princípios

- SOLID, DRY e KISS, sem complexidade que o MVP (10 estabelecimentos piloto) não justifique.
- **Integridade financeira acima de conveniência:** na dúvida entre bloquear e permitir uma operação financeira, bloqueie e pergunte.
- Código, identificadores e nomes de tabela em inglês. Textos de interface em português do Brasil.

## 2.2 Backend (.NET 10 / ASP.NET Core / EF Core)

**Sempre:**
- Seguir o fluxo `Controller → Handler → Domain (agregado) → DbContext/Repository` (ADR-0029).
- Colocar as regras de negócio no agregado ou em serviço de domínio, nunca em Controller nem em Repository.
- Injetar dependências por interface e usar `async`/`await`.
- Usar `TimeProvider`/`ITimeProvider` em vez de `DateTime.Now`/`UtcNow`. Instantes em UTC (`timestamptz`).
- Validar com FluentValidation (nunca DataAnnotations) e mapear o EF com Fluent API.
- Implementar `ITenantOwned` em toda entidade de tenant. O tenant vem **só** do `ITenantContext` (JWT) (ADR-0026).
- Autorizar por **permissão** (`[Authorize(Policy = Permissions.Orders.Create)]`), nunca por perfil (ADR-0028).
- Gravar auditoria explícita via `IAuditWriter` e eventos via outbox, **na mesma transação** (ADR-0031).
- Registrar no `change_feed` toda alteração de entidade sincronizável (ADR-0032).
- Exigir `Idempotency-Key` nas operações listadas no ADR-0033 e `If-Match` nas escritas sobre recursos existentes (ADR-0034).
- Usar o value object `Money` para valores (`decimal`, 2 casas), peso em gramas inteiras e a regra de arredondamento do ADR-0030.
- Gravar snapshot de nome, composição e preço em `OrderItem` no momento da venda (ADR-0020).
- Responder erros com `ProblemDetails` + `errorCode` + `traceId`, com rotas em `/api/v1`. JSON em `camelCase`, enums como string `UPPER_SNAKE`.
- Escrever testes unitários para agregados e handlers, e testes de integração (Testcontainers) para endpoints, isolamento de tenant, idempotência e concorrência.

**Nunca:**
- Ler o tenant de header, rota ou corpo. Usar `IgnoreQueryFilters()` fora do módulo de admin/plataforma (ADR-0025).
- Apagar fisicamente pedido, pagamento, fechamento, auditoria ou produto já vendido.
- Alterar um `DailyClosing` finalizado. Correção é sempre uma nova operação (ADR-0014).
- Editar itens ou valores de pedido com `IsPaid = true` (ADR-0007).
- Publicar no SignalR antes do commit ou fora do outbox.
- Usar `double`/`float` para dinheiro ou peso.
- Introduzir MediatR, Redis, filas externas ou microsserviços sem ADR.
- Gravar telefone, endereço, token ou payload completo em auditoria ou log (ADR-0024).

## 2.3 Mobile (React Native + Expo + TypeScript)

**Sempre:**
- Usar Expo Router, TanStack Query (estado do servidor), Zustand (estado local de interface), React Hook Form + Zod e `expo-sqlite` (rascunhos e cache).
- Acessar a API por hooks (`useOrders`, `useRegisterPayment`), nunca chamando `fetch`/axios dentro de tela.
- Gerar a `Idempotency-Key` ao confirmar a operação, persistir junto com a operação pendente e reutilizar em todo reenvio (ADR-0033).
- Enviar `If-Match` com a `version` em cache e tratar `409 VersionConflict` mostrando a diferença (ADR-0034).
- Diferenciar visualmente o que é rascunho, pendente e confirmado (RN-014.5). Status de produção e status financeiro também são visualmente distintos.
- Mostrar o diálogo de confirmação antes de toda escrita persistente (RF-012), com o resumo da operação e dos valores.
- Fazer contas de dinheiro em centavos inteiros. O valor oficial é sempre o devolvido pela API (ADR-0030).
- Guardar tokens só em `expo-secure-store` (ADR-0027).
- Enviar `X-App-Version`/`X-App-Platform` e tratar `426` (ADR-0035).
- Mostrar ou esconder abas e ações pelas permissões de `GET /auth/me`, sabendo que o backend é quem decide.
- Usar componentes pequenos e reutilizáveis, alvos de toque ≥ 48 dp e testar em aparelho Android de entrada.

**Nunca:**
- Mostrar rascunho como pedido confirmado ou considerar pagamento concluído sem resposta da API (ADR-0010).
- Reenviar uma operação de resultado desconhecido sem antes consultar `GET /operations/{key}`.
- Calcular regra de negócio que decide estado (quitação, bloqueio, totais oficiais) no app.
- Mostrar preço, botão ou link de compra da assinatura (ADR-0017).

## 2.4 Banco de dados (PostgreSQL)

**Sempre:**
- Nomes em `snake_case`. PK `uuid` (gerada na aplicação, UUID v7). `timestamptz` para instantes e `date` para `business_date`.
- `tenant_id` em toda tabela de tenant, com política RLS e FKs compostas `(tenant_id, x_id)` (ADR-0026).
- `numeric(12,2)` para dinheiro e `integer` para gramas (ADR-0030).
- Coluna `version integer` nos agregados concorrentes (ADR-0034).
- Índices para os filtros reais (`tenant_id, business_day_id`, `tenant_id, production_status`, `tenant_id, seq`).
- Migrations EF revisadas, nomeadas e documentadas, executadas com o papel de migration (não o da aplicação).

**Nunca:**
- Dar `UPDATE`/`DELETE` em `audit_log` ao papel da aplicação.
- Dar `BYPASSRLS` ao papel da aplicação.

## 2.5 DevOps

**Sempre:**
- Manter segredos no Key Vault (nuvem) ou `dotnet user-secrets` (local). `appsettings*.json` versionados só com placeholders.
- Fazer o CI rodar testes unitários, de integração e de arquitetura antes de gerar imagem.
- Deploy automático em homologação e com aprovação em produção (ADR-0037).

**Nunca:**
- Commitar credencial real, connection string com senha ou chave de gateway.

## 2.6 Critérios de pronto

Uma funcionalidade só está pronta quando:
1. segue os ADRs aplicáveis;
2. compila sem warnings novos;
3. tem testes (unitários + integração para endpoints; isolamento de tenant para endpoints novos);
4. tem auditoria e entrada no feed, quando aplicável;
5. tem a documentação de `docs/` atualizada.

## 2.7 ADRs

Toda decisão nova relevante vira `docs/adr/NNNN-titulo.md` usando `docs/adr/0000-modelo.md` e entra no índice `docs/adr/README.md`. Um ADR `Proposto` já orienta o código, mas precisa ser revisado antes de produção.
