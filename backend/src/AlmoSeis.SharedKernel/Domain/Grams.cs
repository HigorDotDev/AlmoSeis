namespace AlmoSeis.SharedKernel.Domain;

/// Peso em gramas inteiras, de 1 a 50.000 (ADR-0030).
public readonly record struct Grams
{
    public const int Min = 1;
    public const int Max = 50_000;

    private Grams(int value) => Value = value;

    public int Value { get; }

    public static Grams Of(int value) =>
        value is < Min or > Max
            ? throw new ArgumentOutOfRangeException(nameof(value), value, $"O peso deve estar entre {Min} e {Max} gramas.")
            : new Grams(value);

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public static class KiloPricing
{
    /// Única implementação da regra de pesagem: gramas × preço/kg ÷ 1000, arredondado a 2 casas,
    /// metade para longe do zero (ADR-0030). Igual ao CHECK ck_weighing_entries_amount do 05.
    public static Money Amount(Grams grams, Money pricePerKg) =>
        Money.Of(Math.Round(grams.Value * pricePerKg.Amount / 1000m, 2, MidpointRounding.AwayFromZero));
}
