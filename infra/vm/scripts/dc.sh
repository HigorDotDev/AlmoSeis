#!/usr/bin/env bash
# docker compose da VM com os dois arquivos de variáveis. Ex.:
#   bash /opt/almoseis/infra/vm/scripts/dc.sh ps
#   bash /opt/almoseis/infra/vm/scripts/dc.sh logs -f --tail 100 api
#   bash /opt/almoseis/infra/vm/scripts/dc.sh exec postgres psql -U postgres -d almoseis
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
dc "$@"
