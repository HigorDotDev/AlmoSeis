-- Papéis e logins de HOMOLOG/PROD (docs/05-ddl.md §5.1, ADR-0026, docs/09-devops.md §9.5).
--
-- Idempotente: cria o que falta e (re)define as senhas, então serve também para rotação. Roda como superusuário
-- (`postgres`) no banco `almoseis`, com as senhas como variáveis do psql (nunca escritas neste arquivo):
--
--   psql -v ON_ERROR_STOP=1 -U postgres -d almoseis \
--        -v migrator_password="$ALMOSEIS_MIGRATOR_DB_PASSWORD" \
--        -v app_password="$ALMOSEIS_APP_DB_PASSWORD" \
--        -v platform_password="$ALMOSEIS_PLATFORM_DB_PASSWORD" \
--        -f create-logins.sql
--
-- Na VM, o initdb do contêiner chama este arquivo na primeira subida (infra/vm/postgres/initdb/10-almoseis-roles.sh)
-- e `infra/vm/scripts/db-roles.sh` o roda de novo depois de trocar as senhas no .env.
--
-- Regras: os grupos são NOLOGIN; nenhum login é superusuário, CREATEROLE, CREATEDB, REPLICATION nem BYPASSRLS
-- (o almoseis_app nunca ignora a RLS); o login de migration assume o grupo dono do schema (SET role), para que a posse
-- dos objetos não dependa do login.

\set ON_ERROR_STOP on

-- 1. Grupos (cópia de 05 §5.1).
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'almoseis_migrator') THEN
    CREATE ROLE almoseis_migrator NOLOGIN;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'almoseis_app') THEN
    CREATE ROLE almoseis_app NOLOGIN;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'almoseis_platform') THEN
    CREATE ROLE almoseis_platform NOLOGIN;
  END IF;
END $$;

-- 2. Logins (sem senha aqui: a senha vem logo abaixo, por variável).
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'almoseis_migrator_login') THEN
    CREATE ROLE almoseis_migrator_login LOGIN;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'almoseis_app_login') THEN
    CREATE ROLE almoseis_app_login LOGIN;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'almoseis_platform_login') THEN
    CREATE ROLE almoseis_platform_login LOGIN;
  END IF;
END $$;

ALTER ROLE almoseis_migrator_login NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS LOGIN PASSWORD :'migrator_password';
ALTER ROLE almoseis_app_login      NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS LOGIN PASSWORD :'app_password';
ALTER ROLE almoseis_platform_login NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS LOGIN PASSWORD :'platform_password';

-- Cada login só no seu grupo (05 §5.1: o migrator NÃO é membro de almoseis_platform).
GRANT almoseis_migrator TO almoseis_migrator_login;
GRANT almoseis_app      TO almoseis_app_login;
GRANT almoseis_platform TO almoseis_platform_login;
ALTER ROLE almoseis_migrator_login SET role = 'almoseis_migrator';

-- 3. Banco e schema pertencem ao grupo de migration; ninguém mais cria objetos em public.
ALTER DATABASE almoseis OWNER TO almoseis_migrator;
ALTER SCHEMA public OWNER TO almoseis_migrator;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

-- 4. Conferência: nenhum login com poder de ignorar a RLS.
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname LIKE 'almoseis_%' AND (rolsuper OR rolbypassrls OR rolcreaterole)) THEN
    RAISE EXCEPTION 'Papel almoseis_* com SUPERUSER, BYPASSRLS ou CREATEROLE.';
  END IF;
END $$;
