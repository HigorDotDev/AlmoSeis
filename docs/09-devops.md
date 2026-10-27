# 09. DevOps — ambientes, pipeline e operação

> Implementa os ADRs 0026 (papéis de banco), 0035 (versão do app, EAS), 0037 (hospedagem), 0040 (e-mail) e 0041 (rate limiting), e a regra de segredos de `02-padroes.md` §2.5.

## 9.1 Ambientes

| Ambiente | Onde | Banco | Deploy |
|---|---|---|---|
| `local` | máquina do dev | PostgreSQL 16 no Docker Compose | `dotnet run` / Expo Dev Client |
| `homolog` | Azure Brazil South, resource group `rg-almoseis-homolog` | Flexible Server B1ms, PITR 7 dias | automático a cada merge na `main` |
| `prod` | Azure Brazil South, `rg-almoseis-prod` | Flexible Server B2s, PITR 14 dias, backup geo-redundante desligado (dados ficam no Brasil, ADR-0024) | tag `v*` + aprovação manual (GitHub Environment `production`) |

Os ambientes não compartilham nada: banco, Key Vault, bucket R2 (`almoseis-images-homolog`/`-prod`), domínio de e-mail (`homolog.` como subdomínio), chave JWT e conta Asaas (sandbox em homolog).

## 9.2 Ambiente local

`docker-compose.yml` na raiz:

```yaml
services:
  db:
    image: postgres:16
    environment:
      POSTGRES_PASSWORD: postgres
      POSTGRES_DB: almoseis
    ports: ["5432:5432"]
    volumes:
      - pgdata:/var/lib/postgresql/data
      - ./infra/db/init:/docker-entrypoint-initdb.d:ro
volumes:
  pgdata:
```

`infra/db/init/01-roles.sql` cria os papéis de grupo de 05 §5.1 e os logins locais (senhas fixas **só** de desenvolvimento, iguais às do `appsettings.Development.json`):

| Login | Membro de | Uso |
|---|---|---|
| `almoseis_migrator_login` | `almoseis_migrator` (dono do schema) | `dotnet ef database update` |
| `almoseis_app_login` | `almoseis_app` | API |
| `almoseis_platform_login` | `almoseis_platform` | `PlatformDbContext` |

Passo a passo:
1. `docker compose up -d db`
2. `dotnet ef database update` (projeto `AlmoSeis.Infrastructure`, startup `AlmoSeis.Api`, connection string do migrator)
3. `dotnet run --project src/AlmoSeis.Api`
4. Mobile: `npx expo start --dev-client`, com `EXPO_PUBLIC_API_BASE_URL=http://<ip-da-máquina>:5080`.

Segredos locais (chave JWT de desenvolvimento, R2 de teste, Asaas sandbox) ficam em `dotnet user-secrets`. O `appsettings.Development.json` versionado só tem as connection strings locais e placeholders.

## 9.3 Imagem do backend

`backend/Dockerfile` em multi-stage:
- **build:** `mcr.microsoft.com/dotnet/sdk:10.0`, com `dotnet restore` em cache de camadas e `dotnet publish -c Release -o /app`;
- **runtime:** `mcr.microsoft.com/dotnet/aspnet:10.0` (Debian, **não** chiseled, porque precisa do `tzdata`, conforme 07 §7.11), com usuário não-root `app`, `ASPNETCORE_HTTP_PORTS=8080` e `HEALTHCHECK` em `/health/live`.

A imagem sobe para `ghcr.io/<org>/almoseis-api:{sha}` e `:{versão}` em tags.

## 9.4 Pipelines (GitHub Actions)

### `backend-ci.yml` — PR e push na `main` (paths `backend/**`, `docs/05-ddl*`)

```text
setup-dotnet 10 → restore (cache NuGet) → build -warnaserror
→ test UnitTests
→ test ArchitectureTests
→ test IntegrationTests (Testcontainers; o runner ubuntu-latest já tem Docker)
     inclui SchemaMatchesDdlTest (migrations × 05-ddl.md) e o 05-ddl-smoke-test.sql
→ publica os resultados (dorny/test-reporter) e a cobertura
```

### `backend-cd.yml` — push na `main` (homolog) e tag `v*` (prod)

```text
build & push da imagem → job `migrate` (environment homolog|production):
    dotnet ef migrations bundle → executa o bundle com a connection string do almoseis_migrator (secret do Environment)
→ job `deploy`: az containerapp update --image ghcr.io/...:{sha}  (login via OIDC federado, sem senha)
→ smoke: GET /health/ready até 200 (timeout de 3 min), senão rollback para a revisão anterior (az containerapp revision activate)
```

Produção exige aprovação no Environment `production`. As migrations rodam **antes** do deploy e precisam ser compatíveis com a versão anterior (*expand/contract*): nunca remover nem renomear coluna na mesma release que deixa de usá-la.

### `mobile-ci.yml` — PR e push (paths `mobile/**`)

`npm ci` → `npx tsc --noEmit` → `npx eslint .` → `npx jest --ci`.

### `mobile-release.yml`

| Gatilho | Ação |
|---|---|
| push na `main` (`mobile/**`) | `eas update --channel preview --non-interactive` (homolog) |
| tag `mobile-v*` | `eas build --profile production --platform all` + `eas submit` (Play Console: faixa interna; App Store: TestFlight) |

O token do EAS (`EXPO_TOKEN`) fica nos secrets do repositório.

`eas.json`:

| Perfil | Canal | `EXPO_PUBLIC_API_BASE_URL` | Distribuição |
|---|---|---|---|
| `development` | — | local | Dev Client interno |
| `preview` | `preview` | `https://api-homolog.almoseis.app` | interna (APK/TestFlight) |
| `production` | `production` | `https://api.almoseis.app` | lojas |

## 9.5 Infraestrutura como código (`infra/bicep`)

`main.bicep` parametrizado por ambiente, com módulos:

| Módulo | Recurso | Pontos de atenção |
|---|---|---|
| `log.bicep` | Log Analytics + Application Insights | retenção de 30 dias (homolog) / 90 dias (prod) |
| `kv.bicep` | Key Vault (RBAC) | acesso da managed identity da API como `Key Vault Secrets User` |
| `pg.bicep` | PostgreSQL Flexible Server 16 | autenticação por senha (os papéis da aplicação não usam Entra ID no MVP), `require_secure_transport=on`, firewall só para o Container Apps Environment e o IP do runner durante a migration |
| `cae.bicep` | Container Apps Environment + Container App `almoseis-api` | `minReplicas: 1`, `maxReplicas: 1` no MVP (ADR-0037), ingress HTTPS com WebSockets habilitado, *sticky sessions* ligadas para o SignalR, probes `/health/live` e `/health/ready` |
| `email.bicep` | Communication Services + Email Service + domínio | registros DNS (SPF/DKIM) aplicados fora do Bicep |

O R2 (Cloudflare) e o Asaas são configurados manualmente e documentados em `infra/README.md`. As credenciais vão para o Key Vault.

Os papéis de banco (05 §5.1) e os logins são criados pelo script `infra/db/create-logins.sql`, executado uma vez por ambiente pelo admin do Flexible Server, com senhas geradas e gravadas direto no Key Vault.

## 9.6 Segredos (Key Vault → configuração)

| Segredo no Key Vault | Chave de configuração | Usado por |
|---|---|---|
| `ConnectionStrings--App` | `ConnectionStrings:App` | API |
| `ConnectionStrings--Platform` | `ConnectionStrings:Platform` | API (`PlatformDbContext`) |
| `Jwt--SigningKeyPem` | `Jwt:SigningKeyPem` | API |
| `Billing--Asaas--ApiKey`, `Billing--Asaas--WebhookToken` | `Billing:Asaas:*` | API |
| `Storage--R2--AccessKeyId`, `Storage--R2--SecretAccessKey` | `Storage:R2:*` | API |
| `Email--ConnectionString` | `Email:ConnectionString` | API |
| *(GitHub Environment secret)* `MIGRATOR_CONNECTION_STRING` | — | job `migrate` |

A API carrega o Key Vault via `AddAzureKeyVault(new Uri(KeyVault:Uri), new DefaultAzureCredential())`. Nenhum segredo vai para variável de ambiente do Container App.

## 9.7 Observabilidade e alertas

- **Telemetria:** OpenTelemetry com traces (ASP.NET Core, HttpClient, Npgsql), métricas e logs → Application Insights. Atributo `tenant.id` em todo span (07 §7.4). **Nunca** gravar payloads, telefones nem tokens (ADR-0024).
- **Métricas próprias:**
  - `almoseis.outbox.pending` (gauge);
  - `almoseis.outbox.lag_ms` (histograma);
  - `almoseis.idempotency.replays` (contador, métrica de sucesso do épico §14 "operações duplicadas evitadas");
  - `almoseis.orders.created`;
  - `almoseis.sync.cursor_expired`.

| Alerta (Azure Monitor) | Condição | Severidade |
|---|---|---|
| API indisponível | `/health/ready` ≠ 200 por 3 min | 1 |
| Erros 5xx | > 1% das requisições em 5 min | 2 |
| Latência | p95 > 1 s por 10 min (RNF-002) | 3 |
| Outbox atrasado | `outbox.pending` > 100 ou `lag_ms` p95 > 3 s por 5 min (meta de 3 s) | 2 |
| Webhook do Asaas falhando | eventos não processados há mais de 15 min | 2 |
| Banco | CPU > 80% por 15 min; armazenamento > 80% | 2 |

Os alertas vão por e-mail para o responsável técnico. Não há plantão no piloto.

## 9.8 Backup e restauração (RNF-005)

- O Flexible Server faz backup automático com PITR (7 dias em homolog, 14 em prod).
- **Ensaio obrigatório antes do piloto e a cada trimestre**, registrado em `infra/RESTORE-LOG.md`:
  1. `az postgres flexible-server restore --restore-time <instante> --name almoseis-pg-restore-test` a partir de prod;
  2. conectar como `almoseis_platform` e conferir as contagens de `orders`, `payments` e `daily_closings` contra o instante escolhido;
  3. apontar uma revisão de homolog para o banco restaurado e abrir o histórico de fechamentos no app;
  4. medir o tempo total (meta de RTO: 1 h; RPO: 5 min) e destruir o servidor restaurado.
- Restauração real: um servidor novo a partir do PITR → troca de `ConnectionStrings--*` no Key Vault → nova revisão do Container App. O servidor antigo fica parado por 7 dias antes de ser excluído.

## 9.9 Checklist de ida ao piloto

- [ ] Domínios `api.` e `api-homolog.` com TLS gerenciado do Container Apps
- [ ] DNS de e-mail (SPF/DKIM) verificado no ACS
- [ ] Conta Asaas de produção, webhook apontado para `/webhooks/asaas` com token
- [ ] `MinSupportedAppVersion` configurado
- [ ] Ensaio de restauração registrado
- [ ] Revisão jurídica dos Termos e do contrato de tratamento (ADR-0024)
- [ ] Validação das regras da App Store e do Google Play sobre o app sem compra (ADR-0017)
- [ ] Alertas testados (derrubar a revisão de homolog e conferir o e-mail)
