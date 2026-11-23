-- GERADO por backend/tools/Sync-DdlMigration.ps1 a partir de docs/05-ddl.md. NÃO EDITE À MÃO.
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
