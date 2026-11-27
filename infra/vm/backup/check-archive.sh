#!/usr/bin/env bash
# Conferência do archive contínuo de WAL e do disco, a cada 5 min pelo cron (ADR-0060, docs/09-devops.md §9.8).
# Saudável → chama ARCHIVE_HEARTBEAT_URL. Problema → chama ARCHIVE_HEARTBEAT_URL/fail (Better Stack e
# Healthchecks.io aceitam o sufixo /fail) e o monitor externo manda o e-mail. Sem URL: só registra no log.
#
# Problema =
#   - a última tentativa de archive falhou depois do último sucesso (pg_stat_archiver), ou
#   - mais de 10 segmentos de WAL esperando envio (archive atrasado), ou
#   - disco da raiz acima de 85% (WAL acumulado por falha do bucket enche o disco e derruba o banco).
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../scripts/lib.sh"
require_files

HEARTBEAT="$(envval ARCHIVE_HEARTBEAT_URL)"
problems=()

if [ "$(envval WALG_ENABLED)" != "false" ]; then
  # failing: a última tentativa falhou depois do último sucesso; ready: segmentos esperando envio em pg_wal.
  row="$(dc exec -T -u postgres postgres psql -d almoseis -tA -F '|' -c "
    SELECT coalesce(a.last_failed_time > coalesce(a.last_archived_time, 'epoch'), false),
           a.failed_count,
           (SELECT count(*) FROM pg_ls_archive_statusdir() WHERE name LIKE '%.ready')
      FROM pg_stat_archiver a" 2>/dev/null | head -n 1 || true)"
  if [ -z "$row" ]; then
    problems+=("sem resposta do PostgreSQL")
  else
    IFS='|' read -r failing failed ready <<<"$row"
    if [ "$failing" = "t" ]; then problems+=("archive falhando (failed_count=$failed)"); fi
    # Com archive_timeout de 60 s, mais de 10 segmentos na fila = ~10 min de RPO perdido.
    if [ "${ready:-0}" -gt 10 ]; then problems+=("$ready segmentos de WAL esperando envio"); fi
  fi
fi

disk="$(df --output=pcent / | tail -n 1 | tr -dc '0-9')"
if [ "${disk:-0}" -gt 85 ]; then problems+=("disco em ${disk}%"); fi

if [ ${#problems[@]} -eq 0 ]; then
  [ -n "$HEARTBEAT" ] && curl -fsS -m 10 --retry 2 "$HEARTBEAT" >/dev/null 2>&1 || true
  exit 0
fi

log "PROBLEMA: ${problems[*]}"
[ -n "$HEARTBEAT" ] && curl -fsS -m 10 --retry 2 --data-raw "${problems[*]}" "$HEARTBEAT/fail" >/dev/null 2>&1 || true
exit 1
