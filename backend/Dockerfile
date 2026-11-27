# syntax=docker/dockerfile:1
# Imagem da API (docs/09-devops.md §9.4, ADR-0060). Contexto de build: a pasta backend/.
#
#   docker buildx build --platform linux/amd64,linux/arm64 -t ghcr.io/<owner>/almoseis-api:<sha> backend
#   docker buildx build --target migrator -t ghcr.io/<owner>/almoseis-migrator:<sha> backend
#
# Multi-arch por compilação cruzada: o SDK roda na arquitetura do runner ($BUILDPLATFORM) e publica para a do destino
# ($TARGETARCH). Nada de .NET roda emulado (QEMU), só o `apt-get` do runtime.
ARG DOTNET_VERSION=10.0

# ---------------------------------------------------------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
# O cache do NuGet fica num cache mount do BuildKit (não entra na imagem).
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore src/AlmoSeis.Api/AlmoSeis.Api.csproj -a $TARGETARCH
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/AlmoSeis.Api/AlmoSeis.Api.csproj -c Release -a $TARGETARCH --no-restore \
      -p:UseAppHost=false -o /app

# ---------------------------------------------------------------------------------------------------------------------
# Bundle das migrations (ADR-0042): executável autocontido que aplica as migrations SQL com o login almoseis_migrator.
FROM build AS bundle
ARG TARGETARCH
ARG DOTNET_EF_VERSION=10.0.*
ENV PATH="${PATH}:/root/.dotnet/tools"
RUN dotnet tool install --global dotnet-ef --version "${DOTNET_EF_VERSION}"
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    RID="linux-$( [ "$TARGETARCH" = "amd64" ] && echo x64 || echo "$TARGETARCH" )" \
 && dotnet ef migrations bundle \
      --project src/AlmoSeis.Infrastructure --startup-project src/AlmoSeis.Api --context AppDbContext \
      --configuration Release --target-runtime "$RID" --self-contained --force -o /out/efbundle

# ---------------------------------------------------------------------------------------------------------------------
# Migrator: roda uma vez por deploy (`docker compose run --rm migrator`), antes da API nova.
# Lê a connection string de ALMOSEIS_MIGRATOR_CONNECTION (AppDbContextDesignTimeFactory), nunca do appsettings.
FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION} AS migrator
# Label usado pelo prune do deploy.sh (só imagens do AlmoSeis).
LABEL app=almoseis
WORKDIR /app
COPY --from=bundle /out/efbundle /app/efbundle
USER $APP_UID
ENTRYPOINT ["/app/efbundle"]

# ---------------------------------------------------------------------------------------------------------------------
# API (alvo padrão: último estágio). Debian/Ubuntu, não chiseled: precisa do tzdata (07 §7.11) e do curl do HEALTHCHECK.
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS api
LABEL app=almoseis
RUN apt-get update \
 && apt-get install -y --no-install-recommends tzdata curl ca-certificates \
 && rm -rf /var/lib/apt/lists/*
# Chaves do Data Protection (token do "esqueci a senha", ADR-0040) num volume persistente: o diretório nasce do
# usuário da aplicação para o volume nomeado herdar o dono.
RUN mkdir -p /var/lib/almoseis/dataprotection && chown -R $APP_UID /var/lib/almoseis
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_gcServer=0 \
    DataProtection__KeysPath=/var/lib/almoseis/dataprotection
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=30s --retries=3 \
  CMD curl -fsS http://127.0.0.1:8080/health/live || exit 1
ENTRYPOINT ["dotnet", "AlmoSeis.Api.dll"]
