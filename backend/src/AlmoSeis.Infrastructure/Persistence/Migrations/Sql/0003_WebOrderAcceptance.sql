-- GERADO por backend/tools/Sync-DdlMigration.ps1 a partir de docs/05-ddl.md. NÃO EDITE À MÃO.
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
