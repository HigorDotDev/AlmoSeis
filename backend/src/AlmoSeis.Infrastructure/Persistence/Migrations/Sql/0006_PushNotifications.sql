-- GERADO por backend/tools/Sync-DdlMigration.ps1 a partir de docs/05-ddl.md. NÃO EDITE À MÃO.
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
