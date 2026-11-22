\set ON_ERROR_STOP on
SET client_min_messages = notice;
SET ROLE almoseis_app;

-- ===== Tenant A (autocadastro) =====
SELECT set_config('app.tenant_id', 'aaaaaaaa-0000-0000-0000-000000000001', false),
       set_config('app.user_id',   'aaaaaaaa-0000-0000-0000-0000000000a1', false);

INSERT INTO users (id, full_name, email, normalized_email, password_hash, security_stamp, concurrency_stamp, created_at)
VALUES ('aaaaaaaa-0000-0000-0000-0000000000a1', 'Dona A', 'a@x.com', 'A@X.COM', 'h', 's', 'c', now());
INSERT INTO tenants (id, name, document_type, document_number, city, state, terms_version, terms_accepted_at, terms_accepted_by_user_id, created_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'Marmitaria A', 'CNPJ', '11222333000181', 'Uberlandia', 'MG', '2026-10', now(), 'aaaaaaaa-0000-0000-0000-0000000000a1', now());
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
                    accepted_at, fulfillment_status, fulfillment_status_changed_at,
                    created_by_employee_id, created_at, updated_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000000c01', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-10-27', 1,
        'MARMITA', 'APP', 'PICKUP', 'Joao', 25.00, 0, 25.00, 'OPEN', now(), 'PENDING', now(),
        'aaaaaaaa-0000-0000-0000-0000000000e1', now(), now());
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
                    items_total, weighings_total, total_amount, accepted_at, created_by_employee_id, created_at, updated_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000000c02', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-10-27', 2,
        'KILO', 'APP', 'DINE_IN', 0, 27.07, 27.07, now(), 'aaaaaaaa-0000-0000-0000-0000000000e1', now(), now());
INSERT INTO weighing_entries (id, tenant_id, order_id, weight_grams, price_per_kg, amount, status, created_by_employee_id, created_at)
VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c02', 452, 59.90, 27.07, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
-- 250 g x 1,42 = 0,355 -> 0,36 (metade para longe do zero)
INSERT INTO weighing_entries (id, tenant_id, order_id, weight_grams, price_per_kg, amount, status, created_by_employee_id, created_at)
VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c02', 250, 1.42, 0.36, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());

INSERT INTO shared_devices (id, tenant_id, installation_id, name, platform, secret_hash, registered_by_employee_id, registered_at)
VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'inst_tablet', 'Tablet cozinha', 'ANDROID', repeat('a', 64), 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
-- App no navegador (ADR-0053, migration 0002)
INSERT INTO shared_devices (id, tenant_id, installation_id, name, platform, secret_hash, registered_by_employee_id, registered_at)
VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', '3f0c9a6e-1b2d-4c5e-8f7a-9b0c1d2e3f4a', 'Balcao', 'WEB', repeat('b', 64), 'aaaaaaaa-0000-0000-0000-0000000000e1', now());

-- ===== Falhas esperadas (tenant A) =====
DO $$ BEGIN
  INSERT INTO shared_devices (id, tenant_id, installation_id, name, platform, secret_hash, registered_by_employee_id, registered_at)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'inst_desktop', 'PC', 'WINDOWS', repeat('c', 64), 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
  RAISE EXCEPTION 'FALHOU: plataforma de aparelho desconhecida aceita';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: plataforma do aparelho em ANDROID, IOS ou WEB'; END $$;

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

-- ===== Pedido da página: aceite e atendimento (ADR-0046, migration 0003) =====
INSERT INTO orders (id, tenant_id, business_day_id, business_date, order_number, order_type, origin, fulfillment_type,
                    customer_name, items_total, weighings_total, total_amount, acceptance_status, acceptance_deadline,
                    created_by_employee_id, created_at, updated_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000000c03', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-10-27', NULL,
        'MARMITA', 'WEB', 'PICKUP', 'Carla', 20.00, 0, 20.00, 'AWAITING_ACCEPTANCE', now() + interval '10 minutes', NULL, now(), now()),
       ('aaaaaaaa-0000-0000-0000-000000000c04', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-10-27', NULL,
        'MARMITA', 'WEB', 'PICKUP', 'Davi', 20.00, 0, 20.00, 'AWAITING_ACCEPTANCE', now() + interval '10 minutes', NULL, now(), now());
-- Item montado pelo cliente: sem funcionário autor
INSERT INTO order_items (id, tenant_id, order_id, item_kind, marmita_size_id, quantity, unit_price, line_total, name_snapshot,
                         requires_preparation, status, created_by_employee_id, created_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000000103', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c03', 'MARMITA',
        'aaaaaaaa-0000-0000-0000-0000000000f1', 1, 20.00, 20.00, 'Marmita M', true, 'ACTIVE', NULL, now());

DO $$ BEGIN
  IF (SELECT count(*) FROM orders WHERE business_date = '2026-10-27' AND order_number IS NULL) <> 2 THEN
    RAISE EXCEPTION 'FALHOU: pedidos aguardando aceite nao gravados';
  END IF;
  RAISE NOTICE 'ok: pedidos do site aguardando aceite, sem numero e sem autor';
END $$;

DO $$ BEGIN
  UPDATE orders SET order_number = NULL WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c01';
  RAISE EXCEPTION 'FALHOU: pedido aceito sem numero aceito';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: pedido aceito exige numero'; END $$;

DO $$ BEGIN
  UPDATE orders SET order_number = 50, production_status = 'OPEN' WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c03';
  RAISE EXCEPTION 'FALHOU: pedido nao aceito com numero e producao aceito';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: pedido nao aceito sem numero e fora da producao'; END $$;

DO $$ BEGIN
  UPDATE orders SET cancellation_status = 'CANCELED', canceled_at = now(), canceled_by_employee_id = 'aaaaaaaa-0000-0000-0000-0000000000e1',
                    cancellation_reason = 'cliente desistiu' WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c03';
  RAISE EXCEPTION 'FALHOU: pedido nao aceito cancelado';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: pedido nao aceito nao e cancelado'; END $$;

DO $$ BEGIN
  UPDATE orders SET acceptance_status = 'REJECTED', rejected_at = now(), rejection_reason_code = 'OTHER',
                    rejected_by_employee_id = 'aaaaaaaa-0000-0000-0000-0000000000e1' WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c04';
  RAISE EXCEPTION 'FALHOU: recusa OTHER sem mensagem aceita';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: recusa OTHER exige mensagem'; END $$;

DO $$ BEGIN
  UPDATE orders SET acceptance_status = 'REJECTED', rejected_at = now(), rejection_reason_code = 'BUSINESS_DAY_CLOSED',
                    rejected_by_employee_id = 'aaaaaaaa-0000-0000-0000-0000000000e1' WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c04';
  RAISE EXCEPTION 'FALHOU: recusa do fechamento com funcionario aceita';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: recusa do fechamento e do sistema'; END $$;

DO $$ BEGIN
  UPDATE orders SET fulfillment_status = 'OUT_FOR_DELIVERY', out_for_delivery_at = now()
   WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c01';
  RAISE EXCEPTION 'FALHOU: retirada marcada como saiu para entrega';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: saiu para entrega so em entrega'; END $$;

DO $$ BEGIN
  INSERT INTO orders (id, tenant_id, business_day_id, business_date, order_number, order_type, origin, fulfillment_type,
                      items_total, weighings_total, total_amount, accepted_at, created_by_employee_id, created_at, updated_at)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-10-27', 98,
          'KILO', 'APP', 'DINE_IN', 0, 0, 0, now(), NULL, now(), now());
  RAISE EXCEPTION 'FALHOU: pedido do app sem autor aceito';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: pedido do app exige autor'; END $$;

-- Aceite e recusa válidos: número no aceite (a sequência segue a data operacional), recusa com motivo
UPDATE orders SET acceptance_status = 'ACCEPTED', order_number = 3, accepted_at = now(),
                  accepted_by_employee_id = 'aaaaaaaa-0000-0000-0000-0000000000e1', estimated_ready_minutes = 30,
                  production_status = 'OPEN', production_status_changed_at = now(),
                  fulfillment_status = 'PENDING', fulfillment_status_changed_at = now(), version = version + 1
 WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c03';
UPDATE orders SET acceptance_status = 'REJECTED', rejected_at = now(), rejection_reason_code = 'ITEM_UNAVAILABLE',
                  rejection_message = 'A costela acabou', rejected_by_employee_id = 'aaaaaaaa-0000-0000-0000-0000000000e1', version = version + 1
 WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c04';
DO $$ BEGIN
  IF (SELECT count(*) FROM orders WHERE acceptance_status = 'ACCEPTED' AND order_number IS NOT NULL) <> 3 THEN
    RAISE EXCEPTION 'FALHOU: aceite nao gravado';
  END IF;
  RAISE NOTICE 'ok: aceite e recusa coerentes';
END $$;

-- ===== Página de pedidos: perfil, slug, acompanhamento, LGPD (ADR-0044/0045/0047/0052, migration 0004) =====
INSERT INTO storefront_settings (tenant_id, public_name, updated_at) VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'Marmitaria A', now());
INSERT INTO store_slugs (slug, tenant_id, is_current, created_at) VALUES ('marmitaria-a', 'aaaaaaaa-0000-0000-0000-000000000001', true, now());
INSERT INTO order_tracking_tokens (token_hash, tenant_id, order_id, created_at, expires_at)
VALUES (repeat('a', 64), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c04', now(), now() + interval '7 days');
INSERT INTO web_order_intakes (order_id, tenant_id, ip_hash, created_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000000c04', 'aaaaaaaa-0000-0000-0000-000000000001', repeat('b', 64), now());

DO $$ BEGIN
  UPDATE storefront_settings SET is_page_active = true WHERE tenant_id = 'aaaaaaaa-0000-0000-0000-000000000001';
  RAISE EXCEPTION 'FALHOU: pagina ativa sem endereco aceita';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: pagina ativa exige endereco'; END $$;

DO $$ BEGIN
  INSERT INTO store_slugs (slug, tenant_id, is_current, created_at) VALUES ('marmitaria-a-2', 'aaaaaaaa-0000-0000-0000-000000000001', true, now());
  RAISE EXCEPTION 'FALHOU: dois slugs atuais no mesmo tenant';
EXCEPTION WHEN unique_violation THEN RAISE NOTICE 'ok: um slug atual por tenant'; END $$;

DO $$ BEGIN
  INSERT INTO store_slugs (slug, tenant_id, is_current, replaced_at, created_at)
  VALUES ('Marmitaria--A', 'aaaaaaaa-0000-0000-0000-000000000001', false, now(), now());
  RAISE EXCEPTION 'FALHOU: slug fora do formato aceito';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: formato do slug'; END $$;

DO $$ BEGIN
  UPDATE orders SET expected_payment_method = 'PIX', change_for = 50.00 WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c04';
  RAISE EXCEPTION 'FALHOU: troco em Pix aceito';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: troco so em dinheiro'; END $$;

DO $$ BEGIN
  UPDATE orders SET expected_payment_method = 'CASH' WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c01';
  RAISE EXCEPTION 'FALHOU: forma prevista em pedido do app aceita';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: campos da pagina so em pedido WEB'; END $$;

-- Idempotência anônima da página: user_id nulo, escopo (tenant, key)
INSERT INTO idempotency_records (id, tenant_id, key, user_id, endpoint, request_hash, status, created_at, expires_at)
VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', gen_random_uuid(), NULL,
        'POST /api/v1/public/stores/{slug}/orders', repeat('c', 64), 'IN_PROGRESS', now(), now() + interval '72 hours');
DO $$ BEGIN
  IF (SELECT count(*) FROM idempotency_records WHERE user_id IS NULL) <> 1 THEN RAISE EXCEPTION 'FALHOU: idempotencia anonima'; END IF;
  RAISE NOTICE 'ok: idempotencia anonima da pagina';
END $$;

-- ===== Taxa de entrega por distância (ADR-0048, migration 0005) =====
INSERT INTO delivery_quotes (id, tenant_id, address_hash, formatted_address, latitude, longitude, fee_mode, distance_meters, distance_km,
                             price_per_km, minimum_fee, delivery_fee, settings_version, created_at, expires_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000000e01', 'aaaaaaaa-0000-0000-0000-000000000001', repeat('e', 64), 'Rua das Palmeiras, 45 - Centro',
        -18.912300, -48.270100, 'DISTANCE', 4480, 4.5, 0.80, 3.00, 3.60, 1, now(), now() + interval '15 minutes');
INSERT INTO geocode_cache_entries (tenant_id, address_hash, latitude, longitude, geocoded_at, expires_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000000001', repeat('e', 64), -18.912300, -48.270100, now(), now() + interval '30 days');

DO $$ BEGIN
  INSERT INTO delivery_quotes (id, tenant_id, address_hash, formatted_address, fee_mode, delivery_fee, settings_version, created_at, expires_at)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', repeat('f', 64), 'Rua X, 1', 'DISTANCE', 3.00, 1, now(), now() + interval '15 minutes');
  RAISE EXCEPTION 'FALHOU: cotacao por distancia sem distancia aceita';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: cotacao por distancia exige ponto, metros e regra'; END $$;

DO $$ BEGIN
  UPDATE storefront_settings SET location_confirmed_at = now() WHERE tenant_id = 'aaaaaaaa-0000-0000-0000-000000000001';
  RAISE EXCEPTION 'FALHOU: confirmacao sem ponto aceita';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: confirmacao do ponto exige o ponto'; END $$;

DO $$ BEGIN
  UPDATE orders SET delivery_fee_source = 'FIXED_DEFAULT' WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c02';
  RAISE EXCEPTION 'FALHOU: origem da taxa fora de entrega aceita';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: origem da taxa so em entrega'; END $$;

-- ===== Push dos pedidos da página (ADR-0049, migration 0006) =====
INSERT INTO push_registrations (id, tenant_id, installation_id, tenant_employee_id, platform, expo_push_token, created_at, updated_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000000f01', 'aaaaaaaa-0000-0000-0000-000000000001', 'inst_a', 'aaaaaaaa-0000-0000-0000-0000000000e1',
        'ANDROID', 'ExponentPushToken[aaaaaaaaaaaaaaaaaaaaaa]', now(), now());
INSERT INTO push_deliveries (event_id, registration_id, tenant_id, order_id, kind, ticket_id, sent_at)
VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000f01', 'aaaaaaaa-0000-0000-0000-000000000001',
        'aaaaaaaa-0000-0000-0000-000000000c01', 'WEB_ORDER_AWAITING_ACCEPTANCE', 'ticket-a', now());

DO $$ BEGIN
  INSERT INTO push_registrations (id, tenant_id, installation_id, tenant_employee_id, platform, expo_push_token, created_at, updated_at)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'inst_a2', 'aaaaaaaa-0000-0000-0000-0000000000e1', 'IOS', 'token-qualquer', now(), now());
  RAISE EXCEPTION 'FALHOU: token fora do formato do Expo aceito';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: token de push no formato do Expo'; END $$;

DO $$ BEGIN
  INSERT INTO push_registrations (id, tenant_id, installation_id, tenant_employee_id, platform, expo_push_token, created_at, updated_at)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'inst_a2', 'aaaaaaaa-0000-0000-0000-0000000000e1', 'IOS',
          'ExponentPushToken[aaaaaaaaaaaaaaaaaaaaaa]', now(), now());
  RAISE EXCEPTION 'FALHOU: mesmo token em dois registros';
EXCEPTION WHEN unique_violation THEN RAISE NOTICE 'ok: um registro por token de push'; END $$;

DO $$ BEGIN
  INSERT INTO push_deliveries (event_id, registration_id, tenant_id, order_id, kind, sent_at)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000f01', 'aaaaaaaa-0000-0000-0000-000000000001',
          'aaaaaaaa-0000-0000-0000-000000000c01', 'WEB_ORDER_REMINDER', now());
  RAISE EXCEPTION 'FALHOU: entrega de push sem ticket nem erro aceita';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: entrega de push com ticket ou erro'; END $$;

-- ===== Comportamentos e rótulos do estabelecimento (ADR-0054, migration 0008) =====
INSERT INTO tenant_behaviors (tenant_id, behavior, origin, enabled_at, enabled_by_user_id)
VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'RESTAURANT', 'SIGNUP', now(), 'aaaaaaaa-0000-0000-0000-0000000000a1');
INSERT INTO tenant_labels (tenant_id, label, behavior, created_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'MARMITARIA', 'RESTAURANT', now()),
       ('aaaaaaaa-0000-0000-0000-000000000001', 'SELF_SERVICE', 'RESTAURANT', now());

DO $$ BEGIN
  INSERT INTO tenant_behaviors (tenant_id, behavior, origin, enabled_at, enabled_by_user_id)
  VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'BAR', 'SIGNUP', now(), 'aaaaaaaa-0000-0000-0000-0000000000a1');
  RAISE EXCEPTION 'FALHOU: comportamento desconhecido aceito';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: comportamento em RESTAURANT ou SNACK_BAR'; END $$;

DO $$ BEGIN
  INSERT INTO tenant_behaviors (tenant_id, behavior, origin, enabled_at)
  VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'SNACK_BAR', 'SIGNUP', now());
  RAISE EXCEPTION 'FALHOU: comportamento do cadastro sem autor aceito';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: autor do comportamento coerente com a origem'; END $$;

DO $$ BEGIN
  INSERT INTO tenant_labels (tenant_id, label, behavior, created_at)
  VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'BURGER_JOINT', 'RESTAURANT', now());
  RAISE EXCEPTION 'FALHOU: rotulo de lanchonete marcado como restaurante';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: rotulo da lista e do comportamento certo'; END $$;

DO $$ BEGIN
  INSERT INTO tenant_labels (tenant_id, label, behavior, created_at)
  VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'BURGER_JOINT', 'SNACK_BAR', now());
  RAISE EXCEPTION 'FALHOU: rotulo de comportamento nao habilitado aceito';
EXCEPTION WHEN foreign_key_violation THEN RAISE NOTICE 'ok: rotulo so de comportamento habilitado'; END $$;

DO $$ BEGIN
  BEGIN
    DELETE FROM tenant_behaviors WHERE tenant_id = 'aaaaaaaa-0000-0000-0000-000000000001';
    RAISE EXCEPTION 'FALHOU: aplicacao apagou comportamento';
  EXCEPTION WHEN insufficient_privilege THEN NULL; END;
  BEGIN
    UPDATE tenant_behaviors SET behavior = 'SNACK_BAR' WHERE tenant_id = 'aaaaaaaa-0000-0000-0000-000000000001';
    RAISE EXCEPTION 'FALHOU: aplicacao trocou comportamento';
  EXCEPTION WHEN insufficient_privilege THEN NULL; END;
  RAISE NOTICE 'ok: comportamento nao e alterado nem apagado pela aplicacao';
END $$;

DO $$ BEGIN
  DELETE FROM tenant_labels WHERE tenant_id = 'aaaaaaaa-0000-0000-0000-000000000001' AND label = 'SELF_SERVICE';
  IF NOT FOUND THEN RAISE EXCEPTION 'FALHOU: rotulo nao removido'; END IF;
  IF (SELECT count(*) FROM tenant_labels) <> 1 THEN RAISE EXCEPTION 'FALHOU: rotulos de A errados'; END IF;
  RAISE NOTICE 'ok: rotulo removivel na edicao do estabelecimento';
END $$;

-- ===== Tenant B =====
SELECT set_config('app.tenant_id', 'bbbbbbbb-0000-0000-0000-000000000002', false),
       set_config('app.user_id',   'bbbbbbbb-0000-0000-0000-0000000000b2', false);
INSERT INTO users (id, full_name, phone_number, password_hash, security_stamp, concurrency_stamp, created_at)
VALUES ('bbbbbbbb-0000-0000-0000-0000000000b2', 'Seu B', '+5534999990000', 'h', 's', 'c', now());
INSERT INTO tenants (id, name, document_type, document_number, city, state, terms_version, terms_accepted_at, terms_accepted_by_user_id, created_at)
VALUES ('bbbbbbbb-0000-0000-0000-000000000002', 'Restaurante B', 'CPF', '12345678901', 'Uberlandia', 'MG', '2026-10', now(), 'bbbbbbbb-0000-0000-0000-0000000000b2', now());
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
                      customer_name, items_total, weighings_total, total_amount, production_status,
                      accepted_at, fulfillment_status, fulfillment_status_changed_at, created_by_employee_id, created_at, updated_at)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-10-27', 99,
          'MARMITA', 'APP', 'PICKUP', 'x', 1, 0, 1, 'OPEN', now(), 'PENDING', now(), 'aaaaaaaa-0000-0000-0000-0000000000e1', now(), now());
  RAISE EXCEPTION 'FALHOU: tenant B gravou com tenant_id de A';
EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'ok: WITH CHECK impede gravar em outro tenant'; END $$;

DO $$ BEGIN
  IF (SELECT count(*) FROM delivery_quotes) <> 0 THEN RAISE EXCEPTION 'FALHOU: tenant B ve cotacao de A'; END IF;
  IF (SELECT count(*) FROM geocode_cache_entries) <> 0 THEN RAISE EXCEPTION 'FALHOU: tenant B ve o cache de A'; END IF;
  RAISE NOTICE 'ok: cotacao e cache de geocodificacao isolados';
END $$;

-- Push: isolado. O mesmo token não mostra o registro de A; só a mesma instalação (o aparelho que trocou de
-- estabelecimento) o libera, pela função da 0007, que não devolve a linha
DO $$ BEGIN
  IF (SELECT count(*) FROM push_registrations) <> 0 THEN RAISE EXCEPTION 'FALHOU: tenant B ve registro de push de A'; END IF;
  IF (SELECT count(*) FROM push_deliveries) <> 0 THEN RAISE EXCEPTION 'FALHOU: tenant B ve entregas de push de A'; END IF;
  DELETE FROM push_registrations WHERE installation_id = 'inst_a';
  IF FOUND THEN RAISE EXCEPTION 'FALHOU: tenant B apagou registro de push de A'; END IF;
  RAISE NOTICE 'ok: registro e entregas de push isolados';
END $$;

DO $$ BEGIN
  IF (SELECT count(*) FROM push_registrations WHERE expo_push_token = 'ExponentPushToken[aaaaaaaaaaaaaaaaaaaaaa]') <> 0 THEN
    RAISE EXCEPTION 'FALHOU: tenant B ve o registro de A pelo token';
  END IF;
  IF release_push_token('ExponentPushToken[aaaaaaaaaaaaaaaaaaaaaa]', 'inst_b') <> -1 THEN
    RAISE EXCEPTION 'FALHOU: outra instalacao liberou o token de A';
  END IF;
  IF release_push_token('ExponentPushToken[aaaaaaaaaaaaaaaaaaaaaa]', 'inst_a') <> 1 THEN
    RAISE EXCEPTION 'FALHOU: a mesma instalacao nao liberou o token';
  END IF;
  RAISE NOTICE 'ok: so a mesma instalacao libera o token de push de outro tenant';
END $$;

-- Página: o slug tem leitura aberta (a resolução vem antes do tenant); o token só pela função de resolução da 0007,
-- que devolve o tenant e o prazo; perfil e hash do IP ficam isolados
DO $$ BEGIN
  IF (SELECT count(*) FROM store_slugs WHERE slug = 'marmitaria-a') <> 1 THEN RAISE EXCEPTION 'FALHOU: slug de A nao resolvido'; END IF;
  IF (SELECT count(*) FROM order_tracking_tokens) <> 0 THEN RAISE EXCEPTION 'FALHOU: tenant B le tokens de A'; END IF;
  IF (SELECT tenant_id FROM resolve_tracking_token(repeat('a', 64))) IS DISTINCT FROM 'aaaaaaaa-0000-0000-0000-000000000001' THEN
    RAISE EXCEPTION 'FALHOU: token nao resolvido';
  END IF;
  IF EXISTS (SELECT 1 FROM resolve_tracking_token(repeat('9', 64))) THEN RAISE EXCEPTION 'FALHOU: token inexistente resolvido'; END IF;
  IF (SELECT count(*) FROM storefront_settings) <> 0 THEN RAISE EXCEPTION 'FALHOU: tenant B ve o perfil de A'; END IF;
  IF (SELECT count(*) FROM web_order_intakes) <> 0 THEN RAISE EXCEPTION 'FALHOU: tenant B ve o IP de A'; END IF;
  RAISE NOTICE 'ok: slug e token resolviveis, token so por hash; perfil e IP isolados';
END $$;

DO $$ BEGIN
  INSERT INTO store_slugs (slug, tenant_id, is_current, replaced_at, created_at)
  VALUES ('invasao-a', 'aaaaaaaa-0000-0000-0000-000000000001', false, now(), now());
  RAISE EXCEPTION 'FALHOU: tenant B gravou slug de A';
EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'ok: tenant B nao grava slug de A'; END $$;

DO $$ BEGIN
  UPDATE store_slugs SET is_current = false, replaced_at = now() WHERE slug = 'marmitaria-a';
  IF FOUND THEN RAISE EXCEPTION 'FALHOU: tenant B alterou slug de A'; END IF;
  RAISE NOTICE 'ok: tenant B nao altera slug de A';
END $$;

DO $$ BEGIN
  INSERT INTO order_tracking_tokens (token_hash, tenant_id, order_id, created_at, expires_at)
  VALUES (repeat('d', 64), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c03', now(), now() + interval '7 days');
  RAISE EXCEPTION 'FALHOU: tenant B gravou token de A';
EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'ok: tenant B nao grava token de A'; END $$;

DO $$ BEGIN
  IF (SELECT count(*) FROM tenant_behaviors) <> 0 THEN RAISE EXCEPTION 'FALHOU: tenant B ve comportamentos de A'; END IF;
  IF (SELECT count(*) FROM tenant_labels) <> 0 THEN RAISE EXCEPTION 'FALHOU: tenant B ve rotulos de A'; END IF;
  DELETE FROM tenant_labels WHERE tenant_id = 'aaaaaaaa-0000-0000-0000-000000000001';
  IF FOUND THEN RAISE EXCEPTION 'FALHOU: tenant B apagou rotulo de A'; END IF;
  RAISE NOTICE 'ok: comportamentos e rotulos isolados';
END $$;

DO $$ BEGIN
  INSERT INTO tenant_labels (tenant_id, label, behavior, created_at)
  VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'RESTAURANT', 'RESTAURANT', now());
  RAISE EXCEPTION 'FALHOU: tenant B gravou rotulo de A';
EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'ok: tenant B nao grava rotulo de A'; END $$;

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
  -- Conta só os tenants deste script: o banco pode ter outros (testes de integração em paralelo).
  IF (SELECT count(*) FROM tenants
      WHERE id IN ('aaaaaaaa-0000-0000-0000-000000000001', 'bbbbbbbb-0000-0000-0000-000000000002')) <> 2 THEN
    RAISE EXCEPTION 'FALHOU: plataforma nao ve todos os tenants';
  END IF;
  RAISE NOTICE 'ok: plataforma ve todos os tenants';
END $$;

-- Acréscimo de comportamento pelo admin (ADR-0054 item 5): só a plataforma grava em outro tenant
DO $$ BEGIN
  INSERT INTO tenant_behaviors (tenant_id, behavior, origin, enabled_at, enabled_by_user_id)
  VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'SNACK_BAR', 'PLATFORM_ADMIN', now(), 'bbbbbbbb-0000-0000-0000-0000000000b2');
  IF (SELECT count(*) FROM tenant_behaviors WHERE tenant_id = 'aaaaaaaa-0000-0000-0000-000000000001') <> 2 THEN
    RAISE EXCEPTION 'FALHOU: plataforma nao acrescentou comportamento';
  END IF;
  RAISE NOTICE 'ok: plataforma acrescenta comportamento';
END $$;

\echo SMOKE TEST OK
