# Restauração do banco (WAL-G) — ensaio e restauração real

Desenho em `docs/09-devops.md` §9.7 e ADR-0060. Tudo roda na VM como o usuário `deploy`:

```bash
cd /opt/almoseis/infra/vm
```

Pré-requisito em qualquer máquina: `/opt/almoseis/.env` com as variáveis `WALG_*` do ambiente, **inclusive
`WALG_LIBSODIUM_KEY`**. Sem essa chave, os backups não abrem. Ela fica no cofre de senhas.

## 1. Ver o que existe no bucket

```bash
bash backup/restore.sh list
```

Cada base é um `base_<timeline><wal>`. A mais nova é a `LATEST`. Para restaurar até um instante, a base escolhida
precisa ser **anterior** a ele (o padrão `LATEST` serve se o instante for depois da base de hoje das 03:30).

## 2. Ensaio (obrigatório antes do piloto e a cada trimestre)

O ensaio **não toca no banco em uso**: usa outro volume (`<prefixo>_restore_test`) e um PostgreSQL à parte, numa rede
só dele, com `archive_mode=off` para não gravar nada no bucket.

```bash
# até o último WAL arquivado (~1 min atrás)
bash backup/restore.sh test

# até um instante (sempre com fuso)
bash backup/restore.sh test --time "2026-11-27 14:05:00-03"

# deixar o banco do ensaio no ar para conferir à mão (psql), e apagar depois
bash backup/restore.sh test --keep
bash scripts/dc.sh --profile restore exec pg-restore-test psql -U postgres -d almoseis
bash backup/restore.sh test-clean
```

Conferir:

1. A saída mostra as contagens (`tenants`, `orders`, `payments`, `daily_closings`), o último pedido e o último
   pagamento, a última migration e os papéis (nenhum com `rolsuper` ou `rolbypassrls`).
2. Comparar com o instante escolhido: um pedido lançado no app logo antes dele tem de estar lá, e um lançado logo
   depois, não.
3. Anotar o tempo total que o script mostra. A meta é menos de 3600 s.
4. Registrar em `infra/RESTORE-LOG.md` (data, ambiente, base, instante, contagens, tempo e quem fez).

**Ensaio no plano B** (VPS nova, pelo menos uma vez antes do piloto): `bootstrap-vm.sh` na VPS, copiar o `.env` de
produção, trocar só `API_DOMAIN`/`WEB_DOMAIN` por nomes de teste (ou deixar o Caddy sem subir), e rodar
`restore.sh test`. Isso prova que o backup restaura fora da Oracle e em outra arquitetura (x86).

**Ensaio em homologação a partir de produção:** no `.env` de homologação, `RESTORE_WALG_S3_PREFIX` com o prefixo de
produção e as chaves de leitura do bucket de produção (`WALG_AWS_*`, `WALG_LIBSODIUM_KEY` de produção). Lembrar que
isso traz dados reais para a VM de homologação: apague com `test-clean` no fim.

## 3. Restauração real de produção

Quando: banco corrompido, `DELETE`/`UPDATE` errado, disco perdido. Para "voltar a versão da API", use
`scripts/rollback.sh`, não isto.

1. **Decidir o instante.** Para um erro humano: um pouco antes dele (ver `audit_logs` ou o horário relatado). Para
   perda do banco: sem `--time` (até o último WAL).
2. Avisar os estabelecimentos: a API fica fora por alguns minutos, e o que for lançado depois do instante escolhido
   se perde e precisa ser relançado.
3. Rodar:

   ```bash
   bash backup/restore.sh production --time "2026-11-27 14:05:00-03"
   ```

   O script pede para digitar `RESTAURAR`. Ele:
   - baixa a base num volume novo (`<prefixo>_pgdata_<data>`) com o banco antigo ainda no ar;
   - para `api` e `web`, depois o `postgres` (o desligamento limpo arquiva o último WAL);
   - grava `PGDATA_VOLUME=<volume novo>` no `.env` (cópia do `.env` anterior em `.env.before-restore-*`);
   - sobe o `postgres` em recuperação até o instante, espera a promoção e limpa o alvo de recuperação;
   - mostra as contagens, sobe `api`, `web` e `caddy` e confere `/health/ready`;
   - tira uma base nova (linha do tempo nova).
4. Conferir no app (histórico de fechamentos, pedidos do dia).
5. **Não apagar** o volume antigo por 7 dias. Depois: `docker volume rm <volume antigo>`.
6. Registrar em `infra/RESTORE-LOG.md`.

Se der errado no meio: o volume antigo está intacto. Para voltar a ele, `PGDATA_VOLUME=<volume antigo>` no `.env` e
`bash scripts/dc.sh up -d`.

## 4. Plano B: a VM (ou a conta Oracle) se perdeu

1. Criar a VPS no Brasil (Ubuntu 24.04, 4 vCPU / 8 GB ou mais) e liberar 22/80/443 no painel do provedor.
2. `bootstrap-vm.sh` (ver `infra/README.md` §3), com a mesma chave pública do deploy.
3. Copiar o `.env` de produção do cofre de senhas para `/opt/almoseis/.env` (`chmod 600`). Conferir `PGDATA_VOLUME`
   (pode voltar ao padrão `almoseis_pgdata`).
4. Gravar `/opt/almoseis/images.env` com as tags da última release (o `deploy.yml` mostra no log), fazer
   `docker login ghcr.io` com um token de leitura e restaurar **antes** de subir o resto:

   ```bash
   bash backup/restore.sh production --yes
   ```

   Com o volume vazio, o "banco antigo" não existe: o script só baixa, recupera e sobe.
5. Trocar o IP de `api.{dominio}` e `{dominio}` na Cloudflare (TTL curto) e, no GitHub, o `DEPLOY_HOST` e o
   `DEPLOY_SSH_KNOWN_HOSTS` do Environment `production`.
6. Esperar o Caddy emitir os certificados e conferir `https://api.{dominio}/health/ready`.
7. Rodar o `deploy.yml` manual (produção) para confirmar o caminho normal.
