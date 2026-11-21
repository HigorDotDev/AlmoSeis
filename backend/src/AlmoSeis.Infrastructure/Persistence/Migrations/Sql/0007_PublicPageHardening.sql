-- GERADO por backend/tools/Sync-DdlMigration.ps1 a partir de docs/05-ddl.md. NÃO EDITE À MÃO.
-- Token de push: sem leitura nem DELETE por posse do token; só a função libera, e só da mesma instalação (ADR-0049 item 4)
DROP POLICY push_token_owner_select ON push_registrations;
DROP POLICY push_token_owner_delete ON push_registrations;
DROP FUNCTION app_push_token();

CREATE FUNCTION release_push_token(p_token text, p_installation_id text) RETURNS integer
  LANGUAGE plpgsql VOLATILE SECURITY DEFINER
  SET search_path = public, pg_temp
  AS $$
DECLARE
  session_tenant uuid := app_tenant_id();
  released integer;
BEGIN
  IF session_tenant IS NULL THEN
    RAISE EXCEPTION 'release_push_token exige app.tenant_id';
  END IF;
  -- Outra instalação em outro tenant: não é o mesmo aparelho trocando de estabelecimento.
  IF EXISTS (SELECT 1 FROM push_registrations
              WHERE expo_push_token = p_token AND tenant_id <> session_tenant AND installation_id <> p_installation_id) THEN
    RETURN -1;
  END IF;
  DELETE FROM push_registrations
   WHERE expo_push_token = p_token AND tenant_id <> session_tenant AND installation_id = p_installation_id;
  GET DIAGNOSTICS released = ROW_COUNT;
  RETURN released;
END $$;
ALTER FUNCTION release_push_token(text, text) OWNER TO almoseis_migrator;
REVOKE ALL ON FUNCTION release_push_token(text, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION release_push_token(text, text) TO almoseis_app;
-- O dono da função também está sob FORCE: políticas só com o que a função usa
CREATE POLICY release_push_token_select ON push_registrations FOR SELECT TO almoseis_migrator USING (true);
CREATE POLICY release_push_token_delete ON push_registrations FOR DELETE TO almoseis_migrator USING (true);

-- Token de acompanhamento: leitura só no tenant da sessão; antes do tenant, só pelo hash, pela função (ADR-0047)
DROP POLICY public_lookup ON order_tracking_tokens;
CREATE POLICY tenant_select ON order_tracking_tokens FOR SELECT TO almoseis_app USING (tenant_id = app_tenant_id());

CREATE FUNCTION resolve_tracking_token(p_token_hash bpchar) RETURNS TABLE (tenant_id uuid, expires_at timestamptz)
  LANGUAGE sql STABLE SECURITY DEFINER
  SET search_path = public, pg_temp
  AS $$ SELECT t.tenant_id, t.expires_at FROM order_tracking_tokens t WHERE t.token_hash = p_token_hash $$;
ALTER FUNCTION resolve_tracking_token(bpchar) OWNER TO almoseis_migrator;
REVOKE ALL ON FUNCTION resolve_tracking_token(bpchar) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION resolve_tracking_token(bpchar) TO almoseis_app;
CREATE POLICY resolve_tracking_token_select ON order_tracking_tokens FOR SELECT TO almoseis_migrator USING (true);

-- Cache de geocodificação: hash com segredo e coordenadas com 4 casas (ADR-0048 item 11, ADR-0052 item 5).
-- As linhas antigas (SHA-256 sem segredo, 6 casas) saem; a RLS é FORCE, então o FORCE sai só durante o DELETE.
ALTER TABLE geocode_cache_entries NO FORCE ROW LEVEL SECURITY;
DELETE FROM geocode_cache_entries;
ALTER TABLE geocode_cache_entries FORCE ROW LEVEL SECURITY;
ALTER TABLE geocode_cache_entries
  ALTER COLUMN latitude TYPE numeric(7,4),
  ALTER COLUMN longitude TYPE numeric(7,4);
