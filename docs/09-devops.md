# 09. DevOps — ambientes, pipeline e operação

> Implementa o **ADR-0060** (infra enxuta do piloto: VM única por ambiente, que suspende o ADR-0037 até o gatilho de migração) e os ADRs 0024 (dados no Brasil), 0026 (papéis de banco), 0035 (versão mínima do app, EAS), 0040 (e-mail, provedor trocado pelo 0060), 0041 (limites), 0042 (migrations SQL-first), 0051 e 0053 (página e app no navegador), além da regra de segredos de `02-padroes.md` §2.5. O desenho Azure do ADR-0037 ficou como **alvo futuro** (§9.10).
>
> *Runbook* do primeiro deploy, com o que precisa ser criado em cada conta: [`infra/README.md`](../infra/README.md). Restauração passo a passo: [`infra/vm/restore.md`](../infra/vm/restore.md).

## 9.1 Ambientes

| Ambiente | Onde | Banco | Deploy |
|---|---|---|---|
| `local` | máquina do dev | PostgreSQL 16 no `docker-compose.yml` da raiz | `dotnet run` / Expo Dev Client / `npm run dev` |
| `homolog` | VM Oracle Cloud `sa-saopaulo-1`, Ampere A1 (ARM64), **1 OCPU / 6 GB**, Ubuntu 24.04 | PostgreSQL 16 + WAL-G na própria VM, PITR 14 dias | automático a cada push na `main` |
| `prod` | VM Oracle Cloud `sa-saopaulo-1`, Ampere A1 (ARM64), **3 OCPU / 18 GB**, Ubuntu 24.04 | PostgreSQL 16 + WAL-G na própria VM, PITR 14 dias | tag `v*` + aprovação no GitHub Environment `production` |
| plano B | VPS paga com datacenter no Brasil (x86 ou ARM), criada só para restaurar | restaurado do bucket | `bootstrap-vm.sh` + `restore.sh production` (§9.7) |

As duas VMs somam 4 OCPU / 24 GB, o limite do Always Free, numa conta *pay-as-you-go* (sem recuperação de instância ociosa), com alerta de orçamento em R$ 1.

**Endereços** (domínio `{dominio}`, ainda a definir na Q-07 do EP-002; os exemplos usam `almoseis.app`):

| | Produção | Homologação | Servido por |
|---|---|---|---|
| API (+ SignalR `/hubs/realtime`) | `api.{dominio}` | `api-homolog.{dominio}` | Caddy → `api` na VM |
| Página pública (ADR-0044) | `{dominio}` | `homolog.{dominio}` | Caddy → `web` na VM |
| App do restaurante no navegador (ADR-0053) | `app.{dominio}` | `app-homolog.{dominio}` | Cloudflare Pages |
| Fotos de produto | `img.{dominio}` | `img-homolog.{dominio}` | Cloudflare R2 (domínio público do bucket) |

Os ambientes não compartilham nada: VM, banco, buckets (`almoseis-pg-backup-{homolog,prod}` para o backup, `almoseis-images-{homolog,prod}` para as fotos), chave JWT, segredos do `.env`, projeto do Pages e conta Asaas (sandbox em homologação). O DNS fica na Cloudflare em modo **DNS only** (nuvem cinza) para a API e a página: o Caddy emite o certificado e vê o IP real do cliente (ADR-0041).

## 9.2 Ambiente local

`docker-compose.yml` na raiz (só desenvolvimento, senhas fixas e fracas de propósito):

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
1. `docker compose up -d db` (na raiz)
2. Em `backend/`: `dotnet ef database update --context AppDbContext --project src/AlmoSeis.Infrastructure --startup-project src/AlmoSeis.Api`. A factory de design usa `ALMOSEIS_MIGRATOR_CONNECTION` ou, por padrão, o login local `almoseis_migrator_login`.
3. `dotnet run --project src/AlmoSeis.Api` → `GET /health/ready` responde `Healthy`.
4. Mobile: `npx expo start --dev-client`, com `EXPO_PUBLIC_API_BASE_URL=http://<ip-da-máquina>:5080`. Página: `cd web && npm run dev`.

O login de migration é criado com `ALTER ROLE almoseis_migrator_login SET role = 'almoseis_migrator'`, para que os objetos pertençam ao papel de grupo (05 §5.1). O script das VMs (`infra/db/create-logins.sql`, §9.3) faz o mesmo.

Mudança de schema (ADR-0042): editar o `05` → `pwsh backend/tools/Sync-DdlMigration.ps1 -Target NNNN_Nome.sql` → nova classe de migration → `dotnet test`.

Segredos locais (chave JWT de desenvolvimento, R2 de teste, Asaas sandbox) ficam em `dotnet user-secrets`. O `appsettings.Development.json` versionado só tem as connection strings locais e *placeholders*.

**Teste local do compose da VM** (sem TLS): dá para subir `infra/vm/docker-compose.yml` na máquina do dev com um `.env` de teste (`API_DOMAIN=http://api.localhost`, `WEB_DOMAIN=http://web.localhost`, `HTTP_PORT=8088`, `VOLUME_PREFIX=almoseis-vmtest`, `ASPNETCORE_ENVIRONMENT=Staging`) e um S3 local no lugar do R2. Use **sempre** `ALMOSEIS_PROJECT=almoseis-vmtest` e `ALMOSEIS_PRUNE_IMAGES=false`, para não colidir com o projeto `almoseis` do compose da raiz nem limpar imagens da máquina. No Git Bash do Windows, `MSYS_NO_PATHCONV=1` evita que os caminhos dentro do contêiner sejam convertidos.

## 9.3 VM: Docker Compose de homologação e produção

### Layout na VM

```
/opt/almoseis/
  .env                 segredos e configuração do ambiente (chmod 600, dono deploy) — §9.6
  images.env           tags das imagens em uso, escrito pelo deploy (images.env.prev = anterior)
  infra/vm/            cópia de infra/vm do repositório (substituída a cada deploy)
  infra/db/            cópia de infra/db (create-logins.sql)
/var/log/almoseis/     backup.log, archive-check.log (logrotate semanal)
/etc/cron.d/almoseis   base diária do WAL-G e conferência do archive
```

Todo comando de compose passa pelo *wrapper*, que junta os dois arquivos de variáveis:

```bash
bash /opt/almoseis/infra/vm/scripts/dc.sh ps
bash /opt/almoseis/infra/vm/scripts/dc.sh logs -f --tail 100 api
bash /opt/almoseis/infra/vm/scripts/dc.sh exec postgres psql -U postgres -d almoseis
```

### Serviços (`infra/vm/docker-compose.yml`)

| Serviço | Imagem | Exposição | Memória (prod / homolog) | Saúde |
|---|---|---|---|---|
| `caddy` | `caddy:2.10-alpine` | **80, 443 (TCP e UDP)** | 256 MB | admin `:2019` |
| `api` | `ghcr.io/<owner>/almoseis-api:<sha>` | só rede interna (`api:8080`) | 2 GB / 1 GB | `HEALTHCHECK` da imagem (`/health/live`) |
| `web` | `ghcr.io/<owner>/almoseis-web:<sha>-<ambiente>` | só rede interna (`web:3000`) | 768 MB / 512 MB | `HEALTHCHECK` da imagem (`/`) |
| `postgres` | `ghcr.io/<owner>/almoseis-postgres:16-walg-<hash>` | `127.0.0.1:5432` (túnel SSH) | 6 GB / 2 GB | `pg_isready` |
| `migrator` (perfil `tools`) | `ghcr.io/<owner>/almoseis-migrator:<sha>` | — | — | roda e sai |
| `walg-fetch`, `pg-restore-test` (perfil `restore`) | a do `postgres` | rede `restore`, isolada da API | 1 GB | — |

- **Caddy** (`infra/vm/caddy/Caddyfile`): domínios por variável (`API_DOMAIN`, `WEB_DOMAIN`), certificado automático do Let's Encrypt (e-mail `ACME_EMAIL`), `encode zstd gzip`, HSTS e `nosniff`, WebSockets do SignalR sem limite de leitura. **Sem log de acesso**, porque a URL do acompanhamento leva o token (ADR-0024, ADR-0047). O diretório `caddy/` é montado inteiro, e o deploy faz `caddy reload` depois de trocar o arquivo.
- **Proxy confiável:** a rede do compose tem sub-rede fixa (`DOCKER_SUBNET`, padrão `172.30.0.0/24`), que é a única em `ForwardedHeaders:KnownNetworks`. O Caddy descarta o `X-Forwarded-For` que vem do cliente e grava o IP real.
- **PostgreSQL:** `wal_level=replica`, `archive_mode=on`, `archive_command` = `infra/vm/postgres/scripts/archive-wal.sh`, `archive_timeout=60`, `restore_command` (usado só com `recovery.signal`), *data checksums*, `log_min_duration_statement=1000` **sem parâmetros** (`log_parameter_max_length=0`, ADR-0024). Memória ajustada por variável (`PG_SHARED_BUFFERS` etc., §9.6).
- **Papéis (05 §5.1, ADR-0026):** no primeiro `initdb`, `postgres/initdb/10-almoseis-roles.sh` roda `infra/db/create-logins.sql` com as senhas do `.env`: grupos `NOLOGIN`, logins `almoseis_{migrator,app,platform}_login` sem `SUPERUSER`/`CREATEROLE`/`BYPASSRLS`, `SET role = 'almoseis_migrator'` no login de migration, banco e schema `public` do grupo de migration. O script é idempotente: `scripts/db-roles.sh` o roda de novo para rotação de senha.
- **Endurecimento:** `no-new-privileges` em todos, `cap_drop: ALL` em `api`, `web`, `caddy` (este com `NET_BIND_SERVICE`) e `migrator`. As imagens próprias rodam com usuário não-root (`app` uid 1654 na API, `node` uid 1000 na página).
- **Logs:** driver `json-file` com 10 MB × 5 arquivos por contêiner (também no `daemon.json` da VM).
- **Volumes** com nome fixo (`${VOLUME_PREFIX}_pgdata`, `_dataprotection`, `_caddy_data`, `_caddy_config`). O do banco pode ser trocado (`PGDATA_VOLUME`) na restauração de produção, sem apagar o antigo.
- **Firewall:** só 22, 80 e 443. Na Oracle, a *Security List*/NSG da VCN e o `iptables` da imagem (o `bootstrap-vm.sh` abre 80/443). Na VPS do plano B, `ufw`. O PostgreSQL nunca é exposto, e a administração é por túnel: `ssh -L 5432:127.0.0.1:5432 deploy@<vm>`.

### Preparação da VM (`infra/vm/bootstrap-vm.sh`, uma vez, como root)

Pacotes e `unattended-upgrades` (só segurança, reinício automático às 04:30 se precisar, Docker fora da lista), fuso `America/Sao_Paulo` (cron; os contêineres ficam em UTC), swap (4 GB em prod, 2 GB em homolog, `swappiness=10`), Docker do repositório oficial com `live-restore`, usuário `deploy` (só chave SSH, grupo `docker`), SSH sem senha e sem root, fail2ban, firewall, `/opt/almoseis` com o `.env` (600) copiado do `.env.example`, cron do backup e logrotate. Passo a passo no `infra/README.md` §3.

## 9.4 Imagens e pipelines (GitHub Actions)

### Imagens (ghcr.io, `linux/amd64` + `linux/arm64`)

| Imagem | Dockerfile | Tag | Notas |
|---|---|---|---|
| `almoseis-api` | `backend/Dockerfile` (alvo `api`) | `<sha>` e `<versão>` nas tags | SDK 10 na arquitetura do runner com `dotnet publish -a $TARGETARCH` (compilação cruzada); runtime `aspnet:10.0` (não chiseled: `tzdata` e `curl` do `HEALTHCHECK`), usuário `app`, `ASPNETCORE_HTTP_PORTS=8080`, `DOTNET_gcServer=0`, `/var/lib/almoseis/dataprotection` para o volume |
| `almoseis-migrator` | `backend/Dockerfile` (alvo `migrator`) | `<sha>` | `dotnet ef migrations bundle --self-contained -r linux-{x64,arm64}` sobre `runtime-deps:10.0`. Lê `ALMOSEIS_MIGRATOR_CONNECTION` (`AppDbContextDesignTimeFactory`) |
| `almoseis-web` | `web/Dockerfile` | `<sha>-homolog` / `<sha>-production` | Next.js `output: 'standalone'`, `node:24-alpine`, usuário `node`. **Uma imagem por ambiente**: `NEXT_PUBLIC_API_BASE_URL`, `NEXT_PUBLIC_SITE_URL` e `NEXT_PUBLIC_TURNSTILE_SITE_KEY` entram no build. `API_BASE_URL=http://api:8080` e `WEB_RENDERER_KEY` vêm do ambiente do contêiner. O `sharp` é removido da saída (a página não usa `next/image`) |
| `almoseis-postgres` | `infra/vm/postgres/Dockerfile` | `16-walg-<hash do Dockerfile>` | `postgres:16-bookworm` + binário do WAL-G 3.0.10 baixado com checksum fixo por arquitetura. Sem mudança no Dockerfile, a tag não muda e o compose não recria o banco |

As imagens levam `LABEL app=almoseis`. O `deploy.sh` só faz *prune* de imagens com esse label, sem uso e com mais de 7 dias.

### Workflows

| Workflow | Gatilho | O que faz |
|---|---|---|
| `backend-ci.yml` | PR (`backend/**`, `docs/05-ddl*`) e chamado pelo `deploy.yml` | `setup-dotnet` (pelo `global.json`) → restore (cache NuGet) → build Release (`TreatWarningsAsErrors`) → testes unitários (inclui `DdlMigrationSyncTests`) → arquitetura → integração (Testcontainers: `SchemaTests` + `05-ddl-smoke-test.sql`) → build das imagens `api` e `migrator` (amd64) |
| `web-ci.yml` | PR (`web/**`) e chamado pelo `deploy.yml` | `npm ci` → `typecheck` → `npm test` → `test:smoke` (build de verdade) → confere `.next/standalone/server.js` |
| `mobile-ci.yml` | PR (`mobile/**`) e chamado pelo `mobile-release.yml` | `npm ci` → `tsc --noEmit` → `expo lint` → `jest --ci` → `expo export --platform web` |
| `deploy.yml` | push na `main` (homolog), tag `v*` (produção), manual | CI → `build-api`, `build-web`, `build-postgres` (multi-arch, push no ghcr) → `deploy` (Environment `homolog`/`production`) → `app-web` (Pages) |
| `app-web-pages.yml` | push na `main` em `mobile/**` ou `infra/pages/**` (homolog), chamado pelo `deploy.yml`, manual | `expo export --platform web` com a URL da API do ambiente → `_headers` a partir de `infra/pages/_headers` → `wrangler pages deploy` |
| `mobile-release.yml` | push na `main` (`mobile/**`), tag `mobile-v*` | CI → `eas update --channel preview` (main) ou `eas build --profile production --platform all --auto-submit` (tag) |

### Fluxo do deploy (`deploy.yml` → `infra/vm/scripts/deploy.sh`)

```text
CI backend + web
→ imagens: api, migrator (sha), web (sha-ambiente), postgres (hash) no ghcr.io
→ job deploy (Environment homolog | production; produção espera a aprovação):
    ssh deploy@VM: tar de infra/vm + infra/db → /opt/almoseis/infra
    docker login ghcr.io com o GITHUB_TOKEN do job (vence no fim do job)
    deploy.sh:
      grava images.env (guarda o anterior em images.env.prev)
      pull → postgres no ar (--wait)
      migrator: migrations com almoseis_migrator_login   ── falhou? nada muda, sai com erro
      up -d --wait api web caddy → caddy reload
      smoke pelo Caddy local (--resolve 127.0.0.1, TLS de verdade): /health/ready e a página
          ── falhou? volta às imagens de images.env.prev e sai com erro
      prune das imagens app=almoseis com mais de 7 dias
    docker logout ghcr.io
→ smoke externo do runner (DNS + TLS): /health/ready e a página
→ app-web-pages (depois da API, ADR-0053 §6.4)
```

Rollback manual: `bash /opt/almoseis/infra/vm/scripts/rollback.sh` (troca `images.env` pelo `.prev`). As migrations **não** são desfeitas: rodam **antes** da API nova e precisam ser compatíveis com a versão anterior (*expand/contract*). Nunca remover nem renomear coluna na mesma release que deixa de usá-la.

**Comandos SQL pós-deploy** (abaixo): na VM, com o login de migration, pelo socket local do contêiner:

```bash
bash /opt/almoseis/infra/vm/scripts/dc.sh exec -T postgres psql -U almoseis_migrator_login -d almoseis -v ON_ERROR_STOP=1 -f - < correcao.sql
```

**Contração pendente:** 0003 (05 §5.17), na release seguinte à que pôs a API da 0003 em produção: preencher `accepted_at`/`fulfillment_status` nulos da janela do deploy, apertar `ck_orders_acceptance` e `ck_orders_fulfillment` e `ALTER TABLE orders ALTER COLUMN acceptance_status DROP DEFAULT`. 0009 (05 §5.17), na release seguinte à que pôs a API da 0009 em produção: preencher o `site_price` das bebidas visíveis (`= default_price`, também quando é `0.00`: desde a 0012 a bebida de `0.00` não sai mais do site) e o `price_list = 'SITE'` dos pedidos `WEB` gravados na janela do deploy, criar `ck_products_beverage_site_price`, apertar `ck_orders_price_list` para `(origin = 'WEB') = (price_list = 'SITE')` e `ALTER TABLE orders ALTER COLUMN price_list DROP DEFAULT`. 0010 (05 §5.17), na release seguinte à que pôs a API da 0010 em produção: preencher o `parent_item_kind` dos `ADDON` filhos gravados na janela do deploy (o `UPDATE` da 0010 com `parent_item_kind IS NULL`) e apertar o ramo do `ADDON` em `ck_order_items_parent` para `parent_item_kind IS NOT NULL AND parent_item_kind IN ('MARMITA', 'PF')`. 0011 (05 §5.17), na release seguinte à que pôs a API da 0011 em produção, **condicionada**: se não houver entrega nem retirada `A_LA_CARTE` sem `customer_name` (gravada pela API da 0010), criar `ck_orders_a_la_carte_customer`; se houver, a regra fica só na aplicação e a contração é descartada.

> **Janela do deploy na VM:** com uma instância só, a API anterior para de receber requisições quando o contêiner novo sobe (alguns segundos de `502` no Caddy). A "janela" das correções abaixo é o intervalo entre o fim da migration e a troca do contêiner, bem mais curto que o de uma troca de revisão no Container Apps, mas não é zero: as correções continuam obrigatórias.

**Correção pós-deploy da 0012 (05 §5.17):** um fechamento gravado pela API anterior na janela do deploy fica com o "Vendido pelo site" **com** a taxa de entrega e `site_delivery_fee_total = 0`. **Logo depois do smoke do deploy que pôs a API da 0012 em produção** (e em homolog), repetir o `UPDATE` 3 da 0012 (`daily_closings`) com o login `almoseis_migrator`, numa transação, tirando e devolvendo o `FORCE` de `daily_closings` e `orders` como na migration. É repetível: só toca em fechamento com `site_delivery_fee_total = 0` cujo `site_sold_total` ainda é a soma com a taxa. Sem migration nova.

**Limitações registradas da 0012:** (1) bebida de `0.00` criada pela API da 0009 com `isVisibleOnPage: true` nasceu fora do site e a auditoria dela diz `false`, igual à escondida pelo dono: não volta sozinha. (2) Tamanho de marmita que estava inativo na 0009 e foi **reativado antes da 0012** voltou fora do site e não é mudado (o dono pode ter visto e aceitado). Para conferir com o estabelecimento, com o login `almoseis_migrator` e o `FORCE` fora, numa transação:

```sql
SELECT s.tenant_id, s.id, s.name
  FROM marmita_sizes s
 WHERE s.is_active AND NOT s.sells_on_site AND s.site_price IS NULL
   AND EXISTS (SELECT 1 FROM audit_logs a
                WHERE a.tenant_id = s.tenant_id AND a.entity_type = 'MARMITA_SIZE' AND a.entity_id = s.id
                  AND a.operation_type = 'MarmitaSizeUpdated'
                  AND (a.before ->> 'isActive')::boolean = false AND (a.after ->> 'isActive')::boolean = true);
```

(3) Fechamento cujo `site_sold_total` não bate com os pedidos de hoje (pedido mudou depois do fechamento) fica com a taxa dentro do "Vendido pelo site".

**Correção pós-deploy da 0008 (05 §5.17):** um tenant criado pela API anterior na janela do deploy (depois da migration, antes do contêiner novo) fica sem linha em `tenant_behaviors` e `tenant_labels`. A API nova já o trata como `RESTAURANT` e grava a linha na primeira escrita (07 §7.13), mas a correção não depende disso: **logo depois do smoke do deploy que pôs a API da 0008 em produção** (e em homolog), repetir a carga da 0008 com o login `almoseis_migrator`. Ela é idempotente pelo `NOT EXISTS`, então pode rodar mais de uma vez, e não precisa de migration nova. O rótulo só entra para quem tem `RESTAURANT` (a FK de `tenant_labels` exige o comportamento):

```sql
BEGIN;
ALTER TABLE tenants NO FORCE ROW LEVEL SECURITY;
ALTER TABLE tenant_behaviors NO FORCE ROW LEVEL SECURITY;
ALTER TABLE tenant_labels NO FORCE ROW LEVEL SECURITY;
INSERT INTO tenant_behaviors (tenant_id, behavior, origin, enabled_at)
  SELECT t.id, 'RESTAURANT', 'SYSTEM', now() FROM tenants t
   WHERE NOT EXISTS (SELECT 1 FROM tenant_behaviors b WHERE b.tenant_id = t.id);
INSERT INTO tenant_labels (tenant_id, label, behavior, created_at)
  SELECT t.id, 'RESTAURANT', 'RESTAURANT', now() FROM tenants t
   WHERE NOT EXISTS (SELECT 1 FROM tenant_labels l WHERE l.tenant_id = t.id)
     AND EXISTS (SELECT 1 FROM tenant_behaviors b WHERE b.tenant_id = t.id AND b.behavior = 'RESTAURANT');
ALTER TABLE tenant_labels FORCE ROW LEVEL SECURITY;
ALTER TABLE tenant_behaviors FORCE ROW LEVEL SECURITY;
ALTER TABLE tenants FORCE ROW LEVEL SECURITY;
COMMIT;
-- Conferência: deve voltar 0.
SELECT count(*) FROM tenants t WHERE NOT EXISTS (SELECT 1 FROM tenant_behaviors b WHERE b.tenant_id = t.id);
```

Se a conferência não voltar 0, repetir o bloco. Depois que o contêiner anterior sair, não nasce mais tenant sem comportamento.

### Ordem de deploy do EP-003

O app antigo (sem o EP-003) recebe `422` em duas rotas das APIs do EP-003: o **cadastro** da fatia 1 exige `tenant.behavior` e `tenant.labels` (ADR-0054), e a **entrega de marmita** da fatia 4 exige `customerPhone` e `expectedPaymentMethod` (ADR-0059, Q-312 e Q-316). Por isso a **versão mínima do app** (ADR-0035: `App:MinSupportedAppVersion:ANDROID`/`:IOS`/`:WEB`, conferida pelo `AppVersionMiddleware`, que responde `426 AppVersionUnsupported`) precisa subir **na mesma release** que coloca em produção as APIs das fatias 1 e 4, e não numa release depois:

1. Publicar o app com o EP-003 nas lojas (`mobile-release.yml`, tag `mobile-v*`) e esperar a aprovação das lojas. A API anterior aceita o app novo.
2. Antes de criar a tag `v*` da release do backend que leva as fatias 1 e 4, editar na VM de produção `/opt/almoseis/.env`: `App__MinSupportedAppVersion__ANDROID`/`__IOS` com a versão do passo 1. **Não** rodar `up` à mão: o `deploy.sh` recria a API com o `.env` novo junto com a imagem nova, antes do smoke. O `App__MinSupportedAppVersion__WEB` sobe só depois que o job `app-web` do mesmo `deploy.yml` publicar o app web (ADR-0053 §6.4): editar o `.env` e `bash scripts/dc.sh up -d api`.
3. Rodar a correção pós-deploy da 0008 (acima).

Subir só a API sem a versão mínima deixa o app antigo funcionando até o primeiro cadastro ou a primeira entrega de marmita, que falham com `422`. A estratégia (forçar a atualização, ou aceitar o corpo antigo por uma janela) está em aberto na Q-323 (`12-epico-ep003.md`).

### Mobile

| Gatilho | Ação |
|---|---|
| push na `main` (`mobile/**`) | `eas update --channel preview --non-interactive` (homolog) + `app-web-pages.yml` (app web de homologação) |
| tag `mobile-v*` | `eas build --profile production --platform all` + `--auto-submit` (Play Console: faixa interna; App Store: TestFlight) |

O token do EAS (`EXPO_TOKEN`) fica nos secrets do repositório.

`eas.json`:

| Perfil | Canal | `EXPO_PUBLIC_API_BASE_URL` | Distribuição |
|---|---|---|---|
| `development` | — | local | Dev Client interno |
| `preview` | `preview` | `https://api-homolog.almoseis.app` | interna (APK/TestFlight) |
| `production` | `production` | `https://api.almoseis.app` | lojas |

Quando o domínio da Q-07 for decidido, ele muda aqui, no `ALMOSEIS_DOMAIN` do GitHub e no `.env` das VMs.

### App do restaurante no navegador (Cloudflare Pages, ADR-0053)

`expo export --platform web` com `EXPO_PUBLIC_WEB_API_BASE_URL=https://api[-homolog].{dominio}` e `EXPO_PUBLIC_PUBLIC_PAGE_BASE_URL`, publicado com `wrangler pages deploy` nos projetos `almoseis-app` (produção) e `almoseis-app-homolog`. `infra/pages/_headers` define:

- CSP (`script-src 'self'`; `connect-src` com a API em HTTPS e WSS e a origem das imagens; `frame-ancestors 'none'`). O export não tem script *inline*, e os `eval` do bundle só rodam fora do navegador ou em *try/catch*;
- `X-Robots-Tag: noindex`, `nosniff`, `Referrer-Policy: same-origin`;
- `no-cache` no HTML e cache de 1 ano (`immutable`) em `/_expo/static/*` e `/assets/*` (com hash no nome).

O Pages já devolve o `index.html` para rotas desconhecidas (SPA, sem `404.html`).

## 9.5 Infraestrutura: o que é código e o que é manual

Não há IaC de nuvem no piloto (o Bicep do ADR-0037 é do alvo futuro). O que é código:

| Arquivo | O quê |
|---|---|
| `infra/vm/bootstrap-vm.sh` | preparação da VM (uma vez) |
| `infra/vm/docker-compose.yml`, `caddy/Caddyfile` | serviços da VM |
| `infra/vm/.env.example` | **todas** as variáveis, com comentário e sem valor real |
| `infra/vm/postgres/` | imagem PostgreSQL + WAL-G, `initdb` dos papéis, `archive_command`, preparação da restauração |
| `infra/vm/scripts/` | `dc.sh`, `deploy.sh`, `rollback.sh`, `db-roles.sh`, `lib.sh` |
| `infra/vm/backup/` | `walg-backup.sh` (base diária), `check-archive.sh`, `restore.sh`, cron, logrotate |
| `infra/db/create-logins.sql` | papéis e logins de homolog/prod (idempotente, senhas por variável do `psql`) |
| `infra/pages/_headers` | cabeçalhos do app web no Pages |

O que é manual (contas, VMs, DNS, buckets, tokens, GitHub Environments) está passo a passo em `infra/README.md`.

## 9.6 Segredos e variáveis de ambiente

**Regras:**

- Segredos de homolog/prod só em `/opt/almoseis/.env` na VM (**chmod 600**, dono `deploy`), com cópia no cofre de senhas do responsável técnico. Nunca no repositório, em imagem, em log nem em GitHub Actions (a não ser a chave SSH do deploy).
- GitHub Environment *secrets*: só `DEPLOY_SSH_KEY` e `DEPLOY_SSH_KNOWN_HOSTS` (por ambiente); no repositório, `CLOUDFLARE_API_TOKEN`, `CLOUDFLARE_ACCOUNT_ID` e `EXPO_TOKEN`. As senhas do banco **não** vão para o GitHub: o *migrator* roda na VM.
- A API lê a configuração de `appsettings.json` (sem segredos, só *placeholders*) + **variáveis de ambiente** com `__` no lugar de `:` (`Email__Smtp__Host` = `Email:Smtp:Host`). `KeyVault:Uri` vazio = sem Key Vault (ADR-0060). Localmente, `dotnet user-secrets`.
- Toda variável nova da API entra **ao mesmo tempo** no `infra/vm/.env.example`, no bloco `environment:` do serviço `api` em `infra/vm/docker-compose.yml` e nesta tabela. O compose repassa à API só o que está listado (a API não recebe as senhas do superusuário nem as chaves do WAL-G).
- Gerar segredo: `openssl rand -hex 32`. Senhas do banco só `[A-Za-z0-9]` (vão numa connection string). Chave JWT: `openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048`, entre aspas duplas no `.env` (várias linhas ou `\n`).
- Rotação: editar o `.env` → `bash scripts/db-roles.sh` (senhas do banco) → `bash scripts/dc.sh up -d api web` (o compose recria o que mudou).

### API (`environment:` do serviço `api`)

S = segredo. Obrig. = a API não sobe fora de Development sem o valor (validação ao subir, 07 §7.11).

| Variável | S | Obrig. | Exemplo / padrão | Uso |
|---|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | | sim | `Production` (prod), `Staging` (homolog) | `Staging` permite `Maps`/`Push` `Fake` |
| `Logging__LogLevel__Default` | | | `Information` | |
| `ConnectionStrings__App` | S | sim | montada pelo compose: `Host=postgres;…;Username=almoseis_app_login;Password=${ALMOSEIS_APP_DB_PASSWORD}` | API (RLS) |
| `ConnectionStrings__Platform` | S | sim | idem, `almoseis_platform_login` | `PlatformDbContext` |
| `KeyVault__Uri` | | | vazio | vazio = sem Key Vault |
| `Jwt__SigningKeyPem` | S | sim | PEM RSA ≥ 2048 bits | tokens (ADR-0027) |
| `Jwt__AllowEphemeralKey` | | | `false` (fixo no compose) | |
| `DataProtection__KeysPath` | | sim | `/var/lib/almoseis/dataprotection` (fixo no compose, volume `dataprotection`) | token do "esqueci a senha" |
| `DataProtection__CertificatePath`, `__CertificatePassword` | S | | vazio (risco aceito, 07 §7.11) | cifra das chaves em repouso |
| `App__MinSupportedAppVersion__ANDROID` / `__IOS` / `__WEB` | | | `0.0.0` | ADR-0035, ordem do EP-003 (§9.4) |
| `App__StoreUrl__ANDROID` / `__IOS` | | | URL da loja | tela do `426` |
| `Cors__RestaurantAppOrigins__0` | | sim | `https://app.{dominio}` | ADR-0053 §4 |
| `Cors__PublicPageOrigins__0` | | sim | `https://{dominio}` | ADR-0051 |
| `ForwardedHeaders__KnownNetworks__0` | | | `${DOCKER_SUBNET}` (fixo no compose) | proxy confiável (ADR-0041) |
| `RateLimiting__Enabled` | | | `true` | |
| `Billing__PlanCode`, `Billing__MonthlyPrice` | | | `PILOT`, `0` | ADR-0017 |
| `Billing__Asaas__BaseUrl` | | | `https://api.asaas.com/v3` (homolog: `https://api-sandbox.asaas.com/v3`) | |
| `Billing__Asaas__ApiKey`, `Billing__Asaas__WebhookToken` | S | | | cobrança e `/webhooks/asaas` |
| `Storage__R2__AccountId`, `__Bucket`, `__PublicBaseUrl` | | | `almoseis-images-prod`, `https://img.{dominio}` | fotos (bucket de imagens, não o de backup) |
| `Storage__R2__AccessKeyId`, `__SecretAccessKey` | S | | token R2 só do bucket de imagens | |
| `Turnstile__Enabled` | | | `true` | ADR-0045 |
| `Turnstile__SecretKey` | S | sim com `Enabled` | chave real (as de teste só em Development) | |
| `PublicPage__IpHashSecret`, `PublicPage__TrackingTokenSecret` | S | sim | 64 hex, diferentes entre si | ADR-0045, ADR-0047 |
| `PublicPage__RendererKey` | S | | 64 hex (o compose repassa à página como `WEB_RENDERER_KEY`) | `X-Web-Renderer-Key` |
| `Maps__Provider` | | | `None` \| `Google` \| `Osm` (`Fake` só em Staging) | ADR-0048 |
| `Maps__AddressHashSecret` | S | sim | 64 hex | |
| `Maps__Google__ApiKey` | S | com `Google` | | |
| `Push__Provider` | | | `Expo` (`Fake` só em Staging) | ADR-0049 |
| `Push__Expo__AccessToken` | S | | | |
| `PasswordReset__LinkBaseUrl` | | sim (no compose) | `https://{dominio}/redefinir-senha?token=` | e-mail de recuperação |
| `Invitations__LinkBaseUrl` | | sim (no compose) | `https://{dominio}/convite/` | convite por WhatsApp |
| `Email__Provider` | | sim | `Smtp` (`None`/`Fake` não sobem em Production; `Acs` = alvo futuro) | ADR-0040/0060 |
| `Email__From` | | sim | `AlmoSeis <nao-responda@{dominio}>` | remetente |
| `Email__Smtp__Host`, `__Port` | | com `Smtp` | `smtp-relay.brevo.com`, `587` / `email-smtp.sa-east-1.amazonaws.com`, `587` | |
| `Email__Smtp__UseStartTls` | | | `true` (`false` na 465 = TLS implícito) | |
| `Email__Smtp__Username`, `__Password` | S | com usuário | login SMTP do Brevo / credencial SMTP do SES | |
| `Otel__Endpoint` | | | vazio = sem exportação | OTLP (Grafana Cloud, Better Stack, Sentry OTLP) |
| `Otel__Protocol` | | | `HttpProtobuf` \| `Grpc` | |
| `Otel__Headers` | S | | `Authorization=Basic …` | |
| `Otel__ServiceName`, `Otel__TraceSampleRatio` | | | `almoseis-api` (fixo), `0.2` | |
| `OTEL_RESOURCE_ATTRIBUTES` | | | `deployment.environment=${DEPLOY_ENVIRONMENT}` (fixo no compose) | |

### Página (`web`)

| Variável | Quando | Valor | Origem |
|---|---|---|---|
| `NEXT_PUBLIC_API_BASE_URL` | **build** | `https://api[-homolog].{dominio}` | `deploy.yml` (de `ALMOSEIS_DOMAIN`) |
| `NEXT_PUBLIC_SITE_URL` | **build** | `https://[homolog.]{dominio}` | idem |
| `NEXT_PUBLIC_TURNSTILE_SITE_KEY` | **build** | *site key* pública | variáveis `TURNSTILE_SITE_KEY_{HOMOLOG,PRODUCTION}` do repositório |
| `API_BASE_URL` | execução | `http://api:8080` (fixo no compose) | |
| `WEB_RENDERER_KEY` | execução | `= PublicPage__RendererKey` | `.env` |

### Infra da VM (só compose, PostgreSQL e scripts)

| Variável | S | Uso |
|---|---|---|
| `DEPLOY_ENVIRONMENT` | | `homolog` \| `production` (telemetria) |
| `API_DOMAIN`, `WEB_DOMAIN`, `ACME_EMAIL`, `CADDY_GLOBAL_OPTIONS`, `HTTP_PORT`, `HTTPS_PORT` | | Caddy |
| `DOCKER_SUBNET`, `VOLUME_PREFIX`, `PGDATA_VOLUME` | | rede e volumes (o `PGDATA_VOLUME` muda só na restauração) |
| `CADDY_MEM_LIMIT`, `API_MEM_LIMIT`, `WEB_MEM_LIMIT`, `PG_MEM_LIMIT` | | limites de memória |
| `POSTGRES_SUPERUSER_PASSWORD` | S | superusuário `postgres` (só administração; a API nunca usa) |
| `ALMOSEIS_MIGRATOR_DB_PASSWORD`, `ALMOSEIS_APP_DB_PASSWORD`, `ALMOSEIS_PLATFORM_DB_PASSWORD` | S | logins de 05 §5.1 (`create-logins.sql`) e connection strings |
| `DB_APP_POOL_SIZE`, `DB_PLATFORM_POOL_SIZE` | | `Maximum Pool Size` |
| `PG_SHARED_BUFFERS`, `PG_EFFECTIVE_CACHE_SIZE`, `PG_WORK_MEM`, `PG_MAINTENANCE_WORK_MEM`, `PG_MAX_CONNECTIONS`, `PG_MAX_WAL_SIZE`, `PG_SHM_SIZE`, `PG_ARCHIVE_TIMEOUT`, `PG_LOG_MIN_DURATION_MS`, `PG_LOCAL_PORT` | | ajuste do PostgreSQL |
| `WALG_ENABLED`, `WALG_S3_PREFIX`, `WALG_AWS_ENDPOINT`, `WALG_AWS_REGION`, `WALG_COMPRESSION_METHOD`, `WALG_UPLOAD_CONCURRENCY`, `WALG_RETAIN_FULL` | | WAL-G (§9.7) |
| `WALG_AWS_ACCESS_KEY_ID`, `WALG_AWS_SECRET_ACCESS_KEY` | S | token R2 só do bucket de backup do ambiente |
| `WALG_LIBSODIUM_KEY` | S | chave da criptografia do backup (**perder = perder os backups**) |
| `BACKUP_HEARTBEAT_URL`, `ARCHIVE_HEARTBEAT_URL` | S | *heartbeats* do monitor externo (§9.8) |
| `RESTORE_WALG_S3_PREFIX` | | restaurar de outro prefixo (ex.: ensaio em homolog a partir de prod) |
| `ALMOSEIS_PRUNE_IMAGES` (ambiente do `deploy.sh`) | | `false` desliga o *prune* de imagens |

## 9.7 Backup e restauração (RNF-005)

### Desenho

| Item | Valor |
|---|---|
| Ferramenta | WAL-G 3.0.10 dentro da imagem `almoseis-postgres` |
| Destino | Cloudflare R2, **um bucket por ambiente** (`almoseis-pg-backup-homolog`, `almoseis-pg-backup-prod`), prefixo `pg16`, token de API com "Object Read & Write" **só** nesse bucket. Fora da Oracle de propósito (sobrevive à perda da conta) |
| WAL contínuo | `archive_command` → `wal-g wal-push` a cada segmento, `archive_timeout = 60 s` → **RPO ~1 min** |
| Base completa | diária às 03:30 (cron da VM, `walg-backup.sh`), `backup-push` com o banco no ar |
| Retenção | `WALG_RETAIN_FULL = 14` bases (`delete retain FULL 14`): **PITR de até 14 dias** |
| Criptografia | no cliente (libsodium, `WALG_LIBSODIUM_KEY`); o bucket só vê dados cifrados. Compressão brotli |
| RTO | **< 1 h** (meta); no teste local, base pequena: 41 s (ensaio) e 46 s (restauração de produção) |
| LGPD | o R2 não tem região no Brasil: backup **cifrado** fora do país, Q-0060-1 (revisão jurídica antes do piloto) |
| Custo | dentro do gratuito do R2 (10 GB, 1 milhão de escritas/mês; 1 PUT por segmento de WAL → ≤ 45 mil/mês) |

**Monitoramento do backup** (§9.8): `walg-backup.sh` chama `BACKUP_HEARTBEAT_URL` depois de cada base, ou `…/fail` se der erro. `check-archive.sh` (a cada 5 min) chama `ARCHIVE_HEARTBEAT_URL` se o *archive* está em dia, ou `…/fail` se a última tentativa falhou, se há mais de 10 segmentos esperando envio, ou se o disco passou de 85%. **Bucket fora do ar por muito tempo enche o disco**, porque o PostgreSQL guarda o WAL até conseguir enviar. Por isso o alerta de disco.

### Restauração (`infra/vm/backup/restore.sh`, detalhes em `infra/vm/restore.md`)

| Comando | O que faz |
|---|---|
| `restore.sh list` | bases disponíveis no bucket |
| `restore.sh test [--time "AAAA-MM-DD HH:MM:SS-03"] [--backup NOME] [--keep]` | **ensaio**: baixa a base num volume à parte, sobe um PostgreSQL isolado (rede própria, **sem archive**, sem sujar o bucket), recupera até o instante, confere contagens (`tenants`, `orders`, `payments`, `daily_closings`, últimos registros, última migration, papéis sem `BYPASSRLS`), mede o tempo e apaga tudo |
| `restore.sh production [--time …] [--yes]` | **restauração real**: baixa a base num volume **novo** com o banco antigo ainda no ar, para API e página, para o banco antigo (que arquiva o último WAL), aponta `PGDATA_VOLUME` para o volume novo, recupera, promove, limpa o alvo de recuperação, sobe API e página, faz o smoke e tira uma base nova na linha do tempo nova. O volume antigo **não é apagado** (apagar à mão depois de 7 dias) |

O instante sempre com fuso (`-03`). Sem `--time`, recupera até o último WAL do bucket.

**Ensaio obrigatório antes do piloto e a cada trimestre**, registrado em `infra/RESTORE-LOG.md`: rodar `restore.sh test` em produção (o ensaio não toca no banco em uso) e também na VPS do plano B, ao menos uma vez antes do piloto, a partir do bucket de produção com `RESTORE_WALG_S3_PREFIX`. Conferir os números com o app (histórico de fechamentos) e registrar o tempo total, que precisa ficar abaixo de 1 h.

**Plano B (perda da VM ou da conta Oracle):** criar a VPS no Brasil → `bootstrap-vm.sh` → copiar o `.env` do cofre de senhas para `/opt/almoseis/.env` → rodar o `deploy.yml` manual apontando `DEPLOY_HOST` para a VPS, **ou** `restore.sh production` antes do primeiro deploy (o volume vazio é preenchido pela restauração) → trocar o DNS de `api.` e `{dominio}` → conferir o *smoke*. Passo a passo em `infra/vm/restore.md`.

## 9.8 Monitoramento e alertas

| O quê | Ferramenta (plano gratuito) | Condição | Aviso |
|---|---|---|---|
| API indisponível | UptimeRobot ou Better Stack: HTTP `https://api.{dominio}/health/ready` (espera `Healthy`) a cada 1 a 5 min | 2 falhas seguidas (~3 min) | e-mail (e push do app do monitor) |
| Página indisponível | idem: `https://{dominio}/` | 2 falhas seguidas | e-mail |
| App web | idem: `https://app.{dominio}/` | 2 falhas seguidas | e-mail |
| Certificado | monitor de SSL do mesmo serviço | vence em < 14 dias (o Caddy renova em 30) | e-mail |
| Base diária | *heartbeat* `BACKUP_HEARTBEAT_URL` (Better Stack ou Healthchecks.io) | sem sinal em 26 h ou `/fail` | e-mail |
| *Archive* de WAL e disco | *heartbeat* `ARCHIVE_HEARTBEAT_URL` | sem sinal em 15 min ou `/fail` | e-mail |
| Erros e latência (opcional) | OTLP (`Otel__Endpoint`) → Grafana Cloud, Better Stack ou Sentry | 5xx > 1% em 5 min; p95 > 1 s por 10 min (RNF-002) | e-mail do serviço |
| Outbox atrasado (opcional) | métrica `almoseis.outbox.pending` / `lag_ms` no mesmo destino OTLP | `pending` > 100 ou `lag_ms` p95 > 3 s por 5 min | e-mail do serviço |

- **Sem OTLP**, os logs ficam só no `docker logs` (rotação de 10 MB × 5). Para investigar: `dc.sh logs --since 1h api`. As métricas próprias (`almoseis.outbox.pending`, `almoseis.outbox.lag_ms`, `almoseis.idempotency.replays`, `almoseis.orders.created`, `almoseis.sync.cursor_expired`) só saem com OTLP.
- **Nunca** gravar payloads, telefones nem tokens em log ou telemetria (ADR-0024). O Caddy não tem log de acesso e o PostgreSQL não grava parâmetros.
- Recurso da VM: o painel da Oracle (CPU e memória da instância) e o alarme gratuito do OCI Monitoring (CPU > 80% por 15 min) por e-mail. Esses números alimentam o gatilho de migração (ADR-0060 item 10).
- Os alertas vão por e-mail para o responsável técnico. Não há plantão no piloto.

## 9.9 Checklist de ida ao piloto

- [ ] Domínio decidido (Q-07) e DNS na Cloudflare: `api.`, `{dominio}`, `app.`, `img.` (e os de homologação)
- [ ] VMs criadas e preparadas (`bootstrap-vm.sh`), `.env` preenchido com 600 e cópia no cofre de senhas
- [ ] Primeiro deploy de homologação e de produção verdes (smoke externo)
- [ ] TLS válido nos domínios (Caddy) e HSTS conferido
- [ ] DNS de e-mail (SPF, DKIM, DMARC) verificado no Brevo/SES, e um "esqueci a senha" recebido de verdade
- [ ] Conta Asaas de produção, webhook apontado para `https://api.{dominio}/webhooks/asaas` com token
- [ ] `App__MinSupportedAppVersion__*` configurado (ordem do EP-003, §9.4)
- [ ] Turnstile com chaves reais por domínio (página)
- [ ] App web no Pages (`app.{dominio}`) com a CSP conferida no navegador (sem erros no console)
- [ ] **Ensaio de restauração** registrado em `infra/RESTORE-LOG.md` (na VM de produção **e** na VPS do plano B), com tempo < 1 h
- [ ] Monitores e *heartbeats* criados e **testados** (parar a API de homologação e conferir o e-mail; `WALG_ENABLED` errado em homologação e conferir o `/fail` do *archive*)
- [ ] Revisão jurídica dos Termos e do contrato de tratamento (ADR-0024), **incluindo o backup cifrado no R2 (Q-0060-1)**
- [ ] Validação das regras da App Store e do Google Play sobre o app sem compra (ADR-0017)
- [ ] ADR-0060 revisado e aceito pelo usuário

## 9.10 Alvo futuro: Azure gerenciado (ADR-0037, suspenso)

Quando o gatilho do ADR-0060 (item 10) disparar, o desenho de referência é o do ADR-0037, revisado num ADR novo:

- **Azure Brazil South:** Container Apps para `almoseis-api` e `almoseis-web` (mínimo de 1 réplica, sem *scale-to-zero*, *sticky sessions* e WebSockets; Azure SignalR Service só com mais de uma réplica); PostgreSQL Flexible Server 16 com PITR de 14 dias, sem backup geo-redundante (dados no Brasil); Key Vault com *managed identity* (`KeyVault__Uri` já é suportado pela API); Application Insights por OpenTelemetry (`APPLICATIONINSIGHTS_CONNECTION_STRING`, já suportado); ACS ou o mesmo SMTP para e-mail (`Email__Provider`).
- **IaC:** `infra/bicep` com módulos de log, Key Vault, PostgreSQL, Container Apps Environment e e-mail. Login no Azure por OIDC federado no GitHub Actions; migrations pelo mesmo *migrator* (imagem ou `migrations bundle`) com a connection string do `almoseis_migrator`.
- **App web:** Static Web Apps ou continuar no Cloudflare Pages (não depende da Azure).
- **Migração dos dados:** `pg_dump`/`pg_restore` numa janela curta, ou replicação lógica da VM para o Flexible Server, com corte por troca de DNS. As imagens, as migrations e a configuração por variáveis não mudam.
- Alertas do Azure Monitor equivalentes aos do §9.8 (API indisponível, 5xx > 1%, p95 > 1 s, outbox, webhook do Asaas, CPU e armazenamento do banco > 80%).
