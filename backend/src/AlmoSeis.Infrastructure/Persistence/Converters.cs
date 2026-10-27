using System.Text.Json;
using AlmoSeis.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AlmoSeis.Infrastructure.Persistence;

/// numeric(12,2) ↔ Money (ADR-0030).
public sealed class MoneyConverter() : ValueConverter<Money, decimal>(m => m.Amount, d => Money.Of(d));

/// integer ↔ Grams.
public sealed class GramsConverter() : ValueConverter<Grams, int>(g => g.Value, v => Grams.Of(v));

/// Enum ↔ texto UPPER_SNAKE (JsonNamingPolicy.SnakeCaseUpper, a mesma política do JSON da API).
public sealed class UpperSnakeEnumConverter<TEnum>() : ValueConverter<TEnum, string>(
    v => ToText(v),
    v => FromText(v))
    where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> Texts =
        Enum.GetValues<TEnum>().ToDictionary(v => v, v => JsonNamingPolicy.SnakeCaseUpper.ConvertName(v.ToString()));

    private static readonly Dictionary<string, TEnum> Values = Texts.ToDictionary(p => p.Value, p => p.Key);

    public static string ToText(TEnum value) => Texts[value];

    public static TEnum FromText(string text) =>
        Values.TryGetValue(text, out var value) ? value : throw new InvalidOperationException($"Valor '{text}' desconhecido para {typeof(TEnum).Name}.");
}
