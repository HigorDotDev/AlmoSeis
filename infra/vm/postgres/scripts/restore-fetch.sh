#!/bin/sh
# Baixa uma base do WAL-G para um volume VAZIO montado em /restore e o prepara para recuperação (PITR).
# Usado pelo serviço `walg-fetch` (perfil `restore`) do docker-compose.yml; quem orquestra é backup/restore.sh.
#
#   sh /almoseis/restore-fetch.sh [BACKUP]      BACKUP = LATEST (padrão) ou o nome de `wal-g backup-list`
#
# Variáveis: RESTORE_TARGET_TIME (opcional, ex.: "2026-11-27 14:05:00-03"). Vazio = recupera até o último WAL do bucket.
set -eu

BACKUP="${1:-LATEST}"
DATA=/restore

if [ -n "$(ls -A "$DATA" 2>/dev/null)" ]; then
  echo "restore-fetch: $DATA não está vazio. Use um volume novo." >&2
  exit 1
fi

echo "restore-fetch: baixando a base $BACKUP..."
wal-g backup-fetch "$DATA" "$BACKUP"

# recovery.signal + alvo: o restore_command já está fixo na linha de comando do serviço postgres.
touch "$DATA/recovery.signal"
{
  echo "# --- adicionado por restore-fetch.sh em $(date -u +%Y-%m-%dT%H:%M:%SZ); removido por restore.sh após a promoção"
  if [ -n "${RESTORE_TARGET_TIME:-}" ]; then
    echo "recovery_target_time = '${RESTORE_TARGET_TIME}'"
  fi
  echo "recovery_target_action = 'promote'"
} >> "$DATA/postgresql.auto.conf"

chown -R postgres:postgres "$DATA"
chmod 0700 "$DATA"
echo "restore-fetch: pronto (alvo: ${RESTORE_TARGET_TIME:-último WAL disponível})."
