#!/bin/sh
# archive_command do PostgreSQL (docs/09-devops.md §9.7): envia o segmento de WAL ao bucket com WAL-G.
# Chamado como `sh /almoseis/archive-wal.sh %p` pelo próprio servidor, com o ambiente do contêiner.
#
# Saída diferente de 0 = o PostgreSQL guarda o segmento em pg_wal e tenta de novo. Por isso uma falha longa do bucket
# enche o disco: o alerta de "archive falhando" (backup/check-archive.sh) existe para isso.
#
# WALG_ENABLED=false desliga o envio de propósito (teste local sem bucket): o segmento é descartado e NÃO há PITR.
set -eu

if [ "${WALG_ENABLED:-true}" != "true" ]; then
  exit 0
fi

exec wal-g wal-push "$1"
