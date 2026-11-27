#!/bin/bash
# Primeira subida do PostgreSQL da VM (volume vazio): cria os papéis e logins de 05 §5.1 com as senhas do .env.
# Roda pelo docker-entrypoint-initdb.d da imagem oficial, como `postgres`, só quando o PGDATA está vazio.
# Numa restauração (backup-fetch) o initdb NÃO roda: os papéis já vêm na base.
#
# Sem `set -u`/`set -e` próprios: sem o bit de execução (checkout no Windows) o entrypoint faz `source` deste arquivo,
# e opções de shell alteradas aqui valeriam para o resto do entrypoint. O entrypoint já roda com `set -e`.

for almoseis_var in ALMOSEIS_MIGRATOR_DB_PASSWORD ALMOSEIS_APP_DB_PASSWORD ALMOSEIS_PLATFORM_DB_PASSWORD; do
  if [ -z "${!almoseis_var:-}" ]; then
    echo "10-almoseis-roles: $almoseis_var vazio no .env" >&2
    exit 1
  fi
done

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v migrator_password="$ALMOSEIS_MIGRATOR_DB_PASSWORD" \
  -v app_password="$ALMOSEIS_APP_DB_PASSWORD" \
  -v platform_password="$ALMOSEIS_PLATFORM_DB_PASSWORD" \
  -f /almoseis-db/create-logins.sql
