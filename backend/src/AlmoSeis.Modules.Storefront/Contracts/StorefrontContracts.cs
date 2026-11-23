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

/// Seção da página (EP-003 RF-316, Q-317), com o mesmo nome do `orderType` que o carrinho envia: `MARMITA` (módulo
/// `MARMITA`: marmitas do cardápio do dia e bebidas) e `A_LA_CARTE` (módulos `FIXED_MENU` e `DELIVERY`: o cardápio fixo da
/// lanchonete). Com os dois comportamentos, as duas; um pedido é de uma seção só.
public enum StoreSection
{
    Marmita,
    ALaCarte,
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

// ---------- Cardápio fixo da página da lanchonete (EP-003 fatia 5; ADR-0044 complementado, ADR-0057) ----------

/// Só o que **vende no site** e não está pausado, pelo preço de site (ADR-0057 itens 3 e 6). Pausado não aparece (nem
/// desabilitado, ao contrário do "Acabou" da marmita). Listas na ordem do cadastro (`sortOrder`, nome).
public sealed record PublicFixedMenuDto(
    IReadOnlyList<PublicDishCategoryDto> Categories,
    IReadOnlyList<PublicBeverageTypeDto> Beverages,
    IReadOnlyList<PublicComboDto> Combos,
    IReadOnlyList<PublicAddonGroupDto> AddonGroups);

/// Uma aba por categoria com pelo menos um prato na página.
public sealed record PublicDishCategoryDto(Guid Id, string Name, IReadOnlyList<PublicFixedDishDto> Dishes);

/// Prato sem tamanho: `price` (de site) e `sizes: []`. Com tamanho: `price: null` e só os tamanhos que vendem no site e
/// não estão pausados. `addonGroupIds`: grupos de `addonGroups` que o cliente pode escolher para o prato.
public sealed record PublicFixedDishDto(
    Guid Id, string Name, string? Description, string? ImageUrl, bool HasSizes, Money? Price, IReadOnlyList<PublicDishSizeDto> Sizes,
    IReadOnlyList<Guid> AddonGroupIds);

public sealed record PublicDishSizeDto(Guid Id, string Name, Money Price);

/// Tipo de bebida → tamanhos (que vendem no site) → sabores (não pausados). Sem sabor, vende sem escolha.
public sealed record PublicBeverageTypeDto(Guid Id, string Name, IReadOnlyList<PublicBeverageSizeDto> Sizes);

public sealed record PublicBeverageSizeDto(Guid Id, string Name, Money Price, IReadOnlyList<PublicBeverageFlavorDto> Flavors);

/// Acréscimo do sabor: o mesmo do balcão (Q-308).
public sealed record PublicBeverageFlavorDto(Guid Id, string Name, Money ExtraPrice);

/// Combo que vende no site, não pausado e com todos os componentes vendáveis (ADR-0056 item 23, ADR-0057 item 4).
public sealed record PublicComboDto(
    Guid Id, string Name, string? Description, string? ImageUrl, Money Price, IReadOnlyList<PublicComboComponentDto> Components);

/// `DISH`: prato no tamanho fixo ("Batata frita P"), com os grupos de adicionais dele e observação; `flavors: []`.
/// `BEVERAGE`: o sabor é escolhido no pedido (`flavors`, vazio se o tamanho não tem sabor); `addonGroupIds: []`.
public sealed record PublicComboComponentDto(
    Guid Id, AlmoSeis.Modules.Catalog.Contracts.ComboComponentKind Kind, string Name, int Quantity, IReadOnlyList<Guid> AddonGroupIds,
    IReadOnlyList<PublicBeverageFlavorDto> Flavors);

/// Grupo de adicionais com as opções não pausadas; preço do balcão (Q-308).
public sealed record PublicAddonGroupDto(Guid Id, string Name, IReadOnlyList<PublicAddonOptionDto> Options);

public sealed record PublicAddonOptionDto(Guid Id, string Name, Money Price);

/// Limites do pedido da página (ADR-0044 item 4, ADR-0045 item 13; lanchonete: EP-003 Q-318).
public sealed record PublicOrderLimitsDto(
    int MaxMarmitas, int MaxDishesAndCombos, int MaxLines, int MaxQuantityPerLine, int MaxAddonQuantity, int NotesMaxLength);

/// `sections`: o que a página vende (vazio em `UNAVAILABLE`). `menu`: a seção `MARMITA`; `fixedMenu`: a `A_LA_CARTE`.
public sealed record PublicStoreDto(
    string Slug,
    StoreStatus Status,
    string Name,
    IReadOnlyList<StoreSection> Sections,
    PublicStoreAddressDto? Address,
    string? ContactPhone,
    string? HoursText,
    bool PickupAvailable,
    bool DeliveryAvailable,
    PublicDeliveryRuleDto? DeliveryRule,
    PublicPrivacyDto? Privacy,
    DateOnly? BusinessDate,
    PublicMenuDto? Menu,
    PublicFixedMenuDto? FixedMenu,
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

/// Limites do pedido da página (ADR-0044 item 4; decisões do usuário de 2026-11-20). Lanchonete (EP-003 Q-318): pelo
/// menos 1 prato ou combo, até 20 pratos e combos somados e as mesmas 30 linhas.
public static class PublicOrderLimits
{
    public const int MaxMarmitas = 20;
    public const int MaxDishesAndCombos = 20;
    public const int MaxLines = 30;
    public const int MaxQuantityPerLine = 20;

    /// Quantidade de cada adicional de grupo (a mesma do app, 06 §6.9).
    public const int MaxAddonQuantity = 10;
    public const int NotesMaxLength = 200;

    public static readonly PublicOrderLimitsDto Dto = new(MaxMarmitas, MaxDishesAndCombos, MaxLines, MaxQuantityPerLine, MaxAddonQuantity, NotesMaxLength);
}

/// Seções pelos módulos do tenant (ADR-0054 item 13: a regra pergunta pelo módulo, nunca pelo comportamento).
public static class StoreSections
{
    public static IReadOnlyList<StoreSection> For(IReadOnlyCollection<AlmoSeis.SharedKernel.Security.TenantModule> modules)
    {
        var sections = new List<StoreSection>(2);
        if (modules.Contains(AlmoSeis.SharedKernel.Security.TenantModule.Marmita))
        {
            sections.Add(StoreSection.Marmita);
        }

        if (modules.Contains(AlmoSeis.SharedKernel.Security.TenantModule.FixedMenu) && modules.Contains(AlmoSeis.SharedKernel.Security.TenantModule.Delivery))
        {
            sections.Add(StoreSection.ALaCarte);
        }

        return sections;
    }
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
    DateOnly? BusinessDate,
    int SettingsVersion = 0,
    IReadOnlyList<StoreSection>? Sections = null)
{
    /// O que a página vende (sem a informação, só a marmita, como antes do EP-003).
    public IReadOnlyList<StoreSection> OfferedSections => Sections ?? [StoreSection.Marmita];
}

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
