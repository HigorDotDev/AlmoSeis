-- GERADO por backend/tools/Sync-DdlMigration.ps1 a partir de docs/05-ddl.md. NÃO EDITE À MÃO.
-- Perfil público e configuração da página (ADR-0044; campos da taxa por distância do ADR-0048, usados na fatia 4)
CREATE TABLE storefront_settings (
  tenant_id                 uuid           PRIMARY KEY REFERENCES tenants (id),
  public_name               varchar(80)    NOT NULL CHECK (char_length(public_name) >= 2),
  is_page_active            boolean        NOT NULL DEFAULT false,
  is_accepting_orders       boolean        NOT NULL DEFAULT true,
  is_delivery_enabled       boolean        NOT NULL DEFAULT true,
  offers_delivery           boolean        NOT NULL DEFAULT true,
  offers_pickup             boolean        NOT NULL DEFAULT true,
  contact_phone             varchar(16)    CHECK (contact_phone ~ '^\+[1-9][0-9]{7,14}$'),
  hours_text                varchar(120),
  address_street            varchar(120),
  address_number            varchar(10),
  address_complement        varchar(60),
  address_neighborhood      varchar(60),
  address_city              varchar(80),
  address_state             char(2)        CHECK (address_state ~ '^[A-Z]{2}$'),
  address_zip_code          char(8)        CHECK (address_zip_code ~ '^[0-9]{8}$'),
  latitude                  numeric(9,6)   CHECK (latitude BETWEEN -90 AND 90),
  longitude                 numeric(9,6)   CHECK (longitude BETWEEN -180 AND 180),
  delivery_fee_mode         text           NOT NULL DEFAULT 'DISTANCE' CHECK (delivery_fee_mode IN ('DISTANCE', 'FIXED')),
  price_per_km              numeric(10,2)  CHECK (price_per_km > 0),
  minimum_delivery_fee      numeric(12,2)  NOT NULL DEFAULT 0 CHECK (minimum_delivery_fee >= 0),
  max_delivery_distance_km  numeric(4,1)   CHECK (max_delivery_distance_km > 0),
  updated_at                timestamptz    NOT NULL,
  version                   integer        NOT NULL DEFAULT 1,
  CONSTRAINT ck_storefront_settings_modes CHECK (offers_delivery OR offers_pickup),
  CONSTRAINT ck_storefront_settings_coordinates CHECK ((latitude IS NULL) = (longitude IS NULL)),
  CONSTRAINT ck_storefront_settings_active_address CHECK (
    NOT is_page_active
    OR (address_street IS NOT NULL AND address_number IS NOT NULL AND address_neighborhood IS NOT NULL
        AND address_city IS NOT NULL AND address_state IS NOT NULL))
);

-- Slug único global, atual e antigos (ADR-0044 itens 12 a 15; ADR-0045 item 4)
CREATE TABLE store_slugs (
  slug           varchar(40)  PRIMARY KEY,
  tenant_id      uuid         NOT NULL REFERENCES tenants (id),
  is_current     boolean      NOT NULL,
  created_at     timestamptz  NOT NULL,
  replaced_at    timestamptz,
  blocked_until  timestamptz,
  CONSTRAINT ck_store_slugs_format CHECK (
        slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$' AND char_length(slug) BETWEEN 3 AND 40 AND slug !~ '^[0-9-]+$'),
  CONSTRAINT ck_store_slugs_replaced CHECK (is_current = (replaced_at IS NULL))
);
CREATE UNIQUE INDEX ux_store_slugs_current ON store_slugs (tenant_id) WHERE is_current;
CREATE INDEX ix_store_slugs_tenant ON store_slugs (tenant_id);

-- Token de acompanhamento: só o hash (ADR-0047 itens 1 a 4)
CREATE TABLE order_tracking_tokens (
  token_hash  char(64)     PRIMARY KEY,
  tenant_id   uuid         NOT NULL,
  order_id    uuid         NOT NULL,
  created_at  timestamptz  NOT NULL,
  expires_at  timestamptz  NOT NULL,
  CONSTRAINT ux_order_tracking_tokens_order UNIQUE (order_id),
  CONSTRAINT ck_order_tracking_tokens_expiry CHECK (expires_at > created_at),
  CONSTRAINT fk_order_tracking_tokens_order FOREIGN KEY (tenant_id, order_id) REFERENCES orders (tenant_id, id)
);
CREATE INDEX ix_order_tracking_tokens_expires ON order_tracking_tokens (expires_at);

-- Entrada do pedido da página: hash do IP com sal diário, por 7 dias (ADR-0045 item 14, ADR-0052 item 5)
CREATE TABLE web_order_intakes (
  order_id    uuid         PRIMARY KEY,
  tenant_id   uuid         NOT NULL,
  ip_hash     char(64)     NOT NULL,
  created_at  timestamptz  NOT NULL,
  CONSTRAINT fk_web_order_intakes_order FOREIGN KEY (tenant_id, order_id) REFERENCES orders (tenant_id, id)
);
CREATE INDEX ix_web_order_intakes_ip ON web_order_intakes (tenant_id, ip_hash);
CREATE INDEX ix_web_order_intakes_created ON web_order_intakes (created_at);

-- Pedido da página: forma prevista, troco, risco e anonimização (ADR-0044 item 19, ADR-0045 item 12, ADR-0052 item 3)
ALTER TABLE orders
  ADD COLUMN expected_payment_method  text           CHECK (expected_payment_method IN ('CASH', 'PIX', 'CARD')),
  ADD COLUMN change_for               numeric(12,2)  CHECK (change_for > 0),
  ADD COLUMN risk_flag                text           CHECK (risk_flag IN ('CAPTCHA_UNAVAILABLE')),
  ADD COLUMN anonymized_at            timestamptz;
ALTER TABLE orders
  ADD CONSTRAINT ck_orders_change_for CHECK (change_for IS NULL OR expected_payment_method = 'CASH'),
  ADD CONSTRAINT ck_orders_web_fields CHECK (
    origin = 'WEB' OR (expected_payment_method IS NULL AND change_for IS NULL AND risk_flag IS NULL));
-- Retenção (job de plataforma, todos os tenants): pedidos da página que não viraram venda
CREATE INDEX ix_orders_web_retention ON orders (coalesce(rejected_at, expired_at, withdrawn_at))
  WHERE origin = 'WEB' AND acceptance_status IN ('REJECTED', 'EXPIRED', 'WITHDRAWN') AND anonymized_at IS NULL;
-- Limite de pedidos aguardando aceite por telefone (ADR-0045 item 13)
CREATE INDEX ix_orders_awaiting_phone ON orders (tenant_id, customer_phone)
  WHERE acceptance_status = 'AWAITING_ACCEPTANCE';

-- Bebidas e adicionais podem ser escondidos da página (ADR-0044 item 5)
ALTER TABLE products ADD COLUMN is_visible_on_page boolean NOT NULL DEFAULT true;

-- Idempotência anônima da página (ADR-0045 item 10)
ALTER TABLE idempotency_records ALTER COLUMN user_id DROP NOT NULL;

-- Tenants existentes: página inativa e slug gerado do nome (ADR-0044 itens 11 a 14).
-- A RLS de tenants é FORCE: sem tenant na sessão, o dono não veria nenhuma linha. O FORCE sai só durante a leitura.
ALTER TABLE tenants NO FORCE ROW LEVEL SECURITY;
DO $$
DECLARE
  t          record;
  base       text;
  city       text;
  candidate  text;
  n          integer;
  reserved   text[] := ARRAY[
    'api', 'app', 'admin', 'www', 'web', 'static', 'assets', '_next', 'cdn', 'img', 'login', 'entrar', 'sair', 'cadastro',
    'conta', 'minha-conta', 'restaurantes', 'restaurante', 'cidades', 'cidade', 'categorias', 'perto-de-mim', 'buscar',
    'busca', 'pedido', 'pedidos', 'p', 'r', 's', 'ajuda', 'suporte', 'contato', 'termos', 'privacidade', 'lgpd', 'blog',
    'sobre', 'precos', 'planos', 'parceiros', 'almoseis', 'health', 'status', 'webhooks', 'sitemap.xml', 'robots.txt',
    'favicon.ico', '.well-known'];
BEGIN
  FOR t IN SELECT id, name, tenants.city AS city_name, created_at FROM tenants ORDER BY created_at, id LOOP
    base := trim(both '-' from regexp_replace(
      translate(lower(t.name), 'áàâãäåéèêëíìîïóòôõöúùûüçñý', 'aaaaaaeeeeiiiiooooouuuucny'), '[^a-z0-9]+', '-', 'g'));
    city := trim(both '-' from regexp_replace(
      translate(lower(t.city_name), 'áàâãäåéèêëíìîïóòôõöúùûüçñý', 'aaaaaaeeeeiiiiooooouuuucny'), '[^a-z0-9]+', '-', 'g'));
    base := trim(both '-' from left(base, 40));
    IF char_length(base) < 3 OR base ~ '^[0-9-]+$' OR base = ANY (reserved) THEN
      base := trim(both '-' from left(trim(both '-' from base || '-' || city), 40));
    END IF;
    IF char_length(base) < 3 OR base ~ '^[0-9-]+$' THEN
      base := 'loja';
    END IF;

    candidate := base;
    IF EXISTS (SELECT 1 FROM store_slugs WHERE slug = candidate) OR candidate = ANY (reserved) THEN
      candidate := trim(both '-' from left(base || '-' || city, 40));
    END IF;
    n := 1;
    WHILE EXISTS (SELECT 1 FROM store_slugs WHERE slug = candidate) OR candidate = ANY (reserved) LOOP
      n := n + 1;
      candidate := trim(both '-' from left(base, 39 - char_length(n::text))) || '-' || n;
    END LOOP;

    INSERT INTO store_slugs (slug, tenant_id, is_current, created_at) VALUES (candidate, t.id, true, t.created_at);
    INSERT INTO storefront_settings (tenant_id, public_name, updated_at) VALUES (t.id, left(t.name, 80), t.created_at);
  END LOOP;
END $$;
ALTER TABLE tenants FORCE ROW LEVEL SECURITY;

-- RLS: tabelas de tenant
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['storefront_settings', 'web_order_intakes'] LOOP
    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %I TO almoseis_app '
      'USING (tenant_id = app_tenant_id()) WITH CHECK (tenant_id = app_tenant_id())', t);
    EXECUTE format(
      'CREATE POLICY platform_access ON %I TO almoseis_platform USING (true) WITH CHECK (true)', t);
  END LOOP;
END $$;

-- RLS: tabelas de plataforma resolvidas antes do tenant. Leitura aberta; escrita só no tenant da sessão.
ALTER TABLE store_slugs ENABLE ROW LEVEL SECURITY;
ALTER TABLE store_slugs FORCE ROW LEVEL SECURITY;
CREATE POLICY public_lookup ON store_slugs FOR SELECT TO almoseis_app USING (true);
CREATE POLICY tenant_insert ON store_slugs FOR INSERT TO almoseis_app WITH CHECK (tenant_id = app_tenant_id());
CREATE POLICY tenant_update ON store_slugs FOR UPDATE TO almoseis_app
  USING (tenant_id = app_tenant_id()) WITH CHECK (tenant_id = app_tenant_id());
CREATE POLICY platform_access ON store_slugs TO almoseis_platform USING (true) WITH CHECK (true);

ALTER TABLE order_tracking_tokens ENABLE ROW LEVEL SECURITY;
ALTER TABLE order_tracking_tokens FORCE ROW LEVEL SECURITY;
CREATE POLICY public_lookup ON order_tracking_tokens FOR SELECT TO almoseis_app USING (true);
CREATE POLICY tenant_insert ON order_tracking_tokens FOR INSERT TO almoseis_app WITH CHECK (tenant_id = app_tenant_id());
CREATE POLICY platform_access ON order_tracking_tokens TO almoseis_platform USING (true) WITH CHECK (true);

-- Grants (as tabelas novas não estão no GRANT ... ON ALL TABLES da 0001)
GRANT SELECT ON storefront_settings, store_slugs, order_tracking_tokens, web_order_intakes TO almoseis_app, almoseis_platform;
GRANT INSERT, UPDATE ON storefront_settings, store_slugs TO almoseis_app;
GRANT INSERT ON order_tracking_tokens, web_order_intakes TO almoseis_app;
-- Retenção (ADR-0047 item 4, ADR-0052 item 5): token vencido e hash do IP com mais de 7 dias
GRANT DELETE ON order_tracking_tokens, web_order_intakes TO almoseis_platform;
