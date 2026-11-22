-- GERADO por backend/tools/Sync-DdlMigration.ps1 a partir de docs/05-ddl.md. NÃO EDITE À MÃO.
-- Categorias do cardápio fixo (ADR-0056 item 5)
CREATE TABLE dish_categories (
  id          uuid         PRIMARY KEY,
  tenant_id   uuid         NOT NULL REFERENCES tenants (id),
  name        varchar(40)  NOT NULL CHECK (char_length(name) >= 1),
  sort_order  integer      NOT NULL DEFAULT 0,
  is_active   boolean      NOT NULL DEFAULT true,
  created_at  timestamptz  NOT NULL,
  updated_at  timestamptz  NOT NULL,
  version     integer      NOT NULL DEFAULT 1,
  CONSTRAINT ux_dish_categories_tenant_id UNIQUE (tenant_id, id)
);
CREATE UNIQUE INDEX ux_dish_categories_active_name ON dish_categories (tenant_id, lower(name)) WHERE is_active;

-- Pratos: preço único ou tamanho por prato; "vende no site" no prato sem tamanho (ADR-0056 itens 6 a 8, ADR-0057)
CREATE TABLE dishes (
  id                    uuid           PRIMARY KEY,
  tenant_id             uuid           NOT NULL REFERENCES tenants (id),
  dish_category_id      uuid           NOT NULL,
  name                  varchar(80)    NOT NULL CHECK (char_length(name) >= 1),
  description           varchar(300),
  image_storage_key     varchar(200),
  requires_preparation  boolean        NOT NULL DEFAULT true,
  has_sizes             boolean        NOT NULL DEFAULT false,
  price                 numeric(12,2)  CHECK (price > 0),
  sells_on_site         boolean        NOT NULL DEFAULT false,
  site_price            numeric(12,2)  CHECK (site_price > 0),
  sort_order            integer        NOT NULL DEFAULT 0,
  is_active             boolean        NOT NULL DEFAULT true,
  is_paused             boolean        NOT NULL DEFAULT false,
  created_at            timestamptz    NOT NULL,
  updated_at            timestamptz    NOT NULL,
  version               integer        NOT NULL DEFAULT 1,
  CONSTRAINT ux_dishes_tenant_id UNIQUE (tenant_id, id),
  CONSTRAINT ck_dishes_price CHECK (
       (NOT has_sizes AND price IS NOT NULL)
    OR (has_sizes AND price IS NULL AND NOT sells_on_site AND site_price IS NULL)),
  CONSTRAINT ck_dishes_site_price CHECK (NOT sells_on_site OR site_price IS NOT NULL),
  CONSTRAINT fk_dishes_category FOREIGN KEY (tenant_id, dish_category_id) REFERENCES dish_categories (tenant_id, id)
);
CREATE UNIQUE INDEX ux_dishes_active_name ON dishes (tenant_id, lower(name)) WHERE is_active;
CREATE INDEX ix_dishes_category ON dishes (tenant_id, dish_category_id);

-- Tamanho do prato, com preço próprio e "vende no site" (ADR-0056 item 6, ADR-0057 item 1)
CREATE TABLE dish_sizes (
  id             uuid           PRIMARY KEY,
  tenant_id      uuid           NOT NULL,
  dish_id        uuid           NOT NULL,
  name           varchar(30)    NOT NULL CHECK (char_length(name) >= 1),
  price          numeric(12,2)  NOT NULL CHECK (price > 0),
  sells_on_site  boolean        NOT NULL DEFAULT false,
  site_price     numeric(12,2)  CHECK (site_price > 0),
  sort_order     integer        NOT NULL DEFAULT 0,
  is_active      boolean        NOT NULL DEFAULT true,
  is_paused      boolean        NOT NULL DEFAULT false,
  CONSTRAINT ux_dish_sizes_tenant_id UNIQUE (tenant_id, id),
  -- Alvo da FK do componente de combo: o tamanho é do prato do componente.
  CONSTRAINT ux_dish_sizes_tenant_dish_id UNIQUE (tenant_id, dish_id, id),
  CONSTRAINT ck_dish_sizes_site_price CHECK (NOT sells_on_site OR site_price IS NOT NULL),
  CONSTRAINT fk_dish_sizes_dish FOREIGN KEY (tenant_id, dish_id) REFERENCES dishes (tenant_id, id)
);
CREATE UNIQUE INDEX ux_dish_sizes_active_name ON dish_sizes (dish_id, lower(name)) WHERE is_active;

-- Bebidas: tipo → tamanho (com preço) → sabor do tamanho (ADR-0056 itens 9 a 12, Q-305)
CREATE TABLE beverage_types (
  id          uuid         PRIMARY KEY,
  tenant_id   uuid         NOT NULL REFERENCES tenants (id),
  name        varchar(40)  NOT NULL CHECK (char_length(name) >= 1),
  sort_order  integer      NOT NULL DEFAULT 0,
  is_active   boolean      NOT NULL DEFAULT true,
  created_at  timestamptz  NOT NULL,
  updated_at  timestamptz  NOT NULL,
  version     integer      NOT NULL DEFAULT 1,
  CONSTRAINT ux_beverage_types_tenant_id UNIQUE (tenant_id, id)
);
CREATE UNIQUE INDEX ux_beverage_types_active_name ON beverage_types (tenant_id, lower(name)) WHERE is_active;

CREATE TABLE beverage_sizes (
  id                uuid           PRIMARY KEY,
  tenant_id         uuid           NOT NULL,
  beverage_type_id  uuid           NOT NULL,
  name              varchar(30)    NOT NULL CHECK (char_length(name) >= 1),
  price             numeric(12,2)  NOT NULL CHECK (price > 0),
  sells_on_site     boolean        NOT NULL DEFAULT false,
  site_price        numeric(12,2)  CHECK (site_price > 0),
  sort_order        integer        NOT NULL DEFAULT 0,
  is_active         boolean        NOT NULL DEFAULT true,
  is_paused         boolean        NOT NULL DEFAULT false,
  CONSTRAINT ux_beverage_sizes_tenant_id UNIQUE (tenant_id, id),
  CONSTRAINT ck_beverage_sizes_site_price CHECK (NOT sells_on_site OR site_price IS NOT NULL),
  CONSTRAINT fk_beverage_sizes_type FOREIGN KEY (tenant_id, beverage_type_id) REFERENCES beverage_types (tenant_id, id)
);
CREATE UNIQUE INDEX ux_beverage_sizes_active_name ON beverage_sizes (beverage_type_id, lower(name)) WHERE is_active;
CREATE INDEX ix_beverage_sizes_type ON beverage_sizes (tenant_id, beverage_type_id);

-- Sem sabor cadastrado, o tamanho é vendido sem escolha; com sabores, o sabor é obrigatório no pedido (item 11)
CREATE TABLE beverage_flavors (
  id                uuid           PRIMARY KEY,
  tenant_id         uuid           NOT NULL,
  beverage_size_id  uuid           NOT NULL,
  name              varchar(40)    NOT NULL CHECK (char_length(name) >= 1),
  extra_price       numeric(12,2)  NOT NULL DEFAULT 0 CHECK (extra_price >= 0),
  sort_order        integer        NOT NULL DEFAULT 0,
  is_active         boolean        NOT NULL DEFAULT true,
  is_paused         boolean        NOT NULL DEFAULT false,
  CONSTRAINT ux_beverage_flavors_tenant_id UNIQUE (tenant_id, id),
  -- Alvo da FK do item do pedido (fatia 3): o sabor é do tamanho escolhido.
  CONSTRAINT ux_beverage_flavors_tenant_size_id UNIQUE (tenant_id, beverage_size_id, id),
  CONSTRAINT fk_beverage_flavors_size FOREIGN KEY (tenant_id, beverage_size_id) REFERENCES beverage_sizes (tenant_id, id)
);
CREATE UNIQUE INDEX ux_beverage_flavors_active_name ON beverage_flavors (beverage_size_id, lower(name)) WHERE is_active;

-- Combo fixo com preço fechado (ADR-0056 itens 13 a 16, ADR-0057 item 4)
CREATE TABLE combos (
  id                 uuid           PRIMARY KEY,
  tenant_id          uuid           NOT NULL REFERENCES tenants (id),
  name               varchar(80)    NOT NULL CHECK (char_length(name) >= 1),
  description        varchar(300),
  image_storage_key  varchar(200),
  price              numeric(12,2)  NOT NULL CHECK (price > 0),
  sells_on_site      boolean        NOT NULL DEFAULT false,
  site_price         numeric(12,2)  CHECK (site_price > 0),
  sort_order         integer        NOT NULL DEFAULT 0,
  is_active          boolean        NOT NULL DEFAULT true,
  is_paused          boolean        NOT NULL DEFAULT false,
  created_at         timestamptz    NOT NULL,
  updated_at         timestamptz    NOT NULL,
  version            integer        NOT NULL DEFAULT 1,
  CONSTRAINT ux_combos_tenant_id UNIQUE (tenant_id, id),
  CONSTRAINT ck_combos_site_price CHECK (NOT sells_on_site OR site_price IS NOT NULL)
);
CREATE UNIQUE INDEX ux_combos_active_name ON combos (tenant_id, lower(name)) WHERE is_active;

-- Componente: prato com tamanho fixo OU tamanho de bebida (sabor escolhido no pedido)
CREATE TABLE combo_components (
  id                uuid         PRIMARY KEY,
  tenant_id         uuid         NOT NULL,
  combo_id          uuid         NOT NULL,
  kind              text         NOT NULL CHECK (kind IN ('DISH', 'BEVERAGE')),
  dish_id           uuid,
  dish_size_id      uuid,
  beverage_size_id  uuid,
  quantity          integer      NOT NULL CHECK (quantity BETWEEN 1 AND 10),
  sort_order        integer      NOT NULL DEFAULT 0,
  CONSTRAINT ck_combo_components_target CHECK (
       (kind = 'DISH'     AND dish_id IS NOT NULL AND beverage_size_id IS NULL)
    OR (kind = 'BEVERAGE' AND beverage_size_id IS NOT NULL AND dish_id IS NULL AND dish_size_id IS NULL)),
  CONSTRAINT fk_combo_components_combo FOREIGN KEY (tenant_id, combo_id) REFERENCES combos (tenant_id, id),
  CONSTRAINT fk_combo_components_dish FOREIGN KEY (tenant_id, dish_id) REFERENCES dishes (tenant_id, id),
  CONSTRAINT fk_combo_components_dish_size FOREIGN KEY (tenant_id, dish_id, dish_size_id)
    REFERENCES dish_sizes (tenant_id, dish_id, id),
  CONSTRAINT fk_combo_components_beverage_size FOREIGN KEY (tenant_id, beverage_size_id)
    REFERENCES beverage_sizes (tenant_id, id)
);
CREATE INDEX ix_combo_components_combo ON combo_components (tenant_id, combo_id);
-- Disponibilidade derivada e inativação (itens 23 e 26): quais combos usam este prato, tamanho ou bebida
CREATE INDEX ix_combo_components_dish ON combo_components (tenant_id, dish_id) WHERE dish_id IS NOT NULL;
CREATE INDEX ix_combo_components_dish_size ON combo_components (tenant_id, dish_size_id) WHERE dish_size_id IS NOT NULL;
CREATE INDEX ix_combo_components_beverage_size ON combo_components (tenant_id, beverage_size_id)
  WHERE beverage_size_id IS NOT NULL;

-- Grupos de adicionais do cardápio fixo (ADR-0056 itens 17 a 20)
CREATE TABLE addon_groups (
  id          uuid         PRIMARY KEY,
  tenant_id   uuid         NOT NULL REFERENCES tenants (id),
  name        varchar(60)  NOT NULL CHECK (char_length(name) >= 1),
  sort_order  integer      NOT NULL DEFAULT 0,
  is_active   boolean      NOT NULL DEFAULT true,
  created_at  timestamptz  NOT NULL,
  updated_at  timestamptz  NOT NULL,
  version     integer      NOT NULL DEFAULT 1,
  CONSTRAINT ux_addon_groups_tenant_id UNIQUE (tenant_id, id)
);
CREATE UNIQUE INDEX ux_addon_groups_active_name ON addon_groups (tenant_id, lower(name)) WHERE is_active;

-- Mesmo preço no balcão e no site (Q-308); pode custar 0.00
CREATE TABLE addon_options (
  id              uuid           PRIMARY KEY,
  tenant_id       uuid           NOT NULL,
  addon_group_id  uuid           NOT NULL,
  name            varchar(80)    NOT NULL CHECK (char_length(name) >= 1),
  price           numeric(12,2)  NOT NULL CHECK (price >= 0),
  sort_order      integer        NOT NULL DEFAULT 0,
  is_active       boolean        NOT NULL DEFAULT true,
  is_paused       boolean        NOT NULL DEFAULT false,
  CONSTRAINT ux_addon_options_tenant_id UNIQUE (tenant_id, id),
  CONSTRAINT fk_addon_options_group FOREIGN KEY (tenant_id, addon_group_id) REFERENCES addon_groups (tenant_id, id)
);
CREATE UNIQUE INDEX ux_addon_options_active_name ON addon_options (addon_group_id, lower(name)) WHERE is_active;
CREATE INDEX ix_addon_options_group ON addon_options (tenant_id, addon_group_id);

-- Vínculo do grupo com uma categoria OU um prato (item 18)
CREATE TABLE addon_group_links (
  id                uuid  PRIMARY KEY,
  tenant_id         uuid  NOT NULL,
  addon_group_id    uuid  NOT NULL,
  dish_category_id  uuid,
  dish_id           uuid,
  CONSTRAINT ck_addon_group_links_target CHECK (num_nonnulls(dish_category_id, dish_id) = 1),
  CONSTRAINT fk_addon_group_links_group FOREIGN KEY (tenant_id, addon_group_id) REFERENCES addon_groups (tenant_id, id),
  CONSTRAINT fk_addon_group_links_category FOREIGN KEY (tenant_id, dish_category_id)
    REFERENCES dish_categories (tenant_id, id),
  CONSTRAINT fk_addon_group_links_dish FOREIGN KEY (tenant_id, dish_id) REFERENCES dishes (tenant_id, id)
);
CREATE UNIQUE INDEX ux_addon_group_links_category ON addon_group_links (tenant_id, addon_group_id, dish_category_id)
  WHERE dish_category_id IS NOT NULL;
CREATE UNIQUE INDEX ux_addon_group_links_dish ON addon_group_links (tenant_id, addon_group_id, dish_id)
  WHERE dish_id IS NOT NULL;
-- Adicionais de um prato = grupos da categoria dele ∪ grupos do próprio prato
CREATE INDEX ix_addon_group_links_by_category ON addon_group_links (tenant_id, dish_category_id)
  WHERE dish_category_id IS NOT NULL;
CREATE INDEX ix_addon_group_links_by_dish ON addon_group_links (tenant_id, dish_id) WHERE dish_id IS NOT NULL;

-- Preço de site do tamanho de marmita (ADR-0057 item 1)
ALTER TABLE marmita_sizes
  ADD COLUMN sells_on_site  boolean        NOT NULL DEFAULT false,
  ADD COLUMN site_price     numeric(12,2)  CHECK (site_price > 0);
ALTER TABLE marmita_sizes
  ADD CONSTRAINT ck_marmita_sizes_site_price CHECK (NOT sells_on_site OR site_price IS NOT NULL);

-- Bebida do restaurante: is_visible_on_page é o "vende no site", com preço de site (Q-307); adicional sem (Q-308)
ALTER TABLE products ADD COLUMN site_price numeric(12,2) CHECK (site_price > 0);
ALTER TABLE products ADD CONSTRAINT ck_products_site_price CHECK (site_price IS NULL OR kind = 'BEVERAGE');

-- Tabela de preço do pedido (ADR-0057 item 7): APP → COUNTER, WEB → SITE
ALTER TABLE orders
  ADD COLUMN price_list text NOT NULL DEFAULT 'COUNTER' CHECK (price_list IN ('COUNTER', 'SITE'));
ALTER TABLE orders ADD CONSTRAINT ck_orders_price_list CHECK (origin = 'WEB' OR price_list = 'COUNTER');

-- Carga inicial (ADR-0057 item 15): nenhuma página muda de preço.
-- A RLS das três tabelas é FORCE: o FORCE sai só durante cada UPDATE, na mesma transação (§5.1).
ALTER TABLE marmita_sizes NO FORCE ROW LEVEL SECURITY;
UPDATE marmita_sizes SET sells_on_site = true, site_price = price WHERE is_active;
ALTER TABLE marmita_sizes FORCE ROW LEVEL SECURITY;

ALTER TABLE products NO FORCE ROW LEVEL SECURITY;
UPDATE products SET site_price = default_price
 WHERE kind = 'BEVERAGE' AND is_visible_on_page AND default_price > 0;
-- Bebida de 0.00 não tem preço de site (> 0): sai do site até o estabelecimento decidir
UPDATE products SET is_visible_on_page = false
 WHERE kind = 'BEVERAGE' AND is_visible_on_page AND site_price IS NULL;
ALTER TABLE products FORCE ROW LEVEL SECURITY;

ALTER TABLE orders NO FORCE ROW LEVEL SECURITY;
UPDATE orders SET price_list = 'SITE' WHERE origin = 'WEB';
ALTER TABLE orders FORCE ROW LEVEL SECURITY;

-- RLS: tabelas de tenant
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY[
    'dish_categories', 'dishes', 'dish_sizes', 'beverage_types', 'beverage_sizes', 'beverage_flavors',
    'combos', 'combo_components', 'addon_groups', 'addon_options', 'addon_group_links'] LOOP
    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %I TO almoseis_app '
      'USING (tenant_id = app_tenant_id()) WITH CHECK (tenant_id = app_tenant_id())', t);
    EXECUTE format(
      'CREATE POLICY platform_access ON %I TO almoseis_platform USING (true) WITH CHECK (true)', t);
  END LOOP;
END $$;

-- Grants (tabelas novas). Itens vendáveis são inativados, nunca apagados (ADR-0056 item 26);
-- componentes de combo e vínculos de grupo não têm valor histórico e são trocados na edição.
GRANT SELECT ON
  dish_categories, dishes, dish_sizes, beverage_types, beverage_sizes, beverage_flavors,
  combos, combo_components, addon_groups, addon_options, addon_group_links
TO almoseis_app, almoseis_platform;
GRANT INSERT, UPDATE ON
  dish_categories, dishes, dish_sizes, beverage_types, beverage_sizes, beverage_flavors,
  combos, combo_components, addon_groups, addon_options, addon_group_links
TO almoseis_app;
GRANT DELETE ON combo_components, addon_group_links TO almoseis_app;
