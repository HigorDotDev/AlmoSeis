using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.Modules.Ordering.Application;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Ordering.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using Shouldly;

namespace AlmoSeis.UnitTests.Modules;

/// Pedido à la carte (EP-003 fatia 3, ADR-0058): o resolvedor do cardápio fixo (`FixedMenuItems`) e o agregado `Order`
/// com os três níveis COMBO → COMBO_PART → GROUP_ADDON. Exemplos numéricos do ADR-0058 item 9 e do ADR-0056 itens 12 e 14.
public sealed class ALaCarteOrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid By = Guid.NewGuid();

    // Cardápio: X-Burguer 22,00 (site 23,90) com o grupo "Adicionais de lanche" (Bacon 4,00); Batata frita P 12,00;
    // Açaí 300 ml 14,00 / 500 ml 18,00 com "Complementos" (Granola 2,00); Refrigerante Lata 350 ml 6,00 com Coca-Cola e
    // Coca-Cola Zero (+0,50); Suco 500 ml 9,00 com Laranja com acerola (+1,00); combo "X-Burguer + batata + refri" 32,00
    // (site 34,00) = X-Burguer + Batata P + Lata.
    private static readonly Guid Lanches = Guid.NewGuid(), Porcoes = Guid.NewGuid(), AcaiCategory = Guid.NewGuid();
    private static readonly Guid XBurguer = Guid.NewGuid(), Batata = Guid.NewGuid(), BatataP = Guid.NewGuid(), Acai = Guid.NewGuid();
    private static readonly Guid Acai300 = Guid.NewGuid(), Acai500 = Guid.NewGuid();
    private static readonly Guid Bacon = Guid.NewGuid(), Granola = Guid.NewGuid();
    private static readonly Guid Lata = Guid.NewGuid(), Coca = Guid.NewGuid(), CocaZero = Guid.NewGuid(), Suco = Guid.NewGuid(), Laranja = Guid.NewGuid();
    private static readonly Guid Combo = Guid.NewGuid(), BurgerPart = Guid.NewGuid(), BatataPart = Guid.NewGuid(), LataPart = Guid.NewGuid();

    private static Money M(decimal v) => Money.Of(v);

    private static FixedMenuSnapshot Menu(bool batataPaused = false, bool cocaZeroPaused = false, bool acai500Paused = false)
    {
        var dishes = new Dictionary<Guid, FixedMenuDish>
        {
            [XBurguer] = new(XBurguer, Lanches, "X-Burguer", true, false, new FixedMenuPrice(M(22m), true, M(23.90m)), true, new Dictionary<Guid, FixedMenuDishSize>(), [Bacon]),
            [Batata] = new(Batata, Porcoes, "Batata frita", true, true, null, !batataPaused,
                new Dictionary<Guid, FixedMenuDishSize> { [BatataP] = new(BatataP, "P", new FixedMenuPrice(M(12m), false, null), !batataPaused) }, []),
            [Acai] = new(Acai, AcaiCategory, "Açaí", true, true, null, true,
                new Dictionary<Guid, FixedMenuDishSize>
                {
                    [Acai300] = new(Acai300, "300 ml", new FixedMenuPrice(M(14m), false, null), true),
                    [Acai500] = new(Acai500, "500 ml", new FixedMenuPrice(M(18m), false, null), !acai500Paused),
                },
                [Granola]),
        };
        var lataFlavors = new Dictionary<Guid, FixedMenuBeverageFlavor>
        {
            [Coca] = new(Coca, "Coca-Cola", M(0m), true),
            [CocaZero] = new(CocaZero, "Coca-Cola Zero", M(0.50m), !cocaZeroPaused),
        };
        var beverages = new Dictionary<Guid, FixedMenuBeverageSize>
        {
            [Lata] = new(Lata, Guid.NewGuid(), "Refrigerante", "Lata 350 ml", new FixedMenuPrice(M(6m), true, M(6m)), true, lataFlavors),
            [Suco] = new(Suco, Guid.NewGuid(), "Suco", "500 ml", new FixedMenuPrice(M(9m), false, null), true,
                new Dictionary<Guid, FixedMenuBeverageFlavor> { [Laranja] = new(Laranja, "Laranja com acerola", M(1m), true) }),
        };
        var combos = new Dictionary<Guid, FixedMenuCombo>
        {
            [Combo] = new(Combo, "X-Burguer + batata + refri", new FixedMenuPrice(M(32m), true, M(34m)), !batataPaused,
            [
                new(BurgerPart, ComboComponentKind.Dish, XBurguer, null, null, 1, "X-Burguer", true),
                new(BatataPart, ComboComponentKind.Dish, Batata, BatataP, null, 1, "Batata frita P", !batataPaused),
                new(LataPart, ComboComponentKind.Beverage, null, null, Lata, 1, "Refrigerante Lata 350 ml", true),
            ]),
        };
        var options = new Dictionary<Guid, FixedMenuAddonOption>
        {
            [Bacon] = new(Bacon, Guid.NewGuid(), "Bacon", M(4m), true),
            [Granola] = new(Granola, Guid.NewGuid(), "Granola", M(2m), true),
        };
        return new FixedMenuSnapshot(dishes, beverages, combos, options);
    }

    private static OrderItemInput ComboInput(int quantity, Guid flavor, bool bacon = true) => new(
        OrderItemKind.Combo, null, null, null, quantity, null, null, null, null,
        ComboId: Combo,
        Parts:
        [
            new ComboPartInput(BurgerPart, null, "Sem cebola", bacon ? [new GroupAddonInput(Bacon, 1)] : null),
            new ComboPartInput(LataPart, flavor, null, null),
        ]);

    private static OrderItemInput DishInput(Guid dish, Guid? size, int quantity = 1, params GroupAddonInput[] addons) =>
        new(OrderItemKind.FixedDish, null, null, null, quantity, null, null, null, null, DishId: dish, DishSizeId: size, GroupAddons: addons);

    private static OrderItemInput BeverageInput(Guid size, Guid? flavor, int quantity = 1) =>
        new(OrderItemKind.FixedBeverage, null, null, null, quantity, null, null, null, null, BeverageSizeId: size, BeverageFlavorId: flavor);

    private static IReadOnlyList<NewItem> Resolve(FixedMenuSnapshot menu, PriceList priceList, params OrderItemInput[] inputs) =>
        new FixedMenuItems(menu, new ItemResolution(new DateOnly(2026, 11, 22), priceList, OrderType.ALaCarte)).Resolve(inputs);

    private static Order Create(IReadOnlyList<NewItem> items, FulfillmentType fulfillment = FulfillmentType.DineIn, OrderType type = OrderType.ALaCarte) =>
        Order.Create(
            Guid.NewGuid(), Guid.NewGuid(),
            new NewOrder(type, fulfillment, null, fulfillment == FulfillmentType.DineIn ? null : "Carla", null,
                fulfillment == FulfillmentType.Delivery ? new DeliveryAddressDto("Rua A", "1", null, null, null) : null,
                fulfillment == FulfillmentType.Delivery ? M(5m) : Money.Zero, fulfillment == FulfillmentType.DineIn ? "5" : null, null, null, items, []),
            Guid.NewGuid(), new DateOnly(2026, 11, 22), 1, By, Guid.NewGuid, Now);

    /// ADR-0058 item 9: 2 combos com bacon e Coca Zero = R$ 73,00.
    [Fact]
    public void Two_combos_with_bacon_and_coke_zero_cost_73()
    {
        var order = Create(Resolve(Menu(), PriceList.Counter, ComboInput(2, CocaZero)));

        var combo = order.Items.Single(i => i.ItemKind == OrderItemKind.Combo);
        var parts = order.Items.Where(i => i.ItemKind == OrderItemKind.ComboPart).ToList();
        var bacon = order.Items.Single(i => i.ItemKind == OrderItemKind.GroupAddon);
        var burger = parts.Single(p => p.DishId == XBurguer);
        var lata = parts.Single(p => p.BeverageSizeId == Lata);

        combo.LineTotal.ShouldBe(M(64m));
        combo.RequiresPreparation.ShouldBeFalse();
        combo.ComboId.ShouldBe(Combo);
        parts.Count.ShouldBe(3);
        parts.ShouldAllBe(p => p.ParentOrderItemId == combo.Id && p.ParentItemKind == OrderItemKind.Combo);
        burger.LineTotal.ShouldBe(M(0m));
        burger.Notes.ShouldBe("Sem cebola");
        burger.RequiresPreparation.ShouldBeTrue();
        bacon.UnitPrice.ShouldBe(M(4m));
        bacon.LineTotal.ShouldBe(M(8m)); // 4,00 × 1 × 1 × 2
        bacon.ParentOrderItemId.ShouldBe(burger.Id);
        bacon.ParentItemKind.ShouldBe(OrderItemKind.ComboPart);
        bacon.ParentDishId.ShouldBe(XBurguer);
        bacon.RequiresPreparation.ShouldBeTrue();
        parts.Single(p => p.DishId == Batata).DisplayName.ShouldBe("Batata frita P");
        parts.Single(p => p.DishId == Batata).SizeNameSnapshot.ShouldBe("P");
        lata.UnitPrice.ShouldBe(M(0.50m));
        lata.LineTotal.ShouldBe(M(1m));
        lata.RequiresPreparation.ShouldBeFalse();
        lata.NameSnapshot.ShouldBe("Refrigerante");
        lata.DisplayName.ShouldBe("Refrigerante Lata 350 ml · Coca-Cola Zero");
        order.ItemsTotal.ShouldBe(M(73m));
        order.TotalAmount.ShouldBe(M(73m));
        order.ProductionStatus.ShouldBe(ProductionStatus.Open);
        order.Label.ShouldBe("Mesa 5");
    }

    /// ADR-0058 item 10: mudar a quantidade do COMBO reprecifica as partes e os adicionais (o neto usa o avô).
    [Fact]
    public void Changing_the_combo_quantity_reprices_parts_and_addons()
    {
        var order = Create(Resolve(Menu(), PriceList.Counter, ComboInput(2, CocaZero)));
        var combo = order.Items.Single(i => i.ItemKind == OrderItemKind.Combo);

        order.ChangeItem(combo.Id, new Optional<int>(3), default, By, Guid.NewGuid, Now);

        order.Items.Single(i => i.ItemKind == OrderItemKind.GroupAddon).LineTotal.ShouldBe(M(12m));
        order.Items.Single(i => i.BeverageSizeId == Lata).LineTotal.ShouldBe(M(1.50m));
        order.TotalAmount.ShouldBe(M(109.50m)); // 96,00 + 12,00 + 1,50
        order.ChangeEvents.Last().AffectsProduction.ShouldBeTrue("as partes do combo vão à cozinha");
    }

    [Fact]
    public void A_part_is_not_removed_or_resized_alone_and_removing_the_combo_removes_the_tree()
    {
        var order = Create(Resolve(Menu(), PriceList.Counter, ComboInput(1, Coca), DishInput(Acai, Acai500)));
        var combo = order.Items.Single(i => i.ItemKind == OrderItemKind.Combo);
        var part = order.Items.First(i => i.ItemKind == OrderItemKind.ComboPart);

        Should.Throw<BusinessException>(() => order.RemoveItem(part.Id, By, Guid.NewGuid, Now)).ErrorCode.ShouldBe(ErrorCodes.ValidationFailed);
        Should.Throw<BusinessException>(() => order.ChangeItem(part.Id, new Optional<int>(2), default, By, Guid.NewGuid, Now))
            .ErrorCode.ShouldBe(ErrorCodes.ValidationFailed);
        Should.Throw<BusinessException>(() => order.ChangeItem(combo.Id, default, new Optional<string?>("Capricha"), By, Guid.NewGuid, Now))
            .ErrorCode.ShouldBe(ErrorCodes.ValidationFailed);
        order.ChangeItem(part.Id, default, new Optional<string?>("Bem passado"), By, Guid.NewGuid, Now);
        part.Notes.ShouldBe("Bem passado");

        order.RemoveItem(combo.Id, By, Guid.NewGuid, Now);

        order.Items.Where(i => i.ItemKind is OrderItemKind.Combo or OrderItemKind.ComboPart or OrderItemKind.GroupAddon)
            .ShouldAllBe(i => i.Status == LineStatus.Removed);
        order.TotalAmount.ShouldBe(M(18m));
        order.ChangeEvents.Last().AffectsProduction.ShouldBeTrue();
    }

    /// Prato com tamanho e adicionais; bebida com sabor (Suco 500 ml 9,00 + 1,00 = 10,00, ADR-0056 item 12).
    [Fact]
    public void Dish_with_size_and_addons_and_beverage_with_flavor()
    {
        var order = Create(Resolve(Menu(), PriceList.Counter,
            DishInput(Acai, Acai500, 2, new GroupAddonInput(Granola, 2)),
            BeverageInput(Suco, Laranja, 2)));

        var acai = order.Items.Single(i => i.ItemKind == OrderItemKind.FixedDish);
        acai.UnitPrice.ShouldBe(M(18m));
        acai.LineTotal.ShouldBe(M(36m));
        acai.DishSizeId.ShouldBe(Acai500);
        acai.DisplayName.ShouldBe("Açaí 500 ml");
        var granola = order.Items.Single(i => i.ItemKind == OrderItemKind.GroupAddon);
        granola.LineTotal.ShouldBe(M(8m)); // 2,00 × 2 × 2
        granola.ParentItemKind.ShouldBe(OrderItemKind.FixedDish);
        granola.ParentDishId.ShouldBe(Acai);
        var suco = order.Items.Single(i => i.ItemKind == OrderItemKind.FixedBeverage);
        suco.UnitPrice.ShouldBe(M(10m));
        suco.LineTotal.ShouldBe(M(20m));
        suco.RequiresPreparation.ShouldBeFalse();
        suco.FlavorNameSnapshot.ShouldBe("Laranja com acerola");
        order.TotalAmount.ShouldBe(M(64m));
    }

    [Fact]
    public void Size_and_flavor_are_required_and_must_belong()
    {
        Validation(() => Resolve(Menu(), PriceList.Counter, DishInput(Acai, null))).ShouldContainKey("items[0].dishSizeId");
        Validation(() => Resolve(Menu(), PriceList.Counter, DishInput(XBurguer, Acai500))).ShouldContainKey("items[0].dishSizeId");
        Validation(() => Resolve(Menu(), PriceList.Counter, BeverageInput(Lata, null))).ShouldContainKey("items[0].beverageFlavorId");
        Unavailable(() => Resolve(Menu(), PriceList.Counter, ComboInput(1, Guid.NewGuid()))); // sabor que não é do tamanho
        Validation(() => Resolve(Menu(), PriceList.Counter, DishInput(XBurguer, null, 1, new GroupAddonInput(Granola, 1))))
            .ShouldContainKey("items[0].groupAddons[0].addonOptionId");
        var noFlavor = ComboInput(1, Coca) with { Parts = [new ComboPartInput(BurgerPart, null, null, null)] };
        Validation(() => Resolve(Menu(), PriceList.Counter, noFlavor)).ShouldContainKey("items[0].parts.beverageFlavorId");
        var foreign = ComboInput(1, Coca) with { Parts = [new ComboPartInput(Guid.NewGuid(), null, null, null)] };
        Validation(() => Resolve(Menu(), PriceList.Counter, foreign)).ShouldContainKey("items[0].parts[0].comboComponentId");
    }

    /// Pausa → `422 ProductUnavailable` (ADR-0056 item 24, complementado: o código e o status do 06).
    [Fact]
    public void Paused_items_are_unavailable()
    {
        Unavailable(() => Resolve(Menu(batataPaused: true), PriceList.Counter, ComboInput(1, Coca))).ShouldContain("Pausado: Batata frita P");
        Unavailable(() => Resolve(Menu(cocaZeroPaused: true), PriceList.Counter, ComboInput(1, CocaZero)));
        Unavailable(() => Resolve(Menu(cocaZeroPaused: true), PriceList.Counter, BeverageInput(Lata, CocaZero)));
        Unavailable(() => Resolve(Menu(acai500Paused: true), PriceList.Counter, DishInput(Acai, Acai500)));
        Resolve(Menu(acai500Paused: true), PriceList.Counter, DishInput(Acai, Acai300)).ShouldHaveSingleItem().UnitPrice.ShouldBe(M(14m));
        Unavailable(() => Resolve(Menu(), PriceList.Counter, DishInput(Guid.NewGuid(), null)));
    }

    /// Preço de balcão no app; o de site só na tabela `SITE` (ADR-0057 itens 7 e 8; Q-308: adicional e sabor iguais).
    [Fact]
    public void Price_comes_from_the_order_price_list()
    {
        var counter = Resolve(Menu(), PriceList.Counter, ComboInput(1, CocaZero), DishInput(XBurguer, null));
        var site = Resolve(Menu(), PriceList.Site, ComboInput(1, CocaZero), DishInput(XBurguer, null));

        counter[0].UnitPrice.ShouldBe(M(32m));
        site[0].UnitPrice.ShouldBe(M(34m));
        counter[1].UnitPrice.ShouldBe(M(22m));
        site[1].UnitPrice.ShouldBe(M(23.90m));
        site[0].Addons.SelectMany(p => p.Addons).Single().UnitPrice.ShouldBe(M(4m));
        site[0].Addons.Single(p => p.Fixed!.BeverageSizeId == Lata).UnitPrice.ShouldBe(M(0.50m));
    }

    /// Um tipo por pedido (Q-317, ADR-0058 item 3): o cardápio fixo não entra em marmita, PF nem quilo, e vice-versa.
    [Theory]
    [InlineData(OrderType.ALaCarte, OrderItemKind.FixedDish, true)]
    [InlineData(OrderType.ALaCarte, OrderItemKind.FixedBeverage, true)]
    [InlineData(OrderType.ALaCarte, OrderItemKind.Combo, true)]
    [InlineData(OrderType.ALaCarte, OrderItemKind.Pf, false)]
    [InlineData(OrderType.ALaCarte, OrderItemKind.Marmita, false)]
    [InlineData(OrderType.ALaCarte, OrderItemKind.Beverage, false)]
    [InlineData(OrderType.ALaCarte, OrderItemKind.Addon, false)]
    [InlineData(OrderType.ALaCarte, OrderItemKind.ComboPart, false)]
    [InlineData(OrderType.ALaCarte, OrderItemKind.GroupAddon, false)]
    [InlineData(OrderType.Pf, OrderItemKind.FixedDish, false)]
    [InlineData(OrderType.Pf, OrderItemKind.Combo, false)]
    [InlineData(OrderType.Kilo, OrderItemKind.FixedBeverage, false)]
    [InlineData(OrderType.Marmita, OrderItemKind.FixedDish, false)]
    [InlineData(OrderType.Pf, OrderItemKind.Beverage, true)]
    [InlineData(OrderType.Kilo, OrderItemKind.Addon, true)]
    public void One_order_type_per_order(OrderType orderType, OrderItemKind kind, bool fits) =>
        Order.KindFits(orderType, kind).ShouldBe(fits);

    [Fact]
    public void Fixed_menu_items_are_refused_in_a_pf_order_and_pf_in_a_la_carte()
    {
        var dish = Resolve(Menu(), PriceList.Counter, DishInput(XBurguer, null));
        Should.Throw<BusinessException>(() => Create(dish, type: OrderType.Pf)).ErrorCode.ShouldBe(ErrorCodes.ValidationFailed);

        var pf = new NewItem(OrderItemKind.Pf, null, Guid.NewGuid(), null, 1, M(24m), "PF Frango", null, true, null, [], []);
        Should.Throw<BusinessException>(() => Create([pf])).ErrorCode.ShouldBe(ErrorCodes.ValidationFailed);

        var order = Create(dish);
        Should.Throw<BusinessException>(() => order.AddItems([pf], By, Guid.NewGuid, Now)).ErrorCode.ShouldBe(ErrorCodes.ValidationFailed);
    }

    /// ADR-0058 item 2: à la carte no salão, na entrega e na retirada; PF continua só no salão.
    [Theory]
    [InlineData(FulfillmentType.DineIn)]
    [InlineData(FulfillmentType.Delivery)]
    [InlineData(FulfillmentType.Pickup)]
    public void A_la_carte_accepts_every_fulfillment(FulfillmentType fulfillment)
    {
        var order = Create(Resolve(Menu(), PriceList.Counter, DishInput(XBurguer, null)), fulfillment);

        order.FulfillmentType.ShouldBe(fulfillment);
        order.FulfillmentStatus.ShouldBe(fulfillment == FulfillmentType.DineIn ? null : FulfillmentStatus.Pending);
        order.TotalAmount.ShouldBe(fulfillment == FulfillmentType.Delivery ? M(27m) : M(22m));
    }

    /// Adicional de grupo incluído depois numa parte de prato (ADR-0058 item 10): paga pela quantidade do combo e
    /// reabre a produção concluída (RN-007.10). Nunca numa parte de bebida.
    [Fact]
    public void Addon_added_later_to_a_combo_part_follows_the_combo_quantity_and_reopens_production()
    {
        var menu = Menu();
        var order = Create(Resolve(menu, PriceList.Counter, ComboInput(2, Coca, bacon: false)));
        order.ChangeProductionStatus(ProductionStatus.Completed, Now);
        var burger = order.Items.Single(i => i.DishId == XBurguer);
        var lata = order.Items.Single(i => i.BeverageSizeId == Lata);

        var later = new OrderItemInput(OrderItemKind.GroupAddon, burger.Id, null, null, 1, null, null, null, null, AddonOptionId: Bacon);
        var specs = new FixedMenuItems(menu, new ItemResolution(new DateOnly(2026, 11, 22), PriceList.Counter, OrderType.ALaCarte, order.Items)).Resolve([later]);
        var added = order.AddItems(specs, By, Guid.NewGuid, Now);

        added.ShouldHaveSingleItem().LineTotal.ShouldBe(M(8m));
        order.TotalAmount.ShouldBe(M(72m)); // 64,00 + 8,00
        order.ProductionStatus.ShouldBe(ProductionStatus.InProgress);

        var onDrink = later with { ParentOrderItemId = lata.Id };
        Validation(() => new FixedMenuItems(menu, new ItemResolution(new DateOnly(2026, 11, 22), PriceList.Counter, OrderType.ALaCarte, order.Items)).Resolve([onDrink]))
            .ShouldContainKey("items[0].parentOrderItemId");
    }

    /// Pedido quitado bloqueia a comanda (ADR-0007), e o combo segue a regra de qualquer item.
    [Fact]
    public void Paid_a_la_carte_order_is_locked()
    {
        var order = Create(Resolve(Menu(), PriceList.Counter, ComboInput(1, Coca)));
        order.ApplyPayment(M(36m), Now); // 32,00 + bacon 4,00

        order.IsPaid.ShouldBeTrue();
        var combo = order.Items.Single(i => i.ItemKind == OrderItemKind.Combo);
        Should.Throw<BusinessException>(() => order.RemoveItem(combo.Id, By, Guid.NewGuid, Now)).ErrorCode.ShouldBe(ErrorCodes.OrderAlreadyPaid);
        Should.Throw<BusinessException>(() => order.Cancel("Cliente desistiu", By, Guid.NewGuid, Now)).ErrorCode.ShouldBe(ErrorCodes.OrderHasPayments);
    }

    [Fact]
    public void A_la_carte_text_matches_the_database_check()
    {
        UpperSnakeEnumConverter<OrderType>.ToText(OrderType.ALaCarte).ShouldBe("A_LA_CARTE");
        Enum.GetValues<OrderItemKind>().Select(UpperSnakeEnumConverter<OrderItemKind>.ToText).ShouldBe(
            ["MARMITA", "PF", "BEVERAGE", "ADDON", "FIXED_DISH", "FIXED_BEVERAGE", "COMBO", "COMBO_PART", "GROUP_ADDON"]);
    }

    private static IReadOnlyDictionary<string, string[]> Validation(Func<object> act)
    {
        var ex = Should.Throw<BusinessException>(() => act());
        ex.ErrorCode.ShouldBe(ErrorCodes.ValidationFailed);
        return (IReadOnlyDictionary<string, string[]>)ex.Extensions["errors"]!;
    }

    private static string Unavailable(Func<object> act)
    {
        var ex = Should.Throw<BusinessException>(() => act());
        ex.ErrorCode.ShouldBe(ErrorCodes.ProductUnavailable);
        return ex.Message;
    }
}
