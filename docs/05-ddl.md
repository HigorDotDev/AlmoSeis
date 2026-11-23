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

- `almoseis_migrator`: dono do schema. Roda as migrations. O **login** de migration é criado com `ALTER ROLE <login> SET role = 'almoseis_migrator'`, para que todo objeto (inclusive `__ef_migrations_history`) pertença ao papel de grupo, e não ao login. Assim a rotação do login não muda a posse.
- **Correção de dados em migration:** o papel de migration é dono das tabelas, mas a RLS é `FORCE` (§5.12), então sem `app.tenant_id` na sessão ele não enxerga nenhuma linha. Ele também **não** é membro de `almoseis_platform` (nem aqui nem no `infra/db/init`), então `SET LOCAL ROLE almoseis_platform` não funciona. A técnica é tirar o `FORCE` só em volta da correção, **na mesma transação** da migration, e devolvê-lo antes do commit:

  ```text
  ALTER TABLE orders NO FORCE ROW LEVEL SECURITY;   -- o dono passa a enxergar todas as linhas
  UPDATE orders SET ... ;                             -- a correção, sem depender de tenant
  ALTER TABLE orders FORCE ROW LEVEL SECURITY;      -- volta antes do commit
  ```

  O `ALTER TABLE` trava a tabela (`ACCESS EXCLUSIVE`) até o fim da transação: nenhuma outra sessão enxerga a tabela sem `FORCE`, e um erro no meio desfaz tudo, inclusive o `NO FORCE`. Exemplos: 0003 (`orders`), 0004 (`tenants`) e 0007 (`geocode_cache_entries`).
- **Função `SECURITY DEFINER`** (0007): quando o papel da aplicação precisa de um efeito pontual numa linha de outro tenant (liberar um token de push, resolver um token de acompanhamento antes de o tenant existir), a regra fica numa função de dono `almoseis_migrator` com `SET search_path = public, pg_temp`, `REVOKE ALL ... FROM PUBLIC` e `GRANT EXECUTE` só para quem usa. Como o dono também está sob `FORCE`, a tabela ganha uma política `TO almoseis_migrator` só com o comando que a função usa. A função devolve o mínimo (um número, ou o tenant e o prazo), nunca a linha.
- `almoseis_app`: a API. DML restrito (§5.12), sujeito à RLS de tenant.
- `almoseis_platform`: módulo admin, webhooks de cobrança, dispatcher do outbox e jobs de retenção e limpeza. Acessa todos os tenants por **políticas próprias** (`TO almoseis_platform USING (true)`), não por `BYPASSRLS`. Isso funciona igual no Azure Flexible Server, onde não há superusuário.

## 5.2 Funções de contexto

O interceptor de conexão do EF (ADR-0026) executa a cada abertura de conexão:

```text
SELECT set_config('app.tenant_id', @tenantId, false), set_config('app.user_id', @userId, false);
```

`@userId` é o usuário da sessão, exceto na ação do administrador dentro de um tenant, que grava vazio (§5.12, grupo 2). Na devolução da conexão ao pool, as variáveis são limpas (`RESET app.tenant_id; RESET app.user_id; RESET app.invitation_code`). A `app.push_token` da 0006 deixou de existir na 0007 (a liberação do token passou para a função `release_push_token`).

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
   Na ação do administrador da plataforma dentro de um tenant (acréscimo de comportamento, ADR-0025 complementado em 2026-11-23), a sessão leva `app.tenant_id` do tenant da rota e `app.user_id` **vazio**: com o id do administrador, estas políticas (e a de `tenants`) abririam os estabelecimentos em que ele seja funcionário. Nenhuma política deste grupo fica aberta por `app.user_id` nessa sessão; as do grupo 1 só olham `app.tenant_id`.
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

-- Cliente pelo telefone (ADR-0059 item 14, migration 0011), na transação de criação do pedido. Cria uma vez só,
-- mesmo com dois atendentes ao mesmo tempo, e nunca muda o nome de quem já existe (Q-314). O predicado repete o do
-- índice parcial ux_customers_phone. Sem linha devolvida, o telefone já existe: SELECT id FROM customers
-- WHERE tenant_id = @tenantId AND phone_number = @phone (o ON CONFLICT esperou o commit do outro atendente).
-- Um INSERT comum abortaria a transação do pedido com unique_violation.
INSERT INTO customers (id, tenant_id, name, phone_number, created_at)
VALUES (@id, @tenantId, @name, @phone, @now)
ON CONFLICT (tenant_id, phone_number) WHERE phone_number IS NOT NULL DO NOTHING
RETURNING id;

-- "Usar o endereço do último pedido" (Q-313, ix_orders_customer_recent)
SELECT delivery_address FROM orders
WHERE tenant_id = @tenantId AND customer_id = @customerId AND delivery_address IS NOT NULL
ORDER BY created_at DESC
LIMIT 1;
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
| Slug e token resolvíveis antes do tenant, gravados só pelo próprio tenant (ADR-0045) | políticas `public_lookup`/`tenant_insert`/`tenant_update` (0004); token só por `resolve_tracking_token` (0007) |
| Troco só em dinheiro; campos da página só em pedido `WEB` (ADR-0044 item 19) | `ck_orders_change_for`, `ck_orders_web_fields` (0004; ver 0011) |
| Um registro de push por instalação e tenant; um token em um registro só; push nunca cruza tenants (ADR-0049 item 4) | `ux_push_registrations_installation`, `ux_push_registrations_token`, RLS (0006) + `release_push_token`, só da mesma instalação (0007) |
| Push enviado uma vez por mensagem do outbox e aparelho (ADR-0049 item 8) | PK de `push_deliveries`, `ck_push_deliveries_ticket` (0006) |
| Comportamento da lista, autor coerente com a origem, nunca removido (ADR-0054 itens 1 a 6) | `tenant_behaviors` (`CHECK`s, PK, `ck_tenant_behaviors_enabled_by`), grants sem `UPDATE`/`DELETE` (0008) |
| Rótulo da lista fixa, do comportamento certo e só de comportamento habilitado (ADR-0054 itens 8 e 10) | `ck_tenant_labels_label`, `fk_tenant_labels_behavior` (0008) |
| Prato com preço único ou com tamanhos, nunca os dois; o "vende no site" do prato com tamanho fica em cada tamanho (ADR-0056 item 6, ADR-0057 item 1) | `ck_dishes_price` (0009) |
| "Vende no site" exige preço de site maior que zero (ADR-0057 item 2) | `ck_*_site_price` e `CHECK (site_price > 0)` em `dishes`, `dish_sizes`, `beverage_sizes`, `combos`, `marmita_sizes` (0009) |
| Componente de combo: um prato no tamanho **daquele** prato, ou um tamanho de bebida, de 1 a 10 (ADR-0056 item 13) | `ck_combo_components_target`, `fk_combo_components_dish_size` (0009) |
| Sabor do tamanho de bebida, acréscimo nunca negativo (ADR-0056 itens 9 e 10, Q-305) | `fk_beverage_flavors_size`, `CHECK (extra_price >= 0)` (0009) |
| Grupo de adicionais ligado a uma categoria ou a um prato, sem repetir (ADR-0056 item 18) | `ck_addon_group_links_target`, `ux_addon_group_links_*` (0009) |
| Itens do cardápio fixo inativados, nunca apagados (ADR-0056 item 26) | grants (0009: `DELETE` só em `combo_components` e `addon_group_links`) |
| Preço de site de produto só em bebida (Q-307, Q-308); pedido do app na tabela de balcão (ADR-0057 item 7) | `ck_products_site_price`, `ck_orders_price_list` (0009; contração pendente) |
| À la carte no salão, na entrega e na retirada (ADR-0058 item 2) | `orders_order_type_check`, `ck_orders_fulfillment_by_type` (0010) |
| Item do cardápio fixo com as referências do seu tipo, tamanho do próprio prato, sabor do próprio tamanho, sem cruzar tenants (ADR-0058 item 8) | `ck_order_items_refs`, `fk_order_items_dish_size`, `fk_order_items_beverage_flavor` e demais FKs compostas (0010) |
| Combo em três níveis; parte só sob combo; adicional de grupo só sob prato avulso ou parte de prato; adicional do restaurante só sob marmita ou PF (ADR-0058 item 9, Q-321) | `ck_order_items_parent`, `ck_order_items_parent_dish`, `fk_order_items_parent_kind`, `fk_order_items_parent_dish` (0010; contração pendente) |
| Snapshot de tamanho e sabor (ADR-0020); parte do combo a 0.00, salvo o acréscimo do sabor (ADR-0058 item 8) | `ck_order_items_snapshots`, `ck_order_items_combo_part_price` (0010) |
| Combo e bebidas fora da cozinha (ADR-0058 itens 11 e 12) | `ck_order_items_preparation` (0010) |
| Linha "À la carte" no fechamento; "Vendido pelo site" dentro do vendido (ADR-0058 item 13, ADR-0057 item 12) | `ck_daily_closing_lines_dimension`, `ck_daily_closings_site_sold` (0010; reescrito na 0012) |
| "Vendido pelo site" sem a taxa de entrega; itens e taxa do site dentro do vendido, taxa do site dentro das taxas (ADR-0057 item 12, M1) | `ck_daily_closings_site_sold`, `ck_daily_closings_site_delivery_fee`, `daily_closings_site_delivery_fee_total_check` (0012) |
| Preço de site `0.00` só na bebida do restaurante; os demais `> 0` (ADR-0057 itens 2 e 5, I6a) | `products_site_price_check` (0012), `ck_products_site_price` (0009) |
| Forma prevista e "troco para" também no pedido do app, só em entrega e retirada; troco só com a forma dinheiro, nunca com a forma nula; risco do anti-robô só em `WEB` (ADR-0059 item 5, Q-316) | `ck_orders_expected_payment_fulfillment`, `ck_orders_change_for` (reescrito), `ck_orders_risk_flag` (0011, no lugar de `ck_orders_web_fields`) |
| Um cliente por telefone no tenant, criado uma vez só e sem mudar o nome (ADR-0059 itens 14 e 15, Q-314) | `ux_customers_phone` (0001) + `ON CONFLICT … DO NOTHING` (§5.14) |

## 5.16 Validação

O script foi validado em 2026-10-27 num PostgreSQL 16 (Docker): os blocos `sql` deste documento aplicados do zero (36 tabelas e 65 políticas na `0001`; 40 e 76 com as alterações até a `0004`; 42 e 80 até a `0005`; 44 e 86 até a `0006`; 44 e 87 até a `0007`; 46 e 91 até a `0008`; 57 e 113 até a `0009`, a `0010`, a `0011` e a `0012`) e depois o teste de fumaça [`05-ddl-smoke-test.sql`](05-ddl-smoke-test.sql), que roda como `almoseis_app`/`almoseis_platform` e confere 91 regras (22 da `0001`, 9 da `0003`, 10 da `0004`, 4 da `0005`, 5 da `0006`, 9 da `0008`, 15 da `0009`, 10 da `0010`, 5 da `0011`, 2 da `0012`): isolamento entre dois tenants, plataforma do aparelho compartilhado, login, convite por código, totais, cancelamento, troco, estorno, conciliação, imutabilidade, casos com `NULL` e, desde a `0003`, o pedido da página (sem número, sem produção e sem autor enquanto aguarda aceite; recusa coerente; atendimento só em entrega) e, desde a `0004`, a página de pedidos (endereço para ativar, slug atual único e no formato, troco só em dinheiro, campos da página só em `WEB`, idempotência anônima, slug e token resolvíveis por outro tenant mas nunca gravados por ele, perfil e hash do IP isolados) e, desde a `0005`, a taxa por distância (cotação por distância completa, confirmação do ponto só com ponto, origem da taxa só em entrega, cotação e cache isolados) e, desde a `0006`, o push (token no formato do Expo e único, entrega com ticket ou erro, registro e entregas isolados, só quem comprova o token tira o aparelho do outro tenant; desde a `0007`, só a mesma instalação, pela função, e o token de acompanhamento só pela função de resolução) e, desde a `0008`, os comportamentos e rótulos (comportamento e origem da lista, rótulo do comportamento certo e só de comportamento habilitado, comportamento nunca alterado nem apagado pela aplicação, rótulo removível, isolamento e acréscimo pela plataforma) e, desde a `0009`, o cardápio fixo e o preço de site (prato com preço único ou tamanhos, "vende no site" com preço de site maior que zero em prato, tamanho, bebida, combo e marmita, componente de combo com o tamanho do próprio prato, acréscimo do sabor, vínculo de grupo único com categoria ou prato, preço de site só em bebida, tabela de preço do pedido, itens inativados e nunca apagados, componentes e vínculos trocados na edição, isolamento e combo de um tenant sem prato do outro) e, desde a `0010`, o pedido à la carte (salão, entrega e retirada; combo em três níveis com o total do ADR-0058 e a cozinha sem o combo e sem bebidas; parte só sob combo; adicional de grupo só sob prato avulso ou parte de prato; adicional do restaurante só sob marmita ou PF, com ou sem o tipo do pai; referências por tipo, tamanho do próprio prato e sabor do próprio tamanho; snapshot de tamanho e sabor; preço da parte; linha `A_LA_CARTE` e vendido pelo site no fechamento; isolamento e item de um tenant sem cardápio fixo nem pai do outro) e, desde a `0011`, o delivery por telefone (forma prevista e "troco para" no pedido do app sem pagamento; troco só com a forma dinheiro, inclusive com a forma nula; forma prevista fora do salão; cliente criado uma vez pelo telefone sem mudar o nome e endereço do último pedido; o mesmo telefone em outro tenant e o cliente de A invisível para B) e, desde a `0012`, o preço de site `0.00` só na bebida do restaurante (marmita e combo continuam `> 0`) e a taxa de entrega do site no fechamento (padrão 0, nunca negativa, dentro das taxas e, com os itens do site, dentro do vendido). Toda mudança neste documento deve repetir os dois passos:

```text
docker run -d --name almoseis-ddl-test -e POSTGRES_PASSWORD=test postgres:16
# concatenar os blocos sql deste arquivo, na ordem, em schema.sql; então:
# papéis do §5.1 e, para que as funções SECURITY DEFINER (0007) sejam do mesmo dono das tabelas, o schema como almoseis_migrator:
psql -U postgres -c "ALTER SCHEMA public OWNER TO almoseis_migrator"   # depois de criar os papéis
(echo 'SET ROLE almoseis_migrator;'; cat schema.sql) | psql -U postgres -v ON_ERROR_STOP=1 && psql -U postgres -f docs/05-ddl-smoke-test.sql
```

**Regras que ficam só na aplicação** (dependem de outra tabela ou de estado): `menu_items.section = products.kind`; `pf_price` obrigatório em menu PF; pedido com ao menos um item ou pesagem ativa; `items_total`/`weighings_total` = soma dos filhos; `paid_amount` = soma dos pagamentos `CONFIRMED`; pedido pago imutável; pelo menos um `OWNER` ativo; método de pagamento habilitado no tenant; tipos de item conforme o `order_type` do pedido (cardápio fixo só em `A_LA_CARTE`, ADR-0058 item 3); tamanho e sabor obrigatórios quando o prato tem tamanho e o tamanho tem sabores; opção de adicional de um grupo ligado ao prato; valor do filho = `unit_price × quantity × quantidade do pai × quantidade do avô`; partes do combo = componentes do combo; nome e telefone obrigatórios na entrega e na retirada à la carte e na entrega de marmita, forma prevista obrigatória na entrega e "troco para" ≥ total (ADR-0059, Q-312, Q-316: ver 0011). Desde a `0010`, o adicional filho só de `MARMITA`/`PF` está no banco (`ck_order_items_parent`).

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

**Expand/contract (09 §9.4):** a migration roda antes do deploy, e a API anterior continua criando pedidos sem `accepted_at` e sem `fulfillment_status`. Por isso esta alteração **não** exige esses dois campos em pedido aceito: o `CHECK` só proíbe preenchê-los fora do lugar. A API desta versão sempre os preenche e trata o nulo legado como "aceito na criação" e `PENDING`. **Contração pendente** (próxima release, com esta API em produção): preencher os nulos criados na janela do deploy (`accepted_at = created_at`, `fulfillment_status = 'PENDING'` em entrega/retirada aceita) e apertar `ck_orders_acceptance` (`accepted_at` obrigatório em `ACCEPTED`) e `ck_orders_fulfillment` (obrigatório em entrega/retirada aceita), e tirar o default da coluna nova (`ALTER TABLE orders ALTER COLUMN acceptance_status DROP DEFAULT`): o default `'ACCEPTED'` só existe para a API anterior continuar criando pedidos na janela do deploy, e a API desta versão sempre grava o status. Registrada em 09 §9.4.

**Correção de dados nos pedidos existentes:** a RLS de `orders` é `FORCE` (§5.12), então nem o dono da tabela enxerga linhas sem `app.tenant_id`. A migration usa a convenção de §5.1: tira o `FORCE` só em volta do `UPDATE`, na mesma transação, e o devolve antes do commit.

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
- **RLS das duas tabelas de plataforma:** a resolução acontece **antes** de o tenant ser conhecido, então a leitura é aberta ao papel da aplicação (o slug é público e o token está em hash, ADR-0045 item 5). **Desde a 0007, a leitura aberta vale só para `store_slugs`**: `order_tracking_tokens` passou a ser lida no tenant da sessão, e a resolução do token antes do tenant é pela função `resolve_tracking_token`. A escrita continua presa ao tenant da sessão (`WITH CHECK (tenant_id = app_tenant_id())`): uma página de A nunca grava slug nem token de B. É mais restrito que o "sem RLS" do ADR-0045, com o mesmo efeito para a leitura.
- **`web_order_intakes`** [T] (ADR-0045 item 14, ADR-0052 item 5): hash do IP (SHA-256 com sal diário) de cada pedido da página, para o limite de 3 pedidos aguardando aceite por IP e para investigar trote. Apagado em 7 dias.
- **`orders`**: forma de pagamento prevista e "troco para" (ADR-0044 item 19; só informação, não cria pagamento), `risk_flag` (`CAPTCHA_UNAVAILABLE` quando o Turnstile não respondeu, ADR-0045 item 12) e `anonymized_at` (anonimização dos pedidos da página não aceitos após 7 dias, ADR-0052 item 3). Os três primeiros só existem em pedido `WEB` (a forma prevista e o "troco para" passam ao pedido do app na 0011).
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

### 0006_PushNotifications — 2026-11-20 (ADR-0049, ADR-0046 item 15, ADR-0052)

Push para pedidos da página (EP-002 fatia 5, F-209):

- **`push_registrations`** [T] (ADR-0049 item 4): o token do Expo de uma instalação no estabelecimento, ligado ao funcionário da sessão (`tenant_employee_id`) e, no login por PIN, ao aparelho compartilhado (`shared_device_id`; vale o último funcionário que entrou). Um registro por instalação e tenant (`ux_push_registrations_installation`) e **um registro por token em todo o sistema** (`ux_push_registrations_token`): o aparelho que troca de estabelecimento sem logout sai do anterior quando registra o token no novo. `web_orders_enabled` é a opção "Avisar pedidos do site com o app fechado" (item 6); `show_tenant_name`, calculado a cada registro, põe o nome do estabelecimento no título quando o usuário tem mais de um vínculo (item 3); `updated_at` é o último registro feito pelo app, e o job apaga o que está sem uso há 30 dias. `platform` aceita `WEB`, reservado para o Web Push (Q-26): a API ainda recusa. Logout (fora do aparelho compartilhado), troca de estabelecimento, `AccessRevoked`, revogação do aparelho e `DeviceNotRegistered` apagam o registro, por isso o papel da aplicação tem `DELETE`.
- **Política `push_token_owner`** (**substituída na 0007** pela função `release_push_token`, que só libera o registro da mesma instalação): a sessão que comprova ter o token (`app.push_token`, definido só dentro da transação do `PUT /devices/push-token`) enxerga e apaga os registros com **esse** token em outros tenants, sem enxergar nenhum outro. Não há política de `INSERT`/`UPDATE` além da de tenant: ninguém grava registro em outro estabelecimento.
- **`push_deliveries`** [T] (ADR-0049 item 8, ADR-0031): um push por mensagem do outbox (`event_id`) e registro. É a deduplicação do reenvio (entrega pelo menos uma vez) e guarda o ticket da Expo para a conferência do recibo depois de 15 min, ou o erro do ticket. Sem token nem conteúdo (ADR-0052 item 7). Apagada em 7 dias pelo job, no papel de plataforma.
- **`outbox_messages.available_at`**: entrega agendada (o lembrete aos 3 min, ADR-0046 item 15) e espera entre tentativas de um consumidor interno. Nulo = na hora.

```sql
-- Token que a sessão comprovou ter (PUT /devices/push-token): só dentro da transação (ADR-0049 item 4)
CREATE OR REPLACE FUNCTION app_push_token() RETURNS text
  LANGUAGE sql STABLE
  AS $$ SELECT NULLIF(current_setting('app.push_token', true), '') $$;

-- Aparelho que recebe push dos pedidos da página (ADR-0049 itens 4 a 6)
CREATE TABLE push_registrations (
  id                  uuid          PRIMARY KEY,
  tenant_id           uuid          NOT NULL REFERENCES tenants (id),
  installation_id     varchar(64)   NOT NULL,
  tenant_employee_id  uuid          NOT NULL,
  shared_device_id    uuid,
  platform            text          NOT NULL CHECK (platform IN ('ANDROID', 'IOS', 'WEB')),  -- WEB: reservado (Q-26)
  expo_push_token     varchar(200)  NOT NULL CHECK (expo_push_token ~ '^Expo(nent)?PushToken\[[A-Za-z0-9_-]{8,180}\]$'),
  web_orders_enabled  boolean       NOT NULL DEFAULT true,
  show_tenant_name    boolean       NOT NULL DEFAULT false,
  created_at          timestamptz   NOT NULL,
  updated_at          timestamptz   NOT NULL,
  CONSTRAINT ux_push_registrations_installation UNIQUE (tenant_id, installation_id),
  CONSTRAINT ux_push_registrations_token UNIQUE (expo_push_token),
  CONSTRAINT fk_push_registrations_employee FOREIGN KEY (tenant_id, tenant_employee_id)
    REFERENCES tenant_employees (tenant_id, id),
  CONSTRAINT fk_push_registrations_shared_device FOREIGN KEY (tenant_id, shared_device_id)
    REFERENCES shared_devices (tenant_id, id)
);
CREATE INDEX ix_push_registrations_employee ON push_registrations (tenant_employee_id);
CREATE INDEX ix_push_registrations_shared_device ON push_registrations (shared_device_id) WHERE shared_device_id IS NOT NULL;
-- Limpeza dos registros sem uso há 30 dias (job de plataforma)
CREATE INDEX ix_push_registrations_updated ON push_registrations (updated_at);

-- Push enviado por mensagem do outbox e registro: deduplicação e ticket para o recibo (ADR-0049 item 8)
CREATE TABLE push_deliveries (
  event_id            uuid          NOT NULL,
  registration_id     uuid          NOT NULL,  -- sem FK: o registro pode ser apagado (DeviceNotRegistered)
  tenant_id           uuid          NOT NULL REFERENCES tenants (id),
  order_id            uuid          NOT NULL,
  kind                text          NOT NULL CHECK (kind IN ('WEB_ORDER_AWAITING_ACCEPTANCE', 'WEB_ORDER_REMINDER')),
  ticket_id           varchar(64),
  error               varchar(40),
  sent_at             timestamptz   NOT NULL,
  receipt_checked_at  timestamptz,
  receipt_error       varchar(40),
  PRIMARY KEY (event_id, registration_id),
  CONSTRAINT ck_push_deliveries_ticket CHECK ((ticket_id IS NULL) <> (error IS NULL)),
  CONSTRAINT ck_push_deliveries_receipt CHECK (
        (receipt_checked_at IS NULL OR ticket_id IS NOT NULL)
    AND (receipt_error IS NULL OR receipt_checked_at IS NOT NULL)),
  CONSTRAINT fk_push_deliveries_order FOREIGN KEY (tenant_id, order_id) REFERENCES orders (tenant_id, id)
);
-- Recibos a conferir (job, todos os tenants) e retenção de 7 dias
CREATE INDEX ix_push_deliveries_receipt ON push_deliveries (sent_at) WHERE ticket_id IS NOT NULL AND receipt_checked_at IS NULL;
CREATE INDEX ix_push_deliveries_sent ON push_deliveries (sent_at);

-- Outbox: entrega agendada e espera entre tentativas de um consumidor interno (lembrete do push, ADR-0046 item 15)
ALTER TABLE outbox_messages ADD COLUMN available_at timestamptz;

-- RLS: tabelas de tenant
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['push_registrations', 'push_deliveries'] LOOP
    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %I TO almoseis_app '
      'USING (tenant_id = app_tenant_id()) WITH CHECK (tenant_id = app_tenant_id())', t);
    EXECUTE format(
      'CREATE POLICY platform_access ON %I TO almoseis_platform USING (true) WITH CHECK (true)', t);
  END LOOP;
END $$;

-- O mesmo token em outro tenant: quem comprova ter o token o enxerga e apaga (o aparelho trocou de estabelecimento)
CREATE POLICY push_token_owner_select ON push_registrations FOR SELECT TO almoseis_app
  USING (expo_push_token = app_push_token());
CREATE POLICY push_token_owner_delete ON push_registrations FOR DELETE TO almoseis_app
  USING (expo_push_token = app_push_token());

-- Grants (tabelas novas). A coluna nova do outbox segue os grants da tabela.
GRANT EXECUTE ON FUNCTION app_push_token() TO almoseis_app, almoseis_platform;
GRANT SELECT ON push_registrations, push_deliveries TO almoseis_app, almoseis_platform;
GRANT INSERT, UPDATE, DELETE ON push_registrations TO almoseis_app;
GRANT INSERT, UPDATE ON push_deliveries TO almoseis_app;
-- Limpeza (ADR-0049 item 4, ADR-0052 item 5): registro sem uso há 30 dias e entregas com mais de 7 dias
GRANT DELETE ON push_registrations, push_deliveries TO almoseis_platform;
```

### 0007_PublicPageHardening — 2026-11-20 (ADR-0045, ADR-0047, ADR-0048, ADR-0049, ADR-0052)

Correções da revisão de arquitetura do EP-002 (fatias 1 a 5):

- **Token de push de outro tenant** (ADR-0049 item 4): as políticas `push_token_owner_select`/`_delete` da 0006 deixavam quem enviasse a string do token de **outro** aparelho ler e apagar o registro dele (o restaurante deixava de receber o aviso). Saem as duas políticas e a `app_push_token()`. No lugar, a função `release_push_token(token, installation_id)` (`SECURITY DEFINER`, §5.1), chamada pelo `PUT /devices/push-token`: libera o registro com esse token em **outro** tenant só quando ele é da **mesma instalação** (`installation_id` do JWT), que é o caso legítimo do aparelho que trocou de estabelecimento sem logout. Devolve `1` (liberou), `0` (não havia) ou `-1` (o token está registrado por **outra** instalação em outro tenant: não apaga, e o `PUT` responde `409 PushTokenInUse`). Nunca devolve a linha. O mesmo token em outra instalação do **mesmo** tenant continua saindo pela RLS de tenant.
- **Resolução do token de acompanhamento** (ADR-0047): a leitura aberta de `order_tracking_tokens` (`public_lookup`, `USING (true)`) dava a qualquer sessão da aplicação todos os hashes, tenants e pedidos. A leitura passa a ser só no tenant da sessão (`tenant_select`), e a resolução antes do tenant é a função `resolve_tracking_token(token_hash)`, que devolve só `tenant_id` e `expires_at` de **um** hash. `store_slugs` continua com leitura aberta (o slug é público).
- **Cache de geocodificação** (ADR-0048 item 11, ADR-0052 item 5): o hash do endereço passa a ser HMAC-SHA-256 com o segredo `Maps:AddressHashSecret` (era SHA-256 do tenant + endereço, que qualquer um com a lista de endereços da cidade recalcula), e as coordenadas do cliente guardadas no cache passam a ter **4 casas** (`numeric(7,4)`, cerca de 11 m); a cotação corrente continua com 6 casas em `delivery_quotes` (15 min + 24 h). O ponto de partida da rota (`origin_*`, o ponto do restaurante, que não é dado do cliente) fica com 6 casas, porque é comparado com o ponto confirmado. As linhas existentes (hash antigo, coordenadas exatas) são apagadas: o hash novo nunca as encontraria, e o cache se refaz na próxima cotação. Validade padrão do cache: 7 dias (`Maps:GeocodeCacheDays`, era 30).

**Correção de dados:** a RLS de `geocode_cache_entries` é `FORCE`; o `DELETE` usa a técnica de §5.1 (`NO FORCE` só em volta dele, na mesma transação).

**Expand/contract (09 §9.4):** na janela do deploy, a API anterior ainda chama a `app_push_token()` removida no `PUT /devices/push-token` (falha só esse `PUT`, que o app repete na próxima abertura) e lê `order_tracking_tokens` por SQL antes do tenant, o que deixa de achar o token (o acompanhamento responde `404` até a API nova subir; a página tenta de novo em 20 s). As coordenadas que a API anterior gravar no cache viram 4 casas pelo próprio tipo da coluna, e o hash antigo delas nunca é encontrado pela API nova (somem no vencimento).

```sql
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
```

### 0008_EstablishmentBehaviors — 2026-11-22 (ADR-0054)

Comportamentos e rótulos do estabelecimento (EP-003 fatia 1, F-301 a F-305). ADR-0054 e ADR-0055 estão `Proposto`.

- **`tenant_behaviors`** [T] (ADR-0054 itens 1 a 6): o **conjunto** de comportamentos habilitados, uma linha por `(tenant_id, behavior)`, sem `id` próprio (como `tenant_employee_roles`). `enabled_by_user_id` é o usuário que habilitou: o proprietário no autocadastro (`origin = 'SIGNUP'`) ou o administrador da plataforma (`'PLATFORM_ADMIN'`, que é um `users` sem vínculo com o tenant, por isso a FK é para `users` e não para `tenant_employees`); nulo só em `'SYSTEM'` (a migração). Remover um comportamento está fora desta fase: nenhum papel tem `UPDATE` nem `DELETE` na tabela. Os **módulos** (`TenantModule`) não são tabela: saem do mapa fixo em código (item 12).
- **`tenant_labels`** [T] (itens 7 a 11): os rótulos informativos, uma linha por `(tenant_id, label)`. A lista fixa e o comportamento de cada rótulo ficam no `ck_tenant_labels_label`; a FK composta `(tenant_id, behavior) → tenant_behaviors` garante no banco que só valem rótulos de comportamento **habilitado** (item 10; a API devolve o `422` antes). Vários rótulos por tenant são aceitos em qualquer comportamento (recomendação do Q-302; se o dono do produto decidir "um só no restaurante", a regra fica na aplicação). A edição em "Dados do estabelecimento" (`PATCH /tenant`) troca o conjunto, por isso o papel da aplicação tem `DELETE` (a linha não tem valor histórico; a mudança vai para a auditoria). Os rótulos fazem parte do agregado `Tenant`: o `If-Match` do `PATCH /tenant` usa `tenants.version`, sem versão própria.
- **Ficam na aplicação** (dependem de mais de uma linha): pelo menos um comportamento (o autocadastro grava exatamente um, e nenhuma linha sai depois), pelo menos um rótulo, exatamente um comportamento no cadastro.
- **`SNACK_BAR`** é ao mesmo tempo código de comportamento e de rótulo ("Lanchonete"), como no ADR-0054 item 8.

**Correção de dados (Q-303):** todo tenant existente recebe `RESTAURANT` com `origin = 'SYSTEM'`, `enabled_at` = instante da migração e sem autor, e o rótulo `RESTAURANT` ("Restaurante"), editável depois pelo proprietário. A RLS de `tenants` é `FORCE`, então o `FORCE` sai só durante a leitura, como na 0004. As tabelas novas recebem a RLS depois do preenchimento.

**Expand/contract (09 §9.4):** só acrescenta. A API anterior não lê as tabelas novas. Um tenant criado pela API anterior na janela do deploy (depois da migration, antes da API nova) fica **sem** comportamento: a API nova precisa tratar o tenant sem linha em `tenant_behaviors` como `RESTAURANT` até uma correção, ou o deploy repete o preenchimento abaixo (é idempotente pelo `NOT EXISTS`).

```sql
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
```

### 0009_FixedMenuSitePrice — 2026-11-22 (ADR-0056, ADR-0057)

Cardápio fixo e preço de site (EP-003 fatia 2, F-306 a F-311). ADR-0056 e ADR-0057 estão `Proposto`.

- **Cardápio fixo** (ADR-0056): onze tabelas de tenant no módulo Catalog, separadas de `products` e `marmita_sizes` (item 4). Os agregados [V][S] são **`dish_categories`**, **`dishes`** (com os filhos `dish_sizes`), **`beverage_types`** (com `beverage_sizes` e, abaixo de cada tamanho, `beverage_flavors`: o sabor é **do tamanho**, Q-305), **`combos`** (com `combo_components`) e **`addon_groups`** (com `addon_options` e `addon_group_links`). Os filhos não têm versão própria: toda alteração neles, inclusive a pausa, incrementa a `version` da raiz (ADR-0034) e gera a entrada do feed da raiz (ADR-0032), como `menus`/`menu_items`.
- **Prato** (itens 5 a 8): `has_sizes` desligado exige `price`; ligado proíbe `price`, `sells_on_site` e `site_price` no próprio prato, porque o preço e o "vende no site" passam a ser **de cada tamanho** (`ck_dishes_price`). "Pelo menos um tamanho ativo" depende de outra tabela e fica na aplicação. Preço de balcão de prato, tamanho, tamanho de bebida e combo é `> 0` (como `marmita_sizes.price`), porque o preço de site começa igual a ele e precisa ser `> 0` (ADR-0057 item 2). Opção de adicional pode custar `0.00`; o acréscimo do sabor é `>= 0`, padrão `0.00`.
- **Combo** (item 13): cada componente é **um prato** (`dish_id` + `dish_size_id`, nulo se o prato não tem tamanho) **ou** **um tamanho de bebida** (`beverage_size_id`; o sabor é escolhido no pedido), com quantidade de 1 a 10 (`ck_combo_components_target`). A FK `(tenant_id, dish_id, dish_size_id) → dish_sizes (tenant_id, dish_id, id)` garante que o tamanho é **daquele** prato. "Tamanho obrigatório se o prato tem tamanho" e "pelo menos um componente" ficam na aplicação. Os componentes não são referenciados por pedido (o pedido grava o snapshot, ADR-0058), então a edição do combo os troca (`DELETE` permitido).
- **Grupos de adicionais** (itens 17 a 20): o vínculo é com **uma categoria ou um prato** (`ck_addon_group_links_target`), único por par. Os vínculos não têm valor histórico: `DELETE` permitido.
- **Pausa** (itens 21 a 25): `is_paused` em prato, tamanho de prato, tamanho de bebida, sabor, combo e opção de adicional. Fica até alguém reativar: nenhuma rotina do expediente toca na coluna. A **disponibilidade é derivada**, nunca gravada: o combo não tem coluna de "pausado por componente", e os índices `ix_combo_components_*` servem à consulta "combos que usam este prato/tamanho/bebida" (propagação da pausa, e o `409` ao inativar um prato usado em combo ativo, item 26). A permissão `catalog.availability` (ADR-0028, Q-306) é constante em código (04 §4.1), sem tabela.
- **Exclusão** (item 26): pratos, tamanhos, bebidas, sabores, combos e opções são inativados (`is_active`), nunca apagados: o papel da aplicação não tem `DELETE` neles. O nome é único entre os ativos do mesmo pai.
- **"Vende no site" e preço de site** (ADR-0057 itens 1 a 5): `sells_on_site` + `site_price` em `dishes` (sem tamanho), `dish_sizes`, `beverage_sizes`, `combos` e **`marmita_sizes`**; ligado exige `site_price` (`> 0`). Desligado, o `site_price` pode ficar guardado (volta preenchido se a chave for religada). As bebidas do restaurante (`products` `BEVERAGE`, Q-307) usam o `is_visible_on_page` existente como "vende no site" e ganham `site_price`; adicionais (`ADDON`) e opções de grupo não têm preço de site (Q-308, `ck_products_site_price`).
- **`orders.price_list`** (ADR-0057 item 7): `COUNTER` ou `SITE`. Os pedidos existentes ficam `COUNTER`, e os do site (`origin = 'WEB'`) já gravados viram `SITE`. O "Vendido pelo site" do fechamento (item 12) é da fatia 3, junto com a linha `A_LA_CARTE` (F-314). A estrutura de `order_items` para o cardápio fixo e o combo também é da fatia 3 (ADR-0058): por isso `dish_sizes`, `beverage_flavors` e `addon_options` já nascem com `UNIQUE (tenant_id, id)` (alvo das FKs compostas do item do pedido).

**Correção de dados** (ADR-0057 item 15, critério 7 da fatia 2): todo tamanho de marmita **ativo** recebe `sells_on_site = true` e `site_price = price`; toda bebida com `is_visible_on_page = true` recebe `site_price = default_price`. A bebida visível com `default_price = 0.00` não pode ter preço de site (`> 0`), por isso deixa de ser vendida no site (`is_visible_on_page = false`) e o estabelecimento decide depois (desde a 0012 a bebida aceita `0.00` e volta à página). A versão das linhas não muda (a correção não é edição do usuário, como na 0003). A RLS de `marmita_sizes`, `products` e `orders` é `FORCE`: o `FORCE` sai só durante cada `UPDATE`, na mesma transação (§5.1).

**Expand/contract (09 §9.4):** só acrescenta. Na janela do deploy, a API anterior continua gravando sem as colunas novas: tamanho de marmita novo nasce com `sells_on_site = false` (não aparece no site até o estabelecimento ligar a chave); bebida nova visível nasce sem `site_price`; pedido do site nasce `COUNTER` (cobrado a preço de balcão, que é o que a API anterior faz). Por isso esta alteração **não** exige `site_price` na bebida visível nem `SITE` no pedido `WEB`: o `CHECK` só proíbe o contrário (`ck_products_site_price`, `ck_orders_price_list`). A API desta versão sempre grava os dois. **Contração pendente** (próxima release): `site_price = default_price` nas bebidas visíveis sem preço de site (e `is_visible_on_page = false` nas de `0.00`), `price_list = 'SITE'` nos pedidos `WEB` com `COUNTER`, e então `ADD CONSTRAINT ck_products_beverage_site_price CHECK (kind <> 'BEVERAGE' OR NOT is_visible_on_page OR site_price IS NOT NULL)`, a troca de `ck_orders_price_list` por `CHECK ((origin = 'WEB') = (price_list = 'SITE'))` e `ALTER TABLE orders ALTER COLUMN price_list DROP DEFAULT`.

```sql
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
```

### 0010_ALaCarteOrders — 2026-11-22 (ADR-0058, ADR-0057 item 12)

Pedido à la carte nas comandas e na cozinha (EP-003 fatia 3, F-312 a F-314). ADR-0058 e ADR-0057 estão `Proposto`.

- **`orders.order_type = 'A_LA_CARTE'`** (ADR-0058 itens 1 e 2): aceita `DINE_IN`, `DELIVERY` e `PICKUP` (`ck_orders_fulfillment_by_type`); marmita continua só entrega/retirada e PF/quilo só salão. O balcão "para viagem" da lanchonete é uma comanda `DINE_IN` com número de comanda (Q-320): nada muda no banco. Um pedido tem **um** tipo (Q-317), e o tipo decide de onde vêm os itens (item 3): `A_LA_CARTE` só com os tipos de item do cardápio fixo, os outros só com `MARMITA`/`PF`/`BEVERAGE`/`ADDON`. Assim a comanda nunca mistura PF ou quilo com cardápio fixo. Essa regra depende do pedido (outra tabela) e fica na aplicação, como já ficava "item `MARMITA` só em pedido `MARMITA`". Mesa/comanda, nome e telefone seguem as regras atuais (a fatia 4 decide o delivery por telefone, ADR-0059).
- **Tipos de item novos** (item 8, nomes do ADR): `FIXED_DISH` (prato avulso), `FIXED_BEVERAGE` (bebida avulsa), `COMBO`, `COMBO_PART` e `GROUP_ADDON`. Cada tipo tem as suas referências e nenhuma outra (`ck_order_items_refs`, reescrito com `num_nonnulls`): `FIXED_DISH` → `dish_id` (+ `dish_size_id` se o prato tem tamanho); `FIXED_BEVERAGE` → `beverage_size_id` (+ `beverage_flavor_id` se o tamanho tem sabores); `COMBO` → `combo_id`; `COMBO_PART` → um prato (`dish_id` + `dish_size_id`) **ou** um tamanho de bebida (`beverage_size_id` + `beverage_flavor_id`); `GROUP_ADDON` → `addon_option_id`. As FKs são compostas por tenant, e as de tamanho e sabor incluem o pai (`(tenant_id, dish_id, dish_size_id) → dish_sizes (tenant_id, dish_id, id)`, `(tenant_id, beverage_size_id, beverage_flavor_id) → beverage_flavors (tenant_id, beverage_size_id, id)`, alvos preparados na 0009): o tamanho é **daquele** prato e o sabor é **daquele** tamanho. Os componentes do combo não são referenciados (a edição do combo os troca, 0009); a parte grava o prato ou a bebida do componente. "Tamanho obrigatório se o prato tem tamanho", "sabor obrigatório se o tamanho tem sabores", "opção de um grupo ligado ao prato" e "item pausado" dependem de outras tabelas e ficam na aplicação (`409 ProductUnavailable`).
- **Snapshot** (ADR-0020): nos tipos novos, `name_snapshot` é o nome **base** (prato, tipo de bebida, combo ou opção), e o tamanho e o sabor ganham colunas próprias, `size_name_snapshot` (30, como `dish_sizes.name`/`beverage_sizes.name`) e `flavor_name_snapshot` (40, como `beverage_flavors.name`), preenchidas exatamente quando há tamanho e sabor (`ck_order_items_snapshots`). Assim "Refrigerante" + "Lata 350 ml" + "Coca-Cola Zero" cabem sem estourar os 80 do nome, e a exibição junta as partes ("Refrigerante Lata 350 ml · Coca-Cola Zero"). Nos tipos antigos as duas colunas ficam nulas. O preço é o `unit_price` de sempre: tamanho + acréscimo do sabor na bebida avulsa; na parte do combo, `0.00` ou só o acréscimo do sabor (`ck_order_items_combo_part_price`).
- **Hierarquia em três níveis** (item 9, Q-321): `COMBO` → `COMBO_PART` → `GROUP_ADDON`, e `FIXED_DISH` → `GROUP_ADDON`. Um `CHECK` não enxerga a linha do pai, por isso o filho copia o tipo do pai em **`parent_item_kind`**, garantido pela FK `(tenant_id, order_id, parent_order_item_id, parent_item_kind) → order_items (tenant_id, order_id, id, item_kind)`, e `ck_order_items_parent` decide as combinações: `COMBO_PART` só sob `COMBO`; `GROUP_ADDON` só sob `FIXED_DISH` ou `COMBO_PART`; `ADDON` só sob `MARMITA`/`PF` (antes, só na aplicação); `COMBO_PART` e `GROUP_ADDON` nunca no primeiro nível; os demais tipos nunca têm pai. A profundidade máxima (3) sai das próprias combinações. "Parte **de prato**" (o adicional nunca fica sob a parte de bebida) usa o mesmo recurso: o `GROUP_ADDON` grava o prato do pai em **`parent_dish_id`**, com a FK `(tenant_id, order_id, parent_order_item_id, parent_dish_id) → order_items (tenant_id, order_id, id, dish_id)`; a parte de bebida não tem `dish_id` e não casa. O valor do filho continua na aplicação: `unit_price × quantity × quantidade do pai × quantidade do avô`. Trocar o sabor de uma parte é remover e incluir o combo (Q-322): nenhuma coluna para isso.
- **Cozinha** (itens 11 e 12): o `COMBO` e as bebidas (`FIXED_BEVERAGE` e parte de bebida) nunca têm `requires_preparation` (`ck_order_items_preparation`); a cozinha lista as linhas com preparo, que são os pratos avulsos, as partes de prato e os adicionais deles. O `requires_preparation` do `FIXED_DISH` e da parte de prato é o snapshot de `dishes.requires_preparation`; o do `GROUP_ADDON` é o do pai.
- **Fechamento** (item 13, Q-311, F-314): `daily_closing_lines` aceita a chave `A_LA_CARTE` na dimensão `ORDER_TYPE`, **uma linha**, como as outras modalidades. A abertura por atendimento (salão, entrega, retirada) recomendada no Q-311 é informativa e não é gravada no snapshot (se o usuário decidir gravá-la, vira uma dimensão nova numa migration futura). `daily_closings.site_sold_total` é o "Vendido pelo site" (ADR-0057 item 12): Σ `total_amount` dos pedidos `ACTIVE` aceitos do expediente com `price_list = 'SITE'`, parte de `total_sold` (`ck_daily_closings_site_sold`), só informativo. A base da cobrança da plataforma (com ou sem a taxa de entrega) está em aberto (Q-310). Os fechamentos existentes ficam com `0`, como as colunas informativas da 0003.

**Correção de dados:** os adicionais existentes (`ADDON` com pai) recebem `parent_item_kind` = tipo do pai (`MARMITA`/`PF`). A RLS de `order_items` é `FORCE`: o `FORCE` sai só durante o `UPDATE`, na mesma transação (§5.1). As restrições entram depois da correção.

**Expand/contract (09 §9.4):** a API anterior não conhece os tipos nem as colunas novas e continua gravando `MARMITA`, `PF`, `BEVERAGE` e `ADDON` como antes: as referências, o snapshot e o preparo desses tipos não mudam, e o `CHECK` aceita o `ADDON` filho **sem** `parent_item_kind` (a FK do tipo do pai não é verificada com a coluna nula, mas `fk_order_items_parent` continua garantindo o pai no mesmo pedido). A API desta versão sempre grava `parent_item_kind` nos filhos. O fechamento gravado pela API anterior fica com `site_sold_total = 0` (o default). **Contração pendente** (próxima release): preencher o `parent_item_kind` dos `ADDON` filhos gravados na janela do deploy (o mesmo `UPDATE` abaixo, com `c.parent_item_kind IS NULL`) e trocar o ramo do `ADDON` em `ck_order_items_parent` por `parent_item_kind IS NOT NULL AND parent_item_kind IN ('MARMITA', 'PF')`. O default de `site_sold_total` fica, como os da 0003.

```sql
-- Modalidade à la carte: salão, entrega e retirada (ADR-0058 itens 1 e 2). O CHECK da coluna tem o nome padrão.
ALTER TABLE orders DROP CONSTRAINT orders_order_type_check;
ALTER TABLE orders ADD CONSTRAINT orders_order_type_check CHECK (order_type IN ('MARMITA', 'PF', 'KILO', 'A_LA_CARTE'));
ALTER TABLE orders DROP CONSTRAINT ck_orders_fulfillment_by_type;
ALTER TABLE orders ADD CONSTRAINT ck_orders_fulfillment_by_type CHECK (
     (order_type = 'MARMITA' AND fulfillment_type IN ('DELIVERY', 'PICKUP'))
  OR (order_type IN ('PF', 'KILO') AND fulfillment_type = 'DINE_IN')
  OR (order_type = 'A_LA_CARTE' AND fulfillment_type IN ('DINE_IN', 'DELIVERY', 'PICKUP')));

-- Item do cardápio fixo: referências, snapshot de tamanho e sabor, tipo e prato do pai (ADR-0058 itens 8 e 9)
ALTER TABLE order_items
  ADD COLUMN parent_item_kind      text,
  ADD COLUMN parent_dish_id        uuid,
  ADD COLUMN dish_id               uuid,
  ADD COLUMN dish_size_id          uuid,
  ADD COLUMN beverage_size_id      uuid,
  ADD COLUMN beverage_flavor_id    uuid,
  ADD COLUMN combo_id              uuid,
  ADD COLUMN addon_option_id       uuid,
  ADD COLUMN size_name_snapshot    varchar(30),
  ADD COLUMN flavor_name_snapshot  varchar(40);

-- Adicionais existentes: o tipo do pai. A RLS de order_items é FORCE: o FORCE sai só durante o UPDATE (§5.1).
ALTER TABLE order_items NO FORCE ROW LEVEL SECURITY;
UPDATE order_items c
   SET parent_item_kind = p.item_kind
  FROM order_items p
 WHERE c.parent_order_item_id IS NOT NULL
   AND p.tenant_id = c.tenant_id AND p.order_id = c.order_id AND p.id = c.parent_order_item_id;
ALTER TABLE order_items FORCE ROW LEVEL SECURITY;

ALTER TABLE order_items DROP CONSTRAINT order_items_item_kind_check;
ALTER TABLE order_items ADD CONSTRAINT order_items_item_kind_check CHECK (item_kind IN (
  'MARMITA', 'PF', 'BEVERAGE', 'ADDON', 'FIXED_DISH', 'FIXED_BEVERAGE', 'COMBO', 'COMBO_PART', 'GROUP_ADDON'));

-- Cada tipo com as suas referências e nenhuma outra. Tamanho sem prato e sabor sem tamanho nunca passam:
-- a FK composta não seria verificada com o pai nulo.
ALTER TABLE order_items DROP CONSTRAINT ck_order_items_refs;
ALTER TABLE order_items ADD CONSTRAINT ck_order_items_refs CHECK (
     (item_kind = 'MARMITA' AND marmita_size_id IS NOT NULL
        AND num_nonnulls(product_id, dish_id, dish_size_id, beverage_size_id, beverage_flavor_id, combo_id, addon_option_id) = 0)
  OR (item_kind IN ('PF', 'BEVERAGE', 'ADDON') AND product_id IS NOT NULL
        AND num_nonnulls(marmita_size_id, dish_id, dish_size_id, beverage_size_id, beverage_flavor_id, combo_id, addon_option_id) = 0)
  OR (item_kind = 'FIXED_DISH' AND dish_id IS NOT NULL
        AND num_nonnulls(product_id, marmita_size_id, beverage_size_id, beverage_flavor_id, combo_id, addon_option_id) = 0)
  OR (item_kind = 'FIXED_BEVERAGE' AND beverage_size_id IS NOT NULL
        AND num_nonnulls(product_id, marmita_size_id, dish_id, dish_size_id, combo_id, addon_option_id) = 0)
  OR (item_kind = 'COMBO' AND combo_id IS NOT NULL
        AND num_nonnulls(product_id, marmita_size_id, dish_id, dish_size_id, beverage_size_id, beverage_flavor_id, addon_option_id) = 0)
  OR (item_kind = 'COMBO_PART'
        AND num_nonnulls(product_id, marmita_size_id, combo_id, addon_option_id) = 0
        AND (   (dish_id IS NOT NULL AND beverage_size_id IS NULL AND beverage_flavor_id IS NULL)
             OR (beverage_size_id IS NOT NULL AND dish_id IS NULL AND dish_size_id IS NULL)))
  OR (item_kind = 'GROUP_ADDON' AND addon_option_id IS NOT NULL
        AND num_nonnulls(product_id, marmita_size_id, dish_id, dish_size_id, beverage_size_id, beverage_flavor_id, combo_id) = 0));

-- Snapshot do tamanho e do sabor exatamente quando há tamanho e sabor (ADR-0020)
ALTER TABLE order_items ADD CONSTRAINT ck_order_items_snapshots CHECK (
      (size_name_snapshot IS NOT NULL) = (dish_size_id IS NOT NULL OR beverage_size_id IS NOT NULL)
  AND (flavor_name_snapshot IS NOT NULL) = (beverage_flavor_id IS NOT NULL));

-- Três níveis: COMBO → COMBO_PART → GROUP_ADDON; FIXED_DISH → GROUP_ADDON; MARMITA/PF → ADDON (item 9, Q-321).
-- ADDON filho sem parent_item_kind: só a API anterior, na janela do deploy (contração pendente).
ALTER TABLE order_items DROP CONSTRAINT ck_order_items_parent;
ALTER TABLE order_items ADD CONSTRAINT ck_order_items_parent CHECK (
     (parent_order_item_id IS NULL AND parent_item_kind IS NULL AND item_kind NOT IN ('COMBO_PART', 'GROUP_ADDON'))
  OR (parent_order_item_id IS NOT NULL AND item_kind = 'ADDON'
        AND (parent_item_kind IS NULL OR parent_item_kind IN ('MARMITA', 'PF')))
  OR (parent_order_item_id IS NOT NULL AND item_kind = 'COMBO_PART'
        AND parent_item_kind IS NOT NULL AND parent_item_kind = 'COMBO')
  OR (parent_order_item_id IS NOT NULL AND item_kind = 'GROUP_ADDON'
        AND parent_item_kind IS NOT NULL AND parent_item_kind IN ('FIXED_DISH', 'COMBO_PART')));
-- O adicional de grupo fica sob um prato (avulso ou parte de prato do combo), nunca sob a parte de bebida
ALTER TABLE order_items ADD CONSTRAINT ck_order_items_parent_dish CHECK ((item_kind = 'GROUP_ADDON') = (parent_dish_id IS NOT NULL));

-- Cozinha (itens 11 e 12): combo e bebidas fora; MARMITA/PF sempre com preparo
ALTER TABLE order_items DROP CONSTRAINT ck_order_items_preparation;
ALTER TABLE order_items ADD CONSTRAINT ck_order_items_preparation CHECK (
      (item_kind NOT IN ('MARMITA', 'PF') OR requires_preparation)
  AND (item_kind NOT IN ('COMBO', 'FIXED_BEVERAGE') OR NOT requires_preparation)
  AND (item_kind <> 'COMBO_PART' OR beverage_size_id IS NULL OR NOT requires_preparation));

-- Parte do combo custa 0.00, salvo o acréscimo do sabor (item 8)
ALTER TABLE order_items ADD CONSTRAINT ck_order_items_combo_part_price CHECK (
  item_kind <> 'COMBO_PART' OR beverage_flavor_id IS NOT NULL OR (unit_price = 0 AND line_total = 0));

-- Alvos das FKs do pai: tipo e prato do pai, no mesmo pedido
ALTER TABLE order_items
  ADD CONSTRAINT ux_order_items_parent_kind_target UNIQUE (tenant_id, order_id, id, item_kind),
  ADD CONSTRAINT ux_order_items_parent_dish_target UNIQUE (tenant_id, order_id, id, dish_id);

ALTER TABLE order_items
  ADD CONSTRAINT fk_order_items_parent_kind FOREIGN KEY (tenant_id, order_id, parent_order_item_id, parent_item_kind)
    REFERENCES order_items (tenant_id, order_id, id, item_kind),
  ADD CONSTRAINT fk_order_items_parent_dish FOREIGN KEY (tenant_id, order_id, parent_order_item_id, parent_dish_id)
    REFERENCES order_items (tenant_id, order_id, id, dish_id),
  ADD CONSTRAINT fk_order_items_dish FOREIGN KEY (tenant_id, dish_id) REFERENCES dishes (tenant_id, id),
  ADD CONSTRAINT fk_order_items_dish_size FOREIGN KEY (tenant_id, dish_id, dish_size_id)
    REFERENCES dish_sizes (tenant_id, dish_id, id),
  ADD CONSTRAINT fk_order_items_beverage_size FOREIGN KEY (tenant_id, beverage_size_id)
    REFERENCES beverage_sizes (tenant_id, id),
  ADD CONSTRAINT fk_order_items_beverage_flavor FOREIGN KEY (tenant_id, beverage_size_id, beverage_flavor_id)
    REFERENCES beverage_flavors (tenant_id, beverage_size_id, id),
  ADD CONSTRAINT fk_order_items_combo FOREIGN KEY (tenant_id, combo_id) REFERENCES combos (tenant_id, id),
  ADD CONSTRAINT fk_order_items_addon_option FOREIGN KEY (tenant_id, addon_option_id)
    REFERENCES addon_options (tenant_id, id);

-- Fechamento: linha "À la carte" (item 13, Q-311) e "Vendido pelo site", informativo (ADR-0057 item 12)
ALTER TABLE daily_closing_lines DROP CONSTRAINT ck_daily_closing_lines_dimension;
ALTER TABLE daily_closing_lines ADD CONSTRAINT ck_daily_closing_lines_dimension CHECK (
     (dimension = 'ORDER_TYPE' AND key IN ('MARMITA', 'PF', 'KILO', 'A_LA_CARTE')
        AND sold IS NOT NULL AND pending IS NOT NULL)
  OR (dimension = 'PAYMENT_METHOD' AND key IN ('CASH', 'PIX', 'CARD')
        AND sold IS NULL AND pending IS NULL));
ALTER TABLE daily_closings
  ADD COLUMN site_sold_total numeric(12,2) NOT NULL DEFAULT 0 CHECK (site_sold_total >= 0);
ALTER TABLE daily_closings ADD CONSTRAINT ck_daily_closings_site_sold CHECK (site_sold_total <= total_sold);
```

### 0011_PhoneDeliveryCustomer — 2026-11-22 (ADR-0059, ADR-0021, ADR-0024, ADR-0046)

Delivery por telefone e cliente pelo telefone (EP-003 fatia 4, F-315 a F-318). ADR-0059 está `Proposto`. Quase tudo já existia; a alteração só abre a forma prevista ao pedido do app, corrige o `CHECK` do troco e indexa o último pedido do cliente:

- **Já existia, sem mudança:** o snapshot do nome e do telefone no pedido (`customer_name`, `customer_phone`, E.164) e o vínculo `customer_id` (0001); o endereço **só no pedido** (`delivery_address`; `customer_addresses` continua para a marmita, Q-313); o telefone **único por tenant** em `customers` (`ux_customers_phone`, parcial em `phone_number IS NOT NULL`, desde a 0001: não há duplicados a normalizar, e o cliente anonimizado, de telefone nulo, libera o número); a origem `MANUAL` da taxa (`delivery_fee_source`, 0005). A taxa digitada na criação diferente da padrão grava `MANUAL` (Q-315); o autor é o `created_by_employee_id` e o valor digitado vai no `after` da auditoria da criação (`audit_logs`, sem coluna nova). "Cliente do delivery na lista sem saldo" (ADR-0059 item 16) é consulta: o saldo sai de `ix_orders_customer_pending`, e um cliente sem pedido em aberto aparece com zero. O cadastro que não muda sozinho (Q-314) é o `ON CONFLICT … DO NOTHING` de §5.14.
- **Forma prevista e "troco para" no pedido do app** (ADR-0059 item 5, Q-316): `ck_orders_web_fields` sai e o que sobra dele, o risco do anti-robô só em `WEB`, vira `ck_orders_risk_flag`. A forma prevista fica só em entrega e retirada (`ck_orders_expected_payment_fulfillment`): a comanda do salão paga na mesa e não tem o campo.
- **Troco só com a forma dinheiro, inclusive com a forma nula:** o `ck_orders_change_for` da 0004 (`change_for IS NULL OR expected_payment_method = 'CASH'`) passava com a forma nula, porque `NULL = 'CASH'` é `NULL` (05 §5.0). Reescrito com `IS NOT NULL`.
- **"Troco para" pelo menos o total** (ADR-0059 item 5) fica **na aplicação**, conferido na criação e quando a forma prevista ou o "troco para" mudam, como no pedido do site ("≥ total no envio", 04 §4.7). Não é `CHECK`: uma edição posterior do item ou da taxa pode passar o total do "troco para", e o banco recusaria a edição do pedido por um campo informativo (o pagamento real registra `cash_received`, ADR-0021).
- **"Usar o endereço do último pedido"** (Q-313) e o histórico do cliente: `ix_orders_customer_recent (tenant_id, customer_id, created_at)`. Antes, só as pendências do cliente tinham índice (`ix_orders_customer_pending`, parcial nos não pagos).

**Ficam na aplicação**, e não em `CHECK` (05 §5.16): nome e telefone obrigatórios na entrega e na retirada à la carte e na entrega de marmita (Q-312), e a forma prevista obrigatória na entrega (Q-316). Os pedidos existentes não os têm (marmita com telefone opcional, desde a 0001; entrega à la carte gravada pela API da 0010), um `CHECK` com `NOT VALID` ainda recusaria o próximo `UPDATE` dessas linhas (pagamento, atendimento), e a anonimização do cliente apaga o telefone do snapshot (07, `Order.AnonymizeCustomer`).

**Correção de dados:** pedido com "troco para" e sem forma prevista (nenhum esperado: a API sempre validou) recebe a forma `CASH`, a única com troco, para o `CHECK` novo entrar. A RLS de `orders` é `FORCE`: o `FORCE` sai só durante o `UPDATE`, na mesma transação (§5.1).

**Expand/contract (09 §9.4):** só afrouxa (forma prevista no app) e aperta o que a API anterior nunca grava: ela não manda forma prevista nem troco em pedido do app, nem troco sem forma no site. **Contração pendente** (próxima release, com a API desta versão em produção), **condicionada**: se `SELECT count(*) FROM orders WHERE order_type = 'A_LA_CARTE' AND fulfillment_type <> 'DINE_IN' AND customer_name IS NULL` der 0 (sem entrega ou retirada à la carte sem nome gravada pela API da 0010), criar `ck_orders_a_la_carte_customer CHECK (order_type <> 'A_LA_CARTE' OR fulfillment_type = 'DINE_IN' OR customer_name IS NOT NULL)`, como o `ck_orders_marmita_customer` (a anonimização mantém o nome com o marcador "Cliente anonimizado"). Se der mais que 0, a regra fica só na aplicação e a contração é descartada. O telefone não entra: a anonimização o apaga.

```sql
-- Forma prevista e "troco para" também no pedido do app (ADR-0059 item 5); o risco do anti-robô continua só no WEB
ALTER TABLE orders DROP CONSTRAINT ck_orders_web_fields;
ALTER TABLE orders ADD CONSTRAINT ck_orders_risk_flag CHECK (origin = 'WEB' OR risk_flag IS NULL);

-- "Troco para" sem forma prevista: nenhum esperado; recebe a forma dinheiro, a única com troco.
-- A RLS de orders é FORCE: o FORCE sai só durante o UPDATE (§5.1).
ALTER TABLE orders NO FORCE ROW LEVEL SECURITY;
UPDATE orders SET expected_payment_method = 'CASH' WHERE change_for IS NOT NULL AND expected_payment_method IS NULL;
ALTER TABLE orders FORCE ROW LEVEL SECURITY;

-- Troco só com a forma dinheiro. O CHECK da 0004 passava com a forma nula (NULL = 'CASH' é NULL, §5.0).
ALTER TABLE orders DROP CONSTRAINT ck_orders_change_for;
ALTER TABLE orders ADD CONSTRAINT ck_orders_change_for CHECK (
  change_for IS NULL OR (expected_payment_method IS NOT NULL AND expected_payment_method = 'CASH'));
-- Forma prevista só em entrega e retirada: a comanda do salão paga na mesa (ADR-0059 item 5, Q-316)
ALTER TABLE orders ADD CONSTRAINT ck_orders_expected_payment_fulfillment CHECK (
  expected_payment_method IS NULL OR fulfillment_type IN ('DELIVERY', 'PICKUP'));

-- "Usar o endereço do último pedido" e histórico do cliente (ADR-0059 item 13, Q-313)
CREATE INDEX ix_orders_customer_recent ON orders (tenant_id, customer_id, created_at) WHERE customer_id IS NOT NULL;
```

### 0012_SitePriceCorrections — 2026-11-23 (ADR-0057 itens 2, 5, 12 e 15)

Correções da revisão do EP-003 decididas pelo usuário em 2026-11-23 (achados I6a, I6b e M1). ADR-0057 está `Proposto`, e o item 2 (preço de site `> 0`) e o item 12 ("Vendido pelo site" como Σ do total) precisam ser emendados para registrar as duas mudanças abaixo.

- **Bebida do restaurante com preço de site `0.00`** (I6a): `products.site_price` passa de `> 0` para `>= 0` (`products_site_price_check`, o `CHECK` da coluna da 0009, com o nome padrão). `ck_products_site_price` continua: só `BEVERAGE` tem preço de site, então a exceção vale **só para a bebida do restaurante** (água da casa, cafezinho). Tamanho de marmita, prato, tamanho de prato, tamanho de bebida do cardápio fixo e combo continuam `> 0`.
- **"Vendido pelo site" sem a taxa de entrega** (M1): `daily_closings.site_delivery_fee_total` (≥ 0, padrão `0`) guarda Σ `delivery_fee` dos pedidos `ACTIVE` aceitos do expediente com `price_list = 'SITE'`, e `site_sold_total` passa a ser **sem a taxa**: Σ (`total_amount` − `delivery_fee`) dos mesmos pedidos, ou seja, os itens a preço de site. Os dois juntos são a parte do site em `total_sold` (`ck_daily_closings_site_sold`, reescrito: `site_sold_total + site_delivery_fee_total <= total_sold`), e a taxa do site é parte de `delivery_fees_total` (`ck_daily_closings_site_delivery_fee`). Continua só informativo, e a base da cobrança da plataforma continua em aberto (Q-310): com as duas colunas, qualquer das bases sai do snapshot.

**Correção de dados.** A versão das linhas não muda e nenhum `audit_logs`/`change_feed_entries` é gravado (a correção não é edição do usuário, como na 0009). A RLS de `products`, `marmita_sizes`, `daily_closings`, `orders` e `audit_logs` é `FORCE`: o `FORCE` sai só durante os `UPDATE`s, na mesma transação (§5.1).

1. **Bebidas de `0.00` que a 0009 tirou da página** voltam: `is_visible_on_page = true`, `site_price = 0.00`. A 0009 não deixou registro (o `UPDATE` não grava auditoria nem muda a versão), mas **toda** escrita de produto pela aplicação grava `ProductCreated`/`ProductUpdated` com o estado completo em `after` (`isVisibleOnPage` desde a 0004; antes dela a chave não existia e a coluna nasceu `true`). Por isso a bebida é distinguível: é de `0.00`, está escondida e sem preço de site, e a **última** entrada da auditoria dela diz que estava visível (`after ->> 'isVisibleOnPage'` = `true`, ou ausente por ser anterior à 0004). Se a última entrada diz `false`, o dono a escondeu (antes ou depois da 0009), ou a API da 0009 a criou fora do site: não muda. Produto sem nenhuma entrada de auditoria (só gravado por SQL fora da aplicação) não muda.
2. **Tamanhos de marmita inativos** (I6b): o problema é de banco. A 0009 só ligou a chave nos tamanhos **ativos**; os inativos ficaram `sells_on_site = false`, `site_price NULL`, e o `PATCH` que reativa não toca na chave, então o tamanho volta fora do site. A chave desligada guarda o `site_price` (a API da 0009 nunca o apaga), então `sells_on_site = false` com `site_price NULL` num inativo é um tamanho que nunca teve a chave ligada. Esses recebem `sells_on_site = true`, `site_price = price`, como a carga dos ativos, **exceto** os que a API da 0009 já gravou (há entrada de auditoria com `after ? 'sellsOnSite'`): neles o dono viu a chave desligada, e a escolha fica. O tamanho reativado **entre a 0009 e esta alteração** já está ativo e fora do site: não muda (o dono pode ter visto e aceitado), e a consulta de 09 §9.4 o lista.
3. **Fechamentos existentes** (M1): o `site_sold_total` gravado é Σ `total_amount` (com a taxa). O fechamento é imutável e não guardou a parte da taxa, mas ela sai dos pedidos do expediente **se os pedidos ainda são os do snapshot**: quando Σ `total_amount` dos pedidos `SITE` ativos aceitos do expediente **é igual** ao `site_sold_total` gravado (e a taxa cabe no `delivery_fees_total`), `site_delivery_fee_total` = Σ `delivery_fee` e `site_sold_total` = gravado − taxa. Quando não é igual (algum pedido mudou depois do fechamento, ou o fechamento é anterior à 0010 e ficou com `0`), o fechamento fica como está: **com a taxa dentro** do `site_sold_total` e `site_delivery_fee_total = 0`. O `UPDATE` é repetível: só toca em fechamento com `site_delivery_fee_total = 0` cujo gravado ainda é a soma com a taxa.

**Expand/contract (09 §9.4):** só afrouxa (`>= 0` na bebida) e acrescenta (coluna com padrão `0`). Na janela do deploy, a API anterior ainda grava o fechamento com `site_sold_total` **com** a taxa e `site_delivery_fee_total = 0`; o `CHECK` novo aceita (é o mesmo limite de antes). **Correção pós-deploy** (logo depois do smoke do deploy que põe a API da 0012 em produção, e em homolog): repetir o `UPDATE` 3 abaixo com o login `almoseis_migrator`, sem migration nova. A API anterior continua recusando bebida de `0.00` visível; a desta versão aceita. Sem contração. A contração pendente da 0009 muda: a bebida visível sem preço de site gravada na janela recebe `site_price = default_price` também quando é `0.00`, e não sai mais do site.

```sql
-- Bebida do restaurante pode ter preço de site 0.00 (I6a). O CHECK da coluna tem o nome padrão;
-- ck_products_site_price continua: só BEVERAGE tem preço de site. As outras tabelas continuam > 0.
ALTER TABLE products DROP CONSTRAINT products_site_price_check;
ALTER TABLE products ADD CONSTRAINT products_site_price_check CHECK (site_price >= 0);

-- Taxa de entrega dos pedidos do site, separada do "Vendido pelo site" (M1, ADR-0057 item 12)
ALTER TABLE daily_closings
  ADD COLUMN site_delivery_fee_total numeric(12,2) NOT NULL DEFAULT 0 CHECK (site_delivery_fee_total >= 0);

-- Correção de dados. A RLS das tabelas é FORCE: o FORCE sai só durante os UPDATEs, na mesma transação (§5.1).
ALTER TABLE products NO FORCE ROW LEVEL SECURITY;
ALTER TABLE marmita_sizes NO FORCE ROW LEVEL SECURITY;
ALTER TABLE daily_closings NO FORCE ROW LEVEL SECURITY;
ALTER TABLE orders NO FORCE ROW LEVEL SECURITY;
ALTER TABLE audit_logs NO FORCE ROW LEVEL SECURITY;

-- 1. Bebida de 0.00 escondida pela 0009: a última auditoria dela diz visível (ou é anterior à 0004, quando a coluna
--    nasceu true). Se diz false, o dono a escondeu: não muda. Sem auditoria: não muda.
UPDATE products p
   SET is_visible_on_page = true, site_price = 0.00
  FROM (SELECT DISTINCT ON (tenant_id, entity_id) tenant_id, entity_id, after
          FROM audit_logs
         WHERE entity_type = 'PRODUCT' AND operation_type IN ('ProductCreated', 'ProductUpdated')
         ORDER BY tenant_id, entity_id, occurred_at DESC, id DESC) last
 WHERE p.kind = 'BEVERAGE' AND p.default_price = 0 AND NOT p.is_visible_on_page AND p.site_price IS NULL
   AND last.tenant_id = p.tenant_id AND last.entity_id = p.id
   AND coalesce((last.after ->> 'isVisibleOnPage')::boolean, true);

-- 2. Tamanho de marmita inativo que nunca teve a chave ligada (a 0009 só carregou os ativos), salvo o que a API da
--    0009 já gravou (o dono viu a chave desligada).
UPDATE marmita_sizes s
   SET sells_on_site = true, site_price = s.price
 WHERE NOT s.is_active AND NOT s.sells_on_site AND s.site_price IS NULL
   AND NOT EXISTS (SELECT 1 FROM audit_logs a
                    WHERE a.tenant_id = s.tenant_id AND a.entity_type = 'MARMITA_SIZE' AND a.entity_id = s.id
                      AND a.after ? 'sellsOnSite');

-- 3. Fechamento cujo "Vendido pelo site" ainda é a soma dos pedidos do site com a taxa: separa a taxa.
--    Repetível (correção pós-deploy, 09 §9.4): só toca em site_delivery_fee_total = 0.
UPDATE daily_closings c
   SET site_sold_total = c.site_sold_total - s.fees, site_delivery_fee_total = s.fees
  FROM (SELECT tenant_id, business_day_id, sum(total_amount) AS sold, sum(delivery_fee) AS fees
          FROM orders
         WHERE price_list = 'SITE' AND acceptance_status = 'ACCEPTED' AND cancellation_status = 'ACTIVE'
         GROUP BY tenant_id, business_day_id) s
 WHERE s.tenant_id = c.tenant_id AND s.business_day_id = c.business_day_id
   AND c.site_delivery_fee_total = 0 AND s.fees > 0
   AND c.site_sold_total = s.sold AND s.fees <= c.delivery_fees_total;

ALTER TABLE products FORCE ROW LEVEL SECURITY;
ALTER TABLE marmita_sizes FORCE ROW LEVEL SECURITY;
ALTER TABLE daily_closings FORCE ROW LEVEL SECURITY;
ALTER TABLE orders FORCE ROW LEVEL SECURITY;
ALTER TABLE audit_logs FORCE ROW LEVEL SECURITY;

-- Itens e taxa do site, juntos, dentro do vendido; a taxa do site dentro das taxas (M1)
ALTER TABLE daily_closings DROP CONSTRAINT ck_daily_closings_site_sold;
ALTER TABLE daily_closings ADD CONSTRAINT ck_daily_closings_site_sold CHECK (site_sold_total + site_delivery_fee_total <= total_sold);
ALTER TABLE daily_closings ADD CONSTRAINT ck_daily_closings_site_delivery_fee CHECK (site_delivery_fee_total <= delivery_fees_total);
```
