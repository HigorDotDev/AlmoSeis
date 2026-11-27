# ADR-0060 — Infraestrutura enxuta do piloto: VM única por ambiente (Oracle Cloud São Paulo) com Docker Compose

- **Status:** Proposto
- **Data:** 2026-11-27
- **Origem:** decisão do usuário em 2026-11-27: o desenho Azure do ADR-0037 custa cerca de R$ 1.000/mês, o que não cabe no início. Precisamos de uma infra de baixo custo, com qualidade, para até ~30 estabelecimentos, que depois migre para algo robusto
- **Relacionados:** ADR-0037 (suspenso para o piloto), ADR-0040 (provedor de e-mail trocado), ADR-0051 item 6 e ADR-0053 §2 (hospedagem da página e do app web), ADR-0024 (dados no Brasil), ADR-0026 (papéis de banco), ADR-0035 (versão mínima), ADR-0042 (migrations SQL-first), ADR-0009 (SignalR em 1 réplica)

## Contexto

O ADR-0037 escolheu serviços gerenciados da Azure (Container Apps com 1 réplica sempre ativa, PostgreSQL Flexible Server com PITR, Key Vault, Application Insights, ACS para e-mail). Para o piloto (10 estabelecimentos, com folga até ~30), esse custo fixo vem antes de qualquer receita: a assinatura do piloto é `MonthlyPrice = 0`.

A carga do piloto é pequena: dezenas de pedidos por estabelecimento por dia, picos no almoço, um punhado de conexões SignalR. Cabe com folga numa VM pequena. O que não pode piorar em relação ao ADR-0037:

- **dados financeiros recuperáveis**: restauração ponto a ponto, ensaiada antes do piloto (RNF-005);
- **dados no Brasil** (ADR-0024): computação e banco em região brasileira;
- **segurança mínima**: papéis de banco separados (ADR-0026), segredos fora do repositório, TLS, atualizações de segurança;
- **SignalR**: processo sempre ativo, sem *scale-to-zero* (ADR-0009, ADR-0037).

A Oracle Cloud oferece, no plano Always Free, 4 OCPU Ampere A1 (ARM64) e 24 GB de RAM por conta, 200 GB de block volume e 10 TB/mês de saída, e tem região em São Paulo (`sa-saopaulo-1`). Numa conta *pay-as-you-go*, os recursos Always Free continuam gratuitos e não estão sujeitos à recuperação de instância ociosa das contas só gratuitas.

## Decisão

### 1. Ambientes

| Ambiente | Onde | Tamanho |
|---|---|---|
| `local` | máquina do dev (inalterado: `docker-compose.yml` da raiz + `dotnet run`) | — |
| `homolog` | 1 VM Oracle Cloud `VM.Standard.A1.Flex`, `sa-saopaulo-1` | 1 OCPU, 6 GB, boot 50 GB |
| `prod` | 1 VM Oracle Cloud `VM.Standard.A1.Flex`, `sa-saopaulo-1` | 3 OCPU, 18 GB, boot 100 GB |

- Os dois cabem juntos no Always Free (4 OCPU, 24 GB, 150 de 200 GB). A conta é *pay-as-you-go*, com alerta de orçamento em R$ 1, para nenhum recurso pago nascer por engano.
- Ubuntu 24.04 ARM64. A VM é preparada por `infra/vm/bootstrap-vm.sh`, que instala Docker, firewall, usuário `deploy` só com chave SSH, swap, fail2ban e atualizações automáticas de segurança.
- Os ambientes não compartilham nada: VM, banco, bucket de backup, bucket de imagens, chave JWT, segredos, conta Asaas (sandbox em homologação).
- **Plano B de restauração:** uma VPS paga com datacenter no Brasil, criada só quando for necessária (perda da conta Oracle, falta de capacidade na região). O mesmo `bootstrap-vm.sh` (com `ufw`) e o mesmo `restore.sh` restauram nela a partir do bucket. As imagens são multi-arch (`linux/amd64` e `linux/arm64`), então servem em VPS x86.

### 2. Cada VM: Docker Compose (`infra/vm/docker-compose.yml`)

| Serviço | Imagem | Papel |
|---|---|---|
| `caddy` | `caddy:2.10-alpine` | TLS automático (Let's Encrypt) e proxy reverso para `api` e `web`. Única porta exposta: 80/443 |
| `api` | `ghcr.io/<owner>/almoseis-api:<sha>` | API .NET 10 + SignalR em processo, 1 instância sempre ativa |
| `web` | `ghcr.io/<owner>/almoseis-web:<sha>-<ambiente>` | Página pública Next.js (*standalone*) |
| `postgres` | `ghcr.io/<owner>/almoseis-postgres:16-walg-<hash>` | PostgreSQL 16 + WAL-G, papéis de 05 §5.1, porta só no `127.0.0.1` da VM |
| `migrator` (ferramenta) | `ghcr.io/<owner>/almoseis-migrator:<sha>` | *Bundle* das migrations (ADR-0042), rodado pelo deploy com `almoseis_migrator_login` |

- Limites de memória por serviço, rotação de log do Docker (10 MB × 5), `no-new-privileges`, `cap_drop: ALL` na API, na página e no Caddy, e usuários não-root nas imagens próprias.
- O único proxy confiável para `X-Forwarded-For` é a sub-rede do Compose (`ForwardedHeaders:KnownNetworks`), para o limite por IP (ADR-0041) ver o cliente real.
- **Sem Cloudflare na frente (DNS *only*)** da API e da página no piloto: o Caddy emite o certificado e vê o IP real. O proxy da Cloudflare fica para quando for preciso (exige `trusted_proxies` no Caddy).
- Um domínio por ambiente: `api.{dominio}` + `{dominio}` (produção), `api-homolog.{dominio}` + `homolog.{dominio}` (homologação), como no ADR-0051 item 8.

### 3. App do restaurante no navegador (complementa o ADR-0053 §2)

O build estático (`expo export --platform web`) vai para o **Cloudflare Pages** (plano gratuito), em `app.{dominio}` e `app-homolog.{dominio}`, no lugar do Azure Static Web Apps. Os cabeçalhos do ADR-0053 §2 (CSP, `noindex`, cache) ficam em `infra/pages/_headers`. A ordem do ADR-0053 §6.4 é garantida pelo pipeline: o job do Pages roda depois do deploy da API.

### 4. Imagens e deploy

- Imagens no **ghcr.io**, multi-arch, construídas no GitHub Actions. A API e o *bundle* por compilação cruzada (sem .NET emulado); a página na arquitetura do runner. As variáveis `NEXT_PUBLIC_*` entram no build, então a imagem da página é **por ambiente**.
- **Deploy por SSH** (`.github/workflows/deploy.yml`): CI → imagens → envio de `infra/vm` e `infra/db` → `deploy.sh` na VM (pull, migrations com `almoseis_migrator`, `up`, *smoke* de `/health/ready` e da página pelo Caddy local; volta às imagens anteriores se o *smoke* falhar) → *smoke* externo → app web no Pages.
- Homologação automática a cada push na `main`; produção por tag `v*` com aprovação no GitHub Environment `production`.
- O ghcr.io é acessado na VM com o `GITHUB_TOKEN` do job, que vence no fim do job. Nenhum token de longa duração fica na VM.

### 5. Segredos e configuração

- **Sem Key Vault no piloto.** A API lê a configuração só de variáveis de ambiente (`Secao__Chave`), e `KeyVault:Uri` vazio desliga o cofre. Tabela completa em `docs/09-devops.md` §9.6.
- Os segredos ficam em **`/opt/almoseis/.env` na VM, com permissão 600**, dono `deploy`, e nunca no repositório. Uma cópia fica no cofre de senhas do responsável técnico. Sem essa cópia, a perda da VM leva junto a chave de criptografia dos backups.
- GitHub Environment *secrets* só para o deploy (chave SSH e `known_hosts`). As senhas do banco e a connection string do migrator **não** passam pelo GitHub: o *migrator* roda na VM, com o `.env` dela.
- `appsettings*.json` continuam só com *placeholders* (02 §2.5).

### 6. Banco, backup e restauração

- PostgreSQL 16 com os papéis de 05 §5.1, criados no primeiro `initdb` por `infra/db/create-logins.sql`, com senhas do `.env`. Nenhum login é `SUPERUSER`, `CREATEROLE` nem `BYPASSRLS`.
- **WAL-G** com *archive* contínuo de WAL (`archive_timeout = 60 s`, **RPO ~1 min**) e **base completa diária** às 03:30, retenção de **14 bases** (PITR de 14 dias, igual ao ADR-0037), **criptografia no cliente** (libsodium) e compressão brotli.
- Destino: **Cloudflare R2**, um bucket por ambiente (`almoseis-pg-backup-homolog`, `almoseis-pg-backup-prod`), com token de API restrito ao bucket. O bucket fica **fora da Oracle**, para o backup sobreviver à perda da conta.
- **RTO < 1 h:** `infra/vm/backup/restore.sh` restaura num volume novo (produção) ou num PostgreSQL à parte (ensaio). O ensaio é obrigatório antes do piloto e a cada trimestre, registrado em `infra/RESTORE-LOG.md`.

### 7. E-mail (altera o ADR-0040)

E-mail transacional por **SMTP** (Brevo no plano gratuito, ou Amazon SES em `sa-east-1`), com o domínio verificado (SPF, DKIM e DMARC). O ACS continua suportado pela API (`Email:Provider = Acs`) para o alvo futuro. O resto do ADR-0040 não muda: só recuperação de senha, `202` sempre e link de uso único.

### 8. Monitoramento

- **Externo:** UptimeRobot ou Better Stack (planos gratuitos), com monitor HTTP de `https://api.{dominio}/health/ready` e da página, e *heartbeats* do backup diário e do *archive* de WAL (`backup/check-archive.sh`, a cada 5 min). Os avisos chegam por e-mail.
- **Erros e telemetria (opcional):** OpenTelemetry por OTLP (`Otel:Endpoint`) para um plano gratuito (Grafana Cloud, Better Stack ou o endpoint OTLP do Sentry). Vazio = só `docker logs`.
- Logs do Docker com rotação. O Caddy **não** grava log de acesso, porque a URL do acompanhamento leva o token do pedido (ADR-0024, ADR-0047). O PostgreSQL não grava parâmetros de consulta (`log_parameter_max_length = 0`).

### 9. O que continua valendo do ADR-0037

GitHub Actions; ghcr.io; Cloudflare R2 para as fotos de produto (bucket próprio, separado do backup); computação e banco no Brasil; PITR de 14 dias e ensaio de restauração; papéis de banco separados; API sempre ativa, sem *scale-to-zero*; 1 instância com SignalR em processo (Azure SignalR Service só com mais de uma réplica); OpenTelemetry (agora com exportador opcional); EAS para o app nativo; ambientes `local`, `homolog` e `prod`.

**Suspensos no piloto:** Azure Container Apps, PostgreSQL Flexible Server, Key Vault, Application Insights, Bicep (`infra/bicep`), ACS (ADR-0040), Static Web Apps (ADR-0053 §2) e o Container App `almoseis-web` (ADR-0051 item 6). Ficam como **alvo futuro**, resumido em `docs/09-devops.md` §9.10.

### 10. Gatilho de migração para infra gerenciada

Abrir o ADR de migração (o ADR-0037 volta, revisado, ou outro provedor) quando **qualquer** um destes acontecer:

1. **50 estabelecimentos ativos** pagantes, ou a meta de crescimento indicar 50 em até 3 meses;
2. **alta disponibilidade** exigida: um contrato com SLA, mais de uma réplica da API (Azure SignalR Service), ou dois incidentes com mais de 1 h fora do ar no mesmo trimestre;
3. **receita** que pague a infra gerenciada com folga: receita recorrente mensal de pelo menos 3 vezes o custo estimado dela (~R$ 3.000/mês, com o desenho do ADR-0037);
4. **recurso no limite** por 2 semanas: CPU p95 da VM de produção acima de 70%, memória acima de 80%, ou disco acima de 70%;
5. **risco do provedor:** aviso de encerramento ou suspensão da conta Oracle, ou falta de capacidade A1 que impeça recriar a VM;
6. **exigência de conformidade** que a VM única não atende (por exemplo, backup exigido no Brasil por contrato, ou auditoria de acesso).

A migração é tranquila: as mesmas imagens, as mesmas migrations e a mesma configuração por variáveis, um `pg_dump`/restauração ou réplica lógica para o banco gerenciado, e o DNS trocado no fim.

### Questão em aberto (decisão do usuário antes do piloto)

**Q-0060-1: backup cifrado fora do Brasil.** O Cloudflare R2 não tem região nem jurisdição no Brasil: as opções de localização são América do Norte, Europa, Ásia-Pacífico e Oceania. O backup do banco tem dados pessoais (nomes, telefones e endereços de clientes), e o ADR-0024 item 5 diz "os dados ficam no Brasil". Mitigação já implementada: criptografia no cliente (libsodium), com a chave só na VM e no cofre de senhas, de modo que a Cloudflare só guarda dados cifrados. Opções:

- **(a) R2 com criptografia** (o que está implementado, decisão do usuário): o backup sobrevive à perda da conta Oracle. Exige confirmar com o jurídico que o backup cifrado fora do país é compatível com a LGPD (art. 33) e com o contrato de tratamento, e registrar isso no ADR-0024.
- **(b) Oracle Object Storage em `sa-saopaulo-1`** (20 GB no Always Free, API compatível com S3): fica no Brasil, mas na mesma conta da VM, então perder a conta é perder o backup. Mudar é só trocar `WALG_S3_PREFIX`, `WALG_AWS_ENDPOINT` e as chaves no `.env`.
- **(c) as duas:** a base diária copiada para um segundo destino. É mais trabalho de operação.

Recomendação do agente: (a), com a revisão jurídica junto com a dos Termos (checklist do piloto). Se o jurídico vetar, (b).

## Alternativas consideradas

- **Azure gerenciado (ADR-0037):** PITR, alta disponibilidade e segredos gerenciados, mas ~R$ 1.000/mês antes da receita. É o alvo futuro (gatilho no item 10).
- **VPS paga no Brasil como primária (~R$ 60 a 150/mês):** mesmo desenho, sem a dependência do Always Free. Fica como plano B de restauração, e vira a primária se a Oracle falhar.
- **Hetzner, Contabo ou DigitalOcean fora do Brasil:** mais baratos e estáveis, mas banco e computação fora do país contrariam o ADR-0024 e aumentam a latência.
- **PaaS com região em São Paulo (Fly.io, Render, Railway) e banco gerenciado barato (Supabase, Neon):** menos operação, mas o custo cresce por serviço, os planos gratuitos pausam por inatividade (incompatível com o SignalR sempre ativo), e os papéis próprios com RLS `FORCE` (ADR-0026) ficam limitados ou caros.
- **Kubernetes leve (k3s) na VM:** sem ganho para 1 instância por serviço, e mais operação (as diretrizes de DevOps proíbem sem ADR).
- **Backup só com `pg_dump` diário:** RPO de 24 h, inaceitável para dados financeiros. O WAL-G dá RPO de ~1 min pelo mesmo custo.

## Consequências

- **Sem alta disponibilidade.** Uma VM por ambiente: reinício de atualização de segurança (às 04:30, fora do horário dos restaurantes), falha da VM ou da zona derrubam o serviço até a recuperação (RTO < 1 h, se houver alguém para agir; o piloto não tem plantão). O app suporta a queda curta (rascunhos locais, ADR-0010).
- **A operação é nossa:** atualizar o Docker e as imagens base (PostgreSQL *minor*, Caddy, .NET) todo mês, acompanhar os avisos do monitor, conferir o backup e ensaiar a restauração a cada trimestre.
- **Riscos do provedor gratuito:** a Oracle pode encerrar ou suspender contas, e a região às vezes fica sem capacidade A1 para criar a VM. A mitigação é o backup fora da Oracle e o plano B (VPS no Brasil + `restore.sh`).
- **ARM64:** toda imagem precisa ser multi-arch. Isso já está no pipeline e foi testado no build local.
- **Segredos no disco da VM:** quem tem root na VM (ou o grupo `docker`) lê todos os segredos. O acesso é só por chave SSH, e o usuário `deploy` só existe para o GitHub Actions e o responsável técnico. As chaves do Data Protection ficam sem cifra no volume (risco aceito, 07 §7.11).
- **LGPD:** a computação e o banco ficam no Brasil. O backup cifrado no R2 é a Q-0060-1.
- **Custo estimado:** R$ 0 de infra, mais o domínio (~R$ 40 a 100/ano). Se o R2 passar de 10 GB, US$ 0,015/GB-mês. Se o e-mail passar de 300/dia, o plano do Brevo ou o SES (~US$ 0,10 por mil). O plano B custa ~R$ 60 a 150/mês só enquanto existir.
- `docs/09-devops.md` foi reescrito para este desenho. O Bicep (`infra/bicep`) não é criado no piloto. O `infra/README.md` é o *runbook* do primeiro deploy.
- **Revisitar:** no gatilho do item 10, ou se a Oracle mudar as regras do Always Free.
