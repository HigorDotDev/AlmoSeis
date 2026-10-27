\set ON_ERROR_STOP on
SET client_min_messages = notice;
SET ROLE almoseis_app;

-- ===== Tenant A (autocadastro) =====
SELECT set_config('app.tenant_id', 'aaaaaaaa-0000-0000-0000-000000000001', false),
       set_config('app.user_id',   'aaaaaaaa-0000-0000-0000-0000000000a1', false);

INSERT INTO tenants (id, name, document_type, document_number, city, state, created_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'Marmitaria A', 'CNPJ', '11222333000181', 'Uberlandia', 'MG', now());
INSERT INTO users (id, full_name, email, normalized_email, password_hash, security_stamp, concurrency_stamp, created_at)
VALUES ('aaaaaaaa-0000-0000-0000-0000000000a1', 'Dona A', 'a@x.com', 'A@X.COM', 'h', 's', 'c', now());
INSERT INTO tenant_employees (id, tenant_id, user_id, display_name, status, security_stamp, joined_at)
VALUES ('aaaaaaaa-0000-0000-0000-0000000000e1', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000a1', 'Dona A', 'ACTIVE', gen_random_uuid(), now());
INSERT INTO tenant_employee_roles VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000e1', 'OWNER');
INSERT INTO tenant_settings (tenant_id, kilo_price_per_kg) VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 59.90);
INSERT INTO tenant_sync_counters (tenant_id) VALUES ('aaaaaaaa-0000-0000-0000-000000000001');
INSERT INTO subscriptions (id, tenant_id, status, plan_code, monthly_price, trial_ends_at)
VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'TRIALING', 'BASIC', 99.90, now() + interval '7 days');

-- Expediente, catálogo, pedido de marmita com bebida, pagamento em dinheiro com troco
INSERT INTO business_days (id, tenant_id, business_date, status, opening_balance, opened_by_employee_id, opened_at)
VALUES ('aaaaaaaa-0000-0000-0000-0000000000d1', 'aaaaaaaa-0000-0000-0000-000000000001', '2026-10-27', 'OPEN', 100, 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
INSERT INTO marmita_sizes (id, tenant_id, name, price) VALUES ('aaaaaaaa-0000-0000-0000-0000000000f1', 'aaaaaaaa-0000-0000-0000-000000000001', 'M', 20.00);
INSERT INTO products (id, tenant_id, kind, name, default_price, requires_preparation, created_at, updated_at)
VALUES ('aaaaaaaa-0000-0000-0000-0000000000b1', 'aaaaaaaa-0000-0000-0000-000000000001', 'BEVERAGE', 'Coca 350ml', 5.00, false, now(), now());

INSERT INTO business_date_counters (tenant_id, business_date, last_order_number)
VALUES ('aaaaaaaa-0000-0000-0000-000000000001', '2026-10-27', 1)
ON CONFLICT (tenant_id, business_date) DO UPDATE SET last_order_number = business_date_counters.last_order_number + 1
RETURNING last_order_number AS order_number_1;

INSERT INTO orders (id, tenant_id, business_day_id, business_date, order_number, order_type, origin, fulfillment_type,
                    customer_name, items_total, weighings_total, total_amount, production_status,
                    created_by_employee_id, created_at, updated_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000000c01', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-10-27', 1,
        'MARMITA', 'APP', 'PICKUP', 'Joao', 25.00, 0, 25.00, 'OPEN', 'aaaaaaaa-0000-0000-0000-0000000000e1', now(), now());
INSERT INTO order_items (id, tenant_id, order_id, item_kind, marmita_size_id, quantity, unit_price, line_total, name_snapshot,
                         requires_preparation, status, created_by_employee_id, created_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000000101', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c01', 'MARMITA',
        'aaaaaaaa-0000-0000-0000-0000000000f1', 1, 20.00, 20.00, 'Marmita M', true, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
INSERT INTO order_items (id, tenant_id, order_id, item_kind, product_id, quantity, unit_price, line_total, name_snapshot,
                         requires_preparation, status, created_by_employee_id, created_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000000102', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c01', 'BEVERAGE',
        'aaaaaaaa-0000-0000-0000-0000000000b1', 1, 5.00, 5.00, 'Coca 350ml', false, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());

INSERT INTO payments (id, tenant_id, order_id, business_day_id, business_date, amount, payment_method, cash_received, change_given,
                      status, paid_at, created_by_employee_id)
VALUES ('aaaaaaaa-0000-0000-0000-000000000a01', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c01',
        'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-10-27', 25.00, 'CASH', 30.00, 5.00, 'CONFIRMED', now(), 'aaaaaaaa-0000-0000-0000-0000000000e1');
UPDATE orders SET paid_amount = 25.00, is_paid = true, version = version + 1 WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c01';

-- Pedido de quilo com pesagens (arredondamento AwayFromZero)
INSERT INTO orders (id, tenant_id, business_day_id, business_date, order_number, order_type, origin, fulfillment_type,
                    items_total, weighings_total, total_amount, created_by_employee_id, created_at, updated_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000000c02', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-10-27', 2,
        'KILO', 'APP', 'DINE_IN', 0, 27.07, 27.07, 'aaaaaaaa-0000-0000-0000-0000000000e1', now(), now());
INSERT INTO weighing_entries (id, tenant_id, order_id, weight_grams, price_per_kg, amount, status, created_by_employee_id, created_at)
VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c02', 452, 59.90, 27.07, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
-- 250 g x 1,42 = 0,355 -> 0,36 (metade para longe do zero)
INSERT INTO weighing_entries (id, tenant_id, order_id, weight_grams, price_per_kg, amount, status, created_by_employee_id, created_at)
VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c02', 250, 1.42, 0.36, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());

-- ===== Falhas esperadas (tenant A) =====
DO $$ BEGIN
  INSERT INTO weighing_entries (id, tenant_id, order_id, weight_grams, price_per_kg, amount, status, created_by_employee_id, created_at)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c02', 250, 1.42, 0.35, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
  RAISE EXCEPTION 'FALHOU: pesagem arredondada errado foi aceita';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: arredondamento da pesagem validado'; END $$;

DO $$ BEGIN
  UPDATE orders SET paid_amount = 10 WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c01';
  RAISE EXCEPTION 'FALHOU: is_paid inconsistente aceito';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: is_paid coerente com paid_amount'; END $$;

DO $$ BEGIN
  UPDATE orders SET cancellation_status = 'CANCELED', canceled_at = now(), canceled_by_employee_id = 'aaaaaaaa-0000-0000-0000-0000000000e1',
                    cancellation_reason = 'cliente desistiu' WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c01';
  RAISE EXCEPTION 'FALHOU: cancelamento de pedido pago aceito';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: pedido com pagamento nao cancela'; END $$;

DO $$ BEGIN
  INSERT INTO business_days (id, tenant_id, business_date, status, opening_balance, opened_by_employee_id, opened_at)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', '2026-10-27', 'OPEN', 0, 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
  RAISE EXCEPTION 'FALHOU: segundo expediente aberto aceito';
EXCEPTION WHEN unique_violation THEN RAISE NOTICE 'ok: um expediente aberto por tenant'; END $$;

DO $$ BEGIN
  INSERT INTO payments (id, tenant_id, order_id, business_day_id, business_date, amount, payment_method, cash_received, change_given,
                        status, paid_at, created_by_employee_id)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c02',
          'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-10-28', 5, 'PIX', NULL, NULL, 'CONFIRMED', now(), 'aaaaaaaa-0000-0000-0000-0000000000e1');
  RAISE EXCEPTION 'FALHOU: business_date diferente do expediente aceita';
EXCEPTION WHEN foreign_key_violation THEN RAISE NOTICE 'ok: data do pagamento = data do expediente'; END $$;

DO $$ BEGIN
  INSERT INTO payments (id, tenant_id, order_id, business_day_id, business_date, amount, payment_method, cash_received, change_given,
                        status, paid_at, created_by_employee_id)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c02',
          'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-10-27', 5, 'PIX', 10, 5, 'CONFIRMED', now(), 'aaaaaaaa-0000-0000-0000-0000000000e1');
  RAISE EXCEPTION 'FALHOU: troco em Pix aceito';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: troco so em dinheiro'; END $$;

DO $$ BEGIN
  INSERT INTO payments (id, tenant_id, order_id, business_day_id, business_date, amount, payment_method, cash_received, change_given,
                        status, paid_at, created_by_employee_id)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c02',
          'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-10-27', 5, 'CASH', NULL, 5, 'CONFIRMED', now(), 'aaaaaaaa-0000-0000-0000-0000000000e1');
  RAISE EXCEPTION 'FALHOU: troco sem valor recebido aceito';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: troco exige valor recebido (NULL)'; END $$;

DO $$ BEGIN
  UPDATE orders SET cancellation_status = 'CANCELED', canceled_at = now(), canceled_by_employee_id = 'aaaaaaaa-0000-0000-0000-0000000000e1',
                    cancellation_reason = NULL WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c02';
  RAISE EXCEPTION 'FALHOU: cancelamento sem motivo aceito';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: cancelamento exige motivo (NULL)'; END $$;

DO $$ BEGIN
  INSERT INTO employee_invitations (id, tenant_id, purpose, code, roles, status, expires_at, created_by_employee_id, created_at)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'INVITE', 'XYZ789', NULL, 'PENDING',
          now() + interval '72 hours', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
  RAISE EXCEPTION 'FALHOU: convite sem perfis aceito';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: convite exige perfis (NULL)'; END $$;

DO $$ BEGIN
  DELETE FROM payments WHERE id = 'aaaaaaaa-0000-0000-0000-000000000a01';
  RAISE EXCEPTION 'FALHOU: DELETE em payments permitido';
EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'ok: app nao apaga pagamento'; END $$;

DO $$ BEGIN
  UPDATE payments SET amount = 1 WHERE id = 'aaaaaaaa-0000-0000-0000-000000000a01';
  RAISE EXCEPTION 'FALHOU: UPDATE de amount permitido';
EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'ok: app so altera payments.status'; END $$;

-- Estorno coerente: valor diferente do pagamento é rejeitado
DO $$ BEGIN
  INSERT INTO payment_reversals (id, tenant_id, payment_id, order_id, business_day_id, business_date, amount, payment_method, reason, created_by_employee_id, created_at)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000a01', 'aaaaaaaa-0000-0000-0000-000000000c01',
          'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-10-27', 20.00, 'CASH', 'troco errado', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
  RAISE EXCEPTION 'FALHOU: estorno com valor diferente aceito';
EXCEPTION WHEN foreign_key_violation THEN RAISE NOTICE 'ok: estorno = valor e metodo do pagamento'; END $$;

-- Fechamento: diferença sem justificativa é rejeitada; com justificativa entra e fica imutável
DO $$ BEGIN
  INSERT INTO daily_closings (id, tenant_id, business_day_id, business_date, order_count, total_sold, delivery_fees_total, total_received,
    received_from_previous_days, total_pending, canceled_count, canceled_total, reversed_count, reversed_total, opening_balance,
    cash_received, cash_reversed, supplies, withdrawals, expected_cash, counted_cash, cash_difference, justification,
    closed_by_employee_id, closed_at)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-10-27', 2, 52.07, 0, 25.00,
    0, 27.07, 0, 0, 0, 0, 100, 25.00, 0, 0, 0, 125.00, 120.00, -5.00, NULL, 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
  RAISE EXCEPTION 'FALHOU: divergência sem justificativa aceita';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: divergencia exige justificativa'; END $$;

INSERT INTO daily_closings (id, tenant_id, business_day_id, business_date, order_count, total_sold, delivery_fees_total, total_received,
  received_from_previous_days, total_pending, canceled_count, canceled_total, reversed_count, reversed_total, opening_balance,
  cash_received, cash_reversed, supplies, withdrawals, expected_cash, counted_cash, cash_difference, justification,
  closed_by_employee_id, closed_at)
VALUES ('aaaaaaaa-0000-0000-0000-0000000000c9', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-10-27', 2, 52.07, 0, 25.00,
  0, 27.07, 0, 0, 0, 0, 100, 25.00, 0, 0, 0, 125.00, 120.00, -5.00, 'nota de 5 faltando', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());

DO $$ BEGIN
  UPDATE daily_closings SET counted_cash = 125.00, cash_difference = 0 WHERE id = 'aaaaaaaa-0000-0000-0000-0000000000c9';
  RAISE EXCEPTION 'FALHOU: fechamento alterado';
EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'ok: fechamento imutavel'; END $$;

DO $$ BEGIN
  UPDATE audit_logs SET operation_type = 'x';
  RAISE EXCEPTION 'FALHOU: auditoria alterada';
EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'ok: auditoria append-only'; END $$;

-- ===== Tenant B =====
SELECT set_config('app.tenant_id', 'bbbbbbbb-0000-0000-0000-000000000002', false),
       set_config('app.user_id',   'bbbbbbbb-0000-0000-0000-0000000000b2', false);
INSERT INTO tenants (id, name, document_type, document_number, city, state, created_at)
VALUES ('bbbbbbbb-0000-0000-0000-000000000002', 'Restaurante B', 'CPF', '12345678901', 'Uberlandia', 'MG', now());
INSERT INTO users (id, full_name, phone_number, password_hash, security_stamp, concurrency_stamp, created_at)
VALUES ('bbbbbbbb-0000-0000-0000-0000000000b2', 'Seu B', '+5534999990000', 'h', 's', 'c', now());
INSERT INTO tenant_employees (id, tenant_id, user_id, display_name, status, security_stamp, joined_at)
VALUES ('bbbbbbbb-0000-0000-0000-0000000000e2', 'bbbbbbbb-0000-0000-0000-000000000002', 'bbbbbbbb-0000-0000-0000-0000000000b2', 'Seu B', 'ACTIVE', gen_random_uuid(), now());
INSERT INTO employee_invitations (id, tenant_id, purpose, code, roles, status, expires_at, created_by_employee_id, created_at)
VALUES (gen_random_uuid(), 'bbbbbbbb-0000-0000-0000-000000000002', 'INVITE', 'ABC234', ARRAY['ATTENDANT'], 'PENDING',
        now() + interval '72 hours', 'bbbbbbbb-0000-0000-0000-0000000000e2', now());

DO $$ BEGIN
  IF (SELECT count(*) FROM orders) <> 0 THEN RAISE EXCEPTION 'FALHOU: tenant B ve pedidos de A'; END IF;
  IF (SELECT count(*) FROM payments) <> 0 THEN RAISE EXCEPTION 'FALHOU: tenant B ve pagamentos de A'; END IF;
  IF (SELECT count(*) FROM tenants) <> 1 THEN RAISE EXCEPTION 'FALHOU: tenant B ve outros tenants'; END IF;
  RAISE NOTICE 'ok: tenant B nao le dados de A';
END $$;

DO $$ BEGIN
  UPDATE orders SET notes = 'invadido' WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c01';
  IF FOUND THEN RAISE EXCEPTION 'FALHOU: tenant B alterou pedido de A'; END IF;
  RAISE NOTICE 'ok: tenant B nao altera pedido de A';
END $$;

DO $$ BEGIN
  INSERT INTO orders (id, tenant_id, business_day_id, business_date, order_number, order_type, origin, fulfillment_type,
                      customer_name, items_total, weighings_total, total_amount, production_status, created_by_employee_id, created_at, updated_at)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-10-27', 99,
          'MARMITA', 'APP', 'PICKUP', 'x', 1, 0, 1, 'OPEN', 'aaaaaaaa-0000-0000-0000-0000000000e1', now(), now());
  RAISE EXCEPTION 'FALHOU: tenant B gravou com tenant_id de A';
EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'ok: WITH CHECK impede gravar em outro tenant'; END $$;

-- ===== Login: sem tenant ativo, o usuário A enxerga só os próprios vínculos =====
SELECT set_config('app.tenant_id', '', false), set_config('app.user_id', 'aaaaaaaa-0000-0000-0000-0000000000a1', false);
DO $$ BEGIN
  IF (SELECT count(*) FROM tenant_employees) <> 1 THEN RAISE EXCEPTION 'FALHOU: login ve vinculos errados'; END IF;
  IF (SELECT count(*) FROM tenant_employee_roles) <> 1 THEN RAISE EXCEPTION 'FALHOU: login ve perfis errados'; END IF;
  IF (SELECT name FROM tenants) <> 'Marmitaria A' THEN RAISE EXCEPTION 'FALHOU: login ve tenant errado'; END IF;
  IF (SELECT count(*) FROM orders) <> 0 THEN RAISE EXCEPTION 'FALHOU: sem tenant ativo ve pedidos'; END IF;
  RAISE NOTICE 'ok: login lista so os vinculos do proprio usuario';
END $$;

-- ===== Aceite de convite: sem tenant, só pelo código =====
SELECT set_config('app.user_id', 'cccccccc-0000-0000-0000-0000000000c3', false);
DO $$ BEGIN
  IF (SELECT count(*) FROM employee_invitations) <> 0 THEN RAISE EXCEPTION 'FALHOU: convite visivel sem codigo'; END IF;
  PERFORM set_config('app.invitation_code', 'ABC234', false);
  IF (SELECT count(*) FROM employee_invitations) <> 1 THEN RAISE EXCEPTION 'FALHOU: convite nao encontrado pelo codigo'; END IF;
  PERFORM set_config('app.invitation_code', '', false);
  RAISE NOTICE 'ok: convite localizavel so pelo codigo';
END $$;

-- ===== Plataforma enxerga todos os tenants =====
RESET ROLE;
SET ROLE almoseis_platform;
DO $$ BEGIN
  IF (SELECT count(*) FROM tenants) <> 2 THEN RAISE EXCEPTION 'FALHOU: plataforma nao ve todos os tenants'; END IF;
  RAISE NOTICE 'ok: plataforma ve todos os tenants';
END $$;

\echo SMOKE TEST OK
