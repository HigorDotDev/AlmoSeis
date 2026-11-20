using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;

namespace AlmoSeis.Modules.Storefront.Contracts;

/// Estado da página, calculado no servidor a cada requisição (ADR-0044 item 6).
public enum StoreStatus
{
    /// Página ativa + expediente aberto + não pausada + assinatura com escrita.
    Open,

    /// Sem expediente aberto: "Fechado agora", sem cardápio.
    Closed,

    /// `isAcceptingOrders = false`: "Não estamos recebendo pedidos pela página agora".
    Paused,

    /// Página inativa ou assinatura sem escrita. O motivo nunca é exposto (ADR-0045 item 16).
    Unavailable,
}

/// ADR-0048 item 1. `DISTANCE`: rota × preço por km com mínimo; `FIXED`: a taxa padrão do ADR-0021, sem mapa.
public enum DeliveryFeeMode
{
    Distance,
    Fixed,
}

/// O que falta para a página oferecer entrega (ADR-0048 item 3), na ordem em que o app mostra.
public enum PageDeliveryRequirement
{
    /// `offersDelivery` ou `isDeliveryEnabled` desligado.
    DeliveryOff,

    /// Modo `DISTANCE` sem preço por km.
    PricePerKm,

    /// Modo `DISTANCE` sem o ponto do restaurante confirmado no mapa.
    ConfirmedLocation,

    /// Modo `DISTANCE` sem provedor de mapas configurado na API (`Maps:Provider = None`).
    MapsProvider,
}

// ---------- Restaurante: GET/PUT /storefront (06 §6.18) ----------

public sealed record StorefrontAddressDto(
    string? Street, string? Number, string? Complement, string? Neighborhood, string? City, string? State, string? ZipCode);

/// Ponto do restaurante (ADR-0048 item 3), com 6 casas. No GET: o ponto geocodificado ao salvar o endereço
/// (`isConfirmed: false`) ou o confirmado/arrastado pelo proprietário (`isConfirmed: true`). No PUT: o ponto que o app
/// mostrou no mapa; nulo mantém o atual. Mudar o endereço descarta o ponto enviado e geocodifica de novo.
public sealed record StorefrontLocationDto(decimal Latitude, decimal Longitude, bool IsConfirmed);

/// Situação da entrega pela página, para o app explicar o que falta ("Confirme o ponto do restaurante no mapa").
public sealed record PageDeliveryStatusDto(bool Available, IReadOnlyList<PageDeliveryRequirement> Missing);

/// Regra da taxa da página (ADR-0048). `maxDistanceKm` com uma casa ("6.0"), como a distância mostrada ao cliente.
public sealed record StorefrontDeliveryDto(DeliveryFeeMode FeeMode, Money? PricePerKm, Money MinimumFee, string? MaxDistanceKm);

public sealed record StorefrontDto(
    int Version,
    string Slug,
    IReadOnlyList<string> PreviousSlugs,
    string PublicName,
    bool IsPageActive,
    bool IsAcceptingOrders,
    bool IsDeliveryEnabled,
    bool OffersDelivery,
    bool OffersPickup,
    string? ContactPhone,
    string? HoursText,
    StorefrontAddressDto Address,
    StorefrontLocationDto? Location,
    StorefrontDeliveryDto Delivery,
    PageDeliveryStatusDto PageDelivery,
    StoreStatus Status,
    DateTimeOffset UpdatedAt) : IVersionedDto;

public sealed record GetStorefrontQuery;

/// PUT /storefront [M]: substitui a configuração inteira. Mudar o slug guarda o anterior (redireciona, ADR-0044 item 15).
public sealed record UpdateStorefrontRequest(
    string Slug,
    string PublicName,
    bool IsPageActive,
    bool IsAcceptingOrders,
    bool IsDeliveryEnabled,
    bool OffersDelivery,
    bool OffersPickup,
    string? ContactPhone,
    string? HoursText,
    StorefrontAddressDto Address,
    StorefrontDeliveryDto Delivery,
    StorefrontLocationDto? Location = null);

public sealed record UpdateStorefrontCommand(UpdateStorefrontRequest Request);

// ---------- Público: GET /public/stores/{slug} (06 §6.19) ----------

public sealed record PublicStoreAddressDto(string Street, string? Number, string? Complement, string? Neighborhood, string? City, string? State);

/// Regra da taxa em dados, para a página escrever "R$ 0,80/km, mínimo R$ 3,00, até 6 km" (`DISTANCE`) ou "Entrega
/// R$ 5,00" (`FIXED`). Nula quando a página não oferece entrega.
public sealed record PublicDeliveryRuleDto(DeliveryFeeMode FeeMode, Money? PricePerKm, Money? MinimumFee, string? MaxDistanceKm, Money? FixedFee);

/// Aviso de privacidade (ADR-0052 item 1): o restaurante é o controlador e a AlmoSeis, a operadora. O texto é da página
/// (sob revisão jurídica); a API dá os dados variáveis. `thirdParties`: `ANTI_BOT` (Cloudflare Turnstile), `MAPS`
/// (geocodificação e rota num provedor externo, com a entrega por distância ligada) e `PAYMENTS` (Fase 2).
public sealed record PublicPrivacyDto(string ControllerName, string? ControllerPhone, string OperatorName, IReadOnlyList<string> ThirdParties);

public sealed record PublicMarmitaSizeDto(Guid Id, string Name, Money Price);

/// Prato ou acompanhamento do cardápio do dia. `isAvailable = false` aparece desabilitado ("Acabou").
public sealed record PublicDishDto(Guid ProductId, string Name, string? Description, string? ImageUrl, bool IsAvailable);

/// Adicional ou bebida com preço (do catálogo, ADR-0020).
public sealed record PublicPricedProductDto(Guid ProductId, string Name, string? Description, string? ImageUrl, Money Price, bool IsAvailable);

public sealed record PublicMenuDto(
    IReadOnlyList<PublicMarmitaSizeDto> MarmitaSizes,
    IReadOnlyList<PublicDishDto> MainDishes,
    IReadOnlyList<PublicDishDto> SideDishes,
    IReadOnlyList<PublicPricedProductDto> Addons,
    IReadOnlyList<PublicPricedProductDto> Beverages);

/// Limites do pedido da página (ADR-0044 item 4, ADR-0045 item 13).
public sealed record PublicOrderLimitsDto(int MaxMarmitas, int MaxLines, int MaxQuantityPerLine, int NotesMaxLength);

public sealed record PublicStoreDto(
    string Slug,
    StoreStatus Status,
    string Name,
    PublicStoreAddressDto? Address,
    string? ContactPhone,
    string? HoursText,
    bool PickupAvailable,
    bool DeliveryAvailable,
    PublicDeliveryRuleDto? DeliveryRule,
    PublicPrivacyDto? Privacy,
    DateOnly? BusinessDate,
    PublicMenuDto? Menu,
    PublicOrderLimitsDto Limits);

public sealed record GetPublicStoreQuery(string Slug);

// ---------- Público: POST /public/stores/{slug}/delivery-quotes (06 §6.19, ADR-0048) ----------

/// Endereço do cliente. `city` nula = a cidade do restaurante; a UF é sempre a do restaurante (entrega local).
/// `complement` e `reference` não entram na geocodificação nem no hash do endereço.
public sealed record PublicAddressInput(
    string Street, string? Number, string? Complement, string? Neighborhood, string? City, string? ZipCode, string? Reference);

public sealed record DeliveryQuoteRequest(PublicAddressInput Address);

/// `turnstileToken` do header `X-Turnstile-Token` (ADR-0045 item 12); `clientIp` só para o Turnstile.
public sealed record DeliveryQuoteCommand(DeliveryQuoteRequest Request, string? TurnstileToken = null, string? ClientIp = null);

/// Cotação válida por 15 min (ADR-0048 item 8). `distanceKm` com uma casa (`"4.5"`), nula no modo `FIXED`.
public sealed record DeliveryQuoteDto(Guid QuoteId, string FormattedAddress, string? DistanceKm, Money DeliveryFee, DateTimeOffset ExpiresAt);

/// Taxa conferida no envio do pedido (ADR-0048 itens 10 e 20): o snapshot que o pedido grava.
public sealed record RedeemedDeliveryQuote(
    Guid QuoteId, DeliveryFeeMode FeeMode, Money DeliveryFee, int? DistanceMeters, Money? PricePerKm, Money? MinimumFee);

/// Envio do pedido com entrega pela página (usada pelo Ordering). Cotação válida, do mesmo endereço e com a
/// configuração de quando foi feita → a taxa dela. Vencida, de outro endereço ou com a configuração mudada → recalcula:
/// mesma taxa → segue; taxa diferente → `409 DeliveryQuoteChanged` com a cotação nova em `quote` (já gravada).
/// Sem cotação, ou cotação inexistente/de outro tenant → `422` em `deliveryQuoteId`.
public interface IDeliveryQuoteRedeemer
{
    Task<RedeemedDeliveryQuote> RedeemAsync(Guid? quoteId, PublicAddressInput address, CancellationToken cancellationToken);
}

// ---------- Portas ----------

public static class StoreErrors
{
    /// `409 StoreUnavailable` com o estado (`CLOSED`, `PAUSED` ou `UNAVAILABLE`), nunca o motivo (ADR-0045 item 16).
    public static AlmoSeis.SharedKernel.Errors.BusinessException Unavailable(StoreStatus status) =>
        new(AlmoSeis.SharedKernel.Errors.ErrorCodes.StoreUnavailable,
            extensions: new Dictionary<string, object?> { ["storeStatus"] = status.ToString().ToUpperInvariant() });
}

/// Limites do pedido da página (ADR-0044 item 4; decisões do usuário de 2026-11-20).
public static class PublicOrderLimits
{
    public const int MaxMarmitas = 20;
    public const int MaxLines = 30;
    public const int MaxQuantityPerLine = 20;
    public const int NotesMaxLength = 200;

    public static readonly PublicOrderLimitsDto Dto = new(MaxMarmitas, MaxLines, MaxQuantityPerLine, NotesMaxLength);
}

/// Situação da página para o envio e o acompanhamento (usada pelo Ordering), no tenant corrente.
public sealed record StorefrontOrderingContext(
    StoreStatus Status,
    string Slug,
    string PublicName,
    string? ContactPhone,
    PublicStoreAddressDto? Address,
    bool PickupAvailable,
    bool DeliveryAvailable,
    DateOnly? BusinessDate);

public interface IStorefrontGate
{
    Task<StorefrontOrderingContext> GetAsync(CancellationToken cancellationToken);

    /// Cardápio da página na data (o mesmo do `GET /public/stores/{slug}`): o envio confere o carrinho contra ele.
    Task<PublicMenuDto?> GetMenuAsync(DateOnly businessDate, CancellationToken cancellationToken);
}

/// Slug → tenant, antes de o tenant ser conhecido (ADR-0045 item 4). `CurrentSlug` diferente de `Slug` = slug antigo
/// (redireciona).
public sealed record StoreSlugResolution(Guid TenantId, string Slug, string CurrentSlug);

public interface IStoreSlugResolver
{
    Task<StoreSlugResolution?> ResolveAsync(string slug, CancellationToken cancellationToken);
}
