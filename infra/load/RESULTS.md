# Teste de carga do pico do almoço — VM do piloto (ADR-0060)

> Pergunta: a VM única do piloto aguenta ~30 estabelecimentos no pico do almoço, com folga?
> **Resposta: sim.** Detalhes e números abaixo. Roteiro: `docs/09-devops.md` §9.11. ADR-0060 ainda **Proposto**.

## 1. Método

### Ambiente

| Item | Valor |
|---|---|
| Data | 2026-11-27, commit `ac0d7b1` (API com as métricas do outbox e o `/health/ready` degradado) |
| Máquina | notebook do dev: Intel Core i7-13620H (10 núcleos / 16 threads), 16 GB de RAM, Windows 11 + Docker Desktop 29.9 (WSL2, 12 CPUs e ~9,7 GB visíveis para o Docker) |
| Compose | `infra/vm/docker-compose.yml` (o mesmo da VM) + `infra/load/docker-compose.load.yml`, projeto `almoseis-loadtest`, imagens construídas do `git archive HEAD` (`run.sh build`) |
| "VM" simulada | **2 vCPUs** compartilhadas por `caddy`, `api`, `web` e `postgres` (`cpuset 0,1`, como numa VM de verdade) e **~3,75 GB** (API 1 GB, PostgreSQL 2 GB com `shared_buffers` 512 MB, página 512 MB, Caddy 256 MB) — mais apertado que a VM de produção (3 OCPU / 18 GB) e próximo da de homologação |
| API | `ASPNETCORE_ENVIRONMENT=Staging`, log `Information` (como produção), limites por IP **ligados** (ADR-0041), Turnstile **desligado** (`Turnstile__Enabled=false`, o modo de teste aceito pela validação: a chave de teste do Cloudflare só vale em Development e chamaria a internet), `Push__Provider=Fake`, `Maps__Provider=None`, `Email__Provider=None`, sem OTLP, pool do banco 40 |
| PostgreSQL | imagem `almoseis-postgres` (16 + WAL-G) com `WALG_ENABLED=false` (sem bucket: o envio do WAL ao R2 não entra na medida) |
| k6 | `grafana/k6` v2.4.0 em contêiner **na rede do compose**, nas CPUs 4–11 (fora da "VM"), falando direto com `api:8080` e `web:3000` |

**Por que direto na API, sem o Caddy:** a sub-rede do compose é o único proxy confiável da API (`ForwardedHeaders:KnownNetworks`), então o k6 manda `X-Forwarded-For` e cada aparelho e cada cliente da página tem o seu IP, como na vida real, **com os limites por IP ligados**. Pelo Caddy, todo o tráfego sairia de um IP só e esbarraria no limite de login (10/min). O Caddy sem TLS gasta ~0% de CPU nas medidas; com TLS de verdade ele custa um pouco mais (estimado em poucos % de 1 vCPU nessa vazão).

**Ressalva da CPU:** 2 threads de um i7 de 13ª geração (que podem ser os dois *hyperthreads* do mesmo núcleo) não são 3 OCPU Ampere A1. Por núcleo, o i7 é mais rápido que o Neoverse N1, mas a VM de produção tem 1,5× as vCPUs e núcleos físicos inteiros. Tratamos as duas como da mesma ordem de grandeza: por isso a folga abaixo é contada com margem.

### Semente (`infra/load/seed`)

Sem o autocadastro (exige CPF/CNPJ; o teste não gera documento de ninguém):

- **SQL, com os papéis da aplicação e a RLS ligada** (ADR-0026): `almoseis_app_login` (o login da API) com `app.tenant_id`/`app.user_id` na transação, como o `SignUpHandler`, grava `users`, `tenants` (documento = marcador inválido `99000000000NN`), `tenant_behaviors`, `tenant_labels`, `tenant_sync_counters`, `tenant_settings`, `tenant_employees` (+ papéis), `storefront_settings` e `store_slugs`; `almoseis_platform_login` grava a assinatura `ACTIVE` (quem muda o estado da assinatura é a plataforma, ADR-0017). Nenhum superusuário nem `BYPASSRLS`. As migrations rodaram com `almoseis_migrator_login` (`run.sh up`).
- **Senhas** geradas (20 caracteres aleatórios) com o **mesmo hasher da API** (`PasswordHasher<T>` do ASP.NET Core Identity), gravadas **só** em `infra/load/.out/seed.json` (no `.gitignore`).
- **Pela API, como o dono faria no app** (auditoria, feed e outbox pelo caminho normal): tamanhos de marmita, produtos, cardápios do dia `MARMITA` e `PF` (restaurante), cardápio fixo com categorias, pratos (com e sem tamanho), bebidas com sabores, combo e grupo de adicionais (lanchonete), página de pedidos ativa (só retirada) e expediente aberto.
- **30 estabelecimentos:** 18 restaurantes (rótulos `MARMITARIA`, `RESTAURANT`, `SELF_SERVICE`; 3 ou 4 aparelhos: dono, cozinha, caixa, atendente) e 12 lanchonetes (`BURGER_JOINT`, `PASTEL_SHOP`, `SNACK_BAR`, `ACAI_SHOP`; 2 ou 3 aparelhos). **93 aparelhos.** A semente leva ~3 min (o login da fase HTTP passa pelo Caddy e respeita o limite de 10/min por IP).

### Carga (`lunch-peak.js`)

**Volume:** 150 pedidos/dia por estabelecimento, 70% entre 11h e 14h (105), distribuídos por meia hora em 10/20/30/20/12/8%. Tempo **comprimido, taxa real:** cada meia hora vira 2 min de teste (a das 12h00, o pico, vira 3 min), sempre com as chegadas por hora do dia real; as esperas dentro de um pedido (incluir item, refeição, entrega) encolhem na mesma proporção (÷15).

| Meia hora | Pedidos por estab. | Total (30 estab.)/h | App (85%)/h | Site (15%)/h | Visitas sem compra (4 por pedido do site)/h |
|---|---:|---:|---:|---:|---:|
| 11h00 | 10,5 | 630 | 536 | 95 | 378 |
| 11h30 | 21 | 1.260 | 1.071 | 189 | 756 |
| **12h00 (pico)** | **31,5** | **1.890 (0,53/s)** | **1.607** | **284** | **1.134** |
| 12h30 | 21 | 1.260 | 1.071 | 189 | 756 |
| 13h00 | 12,6 | 756 | 643 | 113 | 454 |
| 13h30 | 8,4 | 504 | 428 | 76 | 302 |

O estresse (`LOAD_MULTIPLIER=3`) multiplica **tudo por estabelecimento**: 3× pedidos (5.670/h no pico, 1,6/s) e 3× aparelhos (279 sessões com SignalR). É mais duro que 90 estabelecimentos normais, porque concentra a disputa na trava do contador do feed de cada tenant (05 §5.14).

**O que cada um faz:**

- **Aparelho** (cenário `devices`, 1 VU por aparelho): login (espalhados no primeiro minuto), conexão SignalR (`negotiate` + WebSocket, protocolo JSON, *ping* a cada 15 s), e o que o app faz a cada evento (TanStack invalidado): lista de pedidos (no máx. a cada 2 s com evento, senão 30 s), "Novos do site" (30 s, ou no evento), painel da cozinha (2 s com evento, senão 60 s), resumo do caixa (perfil caixa), expediente atual (60 s); `/sync/changes` no ritmo do `syncEngine` (no máx. a cada 10 s quando chegou evento, senão a cada 60 s); renovação do token (`/auth/refresh`) e reconexão do hub antes de vencer. A **cozinha** avança `OPEN → IN_PROGRESS → COMPLETED`; o **dono** aceita os pedidos do site e registra o pagamento.
- **Pedido do app** (`app_orders`): marmita (50% no restaurante; 40% entrega com forma prevista), PF (25%, mesa), quilo (25%, pesagem), à la carte (lanchonete: 50% comanda, 30% entrega por telefone com taxa e troco, 20% retirada), com combos, sabores e adicionais de grupo. `Idempotency-Key` em toda escrita crítica (2% reenviam a mesma chave e conferem o `Idempotent-Replayed`), `If-Match` com a versão; em `409 VersionConflict` o fluxo recarrega e tenta de novo, como o app. 30% incluem item ou pesagem; recebimento em 1 ou 2 pagamentos (5% ficam no fiado); entrega/retirada marcadas (`completeProduction`).
- **Página pública:** visitas (SSR da página `web:3000/{slug}` + `GET /public/stores/{slug}`) e pedidos de retirada (`POST /public/stores/{slug}/orders` com `expectedTotal` calculado do cardápio público e `X-Turnstile-Token`, depois o acompanhamento 2×), cada cliente com IP e telefone próprios.

**Limites (thresholds):** p95 < 500 ms nas escritas, p95 < 300 ms nas leituras, `http_req_failed` < 0,5% (só os status esperados de cada chamada contam como sucesso; `409 VersionConflict` tratado como no app). Coleta: `docker stats` a cada ~3 s, amostra do outbox a cada 5 s e, no fim, os percentis de `processed_at − occurred_at` dos eventos de tempo real no banco (papel `almoseis_platform_login`).

## 2. Resultados

### Cenário normal (30 estabelecimentos, 93 aparelhos, ~14 min)

13.939 requisições (14,2/s em média no teste inteiro), **0,000% de falhas**, 100% dos *checks*, nenhum limite violado. 205 pedidos do app + 36 do site (36 aceitos), 255 pagamentos, 440 avanços de produção, 46 conflitos de versão resolvidos (cozinha × caixa no mesmo pedido, esperado), 8.234 eventos SignalR entregues.

| Grupo | n | p50 | p95 | p99 | máx (ms) |
|---|---:|---:|---:|---:|---:|
| **Escritas** | 1.459 | 22,5 | **116,2** | 151,8 | 254,2 |
| **Leituras** | 12.299 | 7,4 | **17,7** | 26,4 | 143,2 |
| Página (SSR Next.js) | 181 | 28,6 | 58,5 | 75,7 | 117,3 |

| Rota | n | p50 | p95 | p99 | máx (ms) |
|---|---:|---:|---:|---:|---:|
| `POST /auth/login` | 161 | 111,5 | 158,5 | 188,3 | 216,6 |
| `POST /orders` | 205 | 31,8 | 65,8 | 82,0 | 130,5 |
| `POST /orders/{id}/items` | 102 | 30,2 | 59,7 | 70,1 | 71,4 |
| `POST /orders/{id}/payments` | 255 | 17,4 | 29,7 | 36,9 | 50,6 |
| `PATCH /orders/{id}/status` | 440 | 19,5 | 37,3 | 47,9 | 78,3 |
| `POST /orders/{id}/accept` | 36 | 29,1 | 50,7 | 70,0 | 70,4 |
| `POST /public/stores/{slug}/orders` | 36 | 43,6 | 73,6 | 193,0 | 254,2 |
| `GET /sync/changes` | 3.237 | 5,4 | 10,9 | 15,2 | 43,0 |
| `GET /orders` | 3.230 | 9,3 | 18,2 | 26,4 | 70,0 |
| `GET /production/orders` | 1.204 | 10,3 | 19,9 | 29,7 | 71,6 |
| `GET /closing/summary` | 593 | 11,9 | 25,8 | 32,7 | 65,3 |
| `GET /public/stores/{slug}` | 181 | 14,8 | 28,4 | 32,2 | 34,1 |

**Recursos** (100% = 1 vCPU; a "VM" tem 200%):

| Contêiner | CPU média | CPU p95 | CPU máx | Memória máx |
|---|---:|---:|---:|---:|
| api | 14% | 31% | 61% | 228 MiB (de 1 GiB) |
| postgres | 8% | 17% | 49% | 112 MiB (de 2 GiB) |
| web | 3% | 22% | 48% | 57 MiB (de 512 MiB) |
| caddy | 0% | 3% | 4% | 12 MiB |
| **VM (soma)** | **12% das 2 vCPUs** | **25%** | 61% (pico de 1 amostra) | 409 MiB |

**Outbox:** eventos de tempo real (1.480 no intervalo) com `processed_at − occurred_at` **p50 142 ms, p95 249 ms, p99 261 ms, máx 278 ms**; nunca mais de 6 pendentes. O dispatcher varre a cada 250 ms (`Outbox:PollMilliseconds`), que é quase todo o atraso. No cliente (k6), `occurredAt → evento recebido`: p50 155 ms, p95 268 ms.

Uma primeira rodada com o commit anterior (`f397229`, antes das métricas) deu números equivalentes (p95 escritas 129,6 ms, leituras 20,5 ms, VM 15% média / 30% p95) e 0,079% de falhas, todas `401` do teste (o token de 15 min da sessão de atendimento do k6 vencia no meio de um pedido longo; o script passou a renovar a cada passo). As métricas novas do ac0d7b1 não mudaram o custo de forma mensurável.

### Estresse 3× (30 estabelecimentos com 3× pedidos e 3× aparelhos)

64.300 requisições (65,8/s em média no teste inteiro), **0,008% de falhas** (5 de 64.300, todas `429` no `POST /auth/login`: artefato do teste, ver §4.5), 99,992% dos *checks*, nenhum limite violado. 616 pedidos do app + 108 do site (108 aceitos), 773 pagamentos, 1.247 avanços de produção, 810 conflitos de versão resolvidos (3 sessões de cozinha por estabelecimento disputando os mesmos pedidos, §4.3), 73.345 eventos SignalR para 279 conexões.

| Grupo | n | p50 | p95 | p99 | máx (ms) |
|---|---:|---:|---:|---:|---:|
| **Escritas** | 4.916 | 14,5 | **60,4** | 74,8 | 110,5 |
| **Leituras** | 58.841 | 6,0 | **14,8** | 22,8 | 66,0 |
| Página (SSR) | 543 | 17,8 | 40,9 | 56,1 | 67,7 |

| Rota | n | p50 | p95 | p99 | máx (ms) |
|---|---:|---:|---:|---:|---:|
| `POST /auth/login` | 424 | 60,7 | 84,3 | 98,3 | 110,5 |
| `POST /orders` | 616 | 20,7 | 46,2 | 62,9 | 108,9 |
| `POST /orders/{id}/payments` | 773 | 10,4 | 23,1 | 36,7 | 63,8 |
| `PATCH /orders/{id}/status` | 1.920 | 13,1 | 30,1 | 42,1 | 68,4 |
| `POST /public/stores/{slug}/orders` | 108 | 26,7 | 62,7 | 78,4 | 90,2 |
| `GET /sync/changes` | 16.718 | 3,9 | 9,5 | 14,8 | 42,7 |
| `GET /orders` | 18.152 | 7,1 | 15,5 | 22,8 | 57,2 |
| `GET /production/orders` | 8.240 | 8,3 | 18,3 | 26,7 | 63,1 |
| `GET /closing/summary` | 3.131 | 7,8 | 19,5 | 31,2 | 66,0 |

| Contêiner | CPU média | CPU p95 | CPU máx | Memória máx |
|---|---:|---:|---:|---:|
| api | 30% | 45% | 95% | 250 MiB |
| postgres | 19% | 34% | 38% | 139 MiB |
| web | 1% | 14% | 20% | 63 MiB |
| caddy | 0% | 0% | 0% | 11 MiB |
| **VM (soma)** | **25% das 2 vCPUs** | **40%** | 53% | 463 MiB |

**Outbox:** 4.374 eventos de tempo real, **p50 136 ms, p95 250 ms, p99 259 ms, máx 312 ms**, no máximo 4 pendentes, vazão de até 13 msg/s.

As latências do 3× saíram **menores** que as do normal: com mais carga, o processo fica quente (JIT, *tiered compilation*, conexões do pool abertas) e a CPU do notebook sai do modo de economia. O que importa é que nenhuma rota chegou perto do limite e a CPU da "VM" ficou em 40% no p95.

### Extra: 6× (para achar o joelho)

Mesmo roteiro com `LOAD_MULTIPLIER=6` (1.232 pedidos do app + 217 do site em ~14 min, 11.340 pedidos/h no pico, 558 sessões planejadas). 144.443 requisições (147,7/s em média), **nenhum limite violado**: escritas p95 **67 ms** (p99 132 ms, máx 1,07 s), leituras p95 **26 ms** (p99 55 ms, máx 834 ms), outbox p95 252 ms (máx 567 ms, até 13 pendentes). `http_req_failed` 0,116%: 167 são `429` do `POST /auth/login` (o mesmo usuário entrando em 6 aparelhos passa do limite de 5 logins/min por login, ADR-0041; artefato do teste, §4.5) e 2 tempos esgotados de rede no k6; nenhum 5xx. Por causa desses 429, até ~150 das 558 sessões de aparelho não entraram nesta rodada (as leituras ficaram em ~4,5× em vez de 6×; os pedidos, sim, em 6×). O script já foi corrigido para repetir o login depois do `Retry-After`, como o app.

| Contêiner | CPU média | CPU p95 | CPU máx | Memória máx |
|---|---:|---:|---:|---:|
| api | 50% | 73% | 94% | 291 MiB |
| postgres | 39% | 66% | 96% | 320 MiB |
| web | 2% | 15% | 38% | 75 MiB |
| **VM (soma)** | **45% das 2 vCPUs** | **73%** | 96% | 697 MiB |

Aqui a "VM" encosta no teto em picos curtos (96%): é onde aparecem as caudas de 0,5 a 1 s. É o começo do joelho.

## 3. Conclusão e folga

**A VM do piloto aguenta 30 estabelecimentos no pico do almoço com muita folga**, mesmo numa "VM" menor que a de produção (2 vCPUs de notebook e ~4 GB, contra 3 OCPU e 18 GB):

| Cenário | Equivale a | CPU da VM (média / p95) | Escritas p95 | Leituras p95 | Outbox p95 | Falhas reais |
|---|---|---:|---:|---:|---:|---:|
| normal | 30 estabelecimentos | 12% / 25% | 116 ms | 18 ms | 249 ms | 0 |
| 3× | ~90 estabelecimentos (mais duro: 3× por tenant) | 25% / 40% | 60 ms | 15 ms | 250 ms | 0 |
| 6× | ~150–180 estabelecimentos | 45% / 73% | 67 ms | 26 ms | 252 ms | 0 (só 429 de login do teste) |

- **Folga estimada:** o custo cresce de forma quase linear (25% → 40% → 73% de CPU no p95). O gatilho do ADR-0060 item 10.4 é CPU p95 > 70% por 2 semanas; nesta "VM" de 2 vCPUs ele seria atingido por volta de **5× a 6× a carga de 30 estabelecimentos, ou seja, ~150 estabelecimentos**. Descontando a diferença de CPU (Ampere A1 é mais lento por núcleo que o i7, mas a produção tem 3 núcleos físicos inteiros) e deixando margem, a estimativa conservadora para a VM de produção é **≥ 90 estabelecimentos dentro de todos os limites e com CPU p95 abaixo de 50%**, e ~150 antes de chegar ao gatilho de CPU. **Memória não é limite:** no 6× a soma não passou de 700 MiB dos ~4 GB.
- O gatilho de **número de estabelecimentos** do ADR-0060 (50 ativos) chega muito antes do de **recurso**. A capacidade da VM não é o que vai forçar a migração.
- **Homologação** (1 OCPU / 6 GB) deve aguentar o normal (25% de 2 vCPUs no p95 ≈ 50% de 1), mas sem folga para estresse: dá para conferir com `LOAD_CPUSET=0`.
- **O que este teste não cobre:** TLS no Caddy (CPU extra pequena), envio do WAL ao R2 (WAL-G desligado), latência de rede real até São Paulo (somar o RTT do celular ao que está acima), a base diária do WAL-G às 03:30 (fora do almoço), um mês de histórico no banco (o banco do teste tem só o dia), Turnstile real (chamada ao Cloudflare, com tempo-limite de 3 s, fora da transação) e o tráfego estático do app web (Cloudflare Pages).

## 4. Gargalos e observações (sem correção no código; para o backend avaliar)

1. **Nenhum gargalo até 3×.** Todas as rotas ficaram abaixo de 120 ms no p95 nas escritas e de 30 ms nas leituras; nenhum erro 5xx, nenhuma consulta acima de 500 ms no log do PostgreSQL (`log_min_duration_statement=500` no teste) e o outbox nunca acumulou.
2. **O que cresce primeiro é a CPU, dividida entre a API (~55%) e o PostgreSQL (~40%).** No 6×, o PostgreSQL chegou a 96% de 1 vCPU em picos. O tráfego é **~90% leitura**, e a maior parte é o refetch que o app faz a cada evento do SignalR: cada pedido gera ~6 eventos, e cada aparelho do estabelecimento recarrega a lista de pedidos ou o painel da cozinha (no 6×: 46 mil `GET /orders`, 24 mil `GET /production/orders`, 32 mil `GET /sync/changes`). Sugestões para quando o crescimento pedir (não agora): (a) no app, usar o `data` do evento para atualizar o card em vez de recarregar a lista inteira, ou agrupar os refetches (o app já faz no máx. 1 a cada 2 s no teste); (b) no backend, conferir os planos de `GET /orders` e `GET /production/orders` com um mês de pedidos no banco (índices por `business_day_id`/`acceptance_status`); (c) o `GET /closing/summary` é a leitura mais cara por chamada no 6× (p99 77 ms) e é recarregado a cada pagamento no perfil caixa.
3. **Conflitos de versão são frequentes quando há vários aparelhos de cozinha** (46 no normal; 810 no 3× e 4.404 no 6×, com 3 e 6 sessões de cozinha por estabelecimento disputando os mesmos pedidos). O servidor responde rápido (`409` com `current`) e o fluxo do app resolve; não é gargalo, mas mostra que duas cozinhas tocando o mesmo pedido vão ver "alterado por outra pessoa" com frequência. Assunto de UX, não de infra.
4. **Atraso do outbox:** p95 ~250 ms em todos os cenários, quase todo vindo da varredura de 250 ms do dispatcher (`Outbox:PollMilliseconds`); a vazão máxima medida (24 msg/s) está longe do limite do lote (100 a cada 250 ms). O alerta de 3 s (§9.8) tem margem de 10×. No cliente, o p99 de ~5,2 s é o `ClosingUpdated`, que o servidor agrupa de propósito em no máximo 1 a cada 5 s por tenant (`Realtime.cs`), e não atraso. O lembrete agendado do push (`WebOrderPushRequested`, ~180 s depois) também não é atraso: a métrica e o `/health/ready` do ac0d7b1 já excluem as agendadas (`available_at` no futuro); uma consulta ingênua por `occurred_at` acusaria 180 s (a amostra do `run.sh` foi corrigida para usar `coalesce(available_at, occurred_at)`, como o `OutboxBacklogMonitor`).
5. **Limite de login por usuário (5/min, ADR-0041):** no 3× e no 6× o mesmo usuário entrou em 3 e 6 aparelhos no mesmo minuto e recebeu `429`. Na vida real, um estabelecimento que compartilha a senha do dono em vários tablets e reinicia todos juntos pode ver o mesmo `429` por alguns segundos; o app já espera o `Retry-After` (08 §8.15). Só registrar: o caminho certo é o aparelho compartilhado com PIN.
6. **Login custa ~110 ms de CPU** (PBKDF2 do Identity, p50 111 ms no normal). Não pesa porque o app renova por `/auth/refresh` (p95 23 ms); um evento que derrube todas as sessões (troca da chave JWT) faria todos entrarem de novo de uma vez: ~100 logins ≈ 11 s de CPU, aceitável.
7. **Memória sobrando:** a API ficou em ~300 MiB e o PostgreSQL em ~320 MiB no 6×. Os limites do compose de produção (API 2 GB, PostgreSQL 6 GB) estão folgados; não há motivo para aumentar `shared_buffers` agora.

## 5. Como repetir

```bash
bash infra/load/run.sh build            # imagens do HEAD (almoseis-loadtest/*:local)
bash infra/load/run.sh up               # .out/vm.env com segredos aleatórios de teste; postgres, migrations, api, web, caddy
bash infra/load/run.sh seed             # 30 estabelecimentos → .out/seed.json (credenciais de teste, fora do git)
TENANTS=4 SLOT_SECONDS=20 PEAK_SLOT_SECONDS=30 bash infra/load/run.sh run warmup 1   # aquece o JIT (~4 min)
bash infra/load/run.sh run normal 1     # ~17 min
bash infra/load/run.sh run stress3x 3   # ~17 min
python infra/load/summarize.py normal stress3x
bash infra/load/run.sh clean            # contêineres, rede, volumes e imagens do teste (filtro por projeto/label)
```

Saídas em `infra/load/.out/`: `summary-<rótulo>.txt|json` (k6), `stats-<rótulo>.csv` (docker stats), `outbox-<rótulo>.csv` e `outbox-lag-<rótulo>.txt`, `counts-`, `pg-slow-` e `api-errors-<rótulo>.txt`. `LOAD_CPUSET=0` simula 1 vCPU (homologação). Para outro volume: `ORDERS_PER_DAY`, `TENANTS`, `LOAD_MULTIPLIER`.
