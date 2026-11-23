using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.Modules.Notifications.Contracts;
using AlmoSeis.Modules.Notifications.Domain;
using AlmoSeis.Modules.Ordering.Application;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Ordering.Domain;
using AlmoSeis.Modules.Storefront.Application;
using AlmoSeis.Modules.Storefront.Contracts;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using AlmoSeis.SharedKernel.Security;
using Shouldly;

namespace AlmoSeis.UnitTests.Modules;

/// Página da lanchonete (EP-003 fatia 5; ADR-0044 complementado, ADR-0057, ADR-0058 item 14; Q-317, Q-318): seções pelos
/// módulos, cardápio público só com o que vende no site e não está pausado, carrinho à la carte pelo preço de site,
/// pedido do site `A_LA_CARTE`, limites e `itemCount` do push.
public sealed class SnackBarStorefrontTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 23);

    private static Money M(decimal v) => Money.Of(v);

    // ---------- Seções ----------

    [Fact]
    public void Sections_come_from_the_modules()
    {
        StoreSections.For(TenantModuleMap.For([EstablishmentBehavior.Restaurant]).ToList()).ShouldBe([StoreSection.Marmita]);
        StoreSections.For(TenantModuleMap.For([EstablishmentBehavior.SnackBar]).ToList()).ShouldBe([StoreSection.ALaCarte]);
        StoreSections.For(TenantModuleMap.For([EstablishmentBehavior.Restaurant, EstablishmentBehavior.SnackBar]).ToList())
            .ShouldBe([StoreSection.Marmita, StoreSection.ALaCarte]);
        StoreSections.For([TenantModule.FixedMenu]).ShouldBeEmpty("sem o módulo DELIVERY a página não vende o cardápio fixo");
    }

    // ---------- Cardápio público ----------

    private static readonly Guid Lanches = Guid.NewGuid(), Porcoes = Guid.NewGuid(), Sobremesas = Guid.NewGuid();
    private static readonly Guid XBurguer = Guid.NewGuid(), Batata = Guid.NewGuid(), BatataP = Guid.NewGuid(), BatataG = Guid.NewGuid(), Pudim = Guid.NewGuid();
    private static readonly Guid Refri = Guid.NewGuid(), Lata = Guid.NewGuid(), DoisLitros = Guid.NewGuid(), Coca = Guid.NewGuid(), CocaZero = Guid.NewGuid();
    private static readonly Guid Grupo = Guid.NewGuid(), Bacon = Guid.NewGuid(), Cheddar = Guid.NewGuid();
    private static readonly Guid Combo = Guid.NewGuid(), ComboOff = Guid.NewGuid(), BurgerPart = Guid.NewGuid(), BatataPart = Guid.NewGuid(), LataPart = Guid.NewGuid();

    private static FixedMenuDto AppMenu(bool batataPPaused = false)
    {
        var t = Now;
        DishSizeDto Size(Guid id, string name, decimal price, bool site, decimal? sitePrice, bool paused = false) =>
            new(id, name, M(price), site, sitePrice is { } p ? M(p) : null, 1, true, paused, !paused);
        return new FixedMenuDto(
            [
                new DishCategoryDto(Lanches, 1, "Lanches", 1, true, t, t),
                new DishCategoryDto(Porcoes, 1, "Porções", 2, true, t, t),
                new DishCategoryDto(Sobremesas, 1, "Sobremesas", 3, true, t, t),
            ],
            [
                new DishDto(XBurguer, 1, Lanches, "X-Burguer", null, null, true, false, M(22m), true, M(23.90m), 1, true, false, true, [], [Grupo], t, t),
                new DishDto(Batata, 1, Porcoes, "Batata frita", null, null, true, true, null, false, null, 1, true, false, true,
                    [Size(BatataP, "P", 12m, true, 13m, batataPPaused), Size(BatataG, "G", 18m, false, null)], [], t, t),
                new DishDto(Pudim, 1, Sobremesas, "Pudim", null, null, false, false, M(8m), false, null, 1, true, false, true, [], [], t, t),
            ],
            [
                new BeverageTypeDto(Refri, 1, "Refrigerante", 1, true,
                [
                    new BeverageSizeDto(Lata, "Lata 350 ml", M(6m), true, M(6.50m), 1, true, false, true,
                    [
                        new BeverageFlavorDto(Coca, "Coca-Cola", M(0m), 1, true, false),
                        new BeverageFlavorDto(CocaZero, "Coca-Cola Zero", M(0.50m), 2, true, true),
                    ]),
                    new BeverageSizeDto(DoisLitros, "2 L", M(14m), false, null, 2, true, false, true, []),
                ], t, t),
            ],
            [
                new ComboDto(Combo, 1, "X-Burguer + batata + refri", null, null, M(32m), true, M(34.90m), 1, true, false, !batataPPaused,
                [
                    new ComboComponentDto(BurgerPart, ComboComponentKind.Dish, XBurguer, null, null, "X-Burguer", 1, 0, true),
                    new ComboComponentDto(BatataPart, ComboComponentKind.Dish, Batata, BatataP, null, "Batata frita P", 1, 1, !batataPPaused),
                    new ComboComponentDto(LataPart, ComboComponentKind.Beverage, null, null, Lata, "Refrigerante Lata 350 ml", 1, 2, true),
                ], t, t),
                new ComboDto(ComboOff, 1, "Batata + refri", null, null, M(16m), false, null, 2, true, false, true, [], t, t),
            ],
            [
                new AddonGroupDto(Grupo, 1, "Adicionais de lanche", 1, true,
                    [new AddonOptionDto(Bacon, "Bacon", M(4m), 1, true, false), new AddonOptionDto(Cheddar, "Cheddar", M(3m), 2, true, true)],
                    [Lanches], [], t, t),
            ]);
    }

    [Fact]
    public void Public_fixed_menu_has_only_what_sells_on_site_and_is_not_paused_at_the_site_price()
    {
        var menu = PublicFixedMenuBuilder.Build(AppMenu());

        menu.Categories.Select(c => c.Name).ShouldBe(["Lanches", "Porções"], "o pudim não vende no site: a aba Sobremesas some");
        var burger = menu.Categories[0].Dishes.ShouldHaveSingleItem();
        burger.Price.ShouldBe(M(23.90m));
        burger.AddonGroupIds.ShouldBe([Grupo]);
        menu.Categories[1].Dishes.ShouldHaveSingleItem().Sizes.ShouldBe([new PublicDishSizeDto(BatataP, "P", M(13m))]);

        var lata = menu.Beverages.ShouldHaveSingleItem().Sizes.ShouldHaveSingleItem();
        lata.Price.ShouldBe(M(6.50m));
        lata.Flavors.Select(f => f.Id).ShouldBe([Coca], "a Coca-Cola Zero está pausada");

        var combo = menu.Combos.ShouldHaveSingleItem();
        combo.Price.ShouldBe(M(34.90m));
        combo.Components.Select(c => c.Name).ShouldBe(["X-Burguer", "Batata frita P", "Refrigerante Lata 350 ml"]);
        combo.Components[0].AddonGroupIds.ShouldBe([Grupo]);
        combo.Components[2].Flavors.Select(f => f.Id).ShouldBe([Coca]);

        menu.AddonGroups.ShouldHaveSingleItem().Options.ShouldBe([new PublicAddonOptionDto(Bacon, "Bacon", M(4m))], "o cheddar está pausado");
    }

    [Fact]
    public void Paused_component_takes_the_combo_and_the_dish_without_sizes_out_of_the_page()
    {
        var menu = PublicFixedMenuBuilder.Build(AppMenu(batataPPaused: true));
        menu.Combos.ShouldBeEmpty();
        menu.Categories.Select(c => c.Name).ShouldBe(["Lanches"], "a Batata G não vende no site e a P está pausada");
    }

    [Fact]
    public void Activation_needs_a_dish_or_combo_sold_on_site()
    {
        StorefrontActivation.HasDishOrCombo(AppMenu()).ShouldBeTrue();
        var onlyBeverages = AppMenu() with
        {
            Dishes = AppMenu().Dishes.Select(d => d with { SellsOnSite = false, Sizes = d.Sizes.Select(z => z with { SellsOnSite = false }).ToList() }).ToList(),
            Combos = AppMenu().Combos.Select(c => c with { SellsOnSite = false }).ToList(),
        };
        StorefrontActivation.HasDishOrCombo(onlyBeverages).ShouldBeFalse("bebida sozinha não é pedido (Q-318)");
        var pausedDish = onlyBeverages with { Dishes = [AppMenu().Dishes[0] with { IsPaused = true, IsSellable = false }] };
        StorefrontActivation.HasDishOrCombo(pausedDish).ShouldBeTrue("a pausa é passageira e não impede ativar");
    }

    // ---------- Carrinho à la carte ----------

    private static FixedMenuSnapshot Snapshot(bool cocaZeroPaused = false)
    {
        var dishes = new Dictionary<Guid, FixedMenuDish>
        {
            [XBurguer] = new(XBurguer, Lanches, "X-Burguer", true, false, new FixedMenuPrice(M(22m), true, M(23.90m)), true, new Dictionary<Guid, FixedMenuDishSize>(), [Bacon]),
            [Batata] = new(Batata, Porcoes, "Batata frita", true, true, null, true,
                new Dictionary<Guid, FixedMenuDishSize>
                {
                    [BatataP] = new(BatataP, "P", new FixedMenuPrice(M(12m), true, M(13m)), true),
                    [BatataG] = new(BatataG, "G", new FixedMenuPrice(M(18m), false, null), true),
                }, []),
        };
        var beverages = new Dictionary<Guid, FixedMenuBeverageSize>
        {
            [Lata] = new(Lata, Refri, "Refrigerante", "Lata 350 ml", new FixedMenuPrice(M(6m), false, null), true,
                new Dictionary<Guid, FixedMenuBeverageFlavor>
                {
                    [Coca] = new(Coca, "Coca-Cola", M(0m), true),
                    [CocaZero] = new(CocaZero, "Coca-Cola Zero", M(0.50m), !cocaZeroPaused),
                }),
        };
        var combos = new Dictionary<Guid, FixedMenuCombo>
        {
            [Combo] = new(Combo, "X-Burguer + batata + refri", new FixedMenuPrice(M(32m), true, M(34.90m)), true,
            [
                new(BurgerPart, ComboComponentKind.Dish, XBurguer, null, null, 1, "X-Burguer", true),
                new(BatataPart, ComboComponentKind.Dish, Batata, BatataP, null, 1, "Batata frita P", true),
                new(LataPart, ComboComponentKind.Beverage, null, null, Lata, 1, "Refrigerante Lata 350 ml", true),
            ]),
        };
        var options = new Dictionary<Guid, FixedMenuAddonOption> { [Bacon] = new(Bacon, Grupo, "Bacon", M(4m), true) };
        return new FixedMenuSnapshot(dishes, beverages, combos, options);
    }

    private static OrderItemInput ComboInput(int quantity, Guid flavor) => new(
        OrderItemKind.Combo, null, null, null, quantity, null, null, null, null,
        ComboId: Combo,
        Parts:
        [
            new ComboPartInput(BurgerPart, null, "Sem cebola", [new GroupAddonInput(Bacon, 1)]),
            new ComboPartInput(LataPart, flavor, null, null),
        ]);

    private static OrderItemInput DishInput(Guid dish, Guid? size = null, int quantity = 1) =>
        new(OrderItemKind.FixedDish, null, null, null, quantity, null, null, null, null, DishId: dish, DishSizeId: size);

    [Fact]
    public void Two_combos_with_bacon_and_coke_zero_at_the_site_price()
    {
        // 2 × 34,90 + bacon 4,00 × 2 + Coca-Cola Zero 0,50 × 2 = 69,80 + 8,00 + 1,00 = 78,80; X-Burguer 23,90; Batata P 13,00.
        var cart = WebOrderCart.PriceFixedMenu([ComboInput(2, CocaZero), DishInput(XBurguer), DishInput(Batata, BatataP)], Snapshot(), Today, M(5m));

        cart.Items.Select(i => (i.Name, i.UnitPrice, i.LineTotal)).ShouldBe(
        [
            ("X-Burguer + batata + refri", (Money?)M(34.90m), (Money?)M(78.80m)),
            ("X-Burguer", M(23.90m), M(23.90m)),
            ("Batata frita P", M(13m), M(13m)),
        ]);
        cart.ItemsTotal.ShouldBe(M(115.70m));
        cart.DeliveryFee.ShouldBe(M(5m));
        cart.TotalAmount.ShouldBe(M(120.70m));
    }

    [Fact]
    public void Item_not_sold_on_site_or_paused_is_unavailable_and_left_out_of_the_total()
    {
        // A lata não vende no site sozinha, mas é componente de combo (ADR-0057 item 4): o combo continua.
        var lata = new OrderItemInput(OrderItemKind.FixedBeverage, null, null, null, 1, null, null, null, null, BeverageSizeId: Lata, BeverageFlavorId: Coca);
        var cart = WebOrderCart.PriceFixedMenu(
            [DishInput(Batata, BatataG), lata, ComboInput(1, CocaZero), DishInput(Guid.NewGuid())], Snapshot(cocaZeroPaused: true), Today, Money.Zero);

        cart.Items.Select(i => i.Available).ShouldBe([false, false, false, false]);
        cart.Items[0].Name.ShouldBe("Batata frita G");
        cart.UnavailableProductIds.Count.ShouldBe(4);
        cart.UnavailableProductIds.ShouldContain(BatataG);
        cart.UnavailableProductIds.ShouldContain(Lata);
        cart.UnavailableProductIds.ShouldContain(CocaZero);
        cart.TotalAmount.ShouldBe(Money.Zero);

        WebOrderCart.PriceFixedMenu([ComboInput(1, Coca)], Snapshot(cocaZeroPaused: true), Today, Money.Zero).TotalAmount.ShouldBe(M(38.90m));
    }

    [Fact]
    public void Inconsistent_choice_is_a_validation_error_on_the_field()
    {
        var noFlavor = new OrderItemInput(OrderItemKind.Combo, null, null, null, 1, null, null, null, null, ComboId: Combo,
            Parts: [new ComboPartInput(LataPart, null, null, null)]);
        Should.Throw<BusinessException>(() => WebOrderCart.PriceFixedMenu([DishInput(XBurguer), noFlavor], Snapshot(), Today, Money.Zero))
            .ShouldSatisfyAllConditions(ex => Fields(ex).ShouldContain("items[1].parts[0].beverageFlavorId"));
    }

    private static IEnumerable<string> Fields(BusinessException ex) =>
        ((IReadOnlyDictionary<string, string[]>)ex.Extensions["errors"]!).Keys;

    // ---------- Formato e limites (Q-317, Q-318) ----------

    private static PublicOrderItemInput Fixed(OrderItemKind kind, int quantity = 1) => kind switch
    {
        OrderItemKind.FixedDish => new(kind, null, null, quantity, null, null, null, null, DishId: XBurguer),
        OrderItemKind.FixedBeverage => new(kind, null, null, quantity, null, null, null, null, BeverageSizeId: Lata, BeverageFlavorId: Coca),
        _ => new(kind, null, null, quantity, null, null, null, null, ComboId: Combo),
    };

    private static PlaceWebOrderRequest Request(OrderType? type, params PublicOrderItemInput[] items) => new(
        FulfillmentType.Pickup, "Carla Souza", "34988887777", null, null, ExpectedPaymentMethod.Pix, null, null, items, M(1m), null, type);

    private static IReadOnlyDictionary<string, string> Errors(PlaceWebOrderRequest request) =>
        new PlaceWebOrderRequestValidator().Validate(request).Errors
            .GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.First().ErrorMessage);

    [Fact]
    public void Snack_bar_order_needs_a_dish_or_combo_and_at_most_twenty()
    {
        Errors(Request(OrderType.ALaCarte, Fixed(OrderItemKind.FixedDish), Fixed(OrderItemKind.FixedBeverage))).ShouldBeEmpty();
        Errors(Request(OrderType.ALaCarte, Fixed(OrderItemKind.FixedBeverage)))["Items"].ShouldContain("prato ou combo");
        Errors(Request(OrderType.ALaCarte, Fixed(OrderItemKind.FixedDish, 11), Fixed(OrderItemKind.Combo, 9), Fixed(OrderItemKind.FixedBeverage, 20)))
            .ShouldBeEmpty("20 pratos e combos; bebidas não contam");
        Errors(Request(OrderType.ALaCarte, Fixed(OrderItemKind.FixedDish, 11), Fixed(OrderItemKind.Combo, 10)))["Items"].ShouldContain("20 pratos e combos");
        Errors(Request(OrderType.ALaCarte, [.. Enumerable.Range(0, 31).Select(_ => Fixed(OrderItemKind.FixedBeverage))]))["Items"].ShouldContain("30 itens");
    }

    [Fact]
    public void One_type_per_order()
    {
        var marmita = new PublicOrderItemInput(OrderItemKind.Marmita, Guid.NewGuid(), null, 1, [Guid.NewGuid()], null, null, null);
        Errors(Request(OrderType.ALaCarte, Fixed(OrderItemKind.FixedDish), marmita))["Items[1].ItemKind"].ShouldContain("um tipo de pedido por vez");
        Errors(Request(null, marmita, Fixed(OrderItemKind.FixedDish)))["Items[1].ItemKind"].ShouldContain("um tipo de pedido por vez");
        Errors(Request(OrderType.Marmita, marmita)).ShouldBeEmpty();
        Errors(Request(OrderType.Pf, marmita)).Keys.ShouldContain("OrderType");

        // Campo de outro tipo no mesmo item; observação no próprio combo.
        Errors(Request(OrderType.ALaCarte, Fixed(OrderItemKind.FixedDish) with { ProductId = Guid.NewGuid() })).Keys.ShouldContain("Items[0].itemKind");
        Errors(Request(OrderType.ALaCarte, Fixed(OrderItemKind.Combo) with { Notes = "Sem cebola" })).Keys.ShouldContain("Items[0].Notes");
    }

    // ---------- Pedido do site à la carte e push ----------

    [Fact]
    public void Web_order_a_la_carte_waits_for_acceptance_with_item_count_and_no_marmitas()
    {
        var items = new FixedMenuItems(Snapshot(), new ItemResolution(Today, PriceList.Site, OrderType.ALaCarte))
            .Resolve([ComboInput(2, CocaZero), DishInput(XBurguer), new OrderItemInput(OrderItemKind.FixedBeverage, null, null, null, 3, null, null, null, null, BeverageSizeId: Lata, BeverageFlavorId: Coca)]);
        var order = Order.SubmitFromWeb(
            Guid.NewGuid(), Guid.NewGuid(),
            new NewOrder(OrderType.ALaCarte, FulfillmentType.Pickup, null, "Carla", "+5534988887777", null, Money.Zero, null, null, null, items, []),
            Guid.NewGuid(), Today, Now.AddMinutes(10), Guid.NewGuid, Now);

        order.PriceList.ShouldBe(PriceList.Site);
        // 78,80 + 23,90 + 3 × 6,00 (a lata avulsa não vende no site: fora da página, o pedido SITE usa o balcão, Q-309) = 120,70
        order.TotalAmount.ShouldBe(M(120.70m));
        var e = order.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<OrderAwaitingAcceptance>();
        e.MarmitaCount.ShouldBe(0);
        e.ItemCount.ShouldBe(6, "2 combos + 1 prato + 3 bebidas");

        Should.Throw<BusinessException>(() => Order.SubmitFromWeb(
                Guid.NewGuid(), Guid.NewGuid(),
                new NewOrder(OrderType.ALaCarte, FulfillmentType.DineIn, null, "Carla", null, null, Money.Zero, "5", null, null, items, []),
                Guid.NewGuid(), Today, Now.AddMinutes(10), Guid.NewGuid, Now))
            .ShouldSatisfyAllConditions(ex => Fields(ex).ShouldContain("fulfillmentType"));
    }

    [Fact]
    public void Push_body_counts_items_on_the_snack_bar_and_marmitas_on_the_restaurant()
    {
        WebOrderPushContent.For(PushKind.WebOrderAwaitingAcceptance, 0, "PICKUP", 10, null, itemCount: 3).Body
            .ShouldBe("3 itens · Retirada · aceite em até 10 min");
        WebOrderPushContent.For(PushKind.WebOrderAwaitingAcceptance, 0, "DELIVERY", 9, null, itemCount: 1).Body
            .ShouldBe("1 item · Entrega · aceite em até 9 min");
        WebOrderPushContent.For(PushKind.WebOrderAwaitingAcceptance, 2, "DELIVERY", 10, null, itemCount: 3).Body
            .ShouldBe("2 marmitas · Entrega · aceite em até 10 min");
    }
}
