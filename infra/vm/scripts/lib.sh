#!/usr/bin/env bash
# Funções comuns dos scripts da VM (ADR-0060). Uso: `source "$(dirname "$0")/../scripts/lib.sh"`.
#
# Caminhos (sobrescrevíveis por variável de ambiente, para o teste local):
#   ALMOSEIS_HOME          /opt/almoseis                  raiz na VM
#   ALMOSEIS_ENV_FILE      $ALMOSEIS_HOME/.env            segredos e configuração (chmod 600)
#   ALMOSEIS_IMAGES_FILE   $ALMOSEIS_HOME/images.env      tags das imagens, escrito pelo deploy
#   ALMOSEIS_PROJECT       almoseis                       nome do projeto do compose

ALMOSEIS_HOME="${ALMOSEIS_HOME:-/opt/almoseis}"
ALMOSEIS_ENV_FILE="${ALMOSEIS_ENV_FILE:-$ALMOSEIS_HOME/.env}"
ALMOSEIS_IMAGES_FILE="${ALMOSEIS_IMAGES_FILE:-$ALMOSEIS_HOME/images.env}"
ALMOSEIS_PROJECT="${ALMOSEIS_PROJECT:-almoseis}"
ALMOSEIS_VM_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ALMOSEIS_COMPOSE_FILE="${ALMOSEIS_COMPOSE_FILE:-$ALMOSEIS_VM_DIR/docker-compose.yml}"

log() { echo "[$(date '+%Y-%m-%d %H:%M:%S%z')] $*"; }
die() { log "ERRO: $*" >&2; exit 1; }

dc() {
  local files=(--env-file "$ALMOSEIS_ENV_FILE")
  if [ -f "$ALMOSEIS_IMAGES_FILE" ]; then files+=(--env-file "$ALMOSEIS_IMAGES_FILE"); fi
  docker compose -p "$ALMOSEIS_PROJECT" "${files[@]}" -f "$ALMOSEIS_COMPOSE_FILE" "$@"
}

# Valor de uma variável do .env sem fazer `source` (o .env tem PEM em várias linhas). Última ocorrência vale.
envval() {
  local key="$1" file="${2:-$ALMOSEIS_ENV_FILE}"
  [ -f "$file" ] || return 0
  grep -E "^${key}=" "$file" | tail -n 1 | cut -d= -f2- | sed -e 's/^"//' -e 's/"$//'
}

# Grava/atualiza KEY=VALUE num arquivo de variáveis (sem tocar no resto).
setenvval() {
  local key="$1" value="$2" file="${3:-$ALMOSEIS_ENV_FILE}"
  if grep -qE "^${key}=" "$file"; then
    sed -i "s|^${key}=.*|${key}=${value}|" "$file"
  else
    printf '%s=%s\n' "$key" "$value" >> "$file"
  fi
}

# URL base a partir de um domínio do Caddy ("api.x.com" → https://api.x.com; "http://api.localhost" fica igual).
site_url() {
  case "$1" in
    http://*|https://*) echo "$1" ;;
    *) echo "https://$1" ;;
  esac
}

# curl contra o Caddy local, resolvendo o domínio para 127.0.0.1 (valida o certificado de verdade sem depender do DNS).
curl_local() {
  local url="$1" host port scheme
  scheme="${url%%://*}"
  host="${url#*://}"; host="${host%%/*}"; host="${host%%:*}"
  if [ "$scheme" = "https" ]; then port="$(envval HTTPS_PORT)"; port="${port:-443}"; else port="$(envval HTTP_PORT)"; port="${port:-80}"; fi
  local path="/${url#*://*/}"; [ "$path" = "/$url" ] && path="/"
  curl -fsS --max-time 10 --resolve "${host}:${port}:127.0.0.1" "${scheme}://${host}:${port}${path}"
}

require_files() {
  [ -f "$ALMOSEIS_ENV_FILE" ] || die "não achei $ALMOSEIS_ENV_FILE (copie de .env.example)"
  local perm
  perm="$(stat -c '%a' "$ALMOSEIS_ENV_FILE" 2>/dev/null || echo 600)"
  if [ "$perm" != "600" ] && [ "$perm" != "400" ]; then
    log "AVISO: $ALMOSEIS_ENV_FILE com permissão $perm (esperado 600): chmod 600 $ALMOSEIS_ENV_FILE"
  fi
}
