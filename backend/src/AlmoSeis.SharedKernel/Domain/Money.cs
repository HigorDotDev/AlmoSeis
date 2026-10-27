using System.Globalization;
using System.Text.RegularExpressions;

namespace AlmoSeis.SharedKernel.Domain;

/// Valor em reais com exatamente 2 casas (ADR-0030). Não existe conversão a partir de double.
public readonly partial record struct Money : IComparable<Money>
{
    private Money(decimal amount) => Amount = amount;

    public decimal Amount { get; }

    public static Money Zero => new(0m);

    public static Money Of(decimal amount)
    {
        if (decimal.Round(amount, 2) != amount)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Valores monetários têm no máximo 2 casas decimais.");
        }

        return new Money(decimal.Round(amount, 2));
    }

    /// Formato da API: "35.50" (string, ponto, exatamente 2 casas, sinal opcional).
    public static bool TryParse(string? text, out Money money)
    {
        money = Zero;
        if (text is null || !ApiFormat().IsMatch(text))
        {
            return false;
        }

        money = new Money(decimal.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture));
        return true;
    }

    public static Money Parse(string text) =>
        TryParse(text, out var money) ? money : throw new FormatException($"Valor monetário inválido: '{text}'. Use o formato \"35.50\".");

    public bool IsZero => Amount == 0m;

    public bool IsNegative => Amount < 0m;

    public override string ToString() => Amount.ToString("0.00", CultureInfo.InvariantCulture);

    public int CompareTo(Money other) => Amount.CompareTo(other.Amount);

    public static Money operator +(Money a, Money b) => new(a.Amount + b.Amount);

    public static Money operator -(Money a, Money b) => new(a.Amount - b.Amount);

    public static Money operator -(Money a) => new(-a.Amount);

    public static Money operator *(Money a, int factor) => new(a.Amount * factor);

    public static bool operator <(Money a, Money b) => a.Amount < b.Amount;

    public static bool operator >(Money a, Money b) => a.Amount > b.Amount;

    public static bool operator <=(Money a, Money b) => a.Amount <= b.Amount;

    public static bool operator >=(Money a, Money b) => a.Amount >= b.Amount;

    public static Money Sum(IEnumerable<Money> values) => values.Aggregate(Zero, (acc, v) => acc + v);

    [GeneratedRegex(@"^-?\d{1,10}\.\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex ApiFormat();
}
