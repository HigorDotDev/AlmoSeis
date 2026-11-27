# infra/ — runbook do primeiro deploy (ADR-0060)

Infra enxuta do piloto: **uma VM por ambiente** (Oracle Cloud São Paulo, ARM64) com Docker Compose (Caddy, API, página,
PostgreSQL 16 + WAL-G), o app do restaurante no navegador no **Cloudflare Pages**, backup no **Cloudflare R2** e deploy
por **GitHub Actions via SSH**. Desenho e regras: `docs/09-devops.md`. Decisão: `docs/adr/0060-infra-enxuta-piloto-vm-unica.md`
(**Proposto**: revisar antes de produção).

```
infra/
  README.md            este runbook
  RESTORE-LOG.md       registro dos ensaios de restauração
  db/init/             papéis e logins do ambiente LOCAL (docker-compose.yml da raiz)
  db/create-logins.sql papéis e logins de homolog/prod (idempotente, senhas por variável)
  vm/                  tudo o que roda na VM (copiado para /opt/almoseis/infra/vm a cada deploy)
    bootstrap-vm.sh    preparação da VM (uma vez)
    docker-compose.yml serviços; caddy/Caddyfile; .env.example (TODAS as variáveis)
    postgres/          imagem PostgreSQL + WAL-G, initdb dos papéis, archive e preparação da restauração
    scripts/           dc.sh, deploy.sh, rollback.sh, db-roles.sh
    backup/            walg-backup.sh, check-archive.sh, restore.sh, cron, logrotate
    restore.md         restauração passo a passo (ensaio, produção, plano B)
  pages/_headers       cabeçalhos do app web no Cloudflare Pages
```

Convenções deste runbook: `{dominio}` é o domínio escolhido (Q-07 do EP-002; os exemplos usam `almoseis.app`).
**Nada aqui cria recurso pago**: tudo cabe nos planos gratuitos, exceto o domínio.

---

## 1. O que você cria (lista de contas e recursos)

| # | Onde | O quê | Para quê |
|---|---|---|---|
| 1 | Registrador (ou Cloudflare Registrar) | domínio `{dominio}` | tudo |
| 2 | Cloudflare | conta + zona do domínio (DNS) | DNS, R2, Pages, Turnstile |
| 3 | Cloudflare R2 | 4 buckets: `almoseis-pg-backup-homolog`, `almoseis-pg-backup-prod`, `almoseis-images-homolog`, `almoseis-images-prod` + 4 tokens de API (um por bucket) | backup do banco e fotos |
| 4 | Cloudflare Pages | 2 projetos: `almoseis-app-homolog`, `almoseis-app` (criação por *Direct Upload*) + domínios `app-homolog.{dominio}` e `app.{dominio}` | app do restaurante no navegador |
| 5 | Cloudflare | token de API "Cloudflare Pages: Edit" + Account ID | deploy do Pages pelo GitHub |
| 6 | Cloudflare Turnstile | 1 widget por ambiente (`{dominio}` e `homolog.{dominio}`): *site key* + *secret key* | página pública (ADR-0045) |
| 7 | Oracle Cloud | conta **pay-as-you-go** (cartão; região de origem **Brazil East (São Paulo)**), alerta de orçamento | VMs |
| 8 | Oracle Cloud | 2 VMs `VM.Standard.A1.Flex` Ubuntu 24.04 aarch64 (prod 3 OCPU/18 GB/100 GB; homolog 1 OCPU/6 GB/50 GB), IP público reservado, VCN com 22/80/443 liberados | API, página e banco |
| 9 | Sua máquina | 2 chaves SSH: a sua (administração) e a do deploy (`ed25519`, sem senha, só para o GitHub) | acesso |
| 10 | Brevo **ou** Amazon SES | conta + domínio verificado (SPF, DKIM, DMARC) + credencial SMTP | "esqueci a senha" (ADR-0040/0060) |
| 11 | UptimeRobot **ou** Better Stack (+ Healthchecks.io se quiser) | monitores HTTP + 2 *heartbeats* por ambiente | alertas (§9.8) |
| 12 | GitHub | Environments `homolog` e `production` (com revisores), variáveis e secrets (§6) | deploy |
| 13 | Asaas | conta sandbox (homolog) e produção, chave de API e token do webhook | assinatura (ADR-0017) |
| 14 | Expo / lojas | `EXPO_TOKEN` (já previsto) | app nativo |
| 15 | Opcional | Grafana Cloud / Better Stack / Sentry (endpoint OTLP) | telemetria |
| 16 | Opcional (plano B) | conta numa VPS com datacenter no Brasil, **sem criar a VPS** até precisar | restauração fora da Oracle |
| 17 | Cofre de senhas | um item por ambiente com o `.env` inteiro e a chave privada do deploy | recuperação |

---

## 2. Oracle Cloud: conta e VMs

1. Criar a conta com a região de origem **Brazil East (São Paulo) `sa-saopaulo-1`** (a região de origem não muda
   depois). Fazer o *upgrade* para **Pay As You Go**: os recursos Always Free continuam grátis e a conta não sofre a
   recuperação de instância ociosa. Em *Billing → Budgets*, criar um orçamento de R$ 1 com alerta por e-mail.
2. *Networking → Virtual Cloud Networks → Start VCN Wizard* (VCN com internet). Na *Security List* pública, regras de
   entrada: TCP 22 (de preferência só do seu IP), TCP 80, TCP 443 e UDP 443 de `0.0.0.0/0`.
3. *Compute → Instances → Create*:
   - imagem **Canonical Ubuntu 24.04** (aarch64), shape **VM.Standard.A1.Flex**;
   - produção: 3 OCPU, 18 GB, boot volume de 100 GB. Homologação: 1 OCPU, 6 GB, 50 GB;
   - sub-rede pública, com IP público. Depois, em *Reserved Public IPs*, reservar o IP (para não mudar ao recriar);
   - chave SSH: a **sua** chave pública (usuário padrão `ubuntu`).
   - Se der "Out of capacity", tente outro *availability domain* ou mais tarde. É comum em São Paulo.
4. Anotar os dois IPs.

## 3. Preparar cada VM (uma vez)

Na sua máquina, na raiz do repositório:

```bash
# chave do deploy (só para o GitHub Actions; NÃO use a sua)
ssh-keygen -t ed25519 -N "" -C "github-deploy-prod" -f ~/.ssh/almoseis_deploy_prod

scp -r infra ubuntu@<IP_PROD>:/tmp/almoseis-infra
ssh ubuntu@<IP_PROD> "sudo DEPLOY_SSH_PUBKEY='$(cat ~/.ssh/almoseis_deploy_prod.pub)' \
  DEPLOY_SSH_PUBKEY_EXTRA='$(cat ~/.ssh/id_ed25519.pub)' SWAP_SIZE=4G \
  bash /tmp/almoseis-infra/vm/bootstrap-vm.sh"
```

Homologação: o mesmo, com outra chave (`almoseis_deploy_homolog`) e `SWAP_SIZE=2G`.

O script instala o Docker, abre 80/443 no `iptables` da imagem da Oracle, cria o usuário `deploy`, desliga o login
por senha, configura swap, fail2ban, atualizações automáticas de segurança (reinício às 04:30 se precisar), o cron do
backup e copia a infra para `/opt/almoseis`. Ao fim, `/opt/almoseis/.env` existe com o conteúdo do `.env.example`.

Conferir: `ssh -i ~/.ssh/almoseis_deploy_prod deploy@<IP_PROD> docker ps` funciona, e `ssh ubuntu@...` com senha não.

## 4. DNS (Cloudflare)

| Registro | Tipo | Valor | Proxy |
|---|---|---|---|
| `api` | A | IP de produção | **DNS only** (nuvem cinza) |
| `@` (`{dominio}`) | A | IP de produção | DNS only |
| `api-homolog` | A | IP de homologação | DNS only |
| `homolog` | A | IP de homologação | DNS only |
| `app`, `app-homolog` | CNAME | criados pelo Pages ao adicionar o domínio personalizado (§5) | proxied (automático) |
| `img`, `img-homolog` | — | domínio personalizado do bucket de imagens no R2 (R2 → bucket → *Settings → Custom Domains*) | automático |
| e-mail | TXT/CNAME | SPF, DKIM e DMARC que o Brevo/SES mostrar | DNS only |

"DNS only" na API e na página: o Caddy precisa responder ao desafio do Let's Encrypt e ver o IP real do cliente.

## 5. Cloudflare: R2, Pages e Turnstile

**R2** (*R2 Object Storage*; ativar o plano gratuito):
1. Criar os 4 buckets (*Location*: automático. Não existe opção Brasil: ver a Q-0060-1 no ADR-0060).
2. *Manage R2 API Tokens → Create API token*, um por bucket, permissão **Object Read & Write**, *Apply to specific
   buckets only* (só aquele). Anotar *Access Key ID* e *Secret Access Key* (só aparecem uma vez) e o *Account ID*.
3. Nos buckets de imagens: domínio personalizado `img.{dominio}` / `img-homolog.{dominio}`. Nos buckets de backup:
   **nada público**.

**Pages:**
1. *Workers & Pages → Create → Pages → Direct Upload*: projeto `almoseis-app-homolog` (envie qualquer pasta com um
   `index.html` só para criar). Repetir para `almoseis-app`.
2. Em cada projeto, *Custom domains*: `app-homolog.{dominio}` e `app.{dominio}`.
3. *My Profile → API Tokens → Create Token*: modelo "Edit Cloudflare Workers" ou um token próprio com
   **Account → Cloudflare Pages → Edit**. Anotar o token e o Account ID.

**Turnstile:** um widget por ambiente, com os hosts da página (`{dominio}`; `homolog.{dominio}`). A *site key* vai para
o GitHub (variável) e a *secret key* para o `.env` (`Turnstile__SecretKey`).

## 6. GitHub: Environments, variáveis e secrets

*Settings → Environments*:

| Environment | Proteção | Variáveis | Secrets |
|---|---|---|---|
| `homolog` | nenhuma (deploy automático da `main`) | `DEPLOY_HOST` = IP/nome da VM de homologação | `DEPLOY_SSH_KEY` = conteúdo de `~/.ssh/almoseis_deploy_homolog`; `DEPLOY_SSH_KNOWN_HOSTS` = saída de `ssh-keyscan -t ed25519 <IP_HOMOLOG>` |
| `production` | **Required reviewers** (você) + *Deployment branches and tags*: só tags `v*` | `DEPLOY_HOST` = IP/nome da VM de produção | `DEPLOY_SSH_KEY` (chave de prod), `DEPLOY_SSH_KNOWN_HOSTS` |

*Settings → Secrets and variables → Actions* (repositório):

| Tipo | Nome | Valor |
|---|---|---|
| variável | `ALMOSEIS_DOMAIN` | `almoseis.app` (o domínio, sem `https://`) |
| variável | `TURNSTILE_SITE_KEY_HOMOLOG`, `TURNSTILE_SITE_KEY_PRODUCTION` | *site keys* do Turnstile |
| variável (opcional) | `IMAGES_ORIGIN_HOMOLOG`, `IMAGES_ORIGIN_PRODUCTION` | `https://img-homolog.{dominio}`, `https://img.{dominio}` (padrão se ausentes) |
| secret | `CLOUDFLARE_API_TOKEN`, `CLOUDFLARE_ACCOUNT_ID` | token do Pages e Account ID |
| secret | `EXPO_TOKEN` | token do EAS |

Com a regra "só tags `v*`", um *Run workflow* manual de produção (deploy ou app web) precisa ser disparado a partir de uma tag `v*`, não da `main`.

O `ssh-keyscan` precisa ser conferido: compare a impressão digital com a que aparece no console serial da Oracle (ou
na primeira conexão SSH de §3). Sem `known_hosts` o deploy não roda, de propósito.

Os pacotes do ghcr.io (`almoseis-api`, `-web`, `-migrator`, `-postgres`) nascem privados e ligados ao repositório. O
deploy usa o `GITHUB_TOKEN` do job, então nada mais é preciso. Para puxar à mão (plano B): um *fine-grained token*
com `read:packages`, usado só na hora e depois apagado.

## 7. Preencher o `.env` de cada VM

```bash
ssh deploy@<IP>
nano /opt/almoseis/.env      # já tem o modelo; chmod 600 já aplicado
```

Seguir os comentários do arquivo (`infra/vm/.env.example`). O mínimo para subir:

- `DEPLOY_ENVIRONMENT`, `ASPNETCORE_ENVIRONMENT` (`Production` / `Staging`), `API_DOMAIN`, `WEB_DOMAIN`, `ACME_EMAIL`;
- limites de memória e `PG_*` (os valores do arquivo são de produção; os de homologação estão no comentário);
- `POSTGRES_SUPERUSER_PASSWORD` e as três `ALMOSEIS_*_DB_PASSWORD` (`openssl rand -hex 32`);
- `WALG_S3_PREFIX=s3://almoseis-pg-backup-<ambiente>/pg16`, `WALG_AWS_ENDPOINT=https://<account-id>.r2.cloudflarestorage.com`,
  `WALG_AWS_ACCESS_KEY_ID`/`SECRET`, `WALG_LIBSODIUM_KEY` (`openssl rand -hex 32`);
- `Jwt__SigningKeyPem` (`openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048`), entre aspas;
- `Cors__RestaurantAppOrigins__0=https://app.{dominio}`, `Cors__PublicPageOrigins__0=https://{dominio}`;
- `Turnstile__SecretKey`, `PublicPage__IpHashSecret`, `PublicPage__TrackingTokenSecret`, `PublicPage__RendererKey`,
  `Maps__AddressHashSecret` (`openssl rand -hex 32`, todos diferentes);
- `Email__*` (SMTP do Brevo/SES), `Storage__R2__*` (bucket de imagens), `Billing__Asaas__*`;
- `PasswordReset__LinkBaseUrl`, `Invitations__LinkBaseUrl`;
- `BACKUP_HEARTBEAT_URL`, `ARCHIVE_HEARTBEAT_URL` (§9).

**Salvar o `.env` inteiro no cofre de senhas.** Sem a `WALG_LIBSODIUM_KEY`, nenhum backup abre.

## 8. Primeiro deploy

1. **Homologação:** fazer merge na `main` (ou *Actions → deploy → Run workflow → homolog*). O workflow roda a CI,
   publica as imagens, envia `infra/` para a VM, roda `deploy.sh` (migrations com `almoseis_migrator`, `up`, smoke) e,
   no fim, o app web no Pages.
2. Na VM, conferir:

   ```bash
   bash /opt/almoseis/infra/vm/scripts/dc.sh ps                 # todos healthy
   bash /opt/almoseis/infra/vm/scripts/dc.sh logs --tail 50 api
   bash /opt/almoseis/infra/vm/backup/walg-backup.sh            # primeira base, sem esperar as 03:30
   bash /opt/almoseis/infra/vm/backup/restore.sh list           # a base aparece no R2
   bash /opt/almoseis/infra/vm/backup/check-archive.sh; echo $? # 0 = archive em dia
   ```

   **Confirmar que o R2 aceita o WAL-G** (o teste local usou um S3 compatível, não o R2): `backup-list` mostra a base
   e `failed_count` de `pg_stat_archiver` fica em 0. Se aparecer erro de assinatura ou checksum, registrar e trocar
   o destino (Q-0060-1, opção b).
3. Rodar `restore.sh test` em homologação (o primeiro ensaio).
4. **Produção:** criar a tag `v0.1.0` → o deploy espera a aprovação no Environment `production` → aprovar.
5. `restore.sh test` em produção e registro em `infra/RESTORE-LOG.md`.

Se o primeiro certificado falhar (DNS ainda propagando), o Caddy tenta de novo sozinho. Para não estourar o limite do
Let's Encrypt em testes repetidos, use `CADDY_GLOBAL_OPTIONS=acme_ca https://acme-staging-v02.api.letsencrypt.org/directory`
até dar certo e depois esvazie a variável.

## 9. Monitoramento

UptimeRobot ou Better Stack (planos gratuitos), com o e-mail do responsável técnico:

- HTTP(S) `https://api.{dominio}/health/ready` (palavra-chave `Healthy`), `https://{dominio}/` e
  `https://app.{dominio}/`, a cada 1 a 5 min (os de homologação também, com intervalo maior);
- vencimento de SSL dos domínios;
- *heartbeat* "backup diário" com período de 1 dia e tolerância de 2 h → URL em `BACKUP_HEARTBEAT_URL`;
- *heartbeat* "archive de WAL" com período de 5 min e tolerância de 10 min → URL em `ARCHIVE_HEARTBEAT_URL`
  (o UptimeRobot só tem *heartbeat* no plano pago: use o Better Stack ou o Healthchecks.io para estes dois).

Na Oracle: *Observability → Alarms*, alarme de CPU da instância > 80% por 15 min (gratuito), por e-mail.

Testar antes do piloto: `bash scripts/dc.sh stop api` em homologação → e-mail em poucos minutos → `up -d api`.

## 10. Ordem de deploy do EP-003 (versão mínima do app)

O app antigo recebe `422` no cadastro (fatia 1) e na entrega de marmita (fatia 4) das APIs do EP-003. A versão mínima
precisa subir **na mesma release** que leva essas APIs a produção (`docs/09-devops.md` §9.4):

1. Publicar o app nativo com o EP-003 (`git tag mobile-vX.Y.Z && git push --tags` → `mobile-release.yml`) e esperar a
   aprovação das lojas.
2. Na VM de produção, editar `/opt/almoseis/.env`: `App__MinSupportedAppVersion__ANDROID` e `__IOS` com a versão do
   passo 1. Não rodar nada: o deploy aplica.
3. Criar a tag `vX.Y.Z` do backend e aprovar o deploy. O `deploy.sh` sobe a API nova **com** a versão mínima nova, e o
   job `app-web` publica o app web depois da API.
4. Com o app web no ar: `App__MinSupportedAppVersion__WEB` no `.env` e `bash scripts/dc.sh up -d api`.
5. Rodar a correção pós-deploy da 0008 (`docs/09-devops.md` §9.4) com o login `almoseis_migrator`.

## 11. Operação do dia a dia

| Tarefa | Como |
|---|---|
| Ver estado e logs | `bash scripts/dc.sh ps`, `bash scripts/dc.sh logs -f --tail 100 api` |
| Voltar a API e a página à versão anterior | `bash scripts/rollback.sh` |
| Mudar uma configuração | editar `/opt/almoseis/.env` → `bash scripts/dc.sh up -d api web` |
| Trocar senha do banco | editar o `.env` → `bash scripts/db-roles.sh` → `bash scripts/dc.sh up -d api` |
| psql de administração | `bash scripts/dc.sh exec postgres psql -U postgres -d almoseis`, ou túnel `ssh -L 5432:127.0.0.1:5432 deploy@<vm>` |
| Atualizar o Docker (mensal) | `sudo apt-get update && sudo apt-get install --only-upgrade docker-ce docker-ce-cli containerd.io docker-compose-plugin` (o `live-restore` mantém os contêineres) |
| Atualizar PostgreSQL *minor* / Caddy | mudar o Dockerfile do PostgreSQL (rebuild pelo deploy) ou `CADDY_IMAGE` no `.env` |
| Restaurar | `infra/vm/restore.md` |
| Ensaio trimestral | `bash backup/restore.sh test` + `infra/RESTORE-LOG.md` |

## 12. Gatilho de migração

Reavaliar a infra (ADR-0060 item 10) com 50 estabelecimentos, com necessidade de alta disponibilidade, com receita
que pague a infra gerenciada, com recurso no limite por 2 semanas, com risco do provedor ou com exigência de
conformidade. O alvo futuro está em `docs/09-devops.md` §9.10.
