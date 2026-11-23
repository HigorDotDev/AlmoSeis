using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.Modules.Catalog.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using Shouldly;

namespace AlmoSeis.UnitTests.Modules;

/// Cardápio fixo (ADR-0056) e preço de site (ADR-0057): agregados, disponibilidade derivada e tabela de preço, com os
/// exemplos do EP-003 (X-Burguer 22,00/23,90; Açaí 300/500/700 ml; Suco 500 ml 9,00 + 1,00; combo 32,00).
public sealed class FixedMenuDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Category = Guid.NewGuid();

    private static Money M(decimal value) => Money.Of(value);

    private static Dish Single(string name, decimal price, bool sellsOnSite = false, decimal? sitePrice = null) =>
        Dish.Create(Guid.NewGuid(), Tenant, new DishData(Category, name, null, true, false, M(price), sellsOnSite, sitePrice is { } s ? M(s) : null, 0, []), Guid.NewGuid, Now);

    private static Dish Sized(string name, params (string Name, decimal Price)[] sizes) =>
        Dish.Create(
            Guid.NewGuid(), Tenant,
            new DishData(Category, name, null, true, true, null, false, null, 0, sizes.Select((s, i) => new DishSizeSpec(null, s.Name, M(s.Price), false, null, i)).ToList()),
            Guid.NewGuid, Now);

    private static BeverageType Refri(params (string Name, decimal Extra)[] flavors) =>
        BeverageType.Create(
            Guid.NewGuid(), Tenant, "Refrigerante", 0,
            [new BeverageSizeSpec(null, "Lata 350 ml", M(6m), true, null, 0, flavors.Select((f, i) => new BeverageFlavorSpec(null, f.Name, M(f.Extra), i)).ToList())],
            Guid.NewGuid, Now);

    private static Combo ComboOf(string name, decimal price, params ComboComponentSpec[] parts) =>
        Combo.Create(Guid.NewGuid(), Tenant, new ComboData(name, null, M(price), false, null, 0, parts), Guid.NewGuid, Now);

    private static ComboComponentSpec DishPart(Dish dish, string? size = null) =>
        new(ComboComponentKind.Dish, dish.Id, size is null ? null : dish.Sizes.Single(s => s.Name == size).Id, null, 1);

    private static ComboComponentSpec BeveragePart(BeverageType type) => new(ComboComponentKind.Beverage, null, null, type.Sizes[0].Id, 1);

    [Fact]
    public void Dish_without_size_has_single_price_and_site_price_defaults_to_counter()
    {
        var xburguer = Single("X-Burguer", 22m, sellsOnSite: true, sitePrice: 23.90m);
        xburguer.Price.ShouldBe(M(22m));
        xburguer.SitePrice.ShouldBe(M(23.90m));

        var pastel = Single("Pastel de carne", 9m, sellsOnSite: true);
        pastel.SitePrice.ShouldBe(M(9m), "ao ligar a chave, vem o preço de balcão (ADR-0057 item 2)");
        pastel.Version.ShouldBe(1);
    }

    [Fact]
    public void Dish_with_sizes_needs_an_active_size_and_no_single_price()
    {
        Should.Throw<BusinessException>(() => Sized("Açaí")).Extensions["errors"].ShouldNotBeNull();
        Should.Throw<BusinessException>(() =>
            Dish.Create(Guid.NewGuid(), Tenant, new DishData(Category, "Açaí", null, true, true, M(14m), false, null, 0, [new DishSizeSpec(null, "300 ml", M(14m), false, null, 0)]), Guid.NewGuid, Now));
        Should.Throw<BusinessException>(() =>
            Dish.Create(Guid.NewGuid(), Tenant, new DishData(Category, "Misto", null, true, false, null, false, null, 0, []), Guid.NewGuid, Now));

        var acai = Sized("Açaí", ("300 ml", 14m), ("500 ml", 18m), ("700 ml", 23m));
        acai.Sizes.Select(s => s.Price).ShouldBe([M(14m), M(18m), M(23m)]);
        acai.Price.ShouldBeNull();
    }

    [Fact]
    public void Turning_sizes_off_inactivates_them_and_requires_the_single_price()
    {
        var acai = Sized("Açaí", ("300 ml", 14m), ("500 ml", 18m));
        Should.Throw<BusinessException>(() => acai.Apply(Changes(hasSizes: false), Guid.NewGuid, Now));

        var acai2 = Sized("Açaí", ("300 ml", 14m));
        acai2.Apply(Changes(hasSizes: false, price: M(15m)), Guid.NewGuid, Now).ShouldBeTrue();
        acai2.Sizes.ShouldAllBe(s => !s.IsActive);
        acai2.Price.ShouldBe(M(15m));
        acai2.Version.ShouldBe(2);
    }

    [Fact]
    public void Replacing_sizes_inactivates_the_missing_ones_and_keeps_the_pause()
    {
        var batata = Sized("Batata frita", ("P", 12m), ("G", 18m));
        var p = batata.Sizes.Single(s => s.Name == "P");
        batata.SetAvailability(null, [(p.Id, true)], Now);
        batata.ClearDomainEvents();

        var g = batata.Sizes.Single(s => s.Name == "G");
        batata.Apply(Changes(sizes: [new DishSizeSpec(p.Id, "P", M(13m), false, null, 0), new DishSizeSpec(null, "M", M(15m), false, null, 1)]), Guid.NewGuid, Now)
            .ShouldBeTrue();
        g.IsActive.ShouldBeFalse("tamanho fora da lista é inativado, nunca apagado");
        p.IsPaused.ShouldBeTrue("a pausa fica até alguém reativar");
        p.Price.ShouldBe(M(13m));
        batata.Sizes.Count.ShouldBe(3);
        Should.Throw<BusinessException>(() => batata.Apply(Changes(sizes: [new DishSizeSpec(Guid.NewGuid(), "X", M(1m), false, null, 0)]), Guid.NewGuid, Now));
    }

    [Fact]
    public void Patch_without_real_change_keeps_the_version()
    {
        var dish = Single("X-Burguer", 22m);
        dish.Apply(Changes(price: M(22m)), Guid.NewGuid, Now).ShouldBeFalse();
        dish.Version.ShouldBe(1);
        dish.SetAvailability(false, [], Now).ShouldBeFalse();
    }

    /// RF-305: Suco 500 ml R$ 9,00 + "Laranja com acerola" (+1,00) = R$ 10,00. O sabor é do tamanho (Q-305).
    [Fact]
    public void Beverage_price_is_size_plus_flavor_extra()
    {
        var suco = BeverageType.Create(
            Guid.NewGuid(), Tenant, "Suco", 0,
            [new BeverageSizeSpec(null, "500 ml", M(9m), false, null, 0, [new BeverageFlavorSpec(null, "Laranja com acerola", M(1m), 0)])],
            Guid.NewGuid, Now);
        var size = suco.Sizes[0];
        (size.Price + size.Flavors[0].ExtraPrice).ShouldBe(M(10m));
        Should.Throw<BusinessException>(() => suco.Apply(default, default, default,
            new Optional<IReadOnlyList<BeverageSizeSpec>>([new BeverageSizeSpec(size.Id, "500 ml", M(9m), false, null, 0, [new BeverageFlavorSpec(null, "Uva", M(0m), 0), new BeverageFlavorSpec(null, "uva", M(0m), 1)])]),
            Guid.NewGuid, Now));
    }

    /// ADR-0056 item 23 / critério 3 da fatia 2: pausar a Batata P tira de venda os dois combos que a usam e nenhum
    /// outro; reativar devolve; nada é gravado no combo.
    [Fact]
    public void Pausing_a_dish_size_derives_the_combos_out_of_sale()
    {
        var xburguer = Single("X-Burguer", 22m);
        var batata = Sized("Batata frita", ("P", 12m), ("G", 18m));
        var acai = Sized("Açaí", ("500 ml", 18m));
        var refri = Refri(("Coca-Cola", 0m), ("Coca-Cola Zero", 0.5m));
        var combo1 = ComboOf("X-Burguer + batata + refri", 32m, DishPart(xburguer), DishPart(batata, "P"), BeveragePart(refri));
        var combo2 = ComboOf("Batata + refri", 16m, DishPart(batata, "P"), BeveragePart(refri));
        var combo3 = ComboOf("Açaí grande", 17m, DishPart(acai, "500 ml"));

        bool Sellable(Combo c) => new FixedMenuAvailability([xburguer, batata, acai], [refri]).IsSellable(c);
        new[] { combo1, combo2, combo3 }.Select(Sellable).ShouldAllBe(sellable => sellable);

        var p = batata.Sizes.Single(s => s.Name == "P");
        batata.SetAvailability(null, [(p.Id, true)], Now);
        Sellable(combo1).ShouldBeFalse();
        Sellable(combo2).ShouldBeFalse();
        Sellable(combo3).ShouldBeTrue();
        FixedMenuAvailability.IsSellable(batata).ShouldBeTrue("a G continua vendável");
        combo1.Version.ShouldBe(1);
        combo1.IsPaused.ShouldBeFalse();
        new FixedMenuAvailability([xburguer, batata, acai], [refri]).Component(combo1.Components[1]).ShouldBe(("Batata frita P", false));

        batata.SetAvailability(null, [(p.Id, false)], Now);
        Sellable(combo1).ShouldBeTrue();
        Sellable(combo2).ShouldBeTrue();
    }

    [Fact]
    public void All_flavors_paused_take_the_beverage_size_and_its_combos_out_of_sale()
    {
        var batata = Sized("Batata frita", ("P", 12m));
        var refri = Refri(("Coca-Cola", 0m), ("Coca-Cola Zero", 0.5m));
        var combo = ComboOf("Batata + refri", 16m, DishPart(batata, "P"), BeveragePart(refri));
        var availability = () => new FixedMenuAvailability([batata], [refri]);

        refri.SetAvailability([], [(refri.Sizes[0].Flavors[0].Id, true)], Now);
        availability().IsSellable(combo).ShouldBeTrue("ainda há um sabor");
        refri.SetAvailability([], [(refri.Sizes[0].Flavors[1].Id, true)], Now);
        FixedMenuAvailability.IsSellable(refri, refri.Sizes[0]).ShouldBeFalse();
        availability().IsSellable(combo).ShouldBeFalse();

        // Tamanho sem sabor ativo é vendido sem escolha (item 11).
        var agua = BeverageType.Create(Guid.NewGuid(), Tenant, "Água", 0, [new BeverageSizeSpec(null, "500 ml", M(4m), false, null, 0, [])], Guid.NewGuid, Now);
        FixedMenuAvailability.IsSellable(agua, agua.Sizes[0]).ShouldBeTrue();
    }

    [Fact]
    public void Paused_or_inactive_combo_and_dish_without_size_rules()
    {
        var xburguer = Single("X-Burguer", 22m);
        var combo = ComboOf("Só o lanche", 20m, DishPart(xburguer));
        var availability = new FixedMenuAvailability([xburguer], []);
        combo.SetPaused(true, Now).ShouldBeTrue();
        combo.SetPaused(true, Now).ShouldBeFalse();
        availability.IsSellable(combo).ShouldBeFalse();
        combo.SetPaused(false, Now);
        availability.IsSellable(combo).ShouldBeTrue();

        xburguer.SetAvailability(true, [], Now);
        availability.IsSellable(combo).ShouldBeFalse();
        Should.Throw<BusinessException>(() => ComboOf("Vazio", 10m));
    }

    [Fact]
    public void Same_components_do_not_bump_the_combo_version()
    {
        var xburguer = Single("X-Burguer", 22m);
        var combo = ComboOf("Combo", 32m, DishPart(xburguer));
        var firstId = combo.Components[0].Id;
        combo.Apply(ComboChangesWith(components: [DishPart(xburguer)]), Guid.NewGuid, Now).ShouldBeFalse();
        combo.Components[0].Id.ShouldBe(firstId);
        combo.Apply(ComboChangesWith(components: [DishPart(xburguer) with { Quantity = 2 }]), Guid.NewGuid, Now).ShouldBeTrue();
        combo.Components.Single().Quantity.ShouldBe(2);
        combo.Version.ShouldBe(2);
    }

    /// RF-306: combo R$ 32,00 + bacon R$ 4,00 + Coca Zero (+0,50) = R$ 36,50.
    [Fact]
    public void Combo_line_example_adds_addons_and_flavor_extra()
    {
        var combo = ComboOf("X-Burguer + batata + refri", 32m, DishPart(Single("X-Burguer", 22m)));
        var refri = Refri(("Coca-Cola Zero", 0.5m));
        var group = AddonGroup.Create(Guid.NewGuid(), Tenant, "Adicionais de lanche", 0, [new AddonOptionSpec(null, "Bacon", M(4m), 0)], [Category], [], Guid.NewGuid, Now);
        (combo.Price + group.Options[0].Price + refri.Sizes[0].Flavors[0].ExtraPrice).ShouldBe(M(36.50m));
    }

    [Fact]
    public void Addon_group_links_are_replaced_and_options_paused()
    {
        var dish = Guid.NewGuid();
        var group = AddonGroup.Create(Guid.NewGuid(), Tenant, "Molhos", 0, [new AddonOptionSpec(null, "Maionese", M(0m), 0)], [Category], [], Guid.NewGuid, Now);
        group.Apply(default, default, default, default, default, new Optional<IReadOnlyList<Guid>>([dish]), Guid.NewGuid, Now).ShouldBeTrue();
        group.Links.Select(l => (l.DishCategoryId, l.DishId)).ShouldBe([(Category, (Guid?)null), (null, dish)], ignoreOrder: true);
        group.Apply(default, default, default, default, new Optional<IReadOnlyList<Guid>>([]), default, Guid.NewGuid, Now).ShouldBeTrue();
        group.Links.Single().DishId.ShouldBe(dish);

        group.SetAvailability([(group.Options[0].Id, true)], Now).ShouldBeTrue();
        FixedMenuAvailability.IsSellable(group, group.Options[0]).ShouldBeFalse();
    }

    /// ADR-0057 itens 6 a 8 e Q-309: `SITE` usa o preço de site de quem vende no site; o resto, o de balcão.
    [Fact]
    public void Price_list_picks_site_price_only_when_the_unit_sells_on_site()
    {
        var xburguer = new FixedMenuPrice(M(22m), true, M(23.90m));
        xburguer.For(PriceList.Site).ShouldBe(M(23.90m));
        xburguer.For(PriceList.Counter).ShouldBe(M(22m));
        new FixedMenuPrice(M(22m), false, M(23.90m)).For(PriceList.Site).ShouldBe(M(22m), "desligada, o preço guardado não vale");

        var size = new CatalogMarmitaSize(Guid.NewGuid(), "M", M(20m), true, 2, true, M(22m));
        size.PriceFor(PriceList.Site).ShouldBe(M(22m));
        size.PriceFor(PriceList.Counter).ShouldBe(M(20m));
        (size with { SellsOnSite = false }).PriceFor(PriceList.Site).ShouldBe(M(20m));

        var coca = new CatalogProduct(Guid.NewGuid(), ProductKind.Beverage, "Coca", null, M(6m), false, null, true, true, true, M(6.5m));
        coca.PriceFor(PriceList.Site).ShouldBe(M(6.5m));
        (coca with { IsVisibleOnPage = false }).PriceFor(PriceList.Site).ShouldBe(M(6m));
        (coca with { SitePrice = null }).PriceFor(PriceList.Site).ShouldBe(M(6m), "janela do deploy da 0009");
        var ovo = new CatalogProduct(Guid.NewGuid(), ProductKind.Addon, "Ovo", null, M(2m), true, null, true, true, true);
        ovo.PriceFor(PriceList.Site).ShouldBe(M(2m), "adicional: mesmo preço (Q-308)");
    }

    [Fact]
    public void Marmita_size_and_beverage_get_site_price()
    {
        var size = MarmitaSize.Create(Guid.NewGuid(), Tenant, "M", M(20m), 2, Now, sellsOnSite: true);
        size.SitePrice.ShouldBe(M(20m));
        size.Apply(default, default, default, default, Now, false, default);
        size.SellsOnSite.ShouldBeFalse();
        size.SitePrice.ShouldBe(M(20m), "desligada, o preço de site fica guardado");

        var coca = Product.Create(Guid.NewGuid(), Tenant, ProductKind.Beverage, "Coca", null, null, M(6m), null, Now);
        coca.SitePrice.ShouldBe(M(6m), "a API sempre grava o preço de site da bebida visível");
        Should.Throw<BusinessException>(() => Product.Create(Guid.NewGuid(), Tenant, ProductKind.Addon, "Ovo", null, null, M(2m), null, Now, M(3m)));
    }

    /// I6 (revisão de 2026-11-23, ADR-0057 item 2): a bebida do restaurante pode ter preço de site 0,00 e continua no
    /// site; preço de site negativo e o de um adicional continuam recusados.
    [Fact]
    public void Free_beverage_stays_on_site_with_site_price_zero()
    {
        var free = Product.Create(Guid.NewGuid(), Tenant, ProductKind.Beverage, "Água da casa", null, null, M(0m), null, Now);
        free.IsVisibleOnPage.ShouldBeTrue("bebida de 0,00 não sai mais do site");
        free.SitePrice.ShouldBe(M(0m), "sem preço de site, vale o de balcão, mesmo 0,00");

        var coffee = Product.Create(Guid.NewGuid(), Tenant, ProductKind.Beverage, "Cafezinho", null, null, M(3m), null, Now, M(0m));
        coffee.SitePrice.ShouldBe(M(0m), "cortesia no site, cobrado no balcão");

        // Escondida sem preço de site e mostrada de novo sem enviar o preço: volta com o de balcão (0,00), sem o 422 de antes.
        free.Apply(ProductChangesWith(isVisibleOnPage: false, sitePrice: new Optional<Money?>(null)), kindLocked: false, Now);
        free.SitePrice.ShouldBeNull();
        free.Apply(ProductChangesWith(isVisibleOnPage: true), kindLocked: false, Now);
        free.IsVisibleOnPage.ShouldBeTrue();
        free.SitePrice.ShouldBe(M(0m));

        coffee.Apply(ProductChangesWith(sitePrice: new Optional<Money?>(M(1m))), kindLocked: false, Now);
        coffee.SitePrice.ShouldBe(M(1m));
        coffee.Apply(ProductChangesWith(sitePrice: new Optional<Money?>(M(0m))), kindLocked: false, Now);
        coffee.SitePrice.ShouldBe(M(0m));

        Should.Throw<BusinessException>(() => coffee.Apply(ProductChangesWith(sitePrice: new Optional<Money?>(M(-1m))), kindLocked: false, Now));
        Should.Throw<BusinessException>(() => coffee.Apply(ProductChangesWith(sitePrice: new Optional<Money?>(null)), kindLocked: false, Now))
            .ErrorCode.ShouldBe(ErrorCodes.ValidationFailed);
        Should.Throw<BusinessException>(() => Product.Create(Guid.NewGuid(), Tenant, ProductKind.Addon, "Ovo", null, null, M(2m), null, Now, M(0m)));
    }

    private static ProductChanges ProductChangesWith(bool? isVisibleOnPage = null, Optional<Money?> sitePrice = default) => new(
        default, default, default, default, default, default, default, default, default,
        isVisibleOnPage is { } v ? new Optional<bool>(v) : default, sitePrice);

    private static DishChanges Changes(bool? hasSizes = null, Money? price = null, IReadOnlyList<DishSizeSpec>? sizes = null) => new(
        default, default, default, default,
        hasSizes is { } h ? new Optional<bool>(h) : default,
        price is { } p ? new Optional<Money?>(p) : default,
        default, default, default, default, default,
        sizes is null ? default : new Optional<IReadOnlyList<DishSizeSpec>>(sizes));

    private static ComboChanges ComboChangesWith(IReadOnlyList<ComboComponentSpec> components) =>
        new(default, default, default, default, default, default, default, default, new Optional<IReadOnlyList<ComboComponentSpec>>(components));
}
