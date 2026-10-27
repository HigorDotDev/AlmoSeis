using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.Modules.Catalog.Domain;
using AlmoSeis.Modules.Menu.Contracts;
using AlmoSeis.Modules.Menu.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using Shouldly;

namespace AlmoSeis.UnitTests.Modules;

public sealed class CatalogAndMenuDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Product_patch_without_real_change_keeps_version()
    {
        var product = Product.Create(Guid.NewGuid(), Guid.NewGuid(), ProductKind.Beverage, "Água", null, null, Money.Of(4m), null, Now);

        product.Apply(Changes(name: "Água", isAvailable: true), kindLocked: false, Now.AddMinutes(1));

        product.Version.ShouldBe(1);
        product.UpdatedAt.ShouldBe(Now);
    }

    [Fact]
    public void Changing_kind_to_side_dish_requires_clearing_the_price()
    {
        var product = Product.Create(Guid.NewGuid(), Guid.NewGuid(), ProductKind.MainDish, "Farofa", null, null, Money.Of(5m), null, Now);

        var ex = Should.Throw<BusinessException>(() => product.Apply(Changes(kind: ProductKind.SideDish), kindLocked: false, Now));
        Errors(ex).Keys.ShouldBe(["defaultPrice"]);

        product.Apply(Changes(kind: ProductKind.SideDish, clearPrice: true), kindLocked: false, Now);
        product.Kind.ShouldBe(ProductKind.SideDish);
        product.Version.ShouldBe(2);
    }

    [Fact]
    public void Locked_kind_cannot_change()
    {
        var product = Product.Create(Guid.NewGuid(), Guid.NewGuid(), ProductKind.SideDish, "Arroz", null, null, null, null, Now);

        var ex = Should.Throw<BusinessException>(() => product.Apply(Changes(kind: ProductKind.MainDish), kindLocked: true, Now));

        Errors(ex).Keys.ShouldBe(["kind"]);
    }

    [Fact]
    public void Menu_item_specs_derive_section_and_pf_price()
    {
        var dish = new MenuProduct(Guid.NewGuid(), ProductKind.MainDish, Money.Of(22m), IsActive: true);
        var side = new MenuProduct(Guid.NewGuid(), ProductKind.SideDish, null, IsActive: true);
        var products = new[] { dish, side }.ToDictionary(p => p.Id);

        var specs = MenuItemSpecs.Build(MenuType.Pf, [new(dish.Id, null), new(side.Id, null, IsAvailable: false, SortOrder: 3)], products);

        specs[0].ShouldBe(new MenuItemSpec(dish.Id, MenuSection.MainDish, Money.Of(22m), true, 0));
        specs[1].ShouldBe(new MenuItemSpec(side.Id, MenuSection.SideDish, null, false, 3));
    }

    [Fact]
    public void Pf_main_dish_without_any_price_is_rejected()
    {
        var dish = new MenuProduct(Guid.NewGuid(), ProductKind.MainDish, null, IsActive: true);

        var ex = Should.Throw<BusinessException>(() =>
            MenuItemSpecs.Build(MenuType.Pf, [new(dish.Id, null)], new Dictionary<Guid, MenuProduct> { [dish.Id] = dish }));

        Errors(ex).Keys.ShouldBe(["items[0].pfPrice"]);
    }

    [Fact]
    public void Duplicate_and_inactive_products_are_reported_by_position()
    {
        var active = new MenuProduct(Guid.NewGuid(), ProductKind.SideDish, null, IsActive: true);
        var inactive = new MenuProduct(Guid.NewGuid(), ProductKind.SideDish, null, IsActive: false);
        var products = new[] { active, inactive }.ToDictionary(p => p.Id);

        var ex = Should.Throw<BusinessException>(() =>
            MenuItemSpecs.Build(MenuType.Marmita, [new(active.Id, null), new(active.Id, null), new(inactive.Id, null)], products));

        Errors(ex).Keys.ShouldBe(["items[1].productId", "items[2].productId"], ignoreOrder: true);
    }

    [Fact]
    public void Replacing_items_bumps_version_once_and_reuses_ids()
    {
        var productA = Guid.NewGuid();
        var productB = Guid.NewGuid();
        var menu = AlmoSeis.Modules.Menu.Domain.Menu.Create(
            Guid.NewGuid(), Guid.NewGuid(), MenuType.Marmita, MenuKind.Daily, new DateOnly(2026, 10, 27), null, null, null,
            [new(productA, MenuSection.MainDish, null, true, 1)], Guid.NewGuid, Now);
        var itemA = menu.Items.Single().Id;

        menu.ReplaceItems([new(productA, MenuSection.MainDish, null, false, 1), new(productB, MenuSection.SideDish, null, true, 2)], Guid.NewGuid, Now);
        menu.UpdateItem(itemA, new Optional<bool>(true), default, Now);

        menu.Version.ShouldBe(2);
        menu.Items.Single(i => i.ProductId == productA).Id.ShouldBe(itemA);
        menu.Items.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("\"3\"", true, 3)]
    [InlineData("3", false, 0)]
    [InlineData("W/\"3\"", false, 0)]
    [InlineData("\"0\"", false, 0)]
    [InlineData("\"-1\"", false, 0)]
    public void If_match_accepts_only_a_quoted_positive_version(string header, bool ok, int version)
    {
        AlmoSeis.Api.Infrastructure.RequiresIfMatchAttribute.TryParseVersion(header, out var parsed).ShouldBe(ok);
        parsed.ShouldBe(version);
    }

    [Fact]
    public void Optional_json_distinguishes_absent_null_and_value()
    {
        var options = new System.Text.Json.JsonSerializerOptions();
        AlmoSeis.Api.Infrastructure.ApiJson.Configure(options);

        var absent = System.Text.Json.JsonSerializer.Deserialize<UpdateProductRequest>("{}", options)!;
        var cleared = System.Text.Json.JsonSerializer.Deserialize<UpdateProductRequest>("""{"defaultPrice":null,"kind":"ADDON"}""", options)!;

        absent.DefaultPrice.HasValue.ShouldBeFalse();
        cleared.DefaultPrice.HasValue.ShouldBeTrue();
        cleared.DefaultPrice.Value.ShouldBeNull();
        cleared.Kind.Value.ShouldBe(ProductKind.Addon);
        Should.Throw<System.Text.Json.JsonException>(() =>
            System.Text.Json.JsonSerializer.Deserialize<UpdateProductRequest>("""{"isActive":null}""", options));
    }

    private static ProductChanges Changes(
        ProductKind? kind = null, string? name = null, bool? isAvailable = null, bool clearPrice = false) =>
        new(
            kind is { } k ? new Optional<ProductKind>(k) : default,
            name is not null ? new Optional<string>(name) : default,
            default,
            default,
            clearPrice ? new Optional<Money?>(null) : default,
            default,
            default,
            default,
            isAvailable is { } a ? new Optional<bool>(a) : default);

    private static IReadOnlyDictionary<string, string[]> Errors(BusinessException ex) =>
        (IReadOnlyDictionary<string, string[]>)ex.Extensions["errors"]!;
}
