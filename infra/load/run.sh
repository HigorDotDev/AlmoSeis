#!/usr/bin/env bash
# Teste de carga do pico do almoço na "VM" local (infra/load/RESULTS.md, docs/09-devops.md §9.11).
#
# Sobe o compose da VM (infra/vm/docker-compose.yml) com o override de limites (docker-compose.load.yml) num projeto
# PRÓPRIO (almoseis-loadtest: contêineres, rede e volumes com esse prefixo e o label almoseis.loadtest=true), sem
# tocar no compose da raiz (almoseis-db-1 / almoseis_pgdata) nem em nada fora do teste.
#
#   bash infra/load/run.sh build            # imagens do commit atual (git archive HEAD), tag almoseis-loadtest/*:local
#   bash infra/load/run.sh up               # gera .out/vm.env (segredos aleatórios de teste), sobe, roda as migrations
#   bash infra/load/run.sh seed             # semente: 30 estabelecimentos (infra/load/seed), saída em .out/seed.json
#   bash infra/load/run.sh run normal 1     # cenário normal (k6 em contêiner na rede do compose) + docker stats + outbox
#   bash infra/load/run.sh run stress3x 3   # estresse 3×
#   bash infra/load/run.sh down             # remove contêineres, rede e volumes do projeto almoseis-loadtest
#   bash infra/load/run.sh clean            # down + imagens almoseis.loadtest=true + imagem do k6 (se não existia antes)
#
# Variáveis: LOAD_CPUSET (CPUs da "VM", padrão 0,1), LOAD_K6_CPUSET (CPUs do k6, padrão 4-11), TENANTS (30),
# K6_IMAGE (grafana/k6:latest), SLOT_SECONDS/PEAK_SLOT_SECONDS (compressão, 120/180).
# Nada aqui faz prune geral do Docker.
set -euo pipefail
export MSYS_NO_PATHCONV=1

LOAD_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$LOAD_DIR/../.." && pwd)"
OUT="$LOAD_DIR/.out"
PROJECT=almoseis-loadtest
ENV_FILE="$OUT/vm.env"
IMAGES_FILE="$OUT/images.env"
K6_IMAGE="${K6_IMAGE:-grafana/k6:latest}"
export LOAD_CPUSET="${LOAD_CPUSET:-0,1}"
LOAD_K6_CPUSET="${LOAD_K6_CPUSET:-4-11}"
TENANTS="${TENANTS:-30}"
mkdir -p "$OUT"

log() { echo "[$(date '+%H:%M:%S')] $*"; }
winpath() { if command -v cygpath >/dev/null; then cygpath -w "$1"; else echo "$1"; fi; }
dc() {
  docker compose -p "$PROJECT" --env-file "$(winpath "$ENV_FILE")" --env-file "$(winpath "$IMAGES_FILE")" \
    -f "$(winpath "$REPO/infra/vm/docker-compose.yml")" -f "$(winpath "$LOAD_DIR/docker-compose.load.yml")" "$@"
}
envval() { grep -E "^$1=" "$ENV_FILE" | tail -n 1 | cut -d= -f2- | sed -e 's/^"//' -e 's/"$//'; }
hex() { openssl rand -hex "${1:-32}"; }

cmd_build() {
  local src; src="$(mktemp -d)"
  log "exportando HEAD ($(git -C "$REPO" rev-parse --short HEAD)) para $src"
  git -C "$REPO" archive HEAD backend infra web | tar -x -C "$src"
  docker build -q --label almoseis.loadtest=true -t almoseis-loadtest/postgres:local "$src/infra/vm/postgres"
  docker build -q --label almoseis.loadtest=true --target api -t almoseis-loadtest/api:local "$src/backend"
  docker build -q --label almoseis.loadtest=true --target migrator -t almoseis-loadtest/migrator:local "$src/backend"
  docker build -q --label almoseis.loadtest=true --build-arg NEXT_PUBLIC_API_BASE_URL=http://api.localhost:8088 \
    --build-arg NEXT_PUBLIC_SITE_URL=http://web.localhost:8088 -t almoseis-loadtest/web:local "$src/web"
  rm -rf "$src"
}

write_env() {
  [ -f "$ENV_FILE" ] && { log "usando $ENV_FILE existente"; return; }
  local pem; pem="$(openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 2>/dev/null | awk '{printf "%s\\n", $0}')"
  umask 077
  cat > "$ENV_FILE" <<EOF
# Gerado por infra/load/run.sh: SEGREDOS DE TESTE, aleatórios, só desta máquina (fora do git).
DEPLOY_ENVIRONMENT=loadtest
ASPNETCORE_ENVIRONMENT=Staging
Logging__LogLevel__Default=Information
API_DOMAIN=http://api.localhost
WEB_DOMAIN=http://web.localhost
ACME_EMAIL=loadtest@example.invalid
CADDY_GLOBAL_OPTIONS=
HTTP_PORT=8088
HTTPS_PORT=8443
DOCKER_SUBNET=172.31.250.0/24
VOLUME_PREFIX=$PROJECT
PGDATA_VOLUME=${PROJECT}_pgdata
PG_MEM_LIMIT=2g
API_MEM_LIMIT=1g
WEB_MEM_LIMIT=512m
CADDY_MEM_LIMIT=256m
POSTGRES_SUPERUSER_PASSWORD=$(hex)
ALMOSEIS_MIGRATOR_DB_PASSWORD=$(hex)
ALMOSEIS_APP_DB_PASSWORD=$(hex)
ALMOSEIS_PLATFORM_DB_PASSWORD=$(hex)
DB_APP_POOL_SIZE=40
DB_PLATFORM_POOL_SIZE=10
PG_SHARED_BUFFERS=512MB
PG_EFFECTIVE_CACHE_SIZE=1536MB
PG_WORK_MEM=8MB
PG_MAINTENANCE_WORK_MEM=128MB
PG_MAX_CONNECTIONS=100
PG_MAX_WAL_SIZE=2GB
PG_SHM_SIZE=256m
PG_ARCHIVE_TIMEOUT=60
PG_LOG_MIN_DURATION_MS=500
PG_LOCAL_PORT=55432
WALG_ENABLED=false
KeyVault__Uri=
Jwt__SigningKeyPem="$pem"
App__MinSupportedAppVersion__ANDROID=0.0.0
App__MinSupportedAppVersion__IOS=0.0.0
App__MinSupportedAppVersion__WEB=0.0.0
Cors__RestaurantAppOrigins__0=http://localhost:8081
Cors__PublicPageOrigins__0=http://localhost:3000
RateLimiting__Enabled=true
Billing__PlanCode=PILOT
Billing__MonthlyPrice=0
Turnstile__Enabled=false
Turnstile__SecretKey=
PublicPage__IpHashSecret=$(hex)
PublicPage__TrackingTokenSecret=$(hex)
PublicPage__RendererKey=$(hex)
Maps__Provider=None
Maps__AddressHashSecret=$(hex)
Push__Provider=Fake
PasswordReset__LinkBaseUrl=http://web.localhost/redefinir-senha?token=
Invitations__LinkBaseUrl=http://web.localhost/convite/
Email__Provider=None
Email__From=loadtest@example.invalid
Otel__Endpoint=
EOF
  cat > "$IMAGES_FILE" <<EOF
API_IMAGE=almoseis-loadtest/api:local
WEB_IMAGE=almoseis-loadtest/web:local
MIGRATOR_IMAGE=almoseis-loadtest/migrator:local
POSTGRES_IMAGE=almoseis-loadtest/postgres:local
EOF
}

cmd_up() {
  write_env
  log "postgres"
  dc up -d --wait --wait-timeout 180 postgres
  log "migrations (almoseis_migrator_login)"
  dc --profile tools run --rm migrator
  log "api, web e caddy"
  dc up -d --wait --wait-timeout 240 api web caddy
  curl -fsS -H "Host: api.localhost" "http://127.0.0.1:$(envval HTTP_PORT)/health/ready"; echo
}

cmd_seed() {
  local port; port="$(envval PG_LOCAL_PORT)"
  local app="Host=127.0.0.1;Port=$port;Database=almoseis;Username=almoseis_app_login;Password=$(envval ALMOSEIS_APP_DB_PASSWORD)"
  local platform="Host=127.0.0.1;Port=$port;Database=almoseis;Username=almoseis_platform_login;Password=$(envval ALMOSEIS_PLATFORM_DB_PASSWORD)"
  dotnet run --project "$(winpath "$LOAD_DIR/seed")" -c Release -- --app-conn "$app" --platform-conn "$platform" \
    --api "http://127.0.0.1:$(envval HTTP_PORT)" --api-host api.localhost --tenants "$TENANTS" --out "$(winpath "$OUT/seed.json")"
}

psql_platform() {
  docker exec -e PGPASSWORD="$(envval ALMOSEIS_PLATFORM_DB_PASSWORD)" "$PROJECT-postgres-1" \
    psql -h 127.0.0.1 -U almoseis_platform_login -d almoseis -At -F ',' -c "$1"
}

cmd_run() {
  local label="${1:?rótulo}" mult="${2:?multiplicador}"
  local stats="$OUT/stats-$label.csv" outbox="$OUT/outbox-$label.csv"
  local started; started="$(date -u '+%Y-%m-%d %H:%M:%S+00')"
  echo "ts,container,cpu_pct,mem_usage,mem_pct" > "$stats"
  echo "ts,pending,oldest_pending_s,processed_last_5s" > "$outbox"
  ( while true; do
      docker stats --no-stream --format "{{.Name}},{{.CPUPerc}},{{.MemUsage}},{{.MemPerc}}" \
        "$PROJECT-api-1" "$PROJECT-postgres-1" "$PROJECT-web-1" "$PROJECT-caddy-1" 2>/dev/null \
        | sed "s/^/$(date '+%H:%M:%S'),/" >> "$stats" || true
      sleep 3
    done ) & local stats_pid=$!
  ( while true; do
      # Pendentes "devidas" (available_at no passado): o lembrete agendado do push (WebOrderPushRequested) não é atraso.
      psql_platform "SELECT count(*) FILTER (WHERE processed_at IS NULL AND coalesce(available_at, occurred_at) <= now()),
                            coalesce(extract(epoch FROM now() - min(coalesce(available_at, occurred_at)) FILTER (WHERE processed_at IS NULL AND coalesce(available_at, occurred_at) <= now())), 0)::numeric(10,1),
                            count(*) FILTER (WHERE processed_at > now() - interval '5 seconds')
                     FROM outbox_messages" 2>/dev/null | sed "s/^/$(date '+%H:%M:%S'),/" >> "$outbox" || true
      sleep 5
    done ) & local outbox_pid=$!
  trap 'kill $stats_pid $outbox_pid 2>/dev/null || true' RETURN

  log "k6 $label (multiplicador $mult) em $K6_IMAGE, CPUs $LOAD_K6_CPUSET; VM em $LOAD_CPUSET"
  docker run --rm --name "$PROJECT-k6" --label almoseis.loadtest=true --network "${PROJECT}_default" \
    --cpuset-cpus "$LOAD_K6_CPUSET" -v "$(winpath "$LOAD_DIR"):/load" \
    -e LOAD_MULTIPLIER="$mult" -e RUN_LABEL="$label" -e TENANTS="$TENANTS" \
    -e SLOT_SECONDS="${SLOT_SECONDS:-120}" -e PEAK_SLOT_SECONDS="${PEAK_SLOT_SECONDS:-180}" \
    -e DEBUG_ERRORS="${DEBUG_ERRORS:-}" -e ORDERS_PER_DAY="${ORDERS_PER_DAY:-150}" -e K6_NO_USAGE_REPORT=true \
    "$K6_IMAGE" run --quiet /load/lunch-peak.js || log "k6 terminou com limite violado ou erro (ver resumo)"

  log "atraso do outbox (occurred_at → processed_at) desde $started, eventos de tempo real (sem o push agendado)"
  psql_platform "SELECT count(*) AS mensagens,
                        round(percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM processed_at - occurred_at) * 1000)::numeric, 1) AS p50_ms,
                        round(percentile_cont(0.95) WITHIN GROUP (ORDER BY extract(epoch FROM processed_at - occurred_at) * 1000)::numeric, 1) AS p95_ms,
                        round(percentile_cont(0.99) WITHIN GROUP (ORDER BY extract(epoch FROM processed_at - occurred_at) * 1000)::numeric, 1) AS p99_ms,
                        round(max(extract(epoch FROM processed_at - occurred_at) * 1000)::numeric, 1) AS max_ms,
                        count(*) FILTER (WHERE processed_at IS NULL) AS pendentes
                 FROM outbox_messages WHERE occurred_at >= '$started' AND event_type <> 'WebOrderPushRequested'" | tee "$OUT/outbox-lag-$label.txt"
  psql_platform "SELECT 'pedidos', count(*) FROM orders UNION ALL SELECT 'pagamentos', count(*) FROM payments
                 UNION ALL SELECT 'feed', count(*) FROM change_feed_entries UNION ALL SELECT 'outbox', count(*) FROM outbox_messages" \
    | tee "$OUT/counts-$label.txt"
  dc logs --no-color --since "$started" api 2>/dev/null | grep -E '"LogLevel":"(Error|Critical)"|fail:|crit:|Exception' | head -n 50 > "$OUT/api-errors-$label.txt" || true
  dc logs --no-color --since "$started" postgres 2>/dev/null | grep -E 'duration:|ERROR|deadlock' | head -n 100 > "$OUT/pg-slow-$label.txt" || true
  log "stats: $stats | outbox: $outbox | resumo: $OUT/summary-$label.txt"
}

cmd_down() {
  [ -f "$ENV_FILE" ] || { log "sem $ENV_FILE: nada a derrubar"; return; }
  dc --profile tools --profile restore down -v --remove-orphans
}

cmd_clean() {
  cmd_down || true
  local ids; ids="$(docker images -q --filter label=almoseis.loadtest=true | sort -u)"
  [ -n "$ids" ] && docker image rm -f $ids || true
  if [ "${REMOVE_K6_IMAGE:-true}" = true ]; then docker image rm "$K6_IMAGE" 2>/dev/null || true; fi
  rm -f "$ENV_FILE" "$IMAGES_FILE"
}

case "${1:-}" in
  build) cmd_build ;;
  up) cmd_up ;;
  seed) cmd_seed ;;
  run) shift; cmd_run "$@" ;;
  down) cmd_down ;;
  clean) cmd_clean ;;
  *) sed -n '2,22p' "$0"; exit 1 ;;
esac
