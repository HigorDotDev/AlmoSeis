#!/usr/bin/env bash
# Volta a API e a página às imagens do deploy anterior (images.env.prev). NÃO desfaz migrations: elas são só para
# frente e compatíveis com a versão anterior (expand/contract, docs/09-devops.md §9.4).
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
require_files
PREV="$ALMOSEIS_IMAGES_FILE.prev"
[ -f "$PREV" ] || die "não há images.env.prev"
cp "$ALMOSEIS_IMAGES_FILE" "$ALMOSEIS_IMAGES_FILE.rolledback"
cp "$PREV" "$ALMOSEIS_IMAGES_FILE"
log "imagens: $(grep -E '^(API|WEB)_IMAGE=' "$ALMOSEIS_IMAGES_FILE" | tr '\n' ' ')"
dc up -d --wait --wait-timeout 240 api web
log "rollback concluído (a versão revertida ficou em images.env.rolledback)"
