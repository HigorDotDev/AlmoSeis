#!/usr/bin/env bash
# Restauração do PostgreSQL a partir do WAL-G (ADR-0060, docs/09-devops.md §9.7, infra/vm/restore.md).
#
#   restore.sh list                                       bases disponíveis no bucket
#   restore.sh test [--time T] [--backup NOME] [--keep]   ENSAIO: restaura num PostgreSQL à parte, confere e apaga
#   restore.sh production [--time T] [--backup NOME] [--yes]
#                                                         RESTAURAÇÃO REAL: volume novo, troca do banco da VM
#
#   T = instante com fuso, ex.: "2026-11-27 14:05:00-03". Sem --time: até o último WAL do bucket.
#   NOME = LATEST (padrão) ou um nome de `restore.sh list`. A base tem de ser ANTERIOR ao instante T.
#
# O ensaio não toca no banco em uso: outro volume (RESTORE_TARGET_VOLUME), outra rede, archive desligado.
# A produção nunca apaga o volume antigo: ele fica parado (apagar à mão depois de 7 dias).
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../scripts/lib.sh"
require_files

MODE="${1:-}"; shift || true
TARGET_TIME="" BACKUP="LATEST" KEEP=false YES=false
while [ $# -gt 0 ]; do
  case "$1" in
    --time) TARGET_TIME="$2"; shift 2 ;;
    --backup) BACKUP="$2"; shift 2 ;;
    --keep) KEEP=true; shift ;;
    --yes) YES=true; shift ;;
    *) die "opção desconhecida: $1" ;;
  esac
done

PREFIX="$(envval VOLUME_PREFIX)"; PREFIX="${PREFIX:-almoseis}"
export RESTORE_TARGET_TIME="$TARGET_TIME"

wait_promoted() {
  local service="$1" limit="${2:-3600}" waited=0 state
  log "aguardando a recuperação terminar ($service)..."
  while [ "$waited" -lt "$limit" ]; do
    state="$(dc --profile restore exec -T -u postgres "$service" psql -d postgres -tAc 'SELECT pg_is_in_recovery()' 2>/dev/null || true)"
    if [ "$state" = "f" ]; then log "promovido"; return 0; fi
    sleep 5; waited=$((waited + 5))
  done
  dc --profile restore logs --tail 60 "$service" || true
  die "a recuperação não terminou em ${limit}s"
}

checks() {
  local service="$1"
  log "conferência (compare com o instante escolhido e com o app):"
  dc --profile restore exec -T -u postgres "$service" psql -d almoseis -v ON_ERROR_STOP=1 -P pager=off -c "
    SELECT (SELECT count(*) FROM tenants)        AS tenants,
           (SELECT count(*) FROM orders)         AS orders,
           (SELECT count(*) FROM payments)       AS payments,
           (SELECT count(*) FROM daily_closings) AS daily_closings,
           (SELECT max(created_at) FROM orders)  AS ultimo_pedido,
           (SELECT max(paid_at) FROM payments) AS ultimo_pagamento,
           (SELECT max(created_at) FROM tenants) AS ultimo_tenant;" \
    -c "SELECT migration_id FROM __ef_migrations_history ORDER BY migration_id DESC LIMIT 1;" \
    -c "SELECT rolname, rolsuper, rolbypassrls FROM pg_roles WHERE rolname LIKE 'almoseis%' ORDER BY 1;"
}

case "$MODE" in
  list)
    dc --profile restore run --rm --no-deps --entrypoint wal-g walg-fetch backup-list --detail
    ;;

  test)
    start=$(date +%s)
    export RESTORE_TARGET_VOLUME="${PREFIX}_restore_test"
    log "ENSAIO no volume $RESTORE_TARGET_VOLUME (base $BACKUP, alvo ${TARGET_TIME:-último WAL})"
    dc --profile restore rm -sf pg-restore-test >/dev/null 2>&1 || true
    docker volume rm -f "$RESTORE_TARGET_VOLUME" >/dev/null 2>&1 || true
    dc --profile restore run --rm --no-deps walg-fetch "$BACKUP"
    dc --profile restore up -d --no-deps pg-restore-test
    wait_promoted pg-restore-test
    checks pg-restore-test
    elapsed=$(( $(date +%s) - start ))
    log "ENSAIO concluído em ${elapsed}s (meta de RTO: 3600 s). Registrar em infra/RESTORE-LOG.md."
    if [ "$KEEP" = true ]; then
      log "--keep: contêiner pg-restore-test no ar. Para apagar: restore.sh test-clean"
    else
      dc --profile restore rm -sf pg-restore-test >/dev/null
      docker volume rm "$RESTORE_TARGET_VOLUME" >/dev/null
      log "contêiner e volume do ensaio apagados"
    fi
    ;;

  test-clean)
    export RESTORE_TARGET_VOLUME="${PREFIX}_restore_test"
    dc --profile restore rm -sf pg-restore-test || true
    docker volume rm -f "$RESTORE_TARGET_VOLUME" || true
    ;;

  production)
    OLD="$(envval PGDATA_VOLUME)"; OLD="${OLD:-${PREFIX}_pgdata}"
    NEW="${PREFIX}_pgdata_$(date +%Y%m%d%H%M%S)"
    log "RESTAURAÇÃO DE PRODUÇÃO: $OLD → $NEW (base $BACKUP, alvo ${TARGET_TIME:-último WAL})"
    log "A API fica fora do ar até o fim. O volume $OLD NÃO é apagado."
    if [ "$YES" != true ]; then
      read -r -p "Digite RESTAURAR para continuar: " answer
      [ "$answer" = "RESTAURAR" ] || die "cancelado"
    fi
    start=$(date +%s)

    # 1. Baixa a base com o banco atual ainda no ar (se ele estiver), para encurtar a parada.
    RESTORE_TARGET_VOLUME="$NEW" dc --profile restore run --rm --no-deps walg-fetch "$BACKUP"

    # 2. Para a API e a página, depois o banco (o desligamento limpo arquiva o último segmento de WAL).
    dc stop api web || true
    dc stop postgres || true

    # 3. Aponta o serviço postgres para o volume novo e sobe em recuperação (restore_command + recovery.signal).
    cp "$ALMOSEIS_ENV_FILE" "$ALMOSEIS_ENV_FILE.before-restore-$(date +%Y%m%d%H%M%S)"
    setenvval PGDATA_VOLUME "$NEW"
    dc up -d --no-deps postgres
    wait_promoted postgres
    dc exec -T -u postgres postgres psql -d postgres -v ON_ERROR_STOP=1 \
      -c "ALTER SYSTEM RESET recovery_target_time" -c "ALTER SYSTEM RESET recovery_target_action" -c "SELECT pg_reload_conf()" >/dev/null
    checks postgres

    # 4. API e página de volta, smoke.
    dc up -d --wait --wait-timeout 240 api web caddy
    API_URL="$(site_url "$(envval API_DOMAIN)")"
    curl_local "$API_URL/health/ready" >/dev/null && log "API saudável"

    # 5. Base nova já na linha do tempo nova (a próxima restauração parte dela).
    bash "$(dirname "${BASH_SOURCE[0]}")/walg-backup.sh" || log "AVISO: base pós-restauração falhou; rode walg-backup.sh à mão"

    elapsed=$(( $(date +%s) - start ))
    log "RESTAURAÇÃO concluída em ${elapsed}s. Volume antigo: $OLD (apagar depois de 7 dias: docker volume rm $OLD)."
    log "Avise os estabelecimentos: o que foi lançado depois de ${TARGET_TIME:-o último WAL arquivado} precisa ser relançado."
    ;;

  *)
    sed -n '2,13p' "$0"
    exit 2
    ;;
esac
