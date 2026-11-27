#!/usr/bin/env bash
# (Re)aplica infra/db/create-logins.sql com as senhas atuais do .env: rotação de senha ou conferência dos papéis.
# Depois de trocar uma senha no .env: bash db-roles.sh && bash dc.sh up -d api
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
require_files
# O contêiner só enxerga senha nova do .env depois de recriado: `up -d` recria o postgres se o ambiente mudou
# (parada de segundos; a API reconecta sozinha).
dc up -d --wait --wait-timeout 180 postgres
# As senhas vêm do ambiente do próprio contêiner (o compose as repassa do .env); nada passa pela linha de comando do host.
# shellcheck disable=SC2016 # expandido dentro do contêiner, de propósito
dc exec -T postgres bash -c 'psql -v ON_ERROR_STOP=1 -U postgres -d almoseis \
  -v migrator_password="$ALMOSEIS_MIGRATOR_DB_PASSWORD" \
  -v app_password="$ALMOSEIS_APP_DB_PASSWORD" \
  -v platform_password="$ALMOSEIS_PLATFORM_DB_PASSWORD" \
  -f /almoseis-db/create-logins.sql'
log "papéis e logins conferidos"
