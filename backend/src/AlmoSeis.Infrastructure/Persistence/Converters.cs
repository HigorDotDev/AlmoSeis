using AlmoSeis.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AlmoSeis.Infrastructure.Persistence;

/// numeric(12,2) ↔ Money (ADR-0030).
public sealed class MoneyConverter() : ValueConverter<Money, decimal>(m => m.Amount, d => Money.Of(d));

/// integer ↔ Grams.
public sealed class GramsConverter() : ValueConverter<Grams, int>(g => g.Value, v => Grams.Of(v));
