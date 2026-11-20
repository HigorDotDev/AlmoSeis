using System.Globalization;
using System.Text;
using AlmoSeis.Modules.Storefront.Contracts;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;

namespace AlmoSeis.Modules.Storefront.Domain;

/// Perfil público e configuração da página de pedidos (ADR-0044 itens 6 a 11, ADR-0048 itens 1 a 3). 1:1 com o tenant:
/// a chave é o próprio tenant (como `TenantSettings`). Nasce **inativa** no autocadastro.
internal sealed class StorefrontSettings : AggregateRoot, ITenantOwned
{
    public const int PublicNameMin = 2;
    public const int PublicNameMax = 80;

    private StorefrontSettings()
    {
    }

    public Guid TenantId => Id;
    public string PublicName { get; private set; } = null!;
    public bool IsPageActive { get; private set; }
    public bool IsAcceptingOrders { get; private set; } = true;
    public bool IsDeliveryEnabled { get; private set; } = true;
    public bool OffersDelivery { get; private set; } = true;
    public bool OffersPickup { get; private set; } = true;
    public string? ContactPhone { get; private set; }
    public string? HoursText { get; private set; }
    public string? AddressStreet { get; private set; }
    public string? AddressNumber { get; private set; }
    public string? AddressComplement { get; private set; }
    public string? AddressNeighborhood { get; private set; }
    public string? AddressCity { get; private set; }
    public string? AddressState { get; private set; }
    public string? AddressZipCode { get; private set; }
    public decimal? Latitude { get; private set; }
    public decimal? Longitude { get; private set; }
    public DeliveryFeeMode DeliveryFeeMode { get; private set; } = DeliveryFeeMode.Distance;
    public Money? PricePerKm { get; private set; }
    public Money MinimumDeliveryFee { get; private set; } = Money.Zero;
    public decimal? MaxDeliveryDistanceKm { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool HasCompleteAddress =>
        AddressStreet is not null && AddressNumber is not null && AddressNeighborhood is not null && AddressCity is not null && AddressState is not null;

    public static StorefrontSettings CreateDefault(Guid tenantId, string publicName, DateTimeOffset now) => new()
    {
        Id = tenantId,
        PublicName = Truncate(publicName.Trim(), PublicNameMax),
        UpdatedAt = now,
    };

    /// PUT /storefront. `slugChanged` sobe a versão mesmo sem outra mudança (o slug mora em `store_slugs`).
    /// `hasActiveMarmitaSize`: ativar a página exige pelo menos um tamanho ativo (ADR-0044 item 11).
    public bool Update(StorefrontChanges changes, bool slugChanged, bool hasActiveMarmitaSize, DateTimeOffset now)
    {
        var before = Snapshot();
        var address = changes.Address;
        var newAddress = (Clean(address.Street), Clean(address.Number), Clean(address.Complement), Clean(address.Neighborhood),
            Clean(address.City), Clean(address.State)?.ToUpperInvariant(), Digits(address.ZipCode));
        var addressChanged = newAddress != (AddressStreet, AddressNumber, AddressComplement, AddressNeighborhood, AddressCity, AddressState, AddressZipCode);

        if (!changes.OffersDelivery && !changes.OffersPickup)
        {
            throw BusinessException.Validation("offersPickup", "Ofereça entrega, retirada ou as duas.");
        }

        if (changes.Delivery.MinimumFee < Money.Zero)
        {
            throw BusinessException.Validation("delivery.minimumFee", "A taxa mínima não pode ser negativa.");
        }

        PublicName = changes.PublicName.Trim();
        IsAcceptingOrders = changes.IsAcceptingOrders;
        IsDeliveryEnabled = changes.IsDeliveryEnabled;
        OffersDelivery = changes.OffersDelivery;
        OffersPickup = changes.OffersPickup;
        ContactPhone = Clean(changes.ContactPhone);
        HoursText = Clean(changes.HoursText);
        (AddressStreet, AddressNumber, AddressComplement, AddressNeighborhood, AddressCity, AddressState, AddressZipCode) = newAddress;
        if (addressChanged)
        {
            // Mudar o endereço exige confirmar o ponto no mapa de novo (ADR-0048 item 3; a confirmação é da fatia 4).
            (Latitude, Longitude) = (null, null);
        }

        DeliveryFeeMode = changes.Delivery.FeeMode;
        PricePerKm = changes.Delivery.PricePerKm;
        MinimumDeliveryFee = changes.Delivery.MinimumFee;
        MaxDeliveryDistanceKm = changes.Delivery.MaxDistanceKm;

        if (changes.IsPageActive)
        {
            if (!HasCompleteAddress)
            {
                throw BusinessException.Validation("isPageActive", "Para ativar a página, informe o endereço do restaurante (rua, número, bairro, cidade e UF).");
            }

            if (!hasActiveMarmitaSize)
            {
                throw BusinessException.Validation("isPageActive", "Para ativar a página, cadastre pelo menos um tamanho de marmita ativo.");
            }
        }

        IsPageActive = changes.IsPageActive;

        if (!slugChanged && Equals(Snapshot(), before))
        {
            return false;
        }

        UpdatedAt = now;
        Touch();
        return true;
    }

    /// Entrega efetiva pela página. Nesta fatia sempre falso: a cotação por distância (ADR-0048) é da fatia 4, e sem ela a
    /// página não cobra taxa (item 13: não existe taxa de reserva).
    public bool DeliveryAvailableOnPage => OffersDelivery && IsDeliveryEnabled && DistanceQuotesAvailable;

    public const bool DistanceQuotesAvailable = false;

    public PublicStoreAddressDto? PublicAddress =>
        HasCompleteAddress
            ? new PublicStoreAddressDto(AddressStreet!, AddressNumber, AddressComplement, AddressNeighborhood, AddressCity, AddressState)
            : null;

    private object Snapshot() => string.Join('|', new object?[]
    {
        PublicName, IsPageActive, IsAcceptingOrders, IsDeliveryEnabled, OffersDelivery, OffersPickup, ContactPhone, HoursText,
        AddressStreet, AddressNumber, AddressComplement, AddressNeighborhood, AddressCity, AddressState, AddressZipCode,
        Latitude, Longitude, DeliveryFeeMode, PricePerKm, MinimumDeliveryFee, MaxDeliveryDistanceKm,
    }.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)));

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? Digits(string? text) =>
        Clean(text) is { } value ? new string(value.Where(char.IsAsciiDigit).ToArray()) is { Length: > 0 } digits ? digits : null : null;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd();
}

internal sealed record StorefrontChanges(
    string PublicName,
    bool IsPageActive,
    bool IsAcceptingOrders,
    bool IsDeliveryEnabled,
    bool OffersDelivery,
    bool OffersPickup,
    string? ContactPhone,
    string? HoursText,
    StorefrontAddressDto Address,
    DeliveryRule Delivery);

internal sealed record DeliveryRule(DeliveryFeeMode FeeMode, Money? PricePerKm, Money MinimumFee, decimal? MaxDistanceKm);

/// Linha de `store_slugs` (ADR-0045 item 4). Tabela de plataforma: leitura aberta, escrita só no tenant da sessão.
internal sealed class StoreSlug : ITenantOwned
{
    private StoreSlug()
    {
    }

    public string Slug { get; private set; } = null!;
    public Guid TenantId { get; private set; }
    public bool IsCurrent { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ReplacedAt { get; private set; }
    public DateTimeOffset? BlockedUntil { get; private set; }
}

/// Regras do slug (ADR-0044 itens 12 a 16). A lista de reservados e a de palavras ofensivas vivem aqui e são cobertas por teste.
internal static class SlugRules
{
    public const int MinLength = 3;
    public const int MaxLength = 40;

    /// Slugs antigos guardados por tenant (item 15).
    public const int MaxPrevious = 5;

    /// Item 16: rotas do sistema e do marketplace futuro. Igual à lista da migration 0004 (05 §5.17).
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
    {
        "api", "app", "admin", "www", "web", "static", "assets", "_next", "cdn", "img", "login", "entrar", "sair", "cadastro",
        "conta", "minha-conta", "restaurantes", "restaurante", "cidades", "cidade", "categorias", "perto-de-mim", "buscar",
        "busca", "pedido", "pedidos", "p", "r", "s", "ajuda", "suporte", "contato", "termos", "privacidade", "lgpd", "blog",
        "sobre", "precos", "planos", "parceiros", "almoseis", "health", "status", "webhooks", "sitemap.xml", "robots.txt",
        "favicon.ico", ".well-known",
    };

    /// Palavras ofensivas: recusadas como trecho inteiro do slug (entre hífens), para não barrar nomes como "Pica-Pau".
    public static readonly IReadOnlySet<string> Offensive = new HashSet<string>(StringComparer.Ordinal)
    {
        "porra", "caralho", "merda", "bosta", "buceta", "boceta", "puta", "puto", "putaria", "foda", "fodase", "fdp",
        "arrombado", "arrombada", "viado", "cuzao", "piroca", "xoxota", "punheta", "vagabunda", "vadia", "corno", "otario",
        "pqp", "vsf", "vtnc", "nazista", "nazi",
    };

    public static string Slugify(string text)
    {
        var decomposed = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var dash = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
            {
                builder.Append(c);
                dash = false;
            }
            else if (!dash && builder.Length > 0)
            {
                builder.Append('-');
                dash = true;
            }
        }

        return Fit(builder.ToString(), MaxLength);
    }

    /// Mensagem do erro, ou nulo se o slug é aceitável.
    public static string? Problem(string slug)
    {
        if (slug.Length is < MinLength or > MaxLength)
        {
            return $"Use de {MinLength} a {MaxLength} caracteres.";
        }

        if (slug.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
            || slug.StartsWith('-') || slug.EndsWith('-') || slug.Contains("--", StringComparison.Ordinal))
        {
            return "Use só letras minúsculas sem acento, números e hífen (sem hífen no começo, no fim ou repetido).";
        }

        if (slug.All(c => char.IsAsciiDigit(c) || c == '-'))
        {
            return "O endereço não pode ser só números.";
        }

        if (Reserved.Contains(slug))
        {
            return "Este endereço é reservado. Escolha outro.";
        }

        if (slug.Split('-').Any(Offensive.Contains))
        {
            return "Escolha outro endereço.";
        }

        return null;
    }

    /// Candidatos do autocadastro, em ordem (itens 12 a 14): o nome; inválido ou curto → nome + cidade; colisão → `-{cidade}`,
    /// depois `-2`, `-3`… O primeiro livre vence (o INSERT com `ON CONFLICT DO NOTHING` decide).
    public static IEnumerable<string> Candidates(string name, string city)
    {
        var citySlug = Slugify(city);
        var baseSlug = Slugify(name);
        if (Problem(baseSlug) is not null)
        {
            baseSlug = Fit($"{baseSlug}-{citySlug}", MaxLength);
        }

        if (Problem(baseSlug) is not null)
        {
            baseSlug = "loja";
        }

        yield return baseSlug;
        var withCity = Fit($"{baseSlug}-{citySlug}", MaxLength);
        if (withCity != baseSlug && Problem(withCity) is null)
        {
            yield return withCity;
        }

        for (var n = 2; n < 10_000; n++)
        {
            var suffix = "-" + n.ToString(CultureInfo.InvariantCulture);
            yield return Fit(baseSlug, MaxLength - suffix.Length) + suffix;
        }
    }

    private static string Fit(string slug, int max) => (slug.Length <= max ? slug : slug[..max]).Trim('-');
}
