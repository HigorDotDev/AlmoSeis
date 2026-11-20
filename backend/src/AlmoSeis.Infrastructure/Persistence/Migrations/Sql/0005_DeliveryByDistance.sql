-- GERADO por backend/tools/Sync-DdlMigration.ps1 a partir de docs/05-ddl.md. NÃO EDITE À MÃO.
-- Ponto do restaurante confirmado no mapa (ADR-0048 item 3)
ALTER TABLE storefront_settings ADD COLUMN location_confirmed_at timestamptz;
ALTER TABLE storefront_settings
  ADD CONSTRAINT ck_storefront_settings_location_confirmed CHECK (location_confirmed_at IS NULL OR latitude IS NOT NULL);

-- Cotação de entrega: 15 min, apagada 24 h depois de vencer (ADR-0048 item 8, ADR-0052 item 5)
CREATE TABLE delivery_quotes (
  id                 uuid           PRIMARY KEY,
  tenant_id          uuid           NOT NULL REFERENCES tenants (id),
  address_hash       char(64)       NOT NULL,
  formatted_address  varchar(200)   NOT NULL,
  latitude           numeric(9,6)   CHECK (latitude BETWEEN -90 AND 90),
  longitude          numeric(9,6)   CHECK (longitude BETWEEN -180 AND 180),
  fee_mode           text           NOT NULL CHECK (fee_mode IN ('DISTANCE', 'FIXED')),
  distance_meters    integer        CHECK (distance_meters >= 0),
  distance_km        numeric(5,1)   CHECK (distance_km >= 0),
  price_per_km       numeric(10,2)  CHECK (price_per_km > 0),
  minimum_fee        numeric(12,2)  CHECK (minimum_fee >= 0),
  delivery_fee       numeric(12,2)  NOT NULL CHECK (delivery_fee >= 0),
  settings_version   integer        NOT NULL,
  created_at         timestamptz    NOT NULL,
  expires_at         timestamptz    NOT NULL,
  CONSTRAINT ck_delivery_quotes_expiry CHECK (expires_at > created_at),
  CONSTRAINT ck_delivery_quotes_mode CHECK (
       (fee_mode = 'DISTANCE' AND latitude IS NOT NULL AND longitude IS NOT NULL AND distance_meters IS NOT NULL
        AND distance_km IS NOT NULL AND price_per_km IS NOT NULL AND minimum_fee IS NOT NULL)
    OR (fee_mode = 'FIXED' AND latitude IS NULL AND longitude IS NULL AND distance_meters IS NULL
        AND distance_km IS NULL AND price_per_km IS NULL AND minimum_fee IS NULL))
);
CREATE INDEX ix_delivery_quotes_expires ON delivery_quotes (expires_at);

-- Cache por tenant: hash do endereço → coordenadas e metros, sem o texto (ADR-0048 item 11, ADR-0052 item 5)
CREATE TABLE geocode_cache_entries (
  tenant_id         uuid          NOT NULL REFERENCES tenants (id),
  address_hash      char(64)      NOT NULL,
  latitude          numeric(9,6)  NOT NULL CHECK (latitude BETWEEN -90 AND 90),
  longitude         numeric(9,6)  NOT NULL CHECK (longitude BETWEEN -180 AND 180),
  geocoded_at       timestamptz   NOT NULL,
  origin_latitude   numeric(9,6),
  origin_longitude  numeric(9,6),
  distance_meters   integer       CHECK (distance_meters >= 0),
  routed_at         timestamptz,
  expires_at        timestamptz   NOT NULL,
  PRIMARY KEY (tenant_id, address_hash),
  CONSTRAINT ck_geocode_cache_entries_route CHECK (
        (origin_latitude IS NULL) = (distance_meters IS NULL)
    AND (origin_longitude IS NULL) = (distance_meters IS NULL)
    AND (routed_at IS NULL) = (distance_meters IS NULL)),
  CONSTRAINT ck_geocode_cache_entries_expiry CHECK (expires_at > geocoded_at)
);
CREATE INDEX ix_geocode_cache_entries_expires ON geocode_cache_entries (expires_at);

-- Pedido: origem da taxa e snapshot da regra por distância (ADR-0048 item 20)
ALTER TABLE orders
  ADD COLUMN delivery_fee_source       text           CHECK (delivery_fee_source IN ('FIXED_DEFAULT', 'DISTANCE', 'MANUAL')),
  ADD COLUMN delivery_distance_meters  integer        CHECK (delivery_distance_meters >= 0),
  ADD COLUMN delivery_price_per_km     numeric(10,2)  CHECK (delivery_price_per_km > 0),
  ADD COLUMN delivery_minimum_fee      numeric(12,2)  CHECK (delivery_minimum_fee >= 0);
ALTER TABLE orders
  ADD CONSTRAINT ck_orders_delivery_fee_source CHECK (delivery_fee_source IS NULL OR fulfillment_type = 'DELIVERY'),
  ADD CONSTRAINT ck_orders_delivery_distance CHECK (
        (delivery_distance_meters IS NULL) = (delivery_price_per_km IS NULL)
    AND (delivery_price_per_km IS NULL) = (delivery_minimum_fee IS NULL)
    AND (delivery_fee_source IS DISTINCT FROM 'DISTANCE' OR delivery_distance_meters IS NOT NULL));

-- RLS: tabelas de tenant
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['delivery_quotes', 'geocode_cache_entries'] LOOP
    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %I TO almoseis_app '
      'USING (tenant_id = app_tenant_id()) WITH CHECK (tenant_id = app_tenant_id())', t);
    EXECUTE format(
      'CREATE POLICY platform_access ON %I TO almoseis_platform USING (true) WITH CHECK (true)', t);
  END LOOP;
END $$;

-- Grants (tabelas novas). Retenção (ADR-0052 item 5): o job de plataforma apaga cotações e cache vencidos.
GRANT SELECT ON delivery_quotes, geocode_cache_entries TO almoseis_app, almoseis_platform;
GRANT INSERT ON delivery_quotes, geocode_cache_entries TO almoseis_app;
GRANT UPDATE ON geocode_cache_entries TO almoseis_app;
GRANT DELETE ON delivery_quotes, geocode_cache_entries TO almoseis_platform;
