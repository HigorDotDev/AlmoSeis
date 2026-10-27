-- GERADO por backend/tools/Sync-DdlMigration.ps1 a partir de docs/05-ddl.md. NÃƒO EDITE Ã€ MÃƒO.
CREATE OR REPLACE FUNCTION app_tenant_id() RETURNS uuid
  LANGUAGE sql STABLE
  AS $$ SELECT NULLIF(current_setting('app.tenant_id', true), '')::uuid $$;

CREATE OR REPLACE FUNCTION app_user_id() RETURNS uuid
  LANGUAGE sql STABLE
  AS $$ SELECT NULLIF(current_setting('app.user_id', true), '')::uuid $$;

-- Só durante o aceite de convite (§5.11): permite localizar o convite pelo código sem tenant no contexto.
CREATE OR REPLACE FUNCTION app_invitation_code() RETURNS text
  LANGUAGE sql STABLE
  AS $$ SELECT NULLIF(current_setting('app.invitation_code', true), '') $$;

CREATE TABLE tenants (
  id               uuid         PRIMARY KEY,
  name             varchar(120) NOT NULL,
  document_type    text         NOT NULL CHECK (document_type IN ('CPF', 'CNPJ')),
  document_number  varchar(14)  NOT NULL,
  city             varchar(80)  NOT NULL,
  state            char(2)      NOT NULL CHECK (state ~ '^[A-Z]{2}$'),
  time_zone        varchar(64)  NOT NULL DEFAULT 'America/Sao_Paulo',
  -- Aceite dos Termos e do contrato de tratamento de dados no autocadastro (ADR-0018, ADR-0024)
  terms_version              varchar(20)  NOT NULL,
  terms_accepted_at          timestamptz  NOT NULL,
  terms_accepted_by_user_id  uuid         NOT NULL,  -- FK adicionada após users
  created_at       timestamptz  NOT NULL,
  version          integer      NOT NULL DEFAULT 1,
  CONSTRAINT ck_tenants_document CHECK (
       (document_type = 'CPF'  AND document_number ~ '^[0-9]{11}$')
    OR (document_type = 'CNPJ' AND document_number ~ '^[0-9]{14}$'))
);
CREATE INDEX ix_tenants_document ON tenants (document_number);

CREATE TABLE users (
  id                      uuid         PRIMARY KEY,
  full_name               varchar(120) NOT NULL,
  email                   varchar(254),
  normalized_email        varchar(254),
  email_confirmed         boolean      NOT NULL DEFAULT false,
  phone_number            varchar(16)  CHECK (phone_number ~ '^\+[1-9][0-9]{7,14}$'),
  phone_number_confirmed  boolean      NOT NULL DEFAULT false,
  password_hash           text         NOT NULL,
  security_stamp          varchar(64)  NOT NULL,
  concurrency_stamp       varchar(64)  NOT NULL,
  lockout_enabled         boolean      NOT NULL DEFAULT true,
  lockout_end             timestamptz,
  access_failed_count     integer      NOT NULL DEFAULT 0 CHECK (access_failed_count >= 0),
  is_platform_admin       boolean      NOT NULL DEFAULT false,
  created_at              timestamptz  NOT NULL,
  CONSTRAINT ck_users_contact CHECK (email IS NOT NULL OR phone_number IS NOT NULL),
  CONSTRAINT ck_users_normalized_email CHECK ((email IS NULL) = (normalized_email IS NULL))
);
CREATE UNIQUE INDEX ux_users_normalized_email ON users (normalized_email) WHERE normalized_email IS NOT NULL;
CREATE UNIQUE INDEX ux_users_phone_number ON users (phone_number) WHERE phone_number IS NOT NULL;

ALTER TABLE tenants ADD CONSTRAINT fk_tenants_terms_accepted_by
  FOREIGN KEY (terms_accepted_by_user_id) REFERENCES users (id);

CREATE TABLE tenant_settings (
  tenant_id                   uuid           PRIMARY KEY REFERENCES tenants (id),
  kilo_price_per_kg           numeric(10,2)  CHECK (kilo_price_per_kg > 0),
  default_delivery_fee        numeric(12,2)  NOT NULL DEFAULT 0 CHECK (default_delivery_fee >= 0),
  enabled_payment_methods     text[]         NOT NULL DEFAULT ARRAY['CASH', 'PIX', 'CARD'],
  shared_device_idle_seconds  integer        NOT NULL DEFAULT 120 CHECK (shared_device_idle_seconds BETWEEN 30 AND 3600),
  version                     integer        NOT NULL DEFAULT 1,
  CONSTRAINT ck_tenant_settings_payment_methods CHECK (
        cardinality(enabled_payment_methods) >= 1
    AND enabled_payment_methods <@ ARRAY['CASH', 'PIX', 'CARD'])
);

CREATE TABLE tenant_employees (
  id                          uuid         PRIMARY KEY,
  tenant_id                   uuid         NOT NULL REFERENCES tenants (id),
  user_id                     uuid         NOT NULL REFERENCES users (id),
  display_name                varchar(60)  NOT NULL,
  status                      text         NOT NULL CHECK (status IN ('ACTIVE', 'INACTIVE')),
  pin_hash                    text,
  pin_failed_attempts         integer      NOT NULL DEFAULT 0 CHECK (pin_failed_attempts >= 0),
  pin_locked_until            timestamptz,
  security_stamp              uuid         NOT NULL,
  joined_at                   timestamptz  NOT NULL,
  deactivated_at              timestamptz,
  deactivated_by_employee_id  uuid,
  version                     integer      NOT NULL DEFAULT 1,
  CONSTRAINT ux_tenant_employees_tenant_id UNIQUE (tenant_id, id),
  CONSTRAINT ux_tenant_employees_user UNIQUE (tenant_id, user_id),
  CONSTRAINT ck_tenant_employees_deactivation CHECK ((status = 'INACTIVE') = (deactivated_at IS NOT NULL)),
  CONSTRAINT fk_tenant_employees_deactivated_by FOREIGN KEY (tenant_id, deactivated_by_employee_id)
    REFERENCES tenant_employees (tenant_id, id)
);
CREATE INDEX ix_tenant_employees_user ON tenant_employees (user_id);

CREATE TABLE tenant_employee_roles (
  tenant_id           uuid  NOT NULL,
  tenant_employee_id  uuid  NOT NULL,
  role                text  NOT NULL CHECK (role IN ('OWNER', 'MANAGER', 'ATTENDANT', 'KITCHEN', 'CASHIER')),
  PRIMARY KEY (tenant_employee_id, role),
  CONSTRAINT fk_tenant_employee_roles_employee FOREIGN KEY (tenant_id, tenant_employee_id)
    REFERENCES tenant_employees (tenant_id, id)
);

CREATE TABLE employee_invitations (
  id                      uuid         PRIMARY KEY,
  tenant_id               uuid         NOT NULL REFERENCES tenants (id),
  purpose                 text         NOT NULL CHECK (purpose IN ('INVITE', 'PASSWORD_RESET')),
  code                    char(6)      NOT NULL CHECK (code ~ '^[A-HJ-NP-Z2-9]{6}$'),
  roles                   text[],
  target_employee_id      uuid,
  status                  text         NOT NULL CHECK (status IN ('PENDING', 'ACCEPTED', 'REVOKED', 'EXPIRED')),
  expires_at              timestamptz  NOT NULL,
  created_by_employee_id  uuid         NOT NULL,
  created_at              timestamptz  NOT NULL,
  accepted_by_user_id     uuid         REFERENCES users (id),
  accepted_at             timestamptz,
  CONSTRAINT ck_employee_invitations_purpose CHECK (
       (purpose = 'INVITE' AND roles IS NOT NULL AND cardinality(roles) >= 1
          AND roles <@ ARRAY['OWNER', 'MANAGER', 'ATTENDANT', 'KITCHEN', 'CASHIER'] AND target_employee_id IS NULL)
    OR (purpose = 'PASSWORD_RESET' AND roles IS NULL AND target_employee_id IS NOT NULL)),
  CONSTRAINT ck_employee_invitations_accepted CHECK (
    (status = 'ACCEPTED') = (accepted_at IS NOT NULL AND accepted_by_user_id IS NOT NULL)),
  CONSTRAINT fk_employee_invitations_created_by FOREIGN KEY (tenant_id, created_by_employee_id)
    REFERENCES tenant_employees (tenant_id, id),
  CONSTRAINT fk_employee_invitations_target FOREIGN KEY (tenant_id, target_employee_id)
    REFERENCES tenant_employees (tenant_id, id)
);
-- Unicidade global entre convites pendentes: o aceite localiza o convite só pelo código.
CREATE UNIQUE INDEX ux_employee_invitations_pending_code ON employee_invitations (code) WHERE status = 'PENDING';
CREATE INDEX ix_employee_invitations_tenant ON employee_invitations (tenant_id, status);

CREATE TABLE shared_devices (
  id                         uuid         PRIMARY KEY,
  tenant_id                  uuid         NOT NULL REFERENCES tenants (id),
  installation_id            varchar(64)  NOT NULL,
  name                       varchar(40)  NOT NULL,
  platform                   text         NOT NULL CHECK (platform IN ('ANDROID', 'IOS')),
  secret_hash                char(64)     NOT NULL,  -- SHA-256 do deviceSecret; o segredo só aparece no registro (06 §6.3)
  registered_by_employee_id  uuid         NOT NULL,
  registered_at              timestamptz  NOT NULL,
  revoked_at                 timestamptz,
  CONSTRAINT ux_shared_devices_tenant_id UNIQUE (tenant_id, id),
  CONSTRAINT fk_shared_devices_registered_by FOREIGN KEY (tenant_id, registered_by_employee_id)
    REFERENCES tenant_employees (tenant_id, id)
);
CREATE UNIQUE INDEX ux_shared_devices_installation ON shared_devices (tenant_id, installation_id) WHERE revoked_at IS NULL;

-- Tabela de identidade: sem RLS de tenant, acessada só pelo módulo Identity (04 §4.12).
CREATE TABLE refresh_tokens (
  id                    uuid         PRIMARY KEY,
  user_id               uuid         NOT NULL REFERENCES users (id),
  tenant_id             uuid         NOT NULL REFERENCES tenants (id),
  tenant_employee_id    uuid         NOT NULL,
  installation_id       varchar(64)  NOT NULL,
  shared_device_id      uuid,
  token_hash            char(64)     NOT NULL,
  family_id             uuid         NOT NULL,
  created_at            timestamptz  NOT NULL,
  expires_at            timestamptz  NOT NULL,
  revoked_at            timestamptz,
  replaced_by_token_id  uuid         REFERENCES refresh_tokens (id),
  CONSTRAINT ux_refresh_tokens_hash UNIQUE (token_hash),
  CONSTRAINT ck_refresh_tokens_expiry CHECK (expires_at > created_at),
  CONSTRAINT fk_refresh_tokens_employee FOREIGN KEY (tenant_id, tenant_employee_id)
    REFERENCES tenant_employees (tenant_id, id),
  CONSTRAINT fk_refresh_tokens_shared_device FOREIGN KEY (tenant_id, shared_device_id)
    REFERENCES shared_devices (tenant_id, id)
);
CREATE INDEX ix_refresh_tokens_family ON refresh_tokens (family_id);
CREATE INDEX ix_refresh_tokens_employee ON refresh_tokens (tenant_employee_id) WHERE revoked_at IS NULL;
CREATE INDEX ix_refresh_tokens_expires ON refresh_tokens (expires_at);

CREATE TABLE subscriptions (
  id                       uuid           PRIMARY KEY,
  tenant_id                uuid           NOT NULL UNIQUE REFERENCES tenants (id),
  status                   text           NOT NULL CHECK (status IN ('TRIALING', 'ACTIVE', 'PAST_DUE', 'SUSPENDED', 'CANCELED')),
  plan_code                varchar(30)    NOT NULL,
  monthly_price            numeric(12,2)  NOT NULL CHECK (monthly_price >= 0),
  trial_ends_at            timestamptz    NOT NULL,
  current_period_ends_at   timestamptz,
  past_due_since           timestamptz,
  suspended_at             timestamptz,
  canceled_at              timestamptz,
  gateway_customer_id      varchar(64),
  gateway_subscription_id  varchar(64),
  version                  integer        NOT NULL DEFAULT 1,
  CONSTRAINT ck_subscriptions_past_due  CHECK (status <> 'PAST_DUE'  OR past_due_since IS NOT NULL),
  CONSTRAINT ck_subscriptions_suspended CHECK (status <> 'SUSPENDED' OR suspended_at IS NOT NULL),
  CONSTRAINT ck_subscriptions_canceled  CHECK (status <> 'CANCELED'  OR canceled_at IS NOT NULL)
);
CREATE UNIQUE INDEX ux_subscriptions_gateway ON subscriptions (gateway_subscription_id) WHERE gateway_subscription_id IS NOT NULL;
CREATE INDEX ix_subscriptions_status ON subscriptions (status);

-- claim_key = SHA-256 (hex) do documento ou telefone normalizado do proprietário (ADR-0017, ADR-0024).
CREATE TABLE trial_claims (
  claim_key   char(64)     PRIMARY KEY,
  tenant_id   uuid         NOT NULL REFERENCES tenants (id),
  claimed_at  timestamptz  NOT NULL
);

CREATE TABLE billing_webhook_events (
  id                uuid         PRIMARY KEY,
  gateway           varchar(20)  NOT NULL,
  gateway_event_id  varchar(100) NOT NULL,
  event_type        varchar(60)  NOT NULL,
  tenant_id         uuid         REFERENCES tenants (id),
  payload           jsonb        NOT NULL,
  received_at       timestamptz  NOT NULL,
  processed_at      timestamptz,
  CONSTRAINT ux_billing_webhook_events_gateway_event UNIQUE (gateway, gateway_event_id)
);
CREATE INDEX ix_billing_webhook_events_pending ON billing_webhook_events (received_at) WHERE processed_at IS NULL;

CREATE TABLE products (
  id                    uuid           PRIMARY KEY,
  tenant_id             uuid           NOT NULL REFERENCES tenants (id),
  kind                  text           NOT NULL CHECK (kind IN ('MAIN_DISH', 'SIDE_DISH', 'BEVERAGE', 'ADDON')),
  name                  varchar(80)    NOT NULL,
  description           varchar(300),
  category              varchar(40),
  default_price         numeric(12,2)  CHECK (default_price >= 0),
  requires_preparation  boolean        NOT NULL,
  image_storage_key     varchar(200),
  is_active             boolean        NOT NULL DEFAULT true,
  is_available          boolean        NOT NULL DEFAULT true,
  created_at            timestamptz    NOT NULL,
  updated_at            timestamptz    NOT NULL,
  version               integer        NOT NULL DEFAULT 1,
  CONSTRAINT ux_products_tenant_id UNIQUE (tenant_id, id),
  CONSTRAINT ck_products_price_by_kind CHECK (
       (kind IN ('BEVERAGE', 'ADDON') AND default_price IS NOT NULL)
    OR (kind = 'MAIN_DISH')
    OR (kind = 'SIDE_DISH' AND default_price IS NULL))
);
CREATE UNIQUE INDEX ux_products_active_name ON products (tenant_id, kind, lower(name)) WHERE is_active;
CREATE INDEX ix_products_tenant_kind ON products (tenant_id, kind) WHERE is_active;

CREATE TABLE marmita_sizes (
  id          uuid           PRIMARY KEY,
  tenant_id   uuid           NOT NULL REFERENCES tenants (id),
  name        varchar(20)    NOT NULL,
  price       numeric(12,2)  NOT NULL CHECK (price > 0),
  sort_order  integer        NOT NULL DEFAULT 0,
  is_active   boolean        NOT NULL DEFAULT true,
  version     integer        NOT NULL DEFAULT 1,
  CONSTRAINT ux_marmita_sizes_tenant_id UNIQUE (tenant_id, id)
);
CREATE UNIQUE INDEX ux_marmita_sizes_active_name ON marmita_sizes (tenant_id, lower(name)) WHERE is_active;

CREATE TABLE menus (
  id                      uuid         PRIMARY KEY,
  tenant_id               uuid         NOT NULL REFERENCES tenants (id),
  menu_type               text         NOT NULL CHECK (menu_type IN ('MARMITA', 'PF')),
  kind                    text         NOT NULL CHECK (kind IN ('DAILY', 'WEEKLY_TEMPLATE')),
  date                    date,
  day_of_week             smallint     CHECK (day_of_week BETWEEN 1 AND 7),
  source_menu_id          uuid,
  created_by_employee_id  uuid,        -- nulo quando materializado pelo sistema (ADR-0038)
  created_at              timestamptz  NOT NULL,
  updated_at              timestamptz  NOT NULL,
  version                 integer      NOT NULL DEFAULT 1,
  CONSTRAINT ux_menus_tenant_id UNIQUE (tenant_id, id),
  CONSTRAINT ck_menus_kind CHECK (
       (kind = 'DAILY'           AND date IS NOT NULL AND day_of_week IS NULL)
    OR (kind = 'WEEKLY_TEMPLATE' AND date IS NULL     AND day_of_week IS NOT NULL)),
  CONSTRAINT fk_menus_source FOREIGN KEY (tenant_id, source_menu_id) REFERENCES menus (tenant_id, id),
  CONSTRAINT fk_menus_created_by FOREIGN KEY (tenant_id, created_by_employee_id)
    REFERENCES tenant_employees (tenant_id, id)
);
CREATE UNIQUE INDEX ux_menus_daily ON menus (tenant_id, menu_type, date) WHERE kind = 'DAILY';
CREATE UNIQUE INDEX ux_menus_weekly ON menus (tenant_id, menu_type, day_of_week) WHERE kind = 'WEEKLY_TEMPLATE';

-- Regras que dependem de outra tabela ficam no agregado Menu (04 §4.6):
-- section = products.kind; pf_price obrigatório em menu PF + MAIN_DISH e nulo nos demais casos.
CREATE TABLE menu_items (
  id            uuid           PRIMARY KEY,
  tenant_id     uuid           NOT NULL,
  menu_id       uuid           NOT NULL,
  product_id    uuid           NOT NULL,
  section       text           NOT NULL CHECK (section IN ('MAIN_DISH', 'SIDE_DISH')),
  pf_price      numeric(12,2)  CHECK (pf_price >= 0),
  is_available  boolean        NOT NULL DEFAULT true,
  sort_order    integer        NOT NULL DEFAULT 0,
  CONSTRAINT ux_menu_items_product UNIQUE (menu_id, product_id),
  CONSTRAINT ck_menu_items_pf_price CHECK (section = 'MAIN_DISH' OR pf_price IS NULL),
  CONSTRAINT fk_menu_items_menu FOREIGN KEY (tenant_id, menu_id) REFERENCES menus (tenant_id, id),
  CONSTRAINT fk_menu_items_product FOREIGN KEY (tenant_id, product_id) REFERENCES products (tenant_id, id)
);
CREATE INDEX ix_menu_items_product ON menu_items (tenant_id, product_id);

CREATE TABLE customers (
  id             uuid         PRIMARY KEY,
  tenant_id      uuid         NOT NULL REFERENCES tenants (id),
  name           varchar(80)  NOT NULL,
  phone_number   varchar(16)  CHECK (phone_number ~ '^\+[1-9][0-9]{7,14}$'),
  notes          varchar(300),
  anonymized_at  timestamptz,
  created_at     timestamptz  NOT NULL,
  version        integer      NOT NULL DEFAULT 1,
  CONSTRAINT ux_customers_tenant_id UNIQUE (tenant_id, id),
  CONSTRAINT ck_customers_anonymized CHECK (anonymized_at IS NULL OR (phone_number IS NULL AND notes IS NULL))
);
CREATE UNIQUE INDEX ux_customers_phone ON customers (tenant_id, phone_number) WHERE phone_number IS NOT NULL;
CREATE INDEX ix_customers_name ON customers (tenant_id, lower(name));

CREATE TABLE customer_addresses (
  id            uuid          PRIMARY KEY,
  tenant_id     uuid          NOT NULL,
  customer_id   uuid          NOT NULL,
  label         varchar(30),
  street        varchar(120)  NOT NULL,
  number        varchar(10),
  complement    varchar(60),
  neighborhood  varchar(60),
  reference     varchar(120),
  is_default    boolean       NOT NULL DEFAULT false,
  CONSTRAINT fk_customer_addresses_customer FOREIGN KEY (tenant_id, customer_id) REFERENCES customers (tenant_id, id)
);
CREATE INDEX ix_customer_addresses_customer ON customer_addresses (tenant_id, customer_id);
-- Um endereço padrão por cliente. Adiável (checado no commit): trocar o padrão desmarca um e marca outro
-- na mesma transação, e a ordem dos UPDATEs não é garantida pelo EF. Índice único parcial não pode ser adiável.
ALTER TABLE customer_addresses ADD CONSTRAINT ux_customer_addresses_default
  EXCLUDE USING btree (customer_id WITH =) WHERE (is_default) DEFERRABLE INITIALLY DEFERRED;

CREATE TABLE business_days (
  id                     uuid           PRIMARY KEY,
  tenant_id              uuid           NOT NULL REFERENCES tenants (id),
  business_date          date           NOT NULL,
  status                 text           NOT NULL CHECK (status IN ('OPEN', 'CLOSED')),
  opening_balance        numeric(12,2)  NOT NULL CHECK (opening_balance >= 0),
  opened_by_employee_id  uuid           NOT NULL,
  opened_at              timestamptz    NOT NULL,
  closed_by_employee_id  uuid,
  closed_at              timestamptz,
  version                integer        NOT NULL DEFAULT 1,
  CONSTRAINT ux_business_days_tenant_id UNIQUE (tenant_id, id),
  -- Alvo das FKs (tenant_id, business_day_id, business_date): garante que a data copiada no pedido/pagamento é a do expediente.
  CONSTRAINT ux_business_days_tenant_id_date UNIQUE (tenant_id, id, business_date),
  CONSTRAINT ck_business_days_closed CHECK (
    (status = 'CLOSED') = (closed_at IS NOT NULL AND closed_by_employee_id IS NOT NULL)),
  CONSTRAINT fk_business_days_opened_by FOREIGN KEY (tenant_id, opened_by_employee_id)
    REFERENCES tenant_employees (tenant_id, id),
  CONSTRAINT fk_business_days_closed_by FOREIGN KEY (tenant_id, closed_by_employee_id)
    REFERENCES tenant_employees (tenant_id, id)
);
-- No máximo um expediente aberto por tenant (ADR-0019). business_date NÃO é única (ADR-0039).
CREATE UNIQUE INDEX ux_business_days_open ON business_days (tenant_id) WHERE status = 'OPEN';
CREATE INDEX ix_business_days_date ON business_days (tenant_id, business_date);

-- Fonte do número do pedido (ADR-0039):
-- INSERT ... ON CONFLICT (tenant_id, business_date) DO UPDATE SET last_order_number = business_date_counters.last_order_number + 1 RETURNING last_order_number
CREATE TABLE business_date_counters (
  tenant_id          uuid     NOT NULL REFERENCES tenants (id),
  business_date      date     NOT NULL,
  last_order_number  integer  NOT NULL CHECK (last_order_number >= 0),
  PRIMARY KEY (tenant_id, business_date)
);

CREATE TABLE orders (
  id                            uuid           PRIMARY KEY,
  tenant_id                     uuid           NOT NULL REFERENCES tenants (id),
  business_day_id               uuid           NOT NULL,
  business_date                 date           NOT NULL,
  order_number                  integer        NOT NULL CHECK (order_number > 0),
  order_type                    text           NOT NULL CHECK (order_type IN ('MARMITA', 'PF', 'KILO')),
  origin                        text           NOT NULL CHECK (origin IN ('APP')),
  fulfillment_type              text           NOT NULL CHECK (fulfillment_type IN ('DELIVERY', 'PICKUP', 'DINE_IN')),
  customer_id                   uuid,
  customer_name                 varchar(80),
  customer_phone                varchar(16)    CHECK (customer_phone ~ '^\+[1-9][0-9]{7,14}$'),
  delivery_address              jsonb,
  table_label                   varchar(10),
  tab_number                    varchar(10),
  notes                         varchar(300),
  delivery_fee                  numeric(12,2)  NOT NULL DEFAULT 0 CHECK (delivery_fee >= 0),
  items_total                   numeric(12,2)  NOT NULL CHECK (items_total >= 0),
  weighings_total               numeric(12,2)  NOT NULL CHECK (weighings_total >= 0),
  total_amount                  numeric(12,2)  NOT NULL,
  paid_amount                   numeric(12,2)  NOT NULL DEFAULT 0,
  is_paid                       boolean        NOT NULL DEFAULT false,
  production_status             text           CHECK (production_status IN ('OPEN', 'IN_PROGRESS', 'COMPLETED')),
  production_status_changed_at  timestamptz,
  cancellation_status           text           NOT NULL DEFAULT 'ACTIVE' CHECK (cancellation_status IN ('ACTIVE', 'CANCELED')),
  canceled_at                   timestamptz,
  canceled_by_employee_id       uuid,
  cancellation_reason           varchar(300),
  created_by_employee_id        uuid           NOT NULL,
  created_at                    timestamptz    NOT NULL,
  updated_at                    timestamptz    NOT NULL,
  version                       integer        NOT NULL DEFAULT 1,

  CONSTRAINT ux_orders_tenant_id UNIQUE (tenant_id, id),
  CONSTRAINT ux_orders_number UNIQUE (tenant_id, business_date, order_number),

  -- Totais derivados (04 §4.7, ADR-0007, ADR-0021)
  CONSTRAINT ck_orders_total CHECK (total_amount = items_total + weighings_total + delivery_fee),
  CONSTRAINT ck_orders_paid_range CHECK (paid_amount >= 0 AND paid_amount <= total_amount),
  CONSTRAINT ck_orders_is_paid CHECK (is_paid = (total_amount > 0 AND paid_amount = total_amount)),

  -- Modalidade (ADR-0005, RN-006.7)
  CONSTRAINT ck_orders_production_by_type CHECK ((order_type = 'KILO') = (production_status IS NULL)),
  CONSTRAINT ck_orders_weighings_only_kilo CHECK (order_type = 'KILO' OR weighings_total = 0),
  CONSTRAINT ck_orders_fulfillment_by_type CHECK (
       (order_type = 'MARMITA' AND fulfillment_type IN ('DELIVERY', 'PICKUP'))
    OR (order_type IN ('PF', 'KILO') AND fulfillment_type = 'DINE_IN')),
  CONSTRAINT ck_orders_marmita_customer CHECK (order_type <> 'MARMITA' OR customer_name IS NOT NULL),
  CONSTRAINT ck_orders_delivery CHECK (
       (fulfillment_type = 'DELIVERY' AND delivery_address IS NOT NULL)
    OR (fulfillment_type <> 'DELIVERY' AND delivery_address IS NULL AND delivery_fee = 0)),

  -- Cancelamento (ADR-0022)
  CONSTRAINT ck_orders_cancellation CHECK (
       (cancellation_status = 'ACTIVE'
          AND canceled_at IS NULL AND canceled_by_employee_id IS NULL AND cancellation_reason IS NULL)
    OR (cancellation_status = 'CANCELED'
          AND canceled_at IS NOT NULL AND canceled_by_employee_id IS NOT NULL
          AND cancellation_reason IS NOT NULL AND char_length(cancellation_reason) >= 5
          AND paid_amount = 0)),

  CONSTRAINT fk_orders_business_day FOREIGN KEY (tenant_id, business_day_id, business_date)
    REFERENCES business_days (tenant_id, id, business_date),
  CONSTRAINT fk_orders_customer FOREIGN KEY (tenant_id, customer_id) REFERENCES customers (tenant_id, id),
  CONSTRAINT fk_orders_created_by FOREIGN KEY (tenant_id, created_by_employee_id)
    REFERENCES tenant_employees (tenant_id, id),
  CONSTRAINT fk_orders_canceled_by FOREIGN KEY (tenant_id, canceled_by_employee_id)
    REFERENCES tenant_employees (tenant_id, id)
);
CREATE INDEX ix_orders_business_day ON orders (tenant_id, business_day_id);
CREATE INDEX ix_orders_production ON orders (tenant_id, production_status, created_at)
  WHERE order_type <> 'KILO' AND cancellation_status = 'ACTIVE';
CREATE INDEX ix_orders_customer_pending ON orders (tenant_id, customer_id)
  WHERE is_paid = false AND cancellation_status = 'ACTIVE';
CREATE INDEX ix_orders_pending ON orders (tenant_id, business_date)
  WHERE is_paid = false AND cancellation_status = 'ACTIVE';

CREATE TABLE order_items (
  id                      uuid           PRIMARY KEY,
  tenant_id               uuid           NOT NULL,
  order_id                uuid           NOT NULL,
  parent_order_item_id    uuid,
  item_kind               text           NOT NULL CHECK (item_kind IN ('MARMITA', 'PF', 'BEVERAGE', 'ADDON')),
  product_id              uuid,
  marmita_size_id         uuid,
  quantity                integer        NOT NULL CHECK (quantity BETWEEN 1 AND 99),
  unit_price              numeric(12,2)  NOT NULL CHECK (unit_price >= 0),
  line_total              numeric(12,2)  NOT NULL CHECK (line_total >= 0),
  name_snapshot           varchar(80)    NOT NULL,
  composition_snapshot    varchar(500),
  requires_preparation    boolean        NOT NULL,
  notes                   varchar(200),
  status                  text           NOT NULL CHECK (status IN ('ACTIVE', 'REMOVED')),
  created_by_employee_id  uuid           NOT NULL,
  created_at              timestamptz    NOT NULL,
  removed_by_employee_id  uuid,
  removed_at              timestamptz,
  CONSTRAINT ux_order_items_tenant_id UNIQUE (tenant_id, id),
  -- Alvo da FK do adicional filho: garante que pai e filho são do mesmo pedido.
  CONSTRAINT ux_order_items_tenant_order_id UNIQUE (tenant_id, order_id, id),
  CONSTRAINT ck_order_items_refs CHECK (
       (item_kind = 'MARMITA' AND marmita_size_id IS NOT NULL AND product_id IS NULL)
    OR (item_kind <> 'MARMITA' AND product_id IS NOT NULL AND marmita_size_id IS NULL)),
  CONSTRAINT ck_order_items_parent CHECK (parent_order_item_id IS NULL OR item_kind = 'ADDON'),
  CONSTRAINT ck_order_items_preparation CHECK (item_kind NOT IN ('MARMITA', 'PF') OR requires_preparation),
  CONSTRAINT ck_order_items_removed CHECK (
    (status = 'REMOVED') = (removed_at IS NOT NULL AND removed_by_employee_id IS NOT NULL)),
  CONSTRAINT fk_order_items_order FOREIGN KEY (tenant_id, order_id) REFERENCES orders (tenant_id, id),
  CONSTRAINT fk_order_items_parent FOREIGN KEY (tenant_id, order_id, parent_order_item_id)
    REFERENCES order_items (tenant_id, order_id, id),
  CONSTRAINT fk_order_items_product FOREIGN KEY (tenant_id, product_id) REFERENCES products (tenant_id, id),
  CONSTRAINT fk_order_items_marmita_size FOREIGN KEY (tenant_id, marmita_size_id)
    REFERENCES marmita_sizes (tenant_id, id),
  CONSTRAINT fk_order_items_created_by FOREIGN KEY (tenant_id, created_by_employee_id)
    REFERENCES tenant_employees (tenant_id, id),
  CONSTRAINT fk_order_items_removed_by FOREIGN KEY (tenant_id, removed_by_employee_id)
    REFERENCES tenant_employees (tenant_id, id)
);
CREATE INDEX ix_order_items_order ON order_items (tenant_id, order_id);

CREATE TABLE order_item_components (
  id             uuid         PRIMARY KEY,
  tenant_id      uuid         NOT NULL,
  order_item_id  uuid         NOT NULL,
  product_id     uuid         NOT NULL,
  role           text         NOT NULL CHECK (role IN ('MAIN_DISH', 'SIDE_DISH')),
  name_snapshot  varchar(80)  NOT NULL,
  CONSTRAINT fk_order_item_components_item FOREIGN KEY (tenant_id, order_item_id)
    REFERENCES order_items (tenant_id, id),
  CONSTRAINT fk_order_item_components_product FOREIGN KEY (tenant_id, product_id)
    REFERENCES products (tenant_id, id)
);
CREATE INDEX ix_order_item_components_item ON order_item_components (tenant_id, order_item_id);

CREATE TABLE weighing_entries (
  id                      uuid           PRIMARY KEY,
  tenant_id               uuid           NOT NULL,
  order_id                uuid           NOT NULL,
  weight_grams            integer        NOT NULL CHECK (weight_grams BETWEEN 1 AND 50000),
  price_per_kg            numeric(10,2)  NOT NULL CHECK (price_per_kg > 0),
  amount                  numeric(12,2)  NOT NULL,
  status                  text           NOT NULL CHECK (status IN ('ACTIVE', 'REMOVED')),
  created_by_employee_id  uuid           NOT NULL,
  created_at              timestamptz    NOT NULL,
  removed_by_employee_id  uuid,
  removed_at              timestamptz,
  -- round(numeric, 2) do PostgreSQL arredonda metade para longe do zero = MidpointRounding.AwayFromZero (ADR-0030).
  CONSTRAINT ck_weighing_entries_amount CHECK (amount = round(weight_grams * price_per_kg / 1000, 2)),
  CONSTRAINT ck_weighing_entries_removed CHECK (
    (status = 'REMOVED') = (removed_at IS NOT NULL AND removed_by_employee_id IS NOT NULL)),
  CONSTRAINT fk_weighing_entries_order FOREIGN KEY (tenant_id, order_id) REFERENCES orders (tenant_id, id),
  CONSTRAINT fk_weighing_entries_created_by FOREIGN KEY (tenant_id, created_by_employee_id)
    REFERENCES tenant_employees (tenant_id, id),
  CONSTRAINT fk_weighing_entries_removed_by FOREIGN KEY (tenant_id, removed_by_employee_id)
    REFERENCES tenant_employees (tenant_id, id)
);
CREATE INDEX ix_weighing_entries_order ON weighing_entries (tenant_id, order_id);

CREATE TABLE order_change_events (
  id                  uuid         PRIMARY KEY,
  tenant_id           uuid         NOT NULL,
  order_id            uuid         NOT NULL,
  order_version       integer      NOT NULL CHECK (order_version > 0),
  change_kind         text         NOT NULL CHECK (change_kind IN (
                        'ITEM_ADDED', 'ITEM_REMOVED', 'QUANTITY_CHANGED', 'ITEM_NOTES_CHANGED',
                        'ORDER_NOTES_CHANGED', 'WEIGHING_ADDED', 'WEIGHING_REMOVED',
                        'FULFILLMENT_CHANGED', 'ORDER_CANCELED')),
  affects_production  boolean      NOT NULL,
  details             jsonb        NOT NULL,
  actor_employee_id   uuid         NOT NULL,
  occurred_at         timestamptz  NOT NULL,
  CONSTRAINT ux_order_change_events_tenant_id UNIQUE (tenant_id, id),
  CONSTRAINT fk_order_change_events_order FOREIGN KEY (tenant_id, order_id) REFERENCES orders (tenant_id, id),
  CONSTRAINT fk_order_change_events_actor FOREIGN KEY (tenant_id, actor_employee_id)
    REFERENCES tenant_employees (tenant_id, id)
);
CREATE INDEX ix_order_change_events_order ON order_change_events (tenant_id, order_id, occurred_at);

CREATE TABLE alert_acknowledgements (
  tenant_id               uuid         NOT NULL,
  order_change_event_id   uuid         NOT NULL,
  tenant_employee_id      uuid         NOT NULL,
  acknowledged_at         timestamptz  NOT NULL,
  PRIMARY KEY (order_change_event_id, tenant_employee_id),
  CONSTRAINT fk_alert_acknowledgements_event FOREIGN KEY (tenant_id, order_change_event_id)
    REFERENCES order_change_events (tenant_id, id),
  CONSTRAINT fk_alert_acknowledgements_employee FOREIGN KEY (tenant_id, tenant_employee_id)
    REFERENCES tenant_employees (tenant_id, id)
);
CREATE INDEX ix_alert_acknowledgements_employee ON alert_acknowledgements (tenant_id, tenant_employee_id);

CREATE TABLE payments (
  id                      uuid           PRIMARY KEY,
  tenant_id               uuid           NOT NULL,
  order_id                uuid           NOT NULL,
  business_day_id         uuid           NOT NULL,
  business_date           date           NOT NULL,
  amount                  numeric(12,2)  NOT NULL CHECK (amount > 0),
  payment_method          text           NOT NULL CHECK (payment_method IN ('CASH', 'PIX', 'CARD')),
  cash_received           numeric(12,2),
  change_given            numeric(12,2),
  label                   varchar(30),
  status                  text           NOT NULL CHECK (status IN ('CONFIRMED', 'REVERSED')),
  paid_at                 timestamptz    NOT NULL,
  created_by_employee_id  uuid           NOT NULL,
  CONSTRAINT ux_payments_tenant_id UNIQUE (tenant_id, id),
  -- Alvo da FK do estorno: garante que estorno e pagamento têm o mesmo pedido, método e valor.
  CONSTRAINT ux_payments_reversal_target UNIQUE (tenant_id, id, order_id, payment_method, amount),
  CONSTRAINT ck_payments_cash CHECK (
       (cash_received IS NULL AND change_given IS NULL)
    OR (payment_method = 'CASH' AND cash_received IS NOT NULL AND change_given IS NOT NULL
          AND cash_received >= amount AND change_given = cash_received - amount)),
  CONSTRAINT fk_payments_order FOREIGN KEY (tenant_id, order_id) REFERENCES orders (tenant_id, id),
  CONSTRAINT fk_payments_business_day FOREIGN KEY (tenant_id, business_day_id, business_date)
    REFERENCES business_days (tenant_id, id, business_date),
  CONSTRAINT fk_payments_created_by FOREIGN KEY (tenant_id, created_by_employee_id)
    REFERENCES tenant_employees (tenant_id, id)
);
CREATE INDEX ix_payments_order ON payments (tenant_id, order_id);
CREATE INDEX ix_payments_business_day ON payments (tenant_id, business_day_id);

CREATE TABLE payment_reversals (
  id                      uuid           PRIMARY KEY,
  tenant_id               uuid           NOT NULL,
  payment_id              uuid           NOT NULL,
  order_id                uuid           NOT NULL,
  business_day_id         uuid           NOT NULL,
  business_date           date           NOT NULL,
  amount                  numeric(12,2)  NOT NULL CHECK (amount > 0),
  payment_method          text           NOT NULL CHECK (payment_method IN ('CASH', 'PIX', 'CARD')),
  reason                  varchar(300)   NOT NULL CHECK (char_length(reason) >= 5),
  created_by_employee_id  uuid           NOT NULL,
  created_at              timestamptz    NOT NULL,
  CONSTRAINT ux_payment_reversals_payment UNIQUE (payment_id),
  CONSTRAINT fk_payment_reversals_payment FOREIGN KEY (tenant_id, payment_id, order_id, payment_method, amount)
    REFERENCES payments (tenant_id, id, order_id, payment_method, amount),
  CONSTRAINT fk_payment_reversals_business_day FOREIGN KEY (tenant_id, business_day_id, business_date)
    REFERENCES business_days (tenant_id, id, business_date),
  CONSTRAINT fk_payment_reversals_created_by FOREIGN KEY (tenant_id, created_by_employee_id)
    REFERENCES tenant_employees (tenant_id, id)
);
CREATE INDEX ix_payment_reversals_business_day ON payment_reversals (tenant_id, business_day_id);

CREATE TABLE cash_movements (
  id                      uuid           PRIMARY KEY,
  tenant_id               uuid           NOT NULL,
  business_day_id         uuid           NOT NULL,
  movement_type           text           NOT NULL CHECK (movement_type IN ('SUPPLY', 'WITHDRAWAL')),
  amount                  numeric(12,2)  NOT NULL CHECK (amount > 0),
  description             varchar(200)   NOT NULL,
  created_by_employee_id  uuid           NOT NULL,
  created_at              timestamptz    NOT NULL,
  CONSTRAINT fk_cash_movements_business_day FOREIGN KEY (tenant_id, business_day_id)
    REFERENCES business_days (tenant_id, id),
  CONSTRAINT fk_cash_movements_created_by FOREIGN KEY (tenant_id, created_by_employee_id)
    REFERENCES tenant_employees (tenant_id, id)
);
CREATE INDEX ix_cash_movements_business_day ON cash_movements (tenant_id, business_day_id);

CREATE TABLE daily_closings (
  id                          uuid           PRIMARY KEY,
  tenant_id                   uuid           NOT NULL,
  business_day_id             uuid           NOT NULL,
  business_date               date           NOT NULL,
  order_count                 integer        NOT NULL CHECK (order_count >= 0),
  total_sold                  numeric(12,2)  NOT NULL CHECK (total_sold >= 0),
  delivery_fees_total         numeric(12,2)  NOT NULL CHECK (delivery_fees_total >= 0),
  total_received              numeric(12,2)  NOT NULL CHECK (total_received >= 0),
  received_from_previous_days numeric(12,2)  NOT NULL CHECK (received_from_previous_days >= 0),
  total_pending               numeric(12,2)  NOT NULL CHECK (total_pending >= 0),
  canceled_count              integer        NOT NULL CHECK (canceled_count >= 0),
  canceled_total              numeric(12,2)  NOT NULL CHECK (canceled_total >= 0),
  reversed_count              integer        NOT NULL CHECK (reversed_count >= 0),
  reversed_total              numeric(12,2)  NOT NULL CHECK (reversed_total >= 0),
  opening_balance             numeric(12,2)  NOT NULL CHECK (opening_balance >= 0),
  cash_received               numeric(12,2)  NOT NULL CHECK (cash_received >= 0),
  cash_reversed               numeric(12,2)  NOT NULL CHECK (cash_reversed >= 0),
  supplies                    numeric(12,2)  NOT NULL CHECK (supplies >= 0),
  withdrawals                 numeric(12,2)  NOT NULL CHECK (withdrawals >= 0),
  expected_cash               numeric(12,2)  NOT NULL,
  counted_cash                numeric(12,2)  NOT NULL CHECK (counted_cash >= 0),
  cash_difference             numeric(12,2)  NOT NULL,
  justification               varchar(500),
  closed_by_employee_id       uuid           NOT NULL,
  closed_at                   timestamptz    NOT NULL,
  CONSTRAINT ux_daily_closings_tenant_id UNIQUE (tenant_id, id),
  CONSTRAINT ux_daily_closings_business_day UNIQUE (business_day_id),
  CONSTRAINT ck_daily_closings_delivery CHECK (delivery_fees_total <= total_sold),
  CONSTRAINT ck_daily_closings_previous CHECK (received_from_previous_days <= total_received),
  CONSTRAINT ck_daily_closings_expected CHECK (
    expected_cash = opening_balance + cash_received + supplies - withdrawals - cash_reversed),
  CONSTRAINT ck_daily_closings_difference CHECK (cash_difference = counted_cash - expected_cash),
  CONSTRAINT ck_daily_closings_justification CHECK (
    cash_difference = 0 OR (justification IS NOT NULL AND char_length(justification) >= 5)),
  CONSTRAINT fk_daily_closings_business_day FOREIGN KEY (tenant_id, business_day_id, business_date)
    REFERENCES business_days (tenant_id, id, business_date),
  CONSTRAINT fk_daily_closings_closed_by FOREIGN KEY (tenant_id, closed_by_employee_id)
    REFERENCES tenant_employees (tenant_id, id)
);
CREATE INDEX ix_daily_closings_date ON daily_closings (tenant_id, business_date);

CREATE TABLE daily_closing_lines (
  id                uuid           PRIMARY KEY,
  tenant_id         uuid           NOT NULL,
  daily_closing_id  uuid           NOT NULL,
  dimension         text           NOT NULL CHECK (dimension IN ('ORDER_TYPE', 'PAYMENT_METHOD')),
  key               text           NOT NULL,
  sold              numeric(12,2)  CHECK (sold >= 0),
  received          numeric(12,2)  NOT NULL CHECK (received >= 0),
  pending           numeric(12,2)  CHECK (pending >= 0),
  reversed          numeric(12,2)  NOT NULL CHECK (reversed >= 0),
  count             integer        NOT NULL CHECK (count >= 0),
  CONSTRAINT ux_daily_closing_lines_key UNIQUE (daily_closing_id, dimension, key),
  CONSTRAINT ck_daily_closing_lines_dimension CHECK (
       (dimension = 'ORDER_TYPE' AND key IN ('MARMITA', 'PF', 'KILO')
          AND sold IS NOT NULL AND pending IS NOT NULL)
    OR (dimension = 'PAYMENT_METHOD' AND key IN ('CASH', 'PIX', 'CARD')
          AND sold IS NULL AND pending IS NULL)),
  CONSTRAINT fk_daily_closing_lines_closing FOREIGN KEY (tenant_id, daily_closing_id)
    REFERENCES daily_closings (tenant_id, id)
);

CREATE TABLE closing_adjustments (
  id                      uuid           PRIMARY KEY,
  tenant_id               uuid           NOT NULL,
  related_closing_id      uuid           NOT NULL,
  business_day_id         uuid           NOT NULL,
  adjustment_type         text           NOT NULL CHECK (adjustment_type IN ('CASH_COUNT_CORRECTION', 'OTHER')),
  amount                  numeric(12,2)  NOT NULL CHECK (amount <> 0),
  reason                  varchar(500)   NOT NULL CHECK (char_length(reason) >= 5),
  created_by_employee_id  uuid           NOT NULL,
  created_at              timestamptz    NOT NULL,
  CONSTRAINT fk_closing_adjustments_closing FOREIGN KEY (tenant_id, related_closing_id)
    REFERENCES daily_closings (tenant_id, id),
  CONSTRAINT fk_closing_adjustments_business_day FOREIGN KEY (tenant_id, business_day_id)
    REFERENCES business_days (tenant_id, id),
  CONSTRAINT fk_closing_adjustments_created_by FOREIGN KEY (tenant_id, created_by_employee_id)
    REFERENCES tenant_employees (tenant_id, id)
);
CREATE INDEX ix_closing_adjustments_closing ON closing_adjustments (tenant_id, related_closing_id);

-- Sem FKs: a auditoria não pode depender do ciclo de vida de nenhuma outra tabela.
CREATE TABLE audit_logs (
  id                 uuid         PRIMARY KEY,
  tenant_id          uuid,
  origin             text         NOT NULL CHECK (origin IN ('APP', 'PLATFORM_ADMIN', 'SYSTEM', 'BILLING_WEBHOOK')),
  actor_user_id      uuid,
  actor_employee_id  uuid,
  shared_device_id   uuid,
  operation_type     varchar(60)  NOT NULL,
  entity_type        varchar(40)  NOT NULL,
  entity_id          uuid         NOT NULL,
  order_id           uuid,
  before             jsonb,
  after              jsonb,
  correlation_id     varchar(64)  NOT NULL,
  occurred_at        timestamptz  NOT NULL,
  CONSTRAINT ck_audit_logs_tenant CHECK (origin IN ('PLATFORM_ADMIN', 'BILLING_WEBHOOK') OR tenant_id IS NOT NULL)
);
CREATE INDEX ix_audit_logs_order ON audit_logs (tenant_id, order_id, occurred_at) WHERE order_id IS NOT NULL;
CREATE INDEX ix_audit_logs_entity ON audit_logs (tenant_id, entity_type, entity_id, occurred_at);
CREATE INDEX ix_audit_logs_occurred ON audit_logs (tenant_id, occurred_at);

-- seq por tenant (ADR-0032): UPDATE tenant_sync_counters SET last_seq = last_seq + 1 WHERE tenant_id = $1 RETURNING last_seq
CREATE TABLE tenant_sync_counters (
  tenant_id  uuid    PRIMARY KEY REFERENCES tenants (id),
  last_seq   bigint  NOT NULL DEFAULT 0 CHECK (last_seq >= 0)
);

CREATE TABLE change_feed_entries (
  tenant_id       uuid         NOT NULL REFERENCES tenants (id),
  seq             bigint       NOT NULL CHECK (seq > 0),
  entity_type     varchar(40)  NOT NULL,
  entity_id       uuid         NOT NULL,
  entity_version  integer      NOT NULL,
  change_kind     text         NOT NULL CHECK (change_kind IN ('UPSERT', 'DELETE')),
  occurred_at     timestamptz  NOT NULL,
  PRIMARY KEY (tenant_id, seq)
);
CREATE INDEX ix_change_feed_entries_occurred ON change_feed_entries (occurred_at);

CREATE TABLE idempotency_records (
  id               uuid          PRIMARY KEY,
  tenant_id        uuid          NOT NULL REFERENCES tenants (id),
  key              uuid          NOT NULL,
  user_id          uuid          NOT NULL,
  endpoint         varchar(120)  NOT NULL,
  request_hash     char(64)      NOT NULL,
  status           text          NOT NULL CHECK (status IN ('IN_PROGRESS', 'COMPLETED')),
  response_status  smallint,
  response_body    jsonb,
  resource_id      uuid,
  created_at       timestamptz   NOT NULL,
  expires_at       timestamptz   NOT NULL,
  CONSTRAINT ux_idempotency_records_key UNIQUE (tenant_id, key),
  CONSTRAINT ck_idempotency_records_completed CHECK ((status = 'COMPLETED') = (response_status IS NOT NULL)),
  CONSTRAINT ck_idempotency_records_expiry CHECK (expires_at > created_at)
);
CREATE INDEX ix_idempotency_records_expires ON idempotency_records (expires_at);

CREATE TABLE outbox_messages (
  id            uuid          PRIMARY KEY,
  tenant_id     uuid          NOT NULL REFERENCES tenants (id),
  event_type    varchar(80)   NOT NULL,
  payload       jsonb         NOT NULL,
  seq           bigint,
  occurred_at   timestamptz   NOT NULL,
  processed_at  timestamptz,
  attempts      integer       NOT NULL DEFAULT 0 CHECK (attempts >= 0),
  last_error    varchar(500)
);
-- Leitura do dispatcher: ... WHERE processed_at IS NULL ORDER BY occurred_at FOR UPDATE SKIP LOCKED LIMIT n
CREATE INDEX ix_outbox_messages_pending ON outbox_messages (occurred_at) WHERE processed_at IS NULL;
CREATE INDEX ix_outbox_messages_processed ON outbox_messages (processed_at) WHERE processed_at IS NOT NULL;

DO $$
DECLARE
  t text;
  tenant_tables text[] := ARRAY[
    'tenant_settings', 'employee_invitations', 'shared_devices',
    'subscriptions',
    'products', 'marmita_sizes', 'menus', 'menu_items',
    'customers', 'customer_addresses',
    'business_days', 'business_date_counters',
    'orders', 'order_items', 'order_item_components', 'weighing_entries',
    'order_change_events', 'alert_acknowledgements',
    'payments', 'payment_reversals', 'cash_movements',
    'daily_closings', 'daily_closing_lines', 'closing_adjustments',
    'audit_logs', 'tenant_sync_counters', 'change_feed_entries',
    'idempotency_records', 'outbox_messages'];
BEGIN
  FOREACH t IN ARRAY tenant_tables LOOP
    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %I TO almoseis_app '
      'USING (tenant_id = app_tenant_id()) WITH CHECK (tenant_id = app_tenant_id())', t);
    EXECUTE format(
      'CREATE POLICY platform_access ON %I TO almoseis_platform USING (true) WITH CHECK (true)', t);
  END LOOP;
END $$;

-- tenants: a "coluna de tenant" é o próprio id.
ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenants FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON tenants TO almoseis_app
  USING (id = app_tenant_id() OR id IN (SELECT tenant_id FROM tenant_employees WHERE user_id = app_user_id()))
  WITH CHECK (id = app_tenant_id());
CREATE POLICY platform_access ON tenants TO almoseis_platform USING (true) WITH CHECK (true);

-- Vínculo: tenant ativo OU linhas do próprio usuário (ADR-0026 item 9). Escrita só no tenant ativo.
ALTER TABLE tenant_employees ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenant_employees FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON tenant_employees TO almoseis_app
  USING (tenant_id = app_tenant_id() OR user_id = app_user_id())
  WITH CHECK (tenant_id = app_tenant_id());
CREATE POLICY platform_access ON tenant_employees TO almoseis_platform USING (true) WITH CHECK (true);

ALTER TABLE tenant_employee_roles ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenant_employee_roles FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON tenant_employee_roles TO almoseis_app
  USING (tenant_id = app_tenant_id()
         OR tenant_employee_id IN (SELECT id FROM tenant_employees WHERE user_id = app_user_id()))
  WITH CHECK (tenant_id = app_tenant_id());
CREATE POLICY platform_access ON tenant_employee_roles TO almoseis_platform USING (true) WITH CHECK (true);

-- Convite: leitura adicional pelo código durante o aceite (ADR-0018).
CREATE POLICY invitation_by_code ON employee_invitations FOR SELECT TO almoseis_app
  USING (status = 'PENDING' AND code = app_invitation_code());

GRANT USAGE ON SCHEMA public TO almoseis_app, almoseis_platform;
GRANT EXECUTE ON FUNCTION app_tenant_id(), app_user_id(), app_invitation_code() TO almoseis_app, almoseis_platform;

-- Leitura geral
GRANT SELECT ON ALL TABLES IN SCHEMA public TO almoseis_app, almoseis_platform;
REVOKE SELECT ON billing_webhook_events FROM almoseis_app;

-- almoseis_app: cadastros e agregados editáveis (sem DELETE)
GRANT INSERT, UPDATE ON
  tenants, users, tenant_settings, tenant_employees, employee_invitations, shared_devices, refresh_tokens,
  products, marmita_sizes, menus, menu_items, customers, customer_addresses,
  business_days, business_date_counters,
  orders, order_items, order_item_components, weighing_entries,
  tenant_sync_counters, idempotency_records
TO almoseis_app;

-- almoseis_app: DELETE apenas onde a linha não tem valor histórico
GRANT DELETE ON tenant_employee_roles, menu_items, customer_addresses TO almoseis_app;
GRANT INSERT ON tenant_employee_roles TO almoseis_app;

-- almoseis_app: assinatura é lida, e só criada no autocadastro (o estado muda pelo platform, ADR-0017)
GRANT INSERT ON subscriptions, trial_claims TO almoseis_app;

-- almoseis_app: pagamentos (só a transição de status)
GRANT INSERT ON payments TO almoseis_app;
GRANT UPDATE (status) ON payments TO almoseis_app;

-- almoseis_app: somente inserção (snapshots, eventos, logs)
GRANT INSERT ON
  payment_reversals, cash_movements, daily_closings, daily_closing_lines, closing_adjustments,
  order_change_events, alert_acknowledgements,
  audit_logs, change_feed_entries, outbox_messages
TO almoseis_app;

-- almoseis_platform: assinatura, webhooks, outbox, retenção e limpeza
GRANT INSERT, UPDATE ON subscriptions, billing_webhook_events, trial_claims TO almoseis_platform;
GRANT UPDATE ON outbox_messages, employee_invitations, tenant_employees, refresh_tokens TO almoseis_platform;
GRANT INSERT ON audit_logs, tenant_sync_counters, change_feed_entries, outbox_messages TO almoseis_platform;
-- Anonimização LGPD (ADR-0024)
GRANT UPDATE ON customers, orders TO almoseis_platform;
GRANT DELETE ON customer_addresses TO almoseis_platform;
-- Limpeza por TTL (ADR-0031–0033) e tokens expirados
GRANT DELETE ON idempotency_records, change_feed_entries, outbox_messages, refresh_tokens TO almoseis_platform;
