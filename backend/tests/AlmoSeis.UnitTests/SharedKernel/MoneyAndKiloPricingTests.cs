using AlmoSeis.SharedKernel.Domain;
using Shouldly;

namespace AlmoSeis.UnitTests.SharedKernel;

public sealed class MoneyTests
{
    [Theory]
    [InlineData("35.50", 35.50)]
    [InlineData("0.00", 0)]
    [InlineData("-10.00", -10)]
    [InlineData("3650.00", 3650)]
    public void Parse_accepts_api_format(string text, decimal expected)
    {
        Money.Parse(text).Amount.ShouldBe(expected);
        Money.Parse(text).ToString().ShouldBe(text);
    }

    [Theory]
    [InlineData("35.5")]
    [InlineData("35")]
    [InlineData("35,50")]
    [InlineData("35.505")]
    [InlineData("R$ 35.50")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_rejects_anything_but_two_decimals(string? text) =>
        Money.TryParse(text, out _).ShouldBeFalse();

    [Fact]
    public void Of_rejects_more_than_two_decimals() =>
        Should.Throw<ArgumentOutOfRangeException>(() => Money.Of(1.005m));

    [Fact]
    public void Arithmetic_keeps_two_decimals()
    {
        var total = Money.Of(20.00m) * 2 + Money.Of(2.00m) * 2 + Money.Of(6.00m) + Money.Of(5.00m);
        total.ToString().ShouldBe("55.00");
        (total - Money.Of(20.00m)).ToString().ShouldBe("35.00");
    }
}

public sealed class KiloPricingTests
{
    // Os mesmos casos do teste de fumaça do DDL (05) e dos exemplos do contrato (06 §6.9).
    [Theory]
    [InlineData(452, "59.90", "27.07")]
    [InlineData(318, "59.90", "19.05")]
    [InlineData(250, "1.42", "0.36")]   // 0,355 → 0,36: metade para longe do zero (ADR-0030)
    [InlineData(1, "0.01", "0.00")]
    [InlineData(50_000, "99.99", "4999.50")]
    public void Amount_rounds_half_away_from_zero(int grams, string pricePerKg, string expected) =>
        KiloPricing.Amount(Grams.Of(grams), Money.Parse(pricePerKg)).ToString().ShouldBe(expected);

    [Theory]
    [InlineData(0)]
    [InlineData(50_001)]
    public void Grams_outside_range_is_rejected(int grams) =>
        Should.Throw<ArgumentOutOfRangeException>(() => Grams.Of(grams));
}
