#!/usr/bin/env bash
# Base diária do WAL-G + retenção (ADR-0060, docs/09-devops.md §9.7). Rodado pelo cron (backup/almoseis-backup.cron)
# como o usuário deploy, e pelo restore.sh logo depois de uma restauração de produção.
#
#   bash /opt/almoseis/infra/vm/backup/walg-backup.sh
#
# O WAL contínuo não depende deste script (archive_command do PostgreSQL). Sem base nova, o PITR continua possível
# a partir da última base, só que a restauração fica mais lenta (mais WAL para reaplicar).
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../scripts/lib.sh"
require_files

if [ "$(envval WALG_ENABLED)" = "false" ]; then
  log "WALG_ENABLED=false: sem backup"
  exit 0
fi

RETAIN="$(envval WALG_RETAIN_FULL)"; RETAIN="${RETAIN:-14}"
HEARTBEAT="$(envval BACKUP_HEARTBEAT_URL)"
start=$(date +%s)

fail() {
  log "ERRO: $*"
  if [ -n "$HEARTBEAT" ]; then curl -fsS -m 10 --retry 3 "$HEARTBEAT/fail" >/dev/null 2>&1 || true; fi
  exit 1
}

log "backup-push"
dc exec -T -u postgres postgres wal-g backup-push /var/lib/postgresql/data || fail "backup-push falhou"

log "retenção: mantém $RETAIN bases completas (e o WAL a partir da mais antiga)"
dc exec -T -u postgres postgres wal-g delete retain FULL "$RETAIN" --confirm || fail "delete retain falhou"

log "bases no bucket:"
dc exec -T -u postgres postgres wal-g backup-list || true

elapsed=$(( $(date +%s) - start ))
log "backup concluído em ${elapsed}s"
if [ -n "$HEARTBEAT" ]; then curl -fsS -m 10 --retry 3 "$HEARTBEAT" >/dev/null 2>&1 || log "AVISO: heartbeat não respondeu"; fi
