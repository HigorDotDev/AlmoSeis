-- GERADO por backend/tools/Sync-DdlMigration.ps1 a partir de docs/05-ddl.md. NÃO EDITE À MÃO.
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
