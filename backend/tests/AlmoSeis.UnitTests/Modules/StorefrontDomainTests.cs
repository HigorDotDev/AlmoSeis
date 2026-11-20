using AlmoSeis.Modules.Ordering.Application;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Storefront.Contracts;
using AlmoSeis.Modules.Storefront.Domain;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using Shouldly;

namespace AlmoSeis.UnitTests.Modules;

/// ADR-0044 itens 12 a 16: geração e validação do slug.
public sealed class SlugRulesTests
{
    [Theory]
    [InlineData("Marmitaria da Dona Zélia", "marmitaria-da-dona-zelia")]
    [InlineData("  Café & Cia. -- Ltda  ", "cafe-cia-ltda")]
    [InlineData("AÇAÍ DO ZÉ", "acai-do-ze")]
    public void Slugify_follows_the_adr_example(string name, string expected) => SlugRules.Slugify(name).ShouldBe(expected);

    [Fact]
    public void Slugify_cuts_at_40_without_trailing_dash() =>
        SlugRules.Slugify("Restaurante e Marmitaria Sabor Caseiro da Vovó Maria Aparecida").Length.ShouldBeLessThanOrEqualTo(40);

    [Theory]
    [InlineData("ab")]
    [InlineData("-abc")]
    [InlineData("abc-")]
    [InlineData("ab--c")]
    [InlineData("Abc")]
    [InlineData("12345")]
    [InlineData("123-456")]
    [InlineData("admin")]
    [InlineData("pedidos")]
    [InlineData("restaurantes")]
    [InlineData("lanche-merda")]
    public void Invalid_slugs_are_refused(string slug) => SlugRules.Problem(slug).ShouldNotBeNull();

    [Theory]
    [InlineData("marmitaria-da-ana")]
    [InlineData("pica-pau-marmitas")]
    [InlineData("bar-do-123")]
    public void Valid_slugs_pass(string slug) => SlugRules.Problem(slug).ShouldBeNull();

    [Fact]
    public void Every_reserved_word_is_refused()
    {
        foreach (var reserved in SlugRules.Reserved)
        {
            SlugRules.Problem(reserved).ShouldNotBeNull(reserved);
        }
    }

    [Fact]
    public void Candidates_try_city_then_numbers()
    {
        var candidates = SlugRules.Candidates("Marmitaria da Ana", "Uberlândia").Take(4).ToList();
        candidates.ShouldBe(["marmitaria-da-ana", "marmitaria-da-ana-uberlandia", "marmitaria-da-ana-2", "marmitaria-da-ana-3"]);
    }

    [Fact]
    public void Reserved_or_short_name_gets_the_city() =>
        SlugRules.Candidates("Zé", "Uberlândia").First().ShouldBe("ze-uberlandia");
}

public sealed class StorefrontSettingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static StorefrontChanges Changes(bool active, StorefrontAddressDto? address = null) => new(
        "Marmitaria da Ana", active, true, true, true, true, null, null,
        address ?? new StorefrontAddressDto(null, null, null, null, null, null, null),
        new DeliveryRule(DeliveryFeeMode.Distance, Money.Of(0.80m), Money.Of(3m), 6.0m));

    private static readonly StorefrontAddressDto Address = new("Rua das Flores", "123", null, "Centro", "Uberlândia", "mg", "38400-000");

    [Fact]
    public void Page_is_born_inactive()
    {
        var settings = StorefrontSettings.CreateDefault(Guid.NewGuid(), "Marmitaria da Ana", Now);
        settings.IsPageActive.ShouldBeFalse();
        settings.DeliveryAvailableOnPage.ShouldBeFalse("a entrega pela página só existe com a cotação da fatia 4");
    }

    [Fact]
    public void Activating_requires_address_and_an_active_size()
    {
        var settings = StorefrontSettings.CreateDefault(Guid.NewGuid(), "Marmitaria da Ana", Now);
        Should.Throw<BusinessException>(() => settings.Update(Changes(true), false, true, Now)).ErrorCode.ShouldBe(ErrorCodes.ValidationFailed);
        Should.Throw<BusinessException>(() => settings.Update(Changes(true, Address), false, false, Now)).ErrorCode.ShouldBe(ErrorCodes.ValidationFailed);

        settings.Update(Changes(true, Address), false, true, Now).ShouldBeTrue();
        settings.IsPageActive.ShouldBeTrue();
        settings.AddressState.ShouldBe("MG");
        settings.AddressZipCode.ShouldBe("38400000");
        settings.Version.ShouldBe(2);
    }

    [Fact]
    public void Same_values_do_not_bump_the_version()
    {
        var settings = StorefrontSettings.CreateDefault(Guid.NewGuid(), "Marmitaria da Ana", Now);
        settings.Update(Changes(false, Address), false, true, Now).ShouldBeTrue();
        settings.ClearDomainEvents();
        settings.Update(Changes(false, Address), false, true, Now).ShouldBeFalse();
        settings.Update(Changes(false, Address), true, true, Now).ShouldBeTrue("trocar o slug sobe a versão");
    }
}

/// Telefone digitado na página → E.164 (ADR-0044 item 18).
public sealed class BrazilianPhoneTests
{
    [Theory]
    [InlineData("(34) 98888-7777", "+5534988887777")]
    [InlineData("34988887777", "+5534988887777")]
    [InlineData("+55 34 98888-7777", "+5534988887777")]
    [InlineData("5534988887777", "+5534988887777")]
    [InlineData("034 98888-7777", "+5534988887777")]
    [InlineData("(34) 3222-1111", "+553432221111")]
    public void Normalizes(string input, string expected) => BrazilianPhone.Normalize(input).ShouldBe(expected);

    [Theory]
    [InlineData("98888-7777")]
    [InlineData("(34) 8888-77777")]
    [InlineData("+1 415 555 0100")]
    [InlineData("(00) 98888-7777")]
    [InlineData("abc")]
    [InlineData("")]
    public void Refuses(string input) => BrazilianPhone.Normalize(input).ShouldBeNull();
}

/// Preço do carrinho da página sempre no servidor (ADR-0044 item 21, ADR-0020).
public sealed class WebOrderCartTests
{
    private static readonly Guid SizeM = Guid.NewGuid(), Frango = Guid.NewGuid(), Costela = Guid.NewGuid(), Arroz = Guid.NewGuid(),
        Ovo = Guid.NewGuid(), Coca = Guid.NewGuid(), Hidden = Guid.NewGuid();

    private static readonly PublicMenuDto Menu = new(
        [new PublicMarmitaSizeDto(SizeM, "M", Money.Of(20m))],
        [new PublicDishDto(Frango, "Frango", null, null, true), new PublicDishDto(Costela, "Costela", null, null, false)],
        [new PublicDishDto(Arroz, "Arroz", null, null, true)],
        [new PublicPricedProductDto(Ovo, "Ovo frito", null, null, Money.Of(2m), true)],
        [new PublicPricedProductDto(Coca, "Coca", null, null, Money.Of(6m), true)]);

    private static PublicOrderItemInput Marmita(int quantity, Guid main, params PublicAddonInput[] addons) =>
        new(OrderItemKind.Marmita, SizeM, null, quantity, [main], [Arroz], addons, null);

    [Fact]
    public void Two_marmitas_with_egg_and_a_coke()
    {
        // 2 × (20,00 + 2,00) + 6,00 = 50,00
        var cart = WebOrderCart.Price(
            [Marmita(2, Frango, new PublicAddonInput(Ovo, 1)), new PublicOrderItemInput(OrderItemKind.Beverage, null, Coca, 1, null, null, null, null)],
            Menu, Money.Zero);
        cart.Items[0].LineTotal.ShouldBe(Money.Of(44m));
        cart.Items[1].LineTotal.ShouldBe(Money.Of(6m));
        cart.TotalAmount.ShouldBe(Money.Of(50m));
        cart.Items.ShouldAllBe(i => i.Available);
    }

    [Fact]
    public void Unavailable_or_hidden_items_are_flagged_and_left_out_of_the_total()
    {
        var cart = WebOrderCart.Price(
            [Marmita(1, Costela), Marmita(1, Frango), new PublicOrderItemInput(OrderItemKind.Beverage, null, Hidden, 1, null, null, null, null)],
            Menu, Money.Zero);
        cart.Items.Select(i => i.Available).ShouldBe([false, true, false]);
        cart.UnavailableProductIds.ShouldBe([Costela, Hidden], ignoreOrder: true);
        cart.TotalAmount.ShouldBe(Money.Of(20m));
    }
}
