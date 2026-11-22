-- GERADO por backend/tools/Sync-DdlMigration.ps1 a partir de docs/05-ddl.md. NÃO EDITE À MÃO.
-- Comportamentos habilitados do tenant (ADR-0054 itens 1 a 6)
CREATE TABLE tenant_behaviors (
  tenant_id           uuid         NOT NULL REFERENCES tenants (id),
  behavior            text         NOT NULL CHECK (behavior IN ('RESTAURANT', 'SNACK_BAR')),
  origin              text         NOT NULL CHECK (origin IN ('SIGNUP', 'PLATFORM_ADMIN', 'SYSTEM')),
  enabled_at          timestamptz  NOT NULL,
  enabled_by_user_id  uuid         REFERENCES users (id),
  PRIMARY KEY (tenant_id, behavior),
  CONSTRAINT ck_tenant_behaviors_enabled_by CHECK ((origin = 'SYSTEM') = (enabled_by_user_id IS NULL))
);

-- Rótulos informativos, lista fixa por comportamento (ADR-0054 itens 7 a 11). Nenhuma regra de negócio lê esta tabela.
CREATE TABLE tenant_labels (
  tenant_id   uuid         NOT NULL,
  label       text         NOT NULL,
  behavior    text         NOT NULL,
  created_at  timestamptz  NOT NULL,
  PRIMARY KEY (tenant_id, label),
  CONSTRAINT ck_tenant_labels_label CHECK (
       (behavior = 'RESTAURANT' AND label IN ('RESTAURANT', 'MARMITARIA', 'SELF_SERVICE'))
    OR (behavior = 'SNACK_BAR'  AND label IN ('BURGER_JOINT', 'PASTEL_SHOP', 'ACAI_SHOP', 'PIZZERIA', 'SNACK_BAR',
                                              'ICE_CREAM_SHOP', 'CAFE', 'BAKERY', 'SKEWER_BAR', 'SUSHI', 'SWEETS_SHOP', 'OTHER'))),
  -- Só rótulo de comportamento habilitado no tenant (item 10)
  CONSTRAINT fk_tenant_labels_behavior FOREIGN KEY (tenant_id, behavior) REFERENCES tenant_behaviors (tenant_id, behavior)
);

-- Tenants existentes: RESTAURANT pela migração e o rótulo inicial "Restaurante" (ADR-0054 item 6, Q-303).
-- A RLS de tenants é FORCE: sem tenant na sessão, o dono não veria nenhuma linha. O FORCE sai só durante a leitura.
ALTER TABLE tenants NO FORCE ROW LEVEL SECURITY;
INSERT INTO tenant_behaviors (tenant_id, behavior, origin, enabled_at)
  SELECT t.id, 'RESTAURANT', 'SYSTEM', now() FROM tenants t
   WHERE NOT EXISTS (SELECT 1 FROM tenant_behaviors b WHERE b.tenant_id = t.id);
INSERT INTO tenant_labels (tenant_id, label, behavior, created_at)
  SELECT t.id, 'RESTAURANT', 'RESTAURANT', now() FROM tenants t
   WHERE NOT EXISTS (SELECT 1 FROM tenant_labels l WHERE l.tenant_id = t.id);
ALTER TABLE tenants FORCE ROW LEVEL SECURITY;

-- RLS: tabelas de tenant
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['tenant_behaviors', 'tenant_labels'] LOOP
    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %I TO almoseis_app '
      'USING (tenant_id = app_tenant_id()) WITH CHECK (tenant_id = app_tenant_id())', t);
    EXECUTE format(
      'CREATE POLICY platform_access ON %I TO almoseis_platform USING (true) WITH CHECK (true)', t);
  END LOOP;
END $$;

-- Grants (tabelas novas). Comportamento: só criado (autocadastro pela aplicação, acréscimo pelo admin), nunca alterado
-- nem removido nesta fase. Rótulos: o PATCH /tenant troca o conjunto (INSERT/DELETE).
GRANT SELECT ON tenant_behaviors, tenant_labels TO almoseis_app, almoseis_platform;
GRANT INSERT ON tenant_behaviors TO almoseis_app, almoseis_platform;
GRANT INSERT, DELETE ON tenant_labels TO almoseis_app;
