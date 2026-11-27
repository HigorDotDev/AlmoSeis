# syntax=docker/dockerfile:1
# PostgreSQL 16 + WAL-G (ADR-0060, docs/09-devops.md §9.7). Imagem publicada como ghcr.io/<owner>/almoseis-postgres.
#
#   docker buildx build --platform linux/amd64,linux/arm64 -t ghcr.io/<owner>/almoseis-postgres:16-walg-3.0.10 infra/vm/postgres
#
# O binário do WAL-G é baixado com checksum fixo por arquitetura (o ADD falha se o arquivo mudar). Para subir a versão:
# trocar a versão nas duas URLs e as duas somas (arquivo .tar.gz.sha256 de cada release em github.com/wal-g/wal-g/releases).
# O build Ubuntu 22.04 (glibc 2.35) roda no Debian bookworm (glibc 2.36) da imagem oficial.
ARG PG_MAJOR=16
ARG TARGETARCH

FROM scratch AS walg-amd64
ADD --checksum=sha256:5dac31ca337fda10a0e22cf722ee751cd64d4b972fb99fe3cedac1f73f5a2fa4 \
    https://github.com/wal-g/wal-g/releases/download/v3.0.10/wal-g-pg-22.04-amd64.tar.gz /walg.tar.gz

FROM scratch AS walg-arm64
ADD --checksum=sha256:d1b720fc1f6f716939fbf29c32b755994dd96a9b374de03cdab607d036bfaea7 \
    https://github.com/wal-g/wal-g/releases/download/v3.0.10/wal-g-pg-22.04-aarch64.tar.gz /walg.tar.gz

FROM walg-${TARGETARCH} AS walg

# Certificados raiz para o HTTPS do R2/S3, tirados de um estágio na arquitetura do runner (arquivo de texto, igual
# em qualquer arquitetura): evita rodar apt-get emulado no build arm64.
FROM --platform=$BUILDPLATFORM debian:bookworm-slim AS certs
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates && rm -rf /var/lib/apt/lists/*

FROM postgres:${PG_MAJOR}-bookworm
# Label usado pelo prune do deploy.sh (só imagens do AlmoSeis).
LABEL app=almoseis
COPY --from=certs /etc/ssl/certs/ca-certificates.crt /etc/ssl/certs/ca-certificates.crt
RUN --mount=type=bind,from=walg,source=/walg.tar.gz,target=/tmp/walg.tar.gz \
    tar -xzf /tmp/walg.tar.gz -C /usr/local/bin \
 && mv /usr/local/bin/wal-g-pg-* /usr/local/bin/wal-g \
 && chmod 0755 /usr/local/bin/wal-g \
 && wal-g --version
ENV SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt
