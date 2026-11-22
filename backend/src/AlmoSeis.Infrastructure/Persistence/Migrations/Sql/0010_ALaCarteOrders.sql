-- GERADO por backend/tools/Sync-DdlMigration.ps1 a partir de docs/05-ddl.md. NÃO EDITE À MÃO.
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
