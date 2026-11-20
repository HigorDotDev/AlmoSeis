# 05. DDL — PostgreSQL (fonte de verdade do schema físico)

> Implementa `docs/04-modelo-dados.md` e os ADRs 0003, 0019–0023, 0026, 0030–0034, 0038 e 0039. **Os blocos `sql` deste documento, na ordem em que aparecem, formam o script completo e executável** (PostgreSQL 16+). As migrations do EF Core precisam produzir exatamente este schema: tabelas e índices pelo modelo, e funções, RLS e grants por `migrationBuilder.Sql(...)`. Mudou aqui → muda a migration, e vice-versa.

## 5.0 Convenções

| Item | Regra |
|---|---|
| Nomes | `snake_case`, tabelas no **plural** (evita as palavras reservadas `user` e `order`) |
| PK | `id uuid` (UUID v7 gerado na aplicação), sem default no banco |
| Instantes | `timestamptz`, **sem `default now()`**: o valor vem do `TimeProvider` da aplicação |
| Data operacional | `date` |
| Dinheiro | `numeric(12,2)`; preço/kg `numeric(10,2)` (ADR-0030) |
| Peso | `integer` em gramas |
| Enums | `text` + `CHECK` com valores `UPPER_SNAKE` (sem tipo `enum` do PostgreSQL) |
| Versão | `version integer not null default 1` nos agregados [V] |
| Isolamento | toda tabela de tenant tem `tenant_id`, `UNIQUE (tenant_id, id)` quando é referenciada, FKs compostas `(tenant_id, …)` e RLS (§5.11) |
| Exclusão | FKs sem `ON DELETE CASCADE`. Entidades financeiras e históricas não recebem `DELETE` do papel da aplicação (§5.12) |
| Strings | `varchar(n)` com os limites de `04-modelo-dados.md` |
| `CHECK` e `NULL` | Um `CHECK` que resulta em `NULL` **passa**. Toda condição sobre uma coluna anulável que precisa estar preenchida usa `IS NOT NULL` explícito (ex.: `justification IS NOT NULL AND char_length(justification) >= 5`) |

**Identity:** o backend usa só a tabela `users`, com um `UserStore` próprio (senha, security stamp, lockout, e-mail, telefone), **sem** `IdentityDbContext`. As tabelas `AspNetRoles`, `AspNetUserClaims` etc. não existem (ADR-0027, ADR-0028).

---

## 5.1 Papéis

Executado uma vez por ambiente por um administrador (o IaC do `devops-agent`). Os papéis são **grupos sem login**. Os usuários de login (`almoseis_app_login`…) são criados pelo IaC com senha vinda do Key Vault e entram nos grupos.

```sql
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
```

- `almoseis_migrator`: dono do schema. Roda as migrations. Correções de dados em migration usam `SET LOCAL ROLE almoseis_platform`. O **login** de migration é criado com `ALTER ROLE <login> SET role = 'almoseis_migrator'`, para que todo objeto (inclusive `__ef_migrations_history`) pertença ao papel de grupo, e não ao login. Assim a rotação do login não muda a posse.
- `almoseis_app`: a API. DML restrito (§5.12), sujeito à RLS de tenant.
- `almoseis_platform`: módulo admin, webhooks de cobrança, dispatcher do outbox e jobs de retenção e limpeza. Acessa todos os tenants por **políticas próprias** (`TO almoseis_platform USING (true)`), não por `BYPASSRLS`. Isso funciona igual no Azure Flexible Server, onde não há superusuário.

## 5.2 Funções de contexto

O interceptor de conexão do EF (ADR-0026) executa a cada abertura de conexão:

```text
SELECT set_config('app.tenant_id', @tenantId, false), set_config('app.user_id', @userId, false);
```

Na devolução da conexão ao pool, as variáveis são limpas (`RESET app.tenant_id; RESET app.user_id; RESET app.invitation_code`).

```sql
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
```

---

## 5.3 Identity

```sql
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
```

## 5.4 Billing

```sql
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
```

## 5.5 Catalog

```sql
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
```

## 5.6 Menu (ADR-0038)

```sql
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
```

## 5.7 Ordering — clientes

```sql
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
```

## 5.8 Closing — expediente (ADR-0019, ADR-0039)

Vem antes de `orders` porque pedidos e pagamentos referenciam o expediente.

```sql
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
```

## 5.9 Ordering — pedidos (ADR-0005, 0007, 0020–0023, 0030)

```sql
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
```

## 5.10 Payments e Closing (ADR-0013–0015, 0021, 0022)

```sql
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
```

## 5.11 Audit e Sync (ADR-0031–0033)

```sql
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
```

---

## 5.12 Row-Level Security (ADR-0026)

Quatro grupos de tabelas (04 §4.12):

1. **Tenant:** `tenant_id = app_tenant_id()` para `almoseis_app`; acesso total para `almoseis_platform`.
2. **Vínculo:** `tenant_employees` e `tenant_employee_roles` também liberam as linhas do próprio usuário (`app_user_id()`). Assim o login lista os estabelecimentos do usuário.
3. **Convite:** `employee_invitations` também libera o convite `PENDING` cujo código está em `app.invitation_code`. O aceite define esse código, lê o convite, passa a usar o `tenant_id` dele e só então grava.
4. **Sem RLS:** `users`, `refresh_tokens` (identidade), `trial_claims`, `billing_webhook_events` (plataforma). O acesso é controlado por grants (§5.13).

```sql
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
```

## 5.13 Grants

Regra: o papel da aplicação **não recebe `DELETE`** em nenhuma tabela financeira ou histórica. Tabelas de snapshot e de log são **somente inserção**. `payments` só pode ter a coluna `status` alterada.

```sql
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
```

**Não existe** grant de `UPDATE`/`DELETE` em `audit_logs`, `daily_closings`, `daily_closing_lines`, `closing_adjustments`, `payment_reversals` e `cash_movements` para nenhum papel da aplicação (ADR-0008, ADR-0014, ADR-0031).

---

## 5.14 Operações SQL de referência

Comandos que o backend executa por SQL explícito (fora do LINQ comum). Ficam aqui para que os handlers e os testes usem exatamente a mesma forma.

```text
-- Próximo número de pedido (ADR-0039), na transação de criação do pedido
INSERT INTO business_date_counters (tenant_id, business_date, last_order_number)
VALUES (@tenantId, @businessDate, 1)
ON CONFLICT (tenant_id, business_date)
DO UPDATE SET last_order_number = business_date_counters.last_order_number + 1
RETURNING last_order_number;

-- Próximo seq do feed (ADR-0032), uma vez por transação de escrita do tenant
UPDATE tenant_sync_counters SET last_seq = last_seq + 1 WHERE tenant_id = @tenantId RETURNING last_seq;

-- Leitura do feed
SELECT DISTINCT ON (entity_type, entity_id) entity_type, entity_id, entity_version, change_kind, seq
FROM change_feed_entries
WHERE tenant_id = @tenantId AND seq > @since
ORDER BY entity_type, entity_id, seq DESC;

-- Dispatcher do outbox (papel almoseis_platform)
SELECT * FROM outbox_messages
WHERE processed_at IS NULL
ORDER BY occurred_at
FOR UPDATE SKIP LOCKED
LIMIT 100;
```

Cada entidade [S] alterada na transação recebe seu próprio `seq`, porque a PK do feed é `(tenant_id, seq)`. Então o handler incrementa o contador **uma vez por entidade alterada**. A trava de linha obtida no primeiro incremento segura as outras escritas do mesmo tenant até o commit, e é isso que garante que a ordem de commit acompanha a ordem de `seq` (ADR-0032). O evento do outbox leva o `seq` da entidade principal da operação.

## 5.15 Rastreabilidade das regras no banco

| Regra | Onde |
|---|---|
| Isolamento de tenant (ADR-0026) | RLS §5.12 + FKs compostas |
| Um expediente aberto (ADR-0019) | `ux_business_days_open` |
| Data da venda/recebimento = data do expediente (ADR-0015) | FKs `(tenant_id, business_day_id, business_date)` |
| Número do pedido por data (ADR-0039) | `ux_orders_number` + `business_date_counters` |
| Totais e quitação (ADR-0007, ADR-0021) | `ck_orders_total`, `ck_orders_paid_range`, `ck_orders_is_paid` |
| Só cancela sem pagamento (ADR-0022) | `ck_orders_cancellation` |
| Quilo fora da produção (RN-006.7) | `ck_orders_production_by_type` |
| Arredondamento da pesagem (ADR-0030) | `ck_weighing_entries_amount` |
| Troco (ADR-0021) | `ck_payments_cash` |
| Estorno único e coerente (ADR-0022) | `ux_payment_reversals_payment` + `fk_payment_reversals_payment` |
| Conciliação do caixa (ADR-0013) | `ck_daily_closings_expected`, `ck_daily_closings_difference`, `ck_daily_closings_justification` |
| Fechamento imutável (ADR-0014) | grants §5.13 (só `INSERT`) |
| Auditoria append-only (ADR-0031) | grants §5.13 |
| Idempotência (ADR-0033) | `ux_idempotency_records_key` |
| Pedido não aceito sem número, produção, cliente, pagamento nem cancelamento (ADR-0046) | `ck_orders_acceptance`, `ck_orders_production_by_type` (migration 0003) |
| Recusa, expiração e desistência coerentes (ADR-0046) | `ck_orders_rejection`, `ck_orders_expired`, `ck_orders_withdrawn` (0003) |
| Atendimento só de entrega/retirada aceita, e coerente com o tipo (ADR-0046) | `ck_orders_fulfillment`, `ck_orders_fulfillment_times` (0003) |
| Slug único global, um atual por tenant, formato (ADR-0044) | PK de `store_slugs`, `ux_store_slugs_current`, `ck_store_slugs_format` (0004) |
| Página ativa só com endereço (ADR-0044 item 11) | `ck_storefront_settings_active_address` (0004) |
| Slug e token resolvíveis antes do tenant, gravados só pelo próprio tenant (ADR-0045) | políticas `public_lookup`/`tenant_insert`/`tenant_update` (0004) |
| Troco só em dinheiro; campos da página só em pedido `WEB` (ADR-0044 item 19) | `ck_orders_change_for`, `ck_orders_web_fields` (0004) |

## 5.16 Validação

O script foi validado em 2026-10-27 num PostgreSQL 16 (Docker): os blocos `sql` deste documento aplicados do zero (36 tabelas e 65 políticas na `0001`; 40 e 76 com as alterações até a `0004`; 42 e 80 até a `0005`) e depois o teste de fumaça [`05-ddl-smoke-test.sql`](05-ddl-smoke-test.sql), que roda como `almoseis_app`/`almoseis_platform` e confere 45 regras (22 da `0001`, 9 da `0003`, 10 da `0004`, 4 da `0005`): isolamento entre dois tenants, plataforma do aparelho compartilhado, login, convite por código, totais, cancelamento, troco, estorno, conciliação, imutabilidade, casos com `NULL` e, desde a `0003`, o pedido da página (sem número, sem produção e sem autor enquanto aguarda aceite; recusa coerente; atendimento só em entrega) e, desde a `0004`, a página de pedidos (endereço para ativar, slug atual único e no formato, troco só em dinheiro, campos da página só em `WEB`, idempotência anônima, slug e token resolvíveis por outro tenant mas nunca gravados por ele, perfil e hash do IP isolados) e, desde a `0005`, a taxa por distância (cotação por distância completa, confirmação do ponto só com ponto, origem da taxa só em entrega, cotação e cache isolados). Toda mudança neste documento deve repetir os dois passos:

```text
docker run -d --name almoseis-ddl-test -e POSTGRES_PASSWORD=test postgres:16
# concatenar os blocos sql deste arquivo, na ordem, em schema.sql; então:
psql -U postgres -v ON_ERROR_STOP=1 -f schema.sql && psql -U postgres -f docs/05-ddl-smoke-test.sql
```

**Regras que ficam só na aplicação** (dependem de outra tabela ou de estado): `menu_items.section = products.kind`; `pf_price` obrigatório em menu PF; pedido com ao menos um item ou pesagem ativa; `items_total`/`weighings_total` = soma dos filhos; `paid_amount` = soma dos pagamentos `CONFIRMED`; pedido pago imutável; pelo menos um `OWNER` ativo; método de pagamento habilitado no tenant; adicional filho só de `MARMITA`/`PF`.

## 5.17 Alterações (ADR-0042)

Cada alteração é uma migration nova, gerada do bloco `sql` da sua subseção por `pwsh backend/tools/Sync-DdlMigration.ps1 -Target NNNN_Nome.sql`. O título da subseção começa pelo nome do script. Os blocos acima desta seção formam a `0001_InitialSchema.sql`, que não muda depois de aplicada em homologação. Validação: as migrations em ordem, do zero, e depois o `05-ddl-smoke-test.sql` (o `SchemaTests` faz isso).

### 0002_SharedDeviceWebPlatform — 2026-11-19 (ADR-0053)

O app do restaurante no navegador registra o aparelho compartilhado com `platform = 'WEB'`. O `CHECK` da coluna tem o nome padrão do PostgreSQL.

```sql
ALTER TABLE shared_devices DROP CONSTRAINT shared_devices_platform_check;
ALTER TABLE shared_devices ADD CONSTRAINT shared_devices_platform_check CHECK (platform IN ('ANDROID', 'IOS', 'WEB'));
```

### 0003_WebOrderAcceptance — 2026-11-20 (ADR-0046)

Aceite do pedido da página e status de atendimento/entrega (EP-002, F-206 e F-207). Duas dimensões novas no pedido, independentes da produção, do financeiro e do cancelamento (ADR-0006):

- **`acceptance_status`**: todo pedido do app nasce (e todo pedido existente fica) `ACCEPTED`, com `accepted_at = created_at` e `accepted_by_employee_id = created_by_employee_id` (ver "Expand/contract" abaixo). Pedido não aceito não tem número, nem produção, nem pagamento, nem cliente vinculado, e não pode ser cancelado (`ck_orders_acceptance`). `AWAITING_PAYMENT` é da Fase 2 (ADR-0050): o `CHECK` já o aceita, mas nenhum caminho o grava ainda.
- **`order_number`** passa a aceitar nulo: obrigatório quando `ACCEPTED`, nulo nos demais. `ux_orders_number` continua igual: no PostgreSQL, `UNIQUE` trata nulos como distintos, então a unicidade vale só para os números atribuídos.
- **`fulfillment_status`**: só em `DELIVERY`/`PICKUP` aceitos (nulo em `DINE_IN` e antes do aceite); `PENDING` nos pedidos existentes. "Pronto para retirar" é derivado e não tem coluna. Os instantes de cada passo (`out_for_delivery_at`, `delivered_at`, `picked_up_at`) acompanham o status: voltar um passo limpa o instante do passo desfeito.
- **Autor opcional:** o pedido da página não tem funcionário (`created_by_employee_id` nulo só em `origin = 'WEB'`), e os itens montados pelo cliente também não (`order_items.created_by_employee_id` nulo; a regra "só em pedido `WEB`" fica na aplicação, porque depende de outra tabela).
- **Recusa:** motivo da lista, texto de 5 a 200 (obrigatório em `OTHER`); `BUSINESS_DAY_CLOSED` é a recusa do sistema na finalização do fechamento, sem funcionário.
- **Fechamento:** contagens informativas no snapshot, fora do vendido (`not_accepted_count` inclui os `auto_rejected_count` recusados pela própria finalização; `unmarked_delivery_count` = entregas aceitas e ativas sem `DELIVERED`). Os fechamentos existentes ficam com 0.
- **Auditoria:** origem `WEB` (envio e desistência pelo cliente, sem ator).
- **Índices:** `ix_orders_acceptance_deadline` serve ao job de expiração, que roda no papel de plataforma e varre todos os tenants (por isso sem `tenant_id` na frente); `ix_orders_awaiting_acceptance` serve à faixa "Novos do site" (`GET /orders?acceptanceStatus=AWAITING_ACCEPTANCE`) e à finalização do fechamento.

Os `CHECK`s de coluna têm o nome padrão do PostgreSQL (`orders_origin_check`, `audit_logs_origin_check`).

**Expand/contract (09 §9.4):** a migration roda antes do deploy, e a API anterior continua criando pedidos sem `accepted_at` e sem `fulfillment_status`. Por isso esta alteração **não** exige esses dois campos em pedido aceito: o `CHECK` só proíbe preenchê-los fora do lugar. A API desta versão sempre os preenche e trata o nulo legado como "aceito na criação" e `PENDING`. **Contração pendente** (próxima release, com esta API em produção): preencher os nulos criados na janela do deploy (`accepted_at = created_at`, `fulfillment_status = 'PENDING'` em entrega/retirada aceita) e apertar `ck_orders_acceptance` (`accepted_at` obrigatório em `ACCEPTED`) e `ck_orders_fulfillment` (obrigatório em entrega/retirada aceita).

**Correção de dados nos pedidos existentes:** a RLS de `orders` é `FORCE` (§5.12), então nem o dono da tabela enxerga linhas sem `app.tenant_id`. A convenção de §5.1 (`SET LOCAL ROLE almoseis_platform`) não serve aqui, porque o papel de migration não é membro de `almoseis_platform` (nem em §5.1 nem no `infra/db/init`). A migration tira o `FORCE` só em volta do `UPDATE`, na mesma transação: o dono passa a enxergar todas as linhas, e o `FORCE` volta antes do commit.

```sql
-- Origem WEB e autor opcional (pedido e itens da página não têm funcionário)
ALTER TABLE orders DROP CONSTRAINT orders_origin_check;
ALTER TABLE orders ADD CONSTRAINT orders_origin_check CHECK (origin IN ('APP', 'WEB'));
ALTER TABLE orders ALTER COLUMN created_by_employee_id DROP NOT NULL;
ALTER TABLE orders ADD CONSTRAINT ck_orders_created_by CHECK (origin = 'WEB' OR created_by_employee_id IS NOT NULL);
ALTER TABLE order_items ALTER COLUMN created_by_employee_id DROP NOT NULL;

-- Aceite (ADR-0046 itens 1 a 17) e atendimento (itens 2 e 18)
ALTER TABLE orders
  ADD COLUMN acceptance_status             text         NOT NULL DEFAULT 'ACCEPTED'
    CHECK (acceptance_status IN ('AWAITING_PAYMENT', 'AWAITING_ACCEPTANCE', 'ACCEPTED', 'REJECTED', 'EXPIRED', 'WITHDRAWN')),
  ADD COLUMN acceptance_deadline           timestamptz,
  ADD COLUMN accepted_at                   timestamptz,
  ADD COLUMN accepted_by_employee_id       uuid,
  ADD COLUMN estimated_ready_minutes       integer      CHECK (estimated_ready_minutes IN (15, 30, 45, 60)),
  ADD COLUMN rejection_reason_code         text
    CHECK (rejection_reason_code IN ('ITEM_UNAVAILABLE', 'OUT_OF_DELIVERY_AREA', 'TOO_BUSY', 'CLOSING_SOON',
                                     'SUSPECTED_PRANK', 'OTHER', 'BUSINESS_DAY_CLOSED')),
  ADD COLUMN rejection_message             varchar(200) CHECK (char_length(rejection_message) >= 5),
  ADD COLUMN rejected_at                   timestamptz,
  ADD COLUMN rejected_by_employee_id       uuid,
  ADD COLUMN expired_at                    timestamptz,
  ADD COLUMN withdrawn_at                  timestamptz,
  ADD COLUMN fulfillment_status            text
    CHECK (fulfillment_status IN ('PENDING', 'OUT_FOR_DELIVERY', 'DELIVERED', 'PICKED_UP')),
  ADD COLUMN fulfillment_status_changed_at timestamptz,
  ADD COLUMN out_for_delivery_at           timestamptz,
  ADD COLUMN delivered_at                  timestamptz,
  ADD COLUMN picked_up_at                  timestamptz;

-- Pedidos existentes: todos do app, aceitos na criação; entrega e retirada começam pendentes.
-- O papel de migration é dono da tabela, mas a RLS é FORCE: sem tenant na sessão, o UPDATE não veria nenhuma linha.
-- O FORCE sai só durante o UPDATE, na mesma transação da migration.
ALTER TABLE orders NO FORCE ROW LEVEL SECURITY;
UPDATE orders
   SET accepted_at = created_at,
       accepted_by_employee_id = created_by_employee_id,
       fulfillment_status = CASE WHEN fulfillment_type IN ('DELIVERY', 'PICKUP') THEN 'PENDING' END,
       fulfillment_status_changed_at = CASE WHEN fulfillment_type IN ('DELIVERY', 'PICKUP') THEN created_at END;
ALTER TABLE orders FORCE ROW LEVEL SECURITY;

-- Número só depois do aceite; recusados, expirados e desistências nunca recebem (itens 4 e 8)
ALTER TABLE orders ALTER COLUMN order_number DROP NOT NULL;

-- Produção só de pedido aceito (item 4): substitui a regra "nulo só em KILO"
ALTER TABLE orders DROP CONSTRAINT ck_orders_production_by_type;
ALTER TABLE orders ADD CONSTRAINT ck_orders_production_by_type CHECK (
  (order_type = 'KILO' OR acceptance_status <> 'ACCEPTED') = (production_status IS NULL));

ALTER TABLE orders
  ADD CONSTRAINT ck_orders_acceptance CHECK (
       (acceptance_status = 'ACCEPTED' AND order_number IS NOT NULL)
    OR (acceptance_status <> 'ACCEPTED' AND order_number IS NULL AND accepted_at IS NULL
          AND accepted_by_employee_id IS NULL AND estimated_ready_minutes IS NULL
          AND customer_id IS NULL AND cancellation_status = 'ACTIVE' AND paid_amount = 0)),
  ADD CONSTRAINT ck_orders_acceptance_deadline CHECK (
    acceptance_status NOT IN ('AWAITING_PAYMENT', 'AWAITING_ACCEPTANCE') OR acceptance_deadline IS NOT NULL),
  ADD CONSTRAINT ck_orders_rejection CHECK (
       (acceptance_status = 'REJECTED' AND rejected_at IS NOT NULL AND rejection_reason_code IS NOT NULL
          AND (rejection_reason_code <> 'OTHER' OR rejection_message IS NOT NULL)
          AND (rejection_reason_code = 'BUSINESS_DAY_CLOSED') = (rejected_by_employee_id IS NULL))
    OR (acceptance_status <> 'REJECTED' AND rejected_at IS NULL AND rejection_reason_code IS NULL
          AND rejection_message IS NULL AND rejected_by_employee_id IS NULL)),
  ADD CONSTRAINT ck_orders_expired CHECK ((acceptance_status = 'EXPIRED') = (expired_at IS NOT NULL)),
  ADD CONSTRAINT ck_orders_withdrawn CHECK ((acceptance_status = 'WITHDRAWN') = (withdrawn_at IS NOT NULL)),
  ADD CONSTRAINT ck_orders_fulfillment CHECK (
        (fulfillment_status IS NULL OR (acceptance_status = 'ACCEPTED' AND fulfillment_type <> 'DINE_IN'))
    AND (fulfillment_status IS NULL) = (fulfillment_status_changed_at IS NULL)
    AND (NOT coalesce(fulfillment_status IN ('OUT_FOR_DELIVERY', 'DELIVERED'), false) OR fulfillment_type = 'DELIVERY')
    AND (NOT coalesce(fulfillment_status = 'PICKED_UP', false) OR fulfillment_type = 'PICKUP')),
  ADD CONSTRAINT ck_orders_fulfillment_times CHECK (
        (out_for_delivery_at IS NOT NULL) = coalesce(fulfillment_status IN ('OUT_FOR_DELIVERY', 'DELIVERED'), false)
    AND (delivered_at IS NOT NULL) = coalesce(fulfillment_status = 'DELIVERED', false)
    AND (picked_up_at IS NOT NULL) = coalesce(fulfillment_status = 'PICKED_UP', false)),
  ADD CONSTRAINT fk_orders_accepted_by FOREIGN KEY (tenant_id, accepted_by_employee_id)
    REFERENCES tenant_employees (tenant_id, id),
  ADD CONSTRAINT fk_orders_rejected_by FOREIGN KEY (tenant_id, rejected_by_employee_id)
    REFERENCES tenant_employees (tenant_id, id);

-- Job de expiração (plataforma, todos os tenants) e faixa "Novos do site" / finalização do fechamento
CREATE INDEX ix_orders_acceptance_deadline ON orders (acceptance_deadline)
  WHERE acceptance_status = 'AWAITING_ACCEPTANCE';
CREATE INDEX ix_orders_awaiting_acceptance ON orders (tenant_id, business_day_id)
  WHERE acceptance_status = 'AWAITING_ACCEPTANCE';

-- Fechamento: indicadores informativos fora do vendido (ADR-0046 itens 13 e 20, RF-210.2)
ALTER TABLE daily_closings
  ADD COLUMN not_accepted_count      integer NOT NULL DEFAULT 0 CHECK (not_accepted_count >= 0),
  ADD COLUMN auto_rejected_count     integer NOT NULL DEFAULT 0 CHECK (auto_rejected_count >= 0),
  ADD COLUMN unmarked_delivery_count integer NOT NULL DEFAULT 0 CHECK (unmarked_delivery_count >= 0);
ALTER TABLE daily_closings ADD CONSTRAINT ck_daily_closings_auto_rejected CHECK (auto_rejected_count <= not_accepted_count);

-- Auditoria: envio e desistência pelo cliente da página (ADR-0046 item 24)
ALTER TABLE audit_logs DROP CONSTRAINT audit_logs_origin_check;
ALTER TABLE audit_logs ADD CONSTRAINT audit_logs_origin_check
  CHECK (origin IN ('APP', 'PLATFORM_ADMIN', 'SYSTEM', 'BILLING_WEBHOOK', 'WEB'));
```

### 0004_WebStorefront — 2026-11-20 (ADR-0044, ADR-0045, ADR-0047, ADR-0052)

Página de pedidos pela web (EP-002 fatia 2: F-201 a F-204, F-208, F-210, F-212):

- **`storefront_settings`** [T][V] (1:1 com o tenant, como `tenant_settings`): perfil público, chave da página (`is_page_active`, nasce `false`), pausa rápida (`is_accepting_orders`, `is_delivery_enabled`), modos oferecidos, endereço do restaurante e os campos da taxa por distância do ADR-0048 (`latitude`/`longitude`, `delivery_fee_mode`, `price_per_km`, `minimum_delivery_fee`, `max_delivery_distance_km`), que nascem agora e só são usados na fatia 4. Página ativa exige endereço completo (`ck_storefront_settings_active_address`); "pelo menos um tamanho de marmita ativo" depende de outra tabela e fica na aplicação.
- **`store_slugs`** (plataforma, ADR-0045 item 4): slug único **global** (a PK), um atual por tenant (`ux_store_slugs_current`) e os antigos, que redirecionam (301) e continuam reservados ao mesmo tenant. `blocked_until` é a quarentena de 12 meses depois da anonimização do tenant (ADR-0044 item 15), preenchida pelo job de retenção do ADR-0024 quando ele existir.
- **`order_tracking_tokens`** (plataforma, ADR-0047): só o SHA-256 do token, um por pedido, válido por 7 dias.
- **RLS das duas tabelas de plataforma:** a resolução acontece **antes** de o tenant ser conhecido, então a leitura é aberta ao papel da aplicação (o slug é público e o token está em hash, ADR-0045 item 5). A escrita continua presa ao tenant da sessão (`WITH CHECK (tenant_id = app_tenant_id())`): uma página de A nunca grava slug nem token de B. É mais restrito que o "sem RLS" do ADR-0045, com o mesmo efeito para a leitura.
- **`web_order_intakes`** [T] (ADR-0045 item 14, ADR-0052 item 5): hash do IP (SHA-256 com sal diário) de cada pedido da página, para o limite de 3 pedidos aguardando aceite por IP e para investigar trote. Apagado em 7 dias.
- **`orders`**: forma de pagamento prevista e "troco para" (ADR-0044 item 19; só informação, não cria pagamento), `risk_flag` (`CAPTCHA_UNAVAILABLE` quando o Turnstile não respondeu, ADR-0045 item 12) e `anonymized_at` (anonimização dos pedidos da página não aceitos após 7 dias, ADR-0052 item 3). Os três primeiros só existem em pedido `WEB`.
- **`products.is_visible_on_page`** (ADR-0044 item 5): bebidas e adicionais podem ser escondidos da página.
- **`idempotency_records.user_id`** passa a aceitar nulo: o envio pela página é anônimo, com escopo `(tenant_id, key)` (ADR-0045 item 10). `GET /operations/{key}` filtra por usuário, então registros anônimos nunca aparecem para o app.

**Correção de dados:** cada tenant existente ganha `storefront_settings` (página inativa, nome público = nome do tenant) e o slug gerado do nome, pela mesma regra do autocadastro (ADR-0044 itens 12 a 14: minúsculas, sem acento, símbolos viram `-`, até 40 caracteres; reservado, curto ou só número → acrescenta a cidade; colisão → `-{cidade}`, depois `-2`, `-3`…). A RLS de `tenants` é `FORCE`, então o `FORCE` sai só durante a leitura, como na 0003. As tabelas novas recebem a RLS depois do preenchimento.

```sql
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
```

### 0005_DeliveryByDistance — 2026-11-20 (ADR-0048, ADR-0052)

Taxa de entrega por distância (EP-002 fatia 4, F-205):

- **`storefront_settings.location_confirmed_at`**: quando o proprietário confirmou (ou arrastou) o ponto do restaurante no mapa. O ponto geocodificado ao salvar o endereço nasce sem confirmação; o modo `DISTANCE` só cota com o ponto confirmado (ADR-0048 item 3). Confirmação exige ponto (`ck_storefront_settings_location_confirmed`).
- **`delivery_quotes`** [T] (ADR-0048 item 8): a cotação de 15 min com o hash do endereço normalizado, o endereço como o geocodificador entendeu, coordenadas, metros, km com uma casa, a regra usada (preço por km, mínimo, versão da configuração) e a taxa. Modo `FIXED`: sem coordenadas nem distância. Apagada 24 h depois de vencer pelo job de retenção, no papel de plataforma (ADR-0052 item 5). Sem FK a partir de `orders`: o pedido guarda o snapshot.
- **`geocode_cache_entries`** [T] (ADR-0048 item 11, ADR-0052 item 5): por tenant, hash do endereço normalizado (SHA-256 com o tenant) → coordenadas e, se a rota foi calculada, o ponto de partida e os metros. **Sem o texto do endereço.** Vale 30 dias (`expires_at`); a rota só vale enquanto o ponto do restaurante for o mesmo. Gravado com `INSERT … ON CONFLICT`, por isso o papel da aplicação tem `UPDATE`.
- **`orders`** (ADR-0048 item 20): `delivery_fee_source` (`FIXED_DEFAULT`, `DISTANCE`, `MANUAL`; só em entrega; nulo nas entregas anteriores à 0005) e o snapshot `delivery_distance_meters`, `delivery_price_per_km`, `delivery_minimum_fee` (os três juntos; obrigatórios em `DISTANCE`; mantidos como histórico quando a taxa é editada depois e vira `MANUAL`).

```sql
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
```
