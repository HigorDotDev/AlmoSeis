-- Ambiente LOCAL (docs/09-devops.md §9.2). Papéis de grupo copiados de docs/05-ddl.md §5.1
-- + logins de desenvolvimento. Em homolog/prod os logins são criados com senhas do Key Vault.
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

CREATE ROLE almoseis_migrator_login LOGIN PASSWORD 'dev-migrator' IN ROLE almoseis_migrator;
ALTER ROLE almoseis_migrator_login SET role = 'almoseis_migrator';
CREATE ROLE almoseis_app_login LOGIN PASSWORD 'dev-app' IN ROLE almoseis_app;
CREATE ROLE almoseis_platform_login LOGIN PASSWORD 'dev-platform' IN ROLE almoseis_platform;
ALTER DATABASE almoseis OWNER TO almoseis_migrator;