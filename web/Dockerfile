# syntax=docker/dockerfile:1
# Imagem da página pública (ADR-0051, ADR-0060, docs/09-devops.md §9.4). Contexto de build: a pasta web/.
#
#   docker buildx build --platform linux/amd64,linux/arm64 \
#     --build-arg NEXT_PUBLIC_API_BASE_URL=https://api.almoseis.app \
#     --build-arg NEXT_PUBLIC_SITE_URL=https://almoseis.app \
#     --build-arg NEXT_PUBLIC_TURNSTILE_SITE_KEY=0x... \
#     -t ghcr.io/<owner>/almoseis-web:<sha>-prod web
#
# As variáveis NEXT_PUBLIC_* entram no JavaScript durante o `next build`, então a imagem é POR AMBIENTE
# (tag `<sha>-homolog` / `<sha>-prod`). As variáveis só do servidor (API_BASE_URL, WEB_RENDERER_KEY) vêm do
# ambiente do contêiner em tempo de execução e nunca entram na imagem.
#
# Multi-arch: o build roda na arquitetura do runner ($BUILDPLATFORM). A saída standalone é JavaScript puro; o único
# binário nativo que o Next copia é o `sharp` (otimização de imagens), que a página não usa (não há next/image) e que
# é removido abaixo para não levar um binário da arquitetura errada.
ARG NODE_VERSION=24

# ---------------------------------------------------------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM node:${NODE_VERSION}-alpine AS build
WORKDIR /app
ENV NEXT_TELEMETRY_DISABLED=1
COPY package.json package-lock.json ./
RUN --mount=type=cache,id=npm,target=/root/.npm npm ci --no-audit --no-fund
COPY . .
ARG NEXT_PUBLIC_API_BASE_URL
ARG NEXT_PUBLIC_SITE_URL
ARG NEXT_PUBLIC_TURNSTILE_SITE_KEY=""
RUN test -n "$NEXT_PUBLIC_API_BASE_URL" && test -n "$NEXT_PUBLIC_SITE_URL" \
 || (echo "NEXT_PUBLIC_API_BASE_URL e NEXT_PUBLIC_SITE_URL são obrigatórios (--build-arg)" && exit 1)
RUN npm run build \
 && rm -rf .next/standalone/node_modules/sharp .next/standalone/node_modules/@img \
 && cp -r .next/static .next/standalone/.next/static \
 && if [ -d public ]; then cp -r public .next/standalone/public; fi

# ---------------------------------------------------------------------------------------------------------------------
FROM node:${NODE_VERSION}-alpine AS runtime
# Label usado pelo prune do deploy.sh (só imagens do AlmoSeis).
LABEL app=almoseis
WORKDIR /app
ENV NODE_ENV=production \
    NEXT_TELEMETRY_DISABLED=1 \
    PORT=3000 \
    HOSTNAME=0.0.0.0
# Usuário `node` (uid 1000) já existe na imagem oficial; nada é gravado fora de /tmp em execução.
COPY --from=build --chown=node:node /app/.next/standalone ./
USER node
EXPOSE 3000
HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=3 \
  CMD node -e "fetch('http://127.0.0.1:3000/').then(r=>process.exit(r.status<500?0:1)).catch(()=>process.exit(1))"
CMD ["node", "server.js"]
