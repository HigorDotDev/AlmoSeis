using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AlmoSeis.Modules.Storefront.Contracts;
using AlmoSeis.SharedKernel.Domain;

namespace AlmoSeis.Modules.Storefront.Domain;

/// Coordenada com 6 casas (`numeric(9,6)`).
internal sealed record GeoPoint(decimal Latitude, decimal Longitude);

/// Fórmula da taxa por distância (ADR-0048 itens 5 a 7; RN-204.1). Exemplos do usuário: 4,5 km × R$ 0,80 = R$ 3,60;
/// 2,1 km × R$ 0,80 = R$ 1,68 → mínimo R$ 3,00; 4,5 km × R$ 0,85 = 3,825 → R$ 3,83.
internal static class DeliveryFeeRule
{
    /// `km = round(metros / 1000, 1, AwayFromZero)`: o que o cliente vê ("4,5 km") é o que entra na conta.
    public static decimal Km(int meters) => Math.Round(meters / 1000m, 1, MidpointRounding.AwayFromZero);

    /// `taxa = max(round(km × preço/km, 2, AwayFromZero), mínimo)`.
    public static Money Fee(decimal km, Money pricePerKm, Money minimumFee)
    {
        var byDistance = Money.Of(Math.Round(km * pricePerKm.Amount, 2, MidpointRounding.AwayFromZero));
        return byDistance > minimumFee ? byDistance : minimumFee;
    }

    /// Raio opcional, comparado com o `km` já arredondado (o mesmo que o cliente vê): 6,0 entrega num raio de 6,0; 6,1 não.
    public static bool WithinRadius(decimal km, decimal? maxDistanceKm) => maxDistanceKm is not { } max || km <= max;

    public static string FormatKm(decimal km) => km.ToString("0.0", CultureInfo.InvariantCulture);
}

/// Endereço do cliente normalizado (ADR-0048 itens 8, 10 e 11): a chave do cache e da conferência da cotação no envio.
/// Rua, número, bairro, cidade, UF e CEP, sem acento, caixa ou pontuação; abreviações comuns do tipo de logradouro
/// viram o nome inteiro ("R." → "rua", "Av." → "avenida"). Complemento e referência ficam de fora: não mudam a rota.
/// O hash leva o tenant (o cache nunca é compartilhado entre restaurantes, ADR-0024) e é o único traço guardado no cache
/// (ADR-0052 item 5: hash e coordenadas, não o texto).
internal sealed record DeliveryAddressKey(string Normalized, string Hash, string Display, string Street, string? Number, string? Neighborhood, string? City, string? State, string? ZipCode)
{
    private static readonly Dictionary<string, string> StreetTypes = new(StringComparer.Ordinal)
    {
        ["r"] = "rua", ["av"] = "avenida", ["avn"] = "avenida", ["tv"] = "travessa", ["trav"] = "travessa", ["al"] = "alameda",
        ["rod"] = "rodovia", ["est"] = "estrada", ["pc"] = "praca", ["pca"] = "praca", ["pr"] = "praca", ["lg"] = "largo",
    };

    public static DeliveryAddressKey Create(Guid tenantId, PublicAddressInput address, string? storeCity, string? storeState)
    {
        var street = Clean(address.Street) ?? string.Empty;
        var number = Clean(address.Number);
        var neighborhood = Clean(address.Neighborhood);
        var city = Clean(address.City) ?? Clean(storeCity);
        var state = Clean(storeState)?.ToUpperInvariant();
        var zip = Clean(address.ZipCode) is { } z && new string(z.Where(char.IsAsciiDigit).ToArray()) is { Length: > 0 } digits ? digits : null;

        var normalized = string.Join('|', NormalizeStreet(street), NormalizeNumber(number), Fold(neighborhood), Fold(city), Fold(state), zip ?? string.Empty);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tenantId.ToString("N") + ":" + normalized)));

        var display = new StringBuilder(street);
        if (number is not null)
        {
            display.Append(", ").Append(number);
        }

        if (neighborhood is not null)
        {
            display.Append(" - ").Append(neighborhood);
        }

        if (city is not null)
        {
            display.Append(", ").Append(city);
            if (state is not null)
            {
                display.Append(" - ").Append(state);
            }
        }

        return new DeliveryAddressKey(normalized, hash, display.ToString(), street, number, neighborhood, city, state, zip);
    }

    private static string NormalizeStreet(string street)
    {
        var words = Fold(street).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && StreetTypes.TryGetValue(words[0], out var full))
        {
            words[0] = full;
        }

        return string.Join(' ', words);
    }

    /// "S/N", "s/n", "SN" → "sn"; "45 A" → "45a".
    private static string NormalizeNumber(string? number) => Fold(number).Replace(" ", string.Empty, StringComparison.Ordinal);

    /// Minúsculas, sem acento, pontuação vira espaço, espaços colapsados.
    private static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
                space = false;
            }
            else if (!space && builder.Length > 0)
            {
                builder.Append(' ');
                space = true;
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// Cotação de entrega [T] (ADR-0048 item 8): válida por 15 min, apagada 24 h depois de vencer (ADR-0052 item 5).
/// Guarda a configuração usada (versão, preço por km, mínimo) para a conferência do envio.
internal sealed class DeliveryQuote : Entity, ITenantOwned
{
    public static readonly TimeSpan Validity = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    public const int FormattedAddressMax = 200;

    private DeliveryQuote()
    {
    }

    public Guid TenantId { get; private set; }
    public string AddressHash { get; private set; } = null!;
    public string FormattedAddress { get; private set; } = null!;
    public decimal? Latitude { get; private set; }
    public decimal? Longitude { get; private set; }
    public DeliveryFeeMode FeeMode { get; private set; }
    public int? DistanceMeters { get; private set; }
    public decimal? DistanceKm { get; private set; }
    public Money? PricePerKm { get; private set; }
    public Money? MinimumFee { get; private set; }
    public Money DeliveryFee { get; private set; }
    public int SettingsVersion { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    public static DeliveryQuote ForDistance(
        Guid id, Guid tenantId, DeliveryAddressKey key, string formattedAddress, GeoPoint destination, int meters,
        Money pricePerKm, Money minimumFee, int settingsVersion, DateTimeOffset now)
    {
        var km = DeliveryFeeRule.Km(meters);
        return new DeliveryQuote
        {
            Id = id,
            TenantId = tenantId,
            AddressHash = key.Hash,
            FormattedAddress = Fit(formattedAddress),
            Latitude = destination.Latitude,
            Longitude = destination.Longitude,
            FeeMode = DeliveryFeeMode.Distance,
            DistanceMeters = meters,
            DistanceKm = km,
            PricePerKm = pricePerKm,
            MinimumFee = minimumFee,
            DeliveryFee = DeliveryFeeRule.Fee(km, pricePerKm, minimumFee),
            SettingsVersion = settingsVersion,
            CreatedAt = now,
            ExpiresAt = now + Validity,
        };
    }

    /// Modo `FIXED` (ADR-0048 item 1): a taxa padrão do ADR-0021, sem mapa nem distância.
    public static DeliveryQuote ForFixed(Guid id, Guid tenantId, DeliveryAddressKey key, Money fee, int settingsVersion, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        AddressHash = key.Hash,
        FormattedAddress = Fit(key.Display),
        FeeMode = DeliveryFeeMode.Fixed,
        DeliveryFee = fee,
        SettingsVersion = settingsVersion,
        CreatedAt = now,
        ExpiresAt = now + Validity,
    };

    public bool IsValidAt(DateTimeOffset now) => now < ExpiresAt;

    public DeliveryQuoteDto ToDto() =>
        new(Id, FormattedAddress, DistanceKm is { } km ? DeliveryFeeRule.FormatKm(km) : null, DeliveryFee, ExpiresAt);

    public RedeemedDeliveryQuote ToRedeemed() => new(Id, FeeMode, DeliveryFee, DistanceMeters, PricePerKm, MinimumFee);

    private static string Fit(string text) => text.Length <= FormattedAddressMax ? text : text[..FormattedAddressMax].TrimEnd();
}

/// Cache por tenant (ADR-0048 item 11, ADR-0052 item 5): hash do endereço → coordenadas (e a distância da rota a partir
/// de um ponto do restaurante). Sem texto do endereço. Só resultado exato entra (aproximado nunca vira taxa).
internal sealed class GeocodeCacheEntry : ITenantOwned
{
    private GeocodeCacheEntry()
    {
    }

    public Guid TenantId { get; private set; }
    public string AddressHash { get; private set; } = null!;
    public decimal Latitude { get; private set; }
    public decimal Longitude { get; private set; }
    public DateTimeOffset GeocodedAt { get; private set; }
    public decimal? OriginLatitude { get; private set; }
    public decimal? OriginLongitude { get; private set; }
    public int? DistanceMeters { get; private set; }
    public DateTimeOffset? RoutedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    public GeoPoint Point => new(Latitude, Longitude);

    /// Metros guardados, se a rota saiu do mesmo ponto do restaurante e ainda está dentro da validade do cache de rota.
    public int? CachedMeters(GeoPoint origin, DateTimeOffset now, TimeSpan routeValidity) =>
        DistanceMeters is { } meters && RoutedAt is { } at && now < at + routeValidity
        && OriginLatitude == origin.Latitude && OriginLongitude == origin.Longitude
            ? meters
            : null;
}
