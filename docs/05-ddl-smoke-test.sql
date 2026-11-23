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
  -- Desde a 0011 a forma prevista e o troco valem também no app (ADR-0059); o risco do anti-robô continua só no WEB
  UPDATE orders SET risk_flag = 'CAPTCHA_UNAVAILABLE' WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c01';
  RAISE EXCEPTION 'FALHOU: risco do anti-robo em pedido do app aceito';
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

-- ===== Cardápio fixo e preço de site (ADR-0056, ADR-0057, migration 0009) =====
INSERT INTO dish_categories (id, tenant_id, name, created_at, updated_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000009001', 'aaaaaaaa-0000-0000-0000-000000000001', 'Lanches', now(), now());
INSERT INTO dishes (id, tenant_id, dish_category_id, name, price, sells_on_site, site_price, created_at, updated_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000009011', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000009001',
        'X-Burguer', 22.00, true, 23.90, now(), now());
INSERT INTO dishes (id, tenant_id, dish_category_id, name, has_sizes, created_at, updated_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000009012', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000009001',
        'Batata frita', true, now(), now());
INSERT INTO dish_sizes (id, tenant_id, dish_id, name, price)
VALUES ('aaaaaaaa-0000-0000-0000-000000009021', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000009012', 'P', 10.00);
INSERT INTO beverage_types (id, tenant_id, name, created_at, updated_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000009031', 'aaaaaaaa-0000-0000-0000-000000000001', 'Refrigerante', now(), now());
INSERT INTO beverage_sizes (id, tenant_id, beverage_type_id, name, price, sells_on_site, site_price)
VALUES ('aaaaaaaa-0000-0000-0000-000000009032', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000009031',
        'Lata 350 ml', 6.00, true, 6.50);
INSERT INTO beverage_flavors (id, tenant_id, beverage_size_id, name, extra_price)
VALUES ('aaaaaaaa-0000-0000-0000-000000009033', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000009032',
        'Coca-Cola Zero', 0.50);
INSERT INTO combos (id, tenant_id, name, price, created_at, updated_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000009041', 'aaaaaaaa-0000-0000-0000-000000000001', 'X-Burguer + batata + refri', 32.00, now(), now());
INSERT INTO combo_components (id, tenant_id, combo_id, kind, dish_id, dish_size_id, beverage_size_id, quantity)
VALUES ('aaaaaaaa-0000-0000-0000-000000009051', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000009041',
        'DISH', 'aaaaaaaa-0000-0000-0000-000000009011', NULL, NULL, 1),
       ('aaaaaaaa-0000-0000-0000-000000009052', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000009041',
        'DISH', 'aaaaaaaa-0000-0000-0000-000000009012', 'aaaaaaaa-0000-0000-0000-000000009021', NULL, 1),
       ('aaaaaaaa-0000-0000-0000-000000009053', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000009041',
        'BEVERAGE', NULL, NULL, 'aaaaaaaa-0000-0000-0000-000000009032', 1);
INSERT INTO addon_groups (id, tenant_id, name, created_at, updated_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000009061', 'aaaaaaaa-0000-0000-0000-000000000001', 'Adicionais de lanche', now(), now());
INSERT INTO addon_options (id, tenant_id, addon_group_id, name, price)
VALUES ('aaaaaaaa-0000-0000-0000-000000009062', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000009061', 'Bacon', 4.00);
INSERT INTO addon_group_links (id, tenant_id, addon_group_id, dish_category_id)
VALUES ('aaaaaaaa-0000-0000-0000-000000009063', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000009061',
        'aaaaaaaa-0000-0000-0000-000000009001');

DO $$ BEGIN
  INSERT INTO dishes (id, tenant_id, dish_category_id, name, has_sizes, price, created_at, updated_at)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000009001', 'Acai', true, 15.00, now(), now());
  RAISE EXCEPTION 'FALHOU: prato com tamanho e preco unico';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: prato com tamanho sem preco unico'; END $$;

DO $$ BEGIN
  INSERT INTO dishes (id, tenant_id, dish_category_id, name, created_at, updated_at)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000009001', 'X-Salada', now(), now());
  RAISE EXCEPTION 'FALHOU: prato sem tamanho e sem preco';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: prato sem tamanho exige preco'; END $$;

DO $$ BEGIN
  BEGIN
    UPDATE dishes SET site_price = NULL WHERE id = 'aaaaaaaa-0000-0000-0000-000000009011';
    RAISE EXCEPTION 'FALHOU: prato vende no site sem preco de site';
  EXCEPTION WHEN check_violation THEN NULL; END;
  BEGIN
    UPDATE dish_sizes SET sells_on_site = true WHERE id = 'aaaaaaaa-0000-0000-0000-000000009021';
    RAISE EXCEPTION 'FALHOU: tamanho vende no site sem preco de site';
  EXCEPTION WHEN check_violation THEN NULL; END;
  BEGIN
    UPDATE beverage_sizes SET site_price = NULL WHERE id = 'aaaaaaaa-0000-0000-0000-000000009032';
    RAISE EXCEPTION 'FALHOU: bebida vende no site sem preco de site';
  EXCEPTION WHEN check_violation THEN NULL; END;
  BEGIN
    UPDATE combos SET sells_on_site = true WHERE id = 'aaaaaaaa-0000-0000-0000-000000009041';
    RAISE EXCEPTION 'FALHOU: combo vende no site sem preco de site';
  EXCEPTION WHEN check_violation THEN NULL; END;
  BEGIN
    UPDATE marmita_sizes SET sells_on_site = true WHERE id = 'aaaaaaaa-0000-0000-0000-0000000000f1';
    RAISE EXCEPTION 'FALHOU: marmita vende no site sem preco de site';
  EXCEPTION WHEN check_violation THEN NULL; END;
  BEGIN
    UPDATE combos SET sells_on_site = true, site_price = 0 WHERE id = 'aaaaaaaa-0000-0000-0000-000000009041';
    RAISE EXCEPTION 'FALHOU: preco de site zero aceito';
  EXCEPTION WHEN check_violation THEN NULL; END;
  RAISE NOTICE 'ok: vende no site exige preco de site maior que zero';
END $$;

DO $$ BEGIN
  INSERT INTO combo_components (id, tenant_id, combo_id, kind, dish_id, dish_size_id, quantity)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000009041',
          'DISH', 'aaaaaaaa-0000-0000-0000-000000009011', 'aaaaaaaa-0000-0000-0000-000000009021', 1);
  RAISE EXCEPTION 'FALHOU: componente com tamanho de outro prato';
EXCEPTION WHEN foreign_key_violation THEN RAISE NOTICE 'ok: tamanho do componente e do proprio prato'; END $$;

DO $$ BEGIN
  BEGIN
    INSERT INTO combo_components (id, tenant_id, combo_id, kind, dish_id, beverage_size_id, quantity)
    VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000009041',
            'DISH', 'aaaaaaaa-0000-0000-0000-000000009011', 'aaaaaaaa-0000-0000-0000-000000009032', 1);
    RAISE EXCEPTION 'FALHOU: componente prato e bebida ao mesmo tempo';
  EXCEPTION WHEN check_violation THEN NULL; END;
  BEGIN
    INSERT INTO combo_components (id, tenant_id, combo_id, kind, quantity)
    VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000009041', 'BEVERAGE', 1);
    RAISE EXCEPTION 'FALHOU: componente de bebida sem tamanho';
  EXCEPTION WHEN check_violation THEN NULL; END;
  BEGIN
    UPDATE combo_components SET quantity = 11 WHERE id = 'aaaaaaaa-0000-0000-0000-000000009051';
    RAISE EXCEPTION 'FALHOU: componente com quantidade 11';
  EXCEPTION WHEN check_violation THEN NULL; END;
  RAISE NOTICE 'ok: componente e um prato ou um tamanho de bebida, de 1 a 10';
END $$;

DO $$ BEGIN
  UPDATE beverage_flavors SET extra_price = -0.50 WHERE id = 'aaaaaaaa-0000-0000-0000-000000009033';
  RAISE EXCEPTION 'FALHOU: acrescimo de sabor negativo';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: acrescimo do sabor nunca negativo'; END $$;

DO $$ BEGIN
  BEGIN
    INSERT INTO addon_group_links (id, tenant_id, addon_group_id, dish_category_id, dish_id)
    VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000009061',
            'aaaaaaaa-0000-0000-0000-000000009001', 'aaaaaaaa-0000-0000-0000-000000009011');
    RAISE EXCEPTION 'FALHOU: vinculo com categoria e prato';
  EXCEPTION WHEN check_violation THEN NULL; END;
  BEGIN
    INSERT INTO addon_group_links (id, tenant_id, addon_group_id, dish_category_id)
    VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000009061',
            'aaaaaaaa-0000-0000-0000-000000009001');
    RAISE EXCEPTION 'FALHOU: vinculo repetido';
  EXCEPTION WHEN unique_violation THEN NULL; END;
  RAISE NOTICE 'ok: grupo ligado a uma categoria ou a um prato, sem repetir';
END $$;

DO $$ BEGIN
  INSERT INTO products (id, tenant_id, kind, name, default_price, requires_preparation, site_price, created_at, updated_at)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'ADDON', 'Ovo', 2.00, true, 2.50, now(), now());
  RAISE EXCEPTION 'FALHOU: adicional com preco de site';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: preco de site de produto so em bebida'; END $$;

DO $$ BEGIN
  UPDATE orders SET price_list = 'SITE' WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c01';
  RAISE EXCEPTION 'FALHOU: pedido do app na tabela de site';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: pedido do app usa a tabela de balcao'; END $$;

DO $$ BEGIN
  UPDATE orders SET price_list = 'SITE' WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c03';
  IF (SELECT price_list FROM orders WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c03') <> 'SITE' THEN
    RAISE EXCEPTION 'FALHOU: pedido do site sem a tabela de site';
  END IF;
  IF (SELECT price_list FROM orders WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c02') <> 'COUNTER' THEN
    RAISE EXCEPTION 'FALHOU: pedido do app nao nasce na tabela de balcao';
  END IF;
  RAISE NOTICE 'ok: pedido do site na tabela de site; do app, balcao por padrao';
END $$;

DO $$ BEGIN
  BEGIN
    DELETE FROM dishes WHERE id = 'aaaaaaaa-0000-0000-0000-000000009011';
    RAISE EXCEPTION 'FALHOU: aplicacao apagou prato';
  EXCEPTION WHEN insufficient_privilege THEN NULL; END;
  BEGIN
    DELETE FROM dish_sizes WHERE id = 'aaaaaaaa-0000-0000-0000-000000009021';
    RAISE EXCEPTION 'FALHOU: aplicacao apagou tamanho';
  EXCEPTION WHEN insufficient_privilege THEN NULL; END;
  BEGIN
    DELETE FROM beverage_flavors WHERE id = 'aaaaaaaa-0000-0000-0000-000000009033';
    RAISE EXCEPTION 'FALHOU: aplicacao apagou sabor';
  EXCEPTION WHEN insufficient_privilege THEN NULL; END;
  BEGIN
    DELETE FROM combos WHERE id = 'aaaaaaaa-0000-0000-0000-000000009041';
    RAISE EXCEPTION 'FALHOU: aplicacao apagou combo';
  EXCEPTION WHEN insufficient_privilege THEN NULL; END;
  BEGIN
    DELETE FROM addon_options WHERE id = 'aaaaaaaa-0000-0000-0000-000000009062';
    RAISE EXCEPTION 'FALHOU: aplicacao apagou adicional';
  EXCEPTION WHEN insufficient_privilege THEN NULL; END;
  RAISE NOTICE 'ok: itens do cardapio fixo sao inativados, nunca apagados';
END $$;

DO $$ BEGIN
  DELETE FROM combo_components WHERE id = 'aaaaaaaa-0000-0000-0000-000000009053';
  IF NOT FOUND THEN RAISE EXCEPTION 'FALHOU: componente nao removido'; END IF;
  DELETE FROM addon_group_links WHERE id = 'aaaaaaaa-0000-0000-0000-000000009063';
  IF NOT FOUND THEN RAISE EXCEPTION 'FALHOU: vinculo nao removido'; END IF;
  INSERT INTO combo_components (id, tenant_id, combo_id, kind, beverage_size_id, quantity)
  VALUES ('aaaaaaaa-0000-0000-0000-000000009053', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000009041',
          'BEVERAGE', 'aaaaaaaa-0000-0000-0000-000000009032', 1);
  INSERT INTO addon_group_links (id, tenant_id, addon_group_id, dish_category_id)
  VALUES ('aaaaaaaa-0000-0000-0000-000000009063', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000009061',
          'aaaaaaaa-0000-0000-0000-000000009001');
  RAISE NOTICE 'ok: componente de combo e vinculo de grupo trocados na edicao';
END $$;

-- ===== Pedido à la carte, combo em três níveis e fechamento (ADR-0058, migration 0010) =====
INSERT INTO products (id, tenant_id, kind, name, default_price, requires_preparation, created_at, updated_at)
VALUES ('aaaaaaaa-0000-0000-0000-0000000000b5', 'aaaaaaaa-0000-0000-0000-000000000001', 'ADDON', 'Ovo', 2.00, true, now(), now());
INSERT INTO beverage_sizes (id, tenant_id, beverage_type_id, name, price)
VALUES ('aaaaaaaa-0000-0000-0000-000000009034', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000009031', '2 L', 14.00);

-- Comanda 47: 2 combos com bacon e Coca Zero (R$ 73,00, ADR-0058 item 9) + X-Burguer com bacon + lata Coca Zero = R$ 105,50
INSERT INTO orders (id, tenant_id, business_day_id, business_date, order_number, order_type, origin, fulfillment_type,
                    tab_number, items_total, weighings_total, total_amount, production_status, accepted_at,
                    created_by_employee_id, created_at, updated_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000000c10', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-10-27', 10,
        'A_LA_CARTE', 'APP', 'DINE_IN', '47', 105.50, 0, 105.50, 'OPEN', now(), 'aaaaaaaa-0000-0000-0000-0000000000e1', now(), now());
-- Primeiro nível
INSERT INTO order_items (id, tenant_id, order_id, item_kind, combo_id, dish_id, beverage_size_id, beverage_flavor_id,
                         quantity, unit_price, line_total, name_snapshot, size_name_snapshot, flavor_name_snapshot,
                         requires_preparation, status, created_by_employee_id, created_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000009101', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10', 'COMBO',
        'aaaaaaaa-0000-0000-0000-000000009041', NULL, NULL, NULL, 2, 32.00, 64.00, 'X-Burguer + batata + refri', NULL, NULL,
        false, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now()),
       ('aaaaaaaa-0000-0000-0000-000000009106', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10', 'FIXED_DISH',
        NULL, 'aaaaaaaa-0000-0000-0000-000000009011', NULL, NULL, 1, 22.00, 22.00, 'X-Burguer', NULL, NULL,
        true, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now()),
       ('aaaaaaaa-0000-0000-0000-000000009108', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10', 'FIXED_BEVERAGE',
        NULL, NULL, 'aaaaaaaa-0000-0000-0000-000000009032', 'aaaaaaaa-0000-0000-0000-000000009033', 1, 6.50, 6.50,
        'Refrigerante', 'Lata 350 ml', 'Coca-Cola Zero', false, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
-- Partes do combo (segundo nível)
INSERT INTO order_items (id, tenant_id, order_id, parent_order_item_id, parent_item_kind, item_kind, dish_id, dish_size_id,
                         beverage_size_id, beverage_flavor_id, quantity, unit_price, line_total, name_snapshot, size_name_snapshot,
                         flavor_name_snapshot, requires_preparation, status, created_by_employee_id, created_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000009102', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10',
        'aaaaaaaa-0000-0000-0000-000000009101', 'COMBO', 'COMBO_PART', 'aaaaaaaa-0000-0000-0000-000000009011', NULL, NULL, NULL,
        1, 0, 0, 'X-Burguer', NULL, NULL, true, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now()),
       ('aaaaaaaa-0000-0000-0000-000000009104', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10',
        'aaaaaaaa-0000-0000-0000-000000009101', 'COMBO', 'COMBO_PART', 'aaaaaaaa-0000-0000-0000-000000009012',
        'aaaaaaaa-0000-0000-0000-000000009021', NULL, NULL, 1, 0, 0, 'Batata frita', 'P', NULL, true, 'ACTIVE',
        'aaaaaaaa-0000-0000-0000-0000000000e1', now()),
       ('aaaaaaaa-0000-0000-0000-000000009105', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10',
        'aaaaaaaa-0000-0000-0000-000000009101', 'COMBO', 'COMBO_PART', NULL, NULL, 'aaaaaaaa-0000-0000-0000-000000009032',
        'aaaaaaaa-0000-0000-0000-000000009033', 1, 0.50, 1.00, 'Refrigerante', 'Lata 350 ml', 'Coca-Cola Zero', false, 'ACTIVE',
        'aaaaaaaa-0000-0000-0000-0000000000e1', now());
-- Adicionais de grupo: sob a parte de prato do combo (terceiro nível) e sob o prato avulso
INSERT INTO order_items (id, tenant_id, order_id, parent_order_item_id, parent_item_kind, parent_dish_id, item_kind, addon_option_id,
                         quantity, unit_price, line_total, name_snapshot, requires_preparation, status, created_by_employee_id, created_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000009103', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10',
        'aaaaaaaa-0000-0000-0000-000000009102', 'COMBO_PART', 'aaaaaaaa-0000-0000-0000-000000009011', 'GROUP_ADDON',
        'aaaaaaaa-0000-0000-0000-000000009062', 1, 4.00, 8.00, 'Bacon', true, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now()),
       ('aaaaaaaa-0000-0000-0000-000000009107', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10',
        'aaaaaaaa-0000-0000-0000-000000009106', 'FIXED_DISH', 'aaaaaaaa-0000-0000-0000-000000009011', 'GROUP_ADDON',
        'aaaaaaaa-0000-0000-0000-000000009062', 1, 4.00, 4.00, 'Bacon', true, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());

DO $$ BEGIN
  INSERT INTO orders (id, tenant_id, business_day_id, business_date, order_number, order_type, origin, fulfillment_type,
                      customer_name, customer_phone, delivery_address, items_total, weighings_total, total_amount, production_status,
                      accepted_at, fulfillment_status, fulfillment_status_changed_at, created_by_employee_id, created_at, updated_at)
  VALUES ('aaaaaaaa-0000-0000-0000-000000000c11', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-10-27', 11,
          'A_LA_CARTE', 'APP', 'PICKUP', 'Ana', '+5534999991111', NULL, 0, 0, 0, 'OPEN', now(), 'PENDING', now(),
          'aaaaaaaa-0000-0000-0000-0000000000e1', now(), now()),
         ('aaaaaaaa-0000-0000-0000-000000000c12', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-10-27', 12,
          'A_LA_CARTE', 'APP', 'DELIVERY', 'Bia', '+5534999992222', '{"street": "Rua A", "number": "10"}', 0, 0, 0, 'OPEN', now(),
          'PENDING', now(), 'aaaaaaaa-0000-0000-0000-0000000000e1', now(), now());
  IF (SELECT string_agg(fulfillment_type, ',' ORDER BY fulfillment_type) FROM orders WHERE order_type = 'A_LA_CARTE')
     <> 'DELIVERY,DINE_IN,PICKUP' THEN
    RAISE EXCEPTION 'FALHOU: a la carte sem os tres atendimentos';
  END IF;
  BEGIN
    UPDATE orders SET fulfillment_type = 'DINE_IN', fulfillment_status = NULL, fulfillment_status_changed_at = NULL,
                      order_type = 'MARMITA', customer_name = 'Ana'
     WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c11';
    RAISE EXCEPTION 'FALHOU: marmita no salao';
  EXCEPTION WHEN check_violation THEN NULL; END;
  RAISE NOTICE 'ok: a la carte no salao, na entrega e na retirada; as outras modalidades como antes';
END $$;

DO $$ BEGIN
  IF (WITH RECURSIVE tree AS (
        SELECT id, line_total FROM order_items WHERE id = 'aaaaaaaa-0000-0000-0000-000000009101'
        UNION ALL
        SELECT i.id, i.line_total FROM order_items i JOIN tree t ON i.parent_order_item_id = t.id)
      SELECT sum(line_total) FROM tree) <> 73.00 THEN
    RAISE EXCEPTION 'FALHOU: combo em tres niveis nao soma 73.00';
  END IF;
  IF (SELECT sum(line_total) FROM order_items WHERE order_id = 'aaaaaaaa-0000-0000-0000-000000000c10' AND status = 'ACTIVE')
     <> (SELECT items_total FROM orders WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c10') THEN
    RAISE EXCEPTION 'FALHOU: total da comanda';
  END IF;
  -- Cozinha: linhas com preparo, sem os adicionais (que aparecem dentro do pai)
  IF (SELECT string_agg(name_snapshot, ',' ORDER BY name_snapshot) FROM order_items
       WHERE order_id = 'aaaaaaaa-0000-0000-0000-000000000c10' AND requires_preparation AND item_kind <> 'GROUP_ADDON')
     <> 'Batata frita,X-Burguer,X-Burguer' THEN
    RAISE EXCEPTION 'FALHOU: cozinha nao ve as partes e o prato avulso';
  END IF;
  RAISE NOTICE 'ok: combo em tres niveis soma 73.00; a cozinha ve 3 itens, sem o combo e sem bebidas';
END $$;

DO $$ BEGIN
  BEGIN
    INSERT INTO order_items (id, tenant_id, order_id, parent_order_item_id, parent_item_kind, item_kind, dish_id,
                             quantity, unit_price, line_total, name_snapshot, requires_preparation, status, created_by_employee_id, created_at)
    VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10',
            'aaaaaaaa-0000-0000-0000-000000009106', 'FIXED_DISH', 'COMBO_PART', 'aaaaaaaa-0000-0000-0000-000000009011',
            1, 0, 0, 'X', true, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
    RAISE EXCEPTION 'FALHOU: parte de combo sob prato avulso';
  EXCEPTION WHEN check_violation THEN NULL; END;
  BEGIN
    INSERT INTO order_items (id, tenant_id, order_id, parent_order_item_id, parent_item_kind, item_kind, dish_id,
                             quantity, unit_price, line_total, name_snapshot, requires_preparation, status, created_by_employee_id, created_at)
    VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10',
            'aaaaaaaa-0000-0000-0000-000000009106', 'COMBO', 'COMBO_PART', 'aaaaaaaa-0000-0000-0000-000000009011',
            1, 0, 0, 'X', true, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
    RAISE EXCEPTION 'FALHOU: parte de combo com tipo do pai falso';
  EXCEPTION WHEN foreign_key_violation THEN NULL; END;
  BEGIN
    INSERT INTO order_items (id, tenant_id, order_id, item_kind, dish_id,
                             quantity, unit_price, line_total, name_snapshot, requires_preparation, status, created_by_employee_id, created_at)
    VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10', 'COMBO_PART',
            'aaaaaaaa-0000-0000-0000-000000009011', 1, 0, 0, 'X', true, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
    RAISE EXCEPTION 'FALHOU: parte de combo sem pai';
  EXCEPTION WHEN check_violation THEN NULL; END;
  RAISE NOTICE 'ok: parte de combo so sob o combo';
END $$;

DO $$ BEGIN
  BEGIN
    INSERT INTO order_items (id, tenant_id, order_id, parent_order_item_id, parent_item_kind, parent_dish_id, item_kind, addon_option_id,
                             quantity, unit_price, line_total, name_snapshot, requires_preparation, status, created_by_employee_id, created_at)
    VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10',
            'aaaaaaaa-0000-0000-0000-000000009101', 'COMBO', 'aaaaaaaa-0000-0000-0000-000000009011', 'GROUP_ADDON',
            'aaaaaaaa-0000-0000-0000-000000009062', 1, 4.00, 8.00, 'Bacon', true, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
    RAISE EXCEPTION 'FALHOU: adicional de grupo sob o combo';
  EXCEPTION WHEN check_violation THEN NULL; END;
  BEGIN
    INSERT INTO order_items (id, tenant_id, order_id, parent_order_item_id, parent_item_kind, parent_dish_id, item_kind, addon_option_id,
                             quantity, unit_price, line_total, name_snapshot, requires_preparation, status, created_by_employee_id, created_at)
    VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10',
            'aaaaaaaa-0000-0000-0000-000000009105', 'COMBO_PART', 'aaaaaaaa-0000-0000-0000-000000009011', 'GROUP_ADDON',
            'aaaaaaaa-0000-0000-0000-000000009062', 1, 4.00, 8.00, 'Bacon', true, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
    RAISE EXCEPTION 'FALHOU: adicional de grupo sob a parte de bebida';
  EXCEPTION WHEN foreign_key_violation THEN NULL; END;
  BEGIN
    INSERT INTO order_items (id, tenant_id, order_id, parent_order_item_id, parent_item_kind, item_kind, addon_option_id,
                             quantity, unit_price, line_total, name_snapshot, requires_preparation, status, created_by_employee_id, created_at)
    VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10',
            'aaaaaaaa-0000-0000-0000-000000009106', 'FIXED_DISH', 'GROUP_ADDON',
            'aaaaaaaa-0000-0000-0000-000000009062', 1, 4.00, 4.00, 'Bacon', true, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
    RAISE EXCEPTION 'FALHOU: adicional de grupo sem o prato do pai';
  EXCEPTION WHEN check_violation THEN NULL; END;
  BEGIN
    INSERT INTO order_items (id, tenant_id, order_id, item_kind, addon_option_id,
                             quantity, unit_price, line_total, name_snapshot, requires_preparation, status, created_by_employee_id, created_at)
    VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10', 'GROUP_ADDON',
            'aaaaaaaa-0000-0000-0000-000000009062', 1, 4.00, 4.00, 'Bacon', true, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
    RAISE EXCEPTION 'FALHOU: adicional de grupo sem pai';
  EXCEPTION WHEN check_violation THEN NULL; END;
  RAISE NOTICE 'ok: adicional de grupo so sob prato avulso ou parte de prato do combo';
END $$;

DO $$ BEGIN
  BEGIN
    INSERT INTO order_items (id, tenant_id, order_id, parent_order_item_id, parent_item_kind, item_kind, product_id,
                             quantity, unit_price, line_total, name_snapshot, requires_preparation, status, created_by_employee_id, created_at)
    VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10',
            'aaaaaaaa-0000-0000-0000-000000009106', 'FIXED_DISH', 'ADDON', 'aaaaaaaa-0000-0000-0000-0000000000b5',
            1, 2.00, 2.00, 'Ovo', true, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
    RAISE EXCEPTION 'FALHOU: adicional do restaurante sob prato do cardapio fixo';
  EXCEPTION WHEN check_violation THEN NULL; END;
  -- API anterior (janela do deploy): adicional sem o tipo do pai; API nova: com o tipo
  INSERT INTO order_items (id, tenant_id, order_id, parent_order_item_id, item_kind, product_id,
                           quantity, unit_price, line_total, name_snapshot, requires_preparation, status, created_by_employee_id, created_at)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c01',
          'aaaaaaaa-0000-0000-0000-000000000101', 'ADDON', 'aaaaaaaa-0000-0000-0000-0000000000b5',
          1, 2.00, 2.00, 'Ovo', true, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
  INSERT INTO order_items (id, tenant_id, order_id, parent_order_item_id, parent_item_kind, item_kind, product_id,
                           quantity, unit_price, line_total, name_snapshot, requires_preparation, status, created_by_employee_id, created_at)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c01',
          'aaaaaaaa-0000-0000-0000-000000000101', 'MARMITA', 'ADDON', 'aaaaaaaa-0000-0000-0000-0000000000b5',
          1, 2.00, 2.00, 'Ovo', true, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
  RAISE NOTICE 'ok: adicional do restaurante so sob marmita ou PF, com ou sem o tipo do pai';
END $$;

DO $$ BEGIN
  BEGIN
    INSERT INTO order_items (id, tenant_id, order_id, item_kind,
                             quantity, unit_price, line_total, name_snapshot, requires_preparation, status, created_by_employee_id, created_at)
    VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10', 'FIXED_DISH',
            1, 22.00, 22.00, 'X', true, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
    RAISE EXCEPTION 'FALHOU: prato avulso sem prato';
  EXCEPTION WHEN check_violation THEN NULL; END;
  BEGIN
    INSERT INTO order_items (id, tenant_id, order_id, item_kind, dish_id, product_id,
                             quantity, unit_price, line_total, name_snapshot, requires_preparation, status, created_by_employee_id, created_at)
    VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10', 'FIXED_DISH',
            'aaaaaaaa-0000-0000-0000-000000009011', 'aaaaaaaa-0000-0000-0000-0000000000b1',
            1, 22.00, 22.00, 'X', true, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
    RAISE EXCEPTION 'FALHOU: prato avulso com produto do restaurante';
  EXCEPTION WHEN check_violation THEN NULL; END;
  BEGIN
    INSERT INTO order_items (id, tenant_id, order_id, parent_order_item_id, parent_item_kind, item_kind, dish_id, beverage_size_id,
                             quantity, unit_price, line_total, name_snapshot, size_name_snapshot, requires_preparation, status,
                             created_by_employee_id, created_at)
    VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10',
            'aaaaaaaa-0000-0000-0000-000000009101', 'COMBO', 'COMBO_PART', 'aaaaaaaa-0000-0000-0000-000000009011',
            'aaaaaaaa-0000-0000-0000-000000009032', 1, 0, 0, 'X', 'Lata 350 ml', false, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
    RAISE EXCEPTION 'FALHOU: parte de combo prato e bebida ao mesmo tempo';
  EXCEPTION WHEN check_violation THEN NULL; END;
  BEGIN
    INSERT INTO order_items (id, tenant_id, order_id, item_kind, beverage_size_id, beverage_flavor_id,
                             quantity, unit_price, line_total, name_snapshot, size_name_snapshot, flavor_name_snapshot,
                             requires_preparation, status, created_by_employee_id, created_at)
    VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10', 'FIXED_BEVERAGE',
            'aaaaaaaa-0000-0000-0000-000000009034', 'aaaaaaaa-0000-0000-0000-000000009033',
            1, 14.50, 14.50, 'Refrigerante', '2 L', 'Coca-Cola Zero', false, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
    RAISE EXCEPTION 'FALHOU: sabor de outro tamanho';
  EXCEPTION WHEN foreign_key_violation THEN NULL; END;
  BEGIN
    INSERT INTO order_items (id, tenant_id, order_id, item_kind, dish_id, dish_size_id,
                             quantity, unit_price, line_total, name_snapshot, size_name_snapshot,
                             requires_preparation, status, created_by_employee_id, created_at)
    VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10', 'FIXED_DISH',
            'aaaaaaaa-0000-0000-0000-000000009011', 'aaaaaaaa-0000-0000-0000-000000009021',
            1, 22.00, 22.00, 'X-Burguer', 'P', true, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
    RAISE EXCEPTION 'FALHOU: tamanho de outro prato';
  EXCEPTION WHEN foreign_key_violation THEN NULL; END;
  RAISE NOTICE 'ok: item do cardapio fixo com as referencias do tipo, tamanho do prato e sabor do tamanho';
END $$;

DO $$ BEGIN
  BEGIN
    INSERT INTO order_items (id, tenant_id, order_id, item_kind, beverage_size_id,
                             quantity, unit_price, line_total, name_snapshot, requires_preparation, status, created_by_employee_id, created_at)
    VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10', 'FIXED_BEVERAGE',
            'aaaaaaaa-0000-0000-0000-000000009034', 1, 14.00, 14.00, 'Refrigerante', false, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
    RAISE EXCEPTION 'FALHOU: bebida sem o tamanho no snapshot';
  EXCEPTION WHEN check_violation THEN NULL; END;
  BEGIN
    INSERT INTO order_items (id, tenant_id, order_id, item_kind, beverage_size_id, beverage_flavor_id,
                             quantity, unit_price, line_total, name_snapshot, size_name_snapshot, requires_preparation, status,
                             created_by_employee_id, created_at)
    VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c10', 'FIXED_BEVERAGE',
            'aaaaaaaa-0000-0000-0000-000000009032', 'aaaaaaaa-0000-0000-0000-000000009033',
            1, 6.50, 6.50, 'Refrigerante', 'Lata 350 ml', false, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
    RAISE EXCEPTION 'FALHOU: bebida sem o sabor no snapshot';
  EXCEPTION WHEN check_violation THEN NULL; END;
  BEGIN
    UPDATE order_items SET size_name_snapshot = 'Grande' WHERE id = 'aaaaaaaa-0000-0000-0000-000000009106';
    RAISE EXCEPTION 'FALHOU: tamanho no snapshot de prato sem tamanho';
  EXCEPTION WHEN check_violation THEN NULL; END;
  RAISE NOTICE 'ok: snapshot do tamanho e do sabor exatamente quando ha tamanho e sabor';
END $$;

DO $$ BEGIN
  BEGIN
    UPDATE order_items SET requires_preparation = true WHERE id = 'aaaaaaaa-0000-0000-0000-000000009101';
    RAISE EXCEPTION 'FALHOU: combo na cozinha';
  EXCEPTION WHEN check_violation THEN NULL; END;
  BEGIN
    UPDATE order_items SET requires_preparation = true WHERE id = 'aaaaaaaa-0000-0000-0000-000000009108';
    RAISE EXCEPTION 'FALHOU: bebida avulsa na cozinha';
  EXCEPTION WHEN check_violation THEN NULL; END;
  BEGIN
    UPDATE order_items SET requires_preparation = true WHERE id = 'aaaaaaaa-0000-0000-0000-000000009105';
    RAISE EXCEPTION 'FALHOU: parte de bebida na cozinha';
  EXCEPTION WHEN check_violation THEN NULL; END;
  BEGIN
    UPDATE order_items SET unit_price = 1.00, line_total = 2.00 WHERE id = 'aaaaaaaa-0000-0000-0000-000000009102';
    RAISE EXCEPTION 'FALHOU: parte de prato do combo com preco';
  EXCEPTION WHEN check_violation THEN NULL; END;
  RAISE NOTICE 'ok: combo e bebidas fora da cozinha; parte do combo a 0.00, salvo o acrescimo do sabor';
END $$;

DO $$ BEGIN
  INSERT INTO daily_closing_lines (id, tenant_id, daily_closing_id, dimension, key, sold, received, pending, reversed, count)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000c9',
          'ORDER_TYPE', 'A_LA_CARTE', 0, 0, 0, 0, 0);
  IF (SELECT site_sold_total FROM daily_closings WHERE id = 'aaaaaaaa-0000-0000-0000-0000000000c9') <> 0 THEN
    RAISE EXCEPTION 'FALHOU: vendido pelo site sem o padrao 0';
  END IF;
  BEGIN
    INSERT INTO daily_closing_lines (id, tenant_id, daily_closing_id, dimension, key, sold, received, pending, reversed, count)
    VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000c9',
            'ORDER_TYPE', 'COMBO', 0, 0, 0, 0, 0);
    RAISE EXCEPTION 'FALHOU: linha de fechamento com modalidade desconhecida';
  EXCEPTION WHEN check_violation THEN NULL; END;
  BEGIN
    INSERT INTO daily_closings (id, tenant_id, business_day_id, business_date, order_count, total_sold, delivery_fees_total, total_received,
      received_from_previous_days, total_pending, canceled_count, canceled_total, reversed_count, reversed_total, opening_balance,
      cash_received, cash_reversed, supplies, withdrawals, expected_cash, counted_cash, cash_difference, site_sold_total,
      closed_by_employee_id, closed_at)
    VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-10-27', 1, 20.00, 0, 0,
      0, 20.00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 25.00, 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
    RAISE EXCEPTION 'FALHOU: vendido pelo site maior que o vendido';
  EXCEPTION WHEN check_violation THEN NULL; END;
  RAISE NOTICE 'ok: fechamento com a linha A_LA_CARTE e o vendido pelo site dentro do vendido';
END $$;

-- ===== Delivery por telefone e cliente pelo telefone (ADR-0059, migration 0011) =====
-- Entrega com "troco para R$ 50,00" e retirada com Pix prevista, os dois do app; nada fica pago (ADR-0059 item 8)
DO $$ BEGIN
  UPDATE orders SET expected_payment_method = 'CASH', change_for = 50.00 WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c12';
  UPDATE orders SET expected_payment_method = 'PIX' WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c11';
  IF (SELECT count(*) FROM orders WHERE origin = 'APP' AND expected_payment_method IS NOT NULL) <> 2 THEN
    RAISE EXCEPTION 'FALHOU: forma prevista no pedido do app';
  END IF;
  IF EXISTS (SELECT 1 FROM orders WHERE id IN ('aaaaaaaa-0000-0000-0000-000000000c11', 'aaaaaaaa-0000-0000-0000-000000000c12')
               AND (paid_amount <> 0 OR is_paid)) THEN
    RAISE EXCEPTION 'FALHOU: forma prevista marcou pagamento';
  END IF;
  RAISE NOTICE 'ok: forma prevista e troco no pedido do app, sem pagamento';
END $$;

DO $$ BEGIN
  BEGIN
    UPDATE orders SET expected_payment_method = NULL WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c12';
    RAISE EXCEPTION 'FALHOU: troco sem forma prevista aceito';
  EXCEPTION WHEN check_violation THEN NULL; END;
  BEGIN
    UPDATE orders SET expected_payment_method = 'CARD' WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c12';
    RAISE EXCEPTION 'FALHOU: troco em cartao aceito no app';
  EXCEPTION WHEN check_violation THEN NULL; END;
  RAISE NOTICE 'ok: troco so com a forma dinheiro, inclusive com a forma nula';
END $$;

DO $$ BEGIN
  UPDATE orders SET expected_payment_method = 'CASH' WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c10';
  RAISE EXCEPTION 'FALHOU: forma prevista na comanda do salao';
EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: forma prevista so em entrega e retirada'; END $$;

-- Cliente pelo telefone: o primeiro pedido cria; o segundo atendente (outro nome) não cria outro nem muda o nome (Q-314)
INSERT INTO customers (id, tenant_id, name, phone_number, created_at)
VALUES ('aaaaaaaa-0000-0000-0000-000000011001', 'aaaaaaaa-0000-0000-0000-000000000001', 'Bia', '+5534999992222', now())
ON CONFLICT (tenant_id, phone_number) WHERE phone_number IS NOT NULL DO NOTHING;
UPDATE orders SET customer_id = 'aaaaaaaa-0000-0000-0000-000000011001', version = version + 1
 WHERE id = 'aaaaaaaa-0000-0000-0000-000000000c12';
DO $$
DECLARE
  created uuid;
BEGIN
  INSERT INTO customers (id, tenant_id, name, phone_number, created_at)
  VALUES ('aaaaaaaa-0000-0000-0000-000000011002', 'aaaaaaaa-0000-0000-0000-000000000001', 'Beatriz', '+5534999992222', now())
  ON CONFLICT (tenant_id, phone_number) WHERE phone_number IS NOT NULL DO NOTHING
  RETURNING id INTO created;
  IF created IS NOT NULL THEN RAISE EXCEPTION 'FALHOU: segundo cliente com o mesmo telefone'; END IF;
  IF (SELECT string_agg(name, ',') FROM customers WHERE phone_number = '+5534999992222') <> 'Bia' THEN
    RAISE EXCEPTION 'FALHOU: cadastro do telefone mudou ou duplicou';
  END IF;
  IF (SELECT delivery_address->>'street' FROM orders
       WHERE tenant_id = 'aaaaaaaa-0000-0000-0000-000000000001' AND customer_id = 'aaaaaaaa-0000-0000-0000-000000011001'
         AND delivery_address IS NOT NULL
       ORDER BY created_at DESC LIMIT 1) IS DISTINCT FROM 'Rua A' THEN
    RAISE EXCEPTION 'FALHOU: endereco do ultimo pedido do cliente';
  END IF;
  RAISE NOTICE 'ok: cliente criado uma vez pelo telefone, sem mudar o nome; endereco do ultimo pedido';
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

DO $$ BEGIN
  IF (SELECT count(*) FROM dish_categories) + (SELECT count(*) FROM dishes) + (SELECT count(*) FROM dish_sizes)
     + (SELECT count(*) FROM beverage_types) + (SELECT count(*) FROM beverage_sizes) + (SELECT count(*) FROM beverage_flavors)
     + (SELECT count(*) FROM combos) + (SELECT count(*) FROM combo_components)
     + (SELECT count(*) FROM addon_groups) + (SELECT count(*) FROM addon_options) + (SELECT count(*) FROM addon_group_links) <> 0 THEN
    RAISE EXCEPTION 'FALHOU: tenant B ve o cardapio fixo de A';
  END IF;
  UPDATE dishes SET is_paused = true WHERE id = 'aaaaaaaa-0000-0000-0000-000000009011';
  IF FOUND THEN RAISE EXCEPTION 'FALHOU: tenant B pausou prato de A'; END IF;
  DELETE FROM combo_components WHERE tenant_id = 'aaaaaaaa-0000-0000-0000-000000000001';
  IF FOUND THEN RAISE EXCEPTION 'FALHOU: tenant B apagou componente de A'; END IF;
  RAISE NOTICE 'ok: cardapio fixo isolado';
END $$;

DO $$ BEGIN
  INSERT INTO dish_categories (id, tenant_id, name, created_at, updated_at)
  VALUES (gen_random_uuid(), 'aaaaaaaa-0000-0000-0000-000000000001', 'Invasao', now(), now());
  RAISE EXCEPTION 'FALHOU: tenant B gravou categoria de A';
EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'ok: tenant B nao grava cardapio fixo de A'; END $$;

INSERT INTO combos (id, tenant_id, name, price, created_at, updated_at)
VALUES ('bbbbbbbb-0000-0000-0000-000000009041', 'bbbbbbbb-0000-0000-0000-000000000002', 'Combo B', 30.00, now(), now());
DO $$ BEGIN
  INSERT INTO combo_components (id, tenant_id, combo_id, kind, dish_id, quantity)
  VALUES (gen_random_uuid(), 'bbbbbbbb-0000-0000-0000-000000000002', 'bbbbbbbb-0000-0000-0000-000000009041',
          'DISH', 'aaaaaaaa-0000-0000-0000-000000009011', 1);
  RAISE EXCEPTION 'FALHOU: combo de B usa prato de A';
EXCEPTION WHEN foreign_key_violation THEN RAISE NOTICE 'ok: combo de B nao usa prato de A'; END $$;

-- Comanda à la carte: B não vê os itens de A nem usa o cardápio fixo ou os itens de A
INSERT INTO business_days (id, tenant_id, business_date, status, opening_balance, opened_by_employee_id, opened_at)
VALUES ('bbbbbbbb-0000-0000-0000-0000000000d2', 'bbbbbbbb-0000-0000-0000-000000000002', '2026-10-27', 'OPEN', 0, 'bbbbbbbb-0000-0000-0000-0000000000e2', now());
INSERT INTO orders (id, tenant_id, business_day_id, business_date, order_number, order_type, origin, fulfillment_type,
                    tab_number, items_total, weighings_total, total_amount, production_status, accepted_at,
                    created_by_employee_id, created_at, updated_at)
VALUES ('bbbbbbbb-0000-0000-0000-000000000c10', 'bbbbbbbb-0000-0000-0000-000000000002', 'bbbbbbbb-0000-0000-0000-0000000000d2', '2026-10-27', 1,
        'A_LA_CARTE', 'APP', 'DINE_IN', '5', 30.00, 0, 30.00, 'OPEN', now(), 'bbbbbbbb-0000-0000-0000-0000000000e2', now(), now());
DO $$ BEGIN
  IF (SELECT count(*) FROM order_items) <> 0 THEN RAISE EXCEPTION 'FALHOU: tenant B ve itens de A'; END IF;
  BEGIN
    INSERT INTO order_items (id, tenant_id, order_id, item_kind, dish_id,
                             quantity, unit_price, line_total, name_snapshot, requires_preparation, status, created_by_employee_id, created_at)
    VALUES (gen_random_uuid(), 'bbbbbbbb-0000-0000-0000-000000000002', 'bbbbbbbb-0000-0000-0000-000000000c10', 'FIXED_DISH',
            'aaaaaaaa-0000-0000-0000-000000009011', 1, 22.00, 22.00, 'X-Burguer', true, 'ACTIVE', 'bbbbbbbb-0000-0000-0000-0000000000e2', now());
    RAISE EXCEPTION 'FALHOU: item de B com prato de A';
  EXCEPTION WHEN foreign_key_violation THEN NULL; END;
  INSERT INTO order_items (id, tenant_id, order_id, item_kind, combo_id,
                           quantity, unit_price, line_total, name_snapshot, requires_preparation, status, created_by_employee_id, created_at)
  VALUES ('bbbbbbbb-0000-0000-0000-000000009101', 'bbbbbbbb-0000-0000-0000-000000000002', 'bbbbbbbb-0000-0000-0000-000000000c10', 'COMBO',
          'bbbbbbbb-0000-0000-0000-000000009041', 1, 30.00, 30.00, 'Combo B', false, 'ACTIVE', 'bbbbbbbb-0000-0000-0000-0000000000e2', now());
  BEGIN
    INSERT INTO order_items (id, tenant_id, order_id, parent_order_item_id, parent_item_kind, item_kind, beverage_size_id,
                             quantity, unit_price, line_total, name_snapshot, size_name_snapshot, requires_preparation, status,
                             created_by_employee_id, created_at)
    VALUES (gen_random_uuid(), 'bbbbbbbb-0000-0000-0000-000000000002', 'bbbbbbbb-0000-0000-0000-000000000c10',
            'bbbbbbbb-0000-0000-0000-000000009101', 'COMBO', 'COMBO_PART', 'aaaaaaaa-0000-0000-0000-000000009032',
            1, 0, 0, 'Refrigerante', 'Lata 350 ml', false, 'ACTIVE', 'bbbbbbbb-0000-0000-0000-0000000000e2', now());
    RAISE EXCEPTION 'FALHOU: parte do combo de B com bebida de A';
  EXCEPTION WHEN foreign_key_violation THEN NULL; END;
  BEGIN
    INSERT INTO order_items (id, tenant_id, order_id, parent_order_item_id, parent_item_kind, parent_dish_id, item_kind, addon_option_id,
                             quantity, unit_price, line_total, name_snapshot, requires_preparation, status, created_by_employee_id, created_at)
    VALUES (gen_random_uuid(), 'bbbbbbbb-0000-0000-0000-000000000002', 'bbbbbbbb-0000-0000-0000-000000000c10',
            'aaaaaaaa-0000-0000-0000-000000009106', 'FIXED_DISH', 'aaaaaaaa-0000-0000-0000-000000009011', 'GROUP_ADDON',
            'aaaaaaaa-0000-0000-0000-000000009062', 1, 4.00, 4.00, 'Bacon', true, 'ACTIVE', 'bbbbbbbb-0000-0000-0000-0000000000e2', now());
    RAISE EXCEPTION 'FALHOU: adicional de B sob item de A';
  EXCEPTION WHEN foreign_key_violation THEN NULL; END;
  RAISE NOTICE 'ok: comanda a la carte isolada; B nao usa cardapio fixo nem itens de A';
END $$;

-- Cliente pelo telefone: único por tenant, não entre tenants (ADR-0059, migration 0011)
DO $$
DECLARE
  created uuid;
BEGIN
  IF (SELECT count(*) FROM customers) <> 0 THEN RAISE EXCEPTION 'FALHOU: tenant B ve clientes de A'; END IF;
  INSERT INTO customers (id, tenant_id, name, phone_number, created_at)
  VALUES ('bbbbbbbb-0000-0000-0000-000000011001', 'bbbbbbbb-0000-0000-0000-000000000002', 'Outra Bia', '+5534999992222', now())
  ON CONFLICT (tenant_id, phone_number) WHERE phone_number IS NOT NULL DO NOTHING
  RETURNING id INTO created;
  IF created IS NULL THEN RAISE EXCEPTION 'FALHOU: telefone de A bloqueou o cliente de B'; END IF;
  RAISE NOTICE 'ok: telefone unico por tenant; cliente de A invisivel para B';
END $$;

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
