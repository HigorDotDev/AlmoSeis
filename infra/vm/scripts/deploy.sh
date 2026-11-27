#!/usr/bin/env bash
# Deploy na VM (ADR-0060, docs/09-devops.md §9.4). Chamado pelo .github/workflows/deploy.yml via SSH, ou à mão:
#
#   bash /opt/almoseis/infra/vm/scripts/deploy.sh \
#     --api ghcr.io/<owner>/almoseis-api:<sha> --migrator ghcr.io/<owner>/almoseis-migrator:<sha> \
#     --web ghcr.io/<owner>/almoseis-web:<sha>-<env> --postgres ghcr.io/<owner>/almoseis-postgres:<tag>
#
# Passos: grava images.env (guardando o anterior) → pull → banco no ar → migrations com almoseis_migrator →
# api/web/caddy novos → smoke /health/ready e página pelo Caddy local. Se a migration falhar, nada muda.
# Se o smoke falhar, volta às imagens anteriores (as migrations são expand/contract: a API anterior aceita o schema novo).
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

API="" WEB="" MIGRATOR="" POSTGRES=""
SKIP_PULL=false
while [ $# -gt 0 ]; do
  case "$1" in
    --api) API="$2"; shift 2 ;;
    --web) WEB="$2"; shift 2 ;;
    --migrator) MIGRATOR="$2"; shift 2 ;;
    --postgres) POSTGRES="$2"; shift 2 ;;
    --skip-pull) SKIP_PULL=true; shift ;;
    *) die "opção desconhecida: $1" ;;
  esac
done

require_files
if command -v flock >/dev/null; then
  exec 9>"$ALMOSEIS_HOME/deploy.lock"
  flock -n 9 || die "outro deploy em andamento"
fi

PREV="$ALMOSEIS_IMAGES_FILE.prev"
if [ -f "$ALMOSEIS_IMAGES_FILE" ]; then cp "$ALMOSEIS_IMAGES_FILE" "$PREV"; fi
API="${API:-$(envval API_IMAGE "$ALMOSEIS_IMAGES_FILE")}"
WEB="${WEB:-$(envval WEB_IMAGE "$ALMOSEIS_IMAGES_FILE")}"
MIGRATOR="${MIGRATOR:-$(envval MIGRATOR_IMAGE "$ALMOSEIS_IMAGES_FILE")}"
POSTGRES="${POSTGRES:-$(envval POSTGRES_IMAGE "$ALMOSEIS_IMAGES_FILE")}"
for v in API WEB MIGRATOR POSTGRES; do [ -n "${!v}" ] || die "imagem $v não informada"; done

umask 077
cat > "$ALMOSEIS_IMAGES_FILE.new" <<EOF
# Escrito por deploy.sh em $(date -u +%Y-%m-%dT%H:%M:%SZ). Anterior em images.env.prev (scripts/rollback.sh).
API_IMAGE=$API
WEB_IMAGE=$WEB
MIGRATOR_IMAGE=$MIGRATOR
POSTGRES_IMAGE=$POSTGRES
EOF
mv "$ALMOSEIS_IMAGES_FILE.new" "$ALMOSEIS_IMAGES_FILE"

restore_previous() {
  if [ -f "$PREV" ]; then
    log "voltando às imagens anteriores"
    cp "$PREV" "$ALMOSEIS_IMAGES_FILE"
    dc up -d --wait --wait-timeout 180 api web || true
  fi
}

if [ "$SKIP_PULL" != true ]; then
  log "pull das imagens"
  dc --profile tools pull --quiet api web migrator postgres caddy
fi

log "banco"
dc up -d --wait --wait-timeout 180 postgres

log "migrations (almoseis_migrator)"
if ! dc --profile tools run --rm migrator; then
  log "migration falhou: a API continua na versão anterior"
  if [ -f "$PREV" ]; then cp "$PREV" "$ALMOSEIS_IMAGES_FILE"; fi
  exit 1
fi

log "api, web e caddy"
if ! dc up -d --wait --wait-timeout 240 api web caddy; then
  dc logs --tail 80 api web || true
  restore_previous
  die "contêineres não ficaram saudáveis"
fi
# Caddyfile pode ter mudado no deploy (diretório montado): recarrega sem derrubar conexões.
dc exec -T caddy caddy reload --config /etc/caddy/Caddyfile >/dev/null 2>&1 || log "AVISO: caddy reload falhou"

API_URL="$(site_url "$(envval API_DOMAIN)")"
WEB_URL="$(site_url "$(envval WEB_DOMAIN)")"
log "smoke: $API_URL/health/ready e $WEB_URL/"
ok=false
for _ in $(seq 1 36); do
  if curl_local "$API_URL/health/ready" >/dev/null 2>&1 && curl_local "$WEB_URL/" >/dev/null 2>&1; then ok=true; break; fi
  sleep 5
done
if [ "$ok" != true ]; then
  curl_local "$API_URL/health/ready" || true
  dc logs --tail 80 api caddy || true
  restore_previous
  die "smoke falhou"
fi

# Só imagens do AlmoSeis (label app=almoseis, posto pelo deploy.yml), sem uso e com mais de 7 dias. Nunca um prune
# geral: a mesma máquina pode ter outras imagens (e o teste local roda este script no Docker do desenvolvedor).
if [ "${ALMOSEIS_PRUNE_IMAGES:-true}" = true ]; then
  log "limpando imagens do AlmoSeis sem uso há mais de 7 dias"
  docker image prune -af --filter "label=app=almoseis" --filter "until=168h" >/dev/null || true
fi
log "deploy concluído: $API"
