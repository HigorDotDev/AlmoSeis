using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;

namespace AlmoSeis.Modules.Ordering.Contracts;

// ---------- Página de pedidos: envio e acompanhamento (06 §6.19, ADR-0044/0045/0047) ----------

/// Forma de pagamento prevista pelo cliente da página (ADR-0044 item 19). Só informação: não cria pagamento.
public enum ExpectedPaymentMethod
{
    Cash,
    Pix,
    Card,
}

/// Risco do pedido da página (ADR-0045 item 12): o Turnstile não respondeu e o pedido foi aceito assim mesmo.
public enum WebOrderRiskFlag
{
    CaptchaUnavailable,
}

/// Endereço digitado pelo cliente da página (ADR-0044 item 18). `city` e `zipCode` entram com a cotação (fatia 4).
public sealed record PublicDeliveryAddressInput(
    string Street, string? Number, string? Complement, string? Neighborhood, string? City, string? ZipCode, string? Reference);

public sealed record PublicAddonInput(Guid ProductId, int Quantity);

/// Linha do carrinho: `MARMITA` (tamanho, pratos, acompanhamentos, adicionais) ou `BEVERAGE`. Sem preço: o servidor
/// recalcula tudo (ADR-0044 item 21).
public sealed record PublicOrderItemInput(
    OrderItemKind ItemKind,
    Guid? MarmitaSizeId,
    Guid? ProductId,
    int Quantity,
    IReadOnlyList<Guid>? MainDishProductIds,
    IReadOnlyList<Guid>? SideDishProductIds,
    IReadOnlyList<PublicAddonInput>? Addons,
    string? Notes);

/// POST /public/stores/{slug}/orders [I] (06 §6.19). `expectedTotal` é o total que o cliente viu; diferente do recalculado
/// → `409 CartChanged`. `website` é o campo-isca: preenchido → `400` sem detalhes (ADR-0045 item 13).
public sealed record PlaceWebOrderRequest(
    FulfillmentType FulfillmentType,
    string CustomerName,
    string CustomerPhone,
    PublicDeliveryAddressInput? DeliveryAddress,
    Guid? DeliveryQuoteId,
    ExpectedPaymentMethod ExpectedPaymentMethod,
    Money? ChangeFor,
    string? Notes,
    IReadOnlyList<PublicOrderItemInput> Items,
    Money ExpectedTotal,
    string? Website);

/// `turnstileToken` vem do header `X-Turnstile-Token` (fora do hash de idempotência: é de uso único). `clientIp` só para o
/// Turnstile e para o hash do IP; nunca é gravado em claro.
/// `preparation`: o que a rota já apurou fora da transação (Turnstile e cotação, `PrepareWebOrderQuery`); nulo → o handler
/// verifica na hora (testes e chamadas internas).
public sealed record PlaceWebOrderCommand(
    PlaceWebOrderRequest Request, string? TurnstileToken, string? ClientIp, WebOrderPreparation? Preparation = null);

/// Pré-checagens do envio pela página **fora** da transação da idempotência e antes dos advisory locks (07 §7.4): o
/// Turnstile e a conferência da cotação de entrega, que pode recalcular no provedor de mapas. Os erros da cotação ficam
/// guardados e o handler os lança na posição deles na ordem das checagens (06 §6.19). Campo nulo = não verificado
/// (página fechada, retirada, campo-isca): o handler verifica na hora, se chegar lá.
public sealed record WebOrderPreparation(
    AlmoSeis.Infrastructure.Security.TurnstileOutcome? Captcha,
    AlmoSeis.Modules.Storefront.Contracts.RedeemedDeliveryQuote? Quote,
    AlmoSeis.SharedKernel.Errors.BusinessException? QuoteError)
{
    public static readonly WebOrderPreparation None = new(null, null, null);

    public bool QuoteChecked => Quote is not null || QuoteError is not null;
}

public sealed record PrepareWebOrderQuery(PlaceWebOrderRequest Request, string? TurnstileToken, string? ClientIp);

/// Resposta do envio. Sem nome, telefone nem endereço: o corpo fica guardado no registro de idempotência (ADR-0052 item 5),
/// **sem** o `trackingToken`, que é recalculado no reenvio (`TrackingTokenReplaySource`, ADR-0047 item 2).
public sealed record PlacedWebOrderDto(
    string TrackingToken,
    DateTimeOffset TrackingExpiresAt,
    AcceptanceStatus AcceptanceStatus,
    DateTimeOffset AcceptanceDeadline,
    Money ItemsTotal,
    Money DeliveryFee,
    Money TotalAmount) : IHasResourceId
{
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid OrderId { get; init; }

    Guid IHasResourceId.ResourceId => OrderId;
}

/// Linha recalculada do `409 CartChanged` (na ordem de `items` do envio). Indisponível → `available: false` e sem valores.
public sealed record CartLineDto(int Index, string? Name, bool Available, Money? UnitPrice, Money? LineTotal);

/// Extensão `cart` do `409 CartChanged`: o carrinho como o servidor o calcula agora (ADR-0044 item 21).
public sealed record CartQuoteDto(
    IReadOnlyList<CartLineDto> Items,
    IReadOnlyList<Guid> UnavailableProductIds,
    Money ItemsTotal,
    Money DeliveryFee,
    Money TotalAmount);

/// Status mostrado ao cliente (ADR-0047 item 7), derivado das dimensões do pedido.
public enum TrackingStatus
{
    AwaitingPayment,
    AwaitingAcceptance,
    Rejected,
    Expired,
    Withdrawn,
    Confirmed,
    InPreparation,
    ReadyForDelivery,
    ReadyForPickup,
    OutForDelivery,
    Delivered,
    PickedUp,
    Canceled,
}

/// Passos da linha do tempo do acompanhamento, com o horário de cada um.
public enum TrackingStep
{
    Submitted,
    Accepted,
    InPreparation,
    Ready,
    OutForDelivery,
    Delivered,
    PickedUp,
    Rejected,
    Expired,
    Withdrawn,
    Canceled,
}

public sealed record TrackingStepDto(TrackingStep Step, DateTimeOffset At);

public sealed record TrackingStoreAddressDto(string Street, string? Number, string? Complement, string? Neighborhood, string? City, string? State);

public sealed record TrackingStoreDto(string Slug, string Name, string? ContactPhone, TrackingStoreAddressDto? Address);

/// `message` nula em `SUSPECTED_PRANK` (o texto não é mostrado ao cliente, ADR-0046 item 12).
public sealed record TrackingRejectionDto(RejectionReason ReasonCode, string? Message);

public sealed record TrackingAddonDto(string Name, int Quantity, Money UnitPrice, Money LineTotal);

public sealed record TrackingItemDto(
    OrderItemKind ItemKind, string Name, string? Composition, int Quantity, Money UnitPrice, Money LineTotal, string? Notes,
    IReadOnlyList<TrackingAddonDto> Addons);

/// Entrega: só rua e número (ADR-0047 item 5); nunca complemento nem referência.
public sealed record TrackingAddressDto(string Street, string? Number);

/// GET /public/tracking/{token} (06 §6.19, ADR-0047 itens 5 a 7). Nunca leva telefone, sobrenome, complemento, referência,
/// funcionários nem outros pedidos. `version` vai no `ETag` (consulta a cada 20 s com `If-None-Match` → `304`).
public sealed record WebOrderTrackingDto(
    int Version,
    TrackingStoreDto Store,
    int? OrderNumber,
    DateTimeOffset SubmittedAt,
    TrackingStatus Status,
    AcceptanceStatus AcceptanceStatus,
    DateTimeOffset? AcceptanceDeadline,
    int? EstimatedReadyMinutes,
    bool CanWithdraw,
    IReadOnlyList<TrackingStepDto> Timeline,
    TrackingRejectionDto? Rejection,
    FulfillmentType FulfillmentType,
    string CustomerFirstName,
    TrackingAddressDto? DeliveryAddress,
    IReadOnlyList<TrackingItemDto> Items,
    Money ItemsTotal,
    Money DeliveryFee,
    string? DeliveryDistanceKm,
    Money TotalAmount,
    ExpectedPaymentMethod? ExpectedPaymentMethod,
    Money? ChangeFor,
    bool ChangedByStore,
    DateTimeOffset TrackingExpiresAt)
{
    /// `ETag` do acompanhamento (ADR-0047 item 8): a versão do pedido não basta, porque o `status` também muda sozinho
    /// (prazo de aceite vencido aparece como `EXPIRED` antes de o job gravar) e o bloco `store` vem da configuração da
    /// página. Fora do JSON.
    [System.Text.Json.Serialization.JsonIgnore]
    public string ETag { get; init; } = string.Empty;
}

public sealed record GetWebOrderTrackingQuery(string TrackingToken);

/// POST /public/tracking/{token}/withdraw (ADR-0046 item 17).
public sealed record WithdrawWebOrderByTrackingCommand(string TrackingToken);

/// Token → tenant, antes de o tenant ser conhecido (ADR-0045 item 4). Só o hash é consultado.
public sealed record TrackingTokenResolution(Guid TenantId, DateTimeOffset ExpiresAt);

public interface ITrackingTokenResolver
{
    Task<TrackingTokenResolution?> ResolveAsync(string trackingToken, CancellationToken cancellationToken);
}
