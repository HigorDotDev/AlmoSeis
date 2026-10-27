using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;

namespace AlmoSeis.Modules.Ordering.Contracts;

public enum OrderType
{
    Marmita,
    Pf,
    Kilo,
}

public enum OrderOrigin
{
    App,
}

public enum FulfillmentType
{
    Delivery,
    Pickup,
    DineIn,
}

public enum ProductionStatus
{
    Open,
    InProgress,
    Completed,
}

public enum CancellationStatus
{
    Active,
    Canceled,
}

public enum OrderItemKind
{
    Marmita,
    Pf,
    Beverage,
    Addon,
}

public enum ComponentRole
{
    MainDish,
    SideDish,
}

public enum LineStatus
{
    Active,
    Removed,
}

public enum OrderChangeKind
{
    ItemAdded,
    ItemRemoved,
    QuantityChanged,
    ItemNotesChanged,
    OrderNotesChanged,
    WeighingAdded,
    WeighingRemoved,
    FulfillmentChanged,
    OrderCanceled,
}

// ---------- DTOs (06 §6.8 e §6.9) ----------

/// Endereço de entrega: snapshot no pedido e entrada de `deliveryAddress`.
public sealed record DeliveryAddressDto(string Street, string? Number, string? Complement, string? Neighborhood, string? Reference);

/// `id` nulo: nome avulso sem cadastro (ex.: "Mesa 3" num pedido por quilo).
public sealed record OrderCustomerDto(Guid? Id, string Name, string? PhoneNumber);

public sealed record OrderItemComponentDto(Guid ProductId, ComponentRole Role, string Name);

public sealed record OrderAddonDto(
    Guid Id,
    OrderItemKind ItemKind,
    Guid ProductId,
    string Name,
    int Quantity,
    Money UnitPrice,
    Money LineTotal,
    bool RequiresPreparation,
    string? Notes,
    DateTimeOffset CreatedAt,
    AuthorDto CreatedBy);

public sealed record OrderItemDto(
    Guid Id,
    OrderItemKind ItemKind,
    Guid? MarmitaSizeId,
    Guid? ProductId,
    string Name,
    string? Composition,
    IReadOnlyList<OrderItemComponentDto> Components,
    int Quantity,
    Money UnitPrice,
    Money LineTotal,
    bool RequiresPreparation,
    string? Notes,
    IReadOnlyList<OrderAddonDto> Addons,
    DateTimeOffset CreatedAt,
    AuthorDto CreatedBy);

public sealed record WeighingDto(Guid Id, int WeightGrams, Money PricePerKg, Money Amount, DateTimeOffset CreatedAt, AuthorDto CreatedBy);

/// Pagamento resumido dentro do pedido. Vem do módulo Payments pela porta IOrderPaymentsReader.
public sealed record OrderPaymentDto(
    Guid Id, Money Amount, string PaymentMethod, string? Label, string Status, DateTimeOffset PaidAt, DateOnly BusinessDate);

public sealed record CancellationDto(DateTimeOffset CanceledAt, AuthorDto CanceledBy, string Reason);

public sealed record OrderDto(
    Guid Id,
    int Version,
    int OrderNumber,
    Guid BusinessDayId,
    DateOnly BusinessDate,
    OrderType OrderType,
    OrderOrigin Origin,
    FulfillmentType FulfillmentType,
    OrderCustomerDto? Customer,
    DeliveryAddressDto? DeliveryAddress,
    string? TableLabel,
    string? TabNumber,
    string? Notes,
    Money DeliveryFee,
    Money ItemsTotal,
    Money WeighingsTotal,
    Money TotalAmount,
    Money PaidAmount,
    Money Balance,
    bool IsPaid,
    ProductionStatus? ProductionStatus,
    DateTimeOffset? ProductionStatusChangedAt,
    CancellationStatus CancellationStatus,
    CancellationDto? Cancellation,
    IReadOnlyList<OrderItemDto> Items,
    IReadOnlyList<WeighingDto> Weighings,
    IReadOnlyList<OrderPaymentDto> Payments,
    int PendingAlertCount,
    DateTimeOffset CreatedAt,
    AuthorDto CreatedBy,
    DateTimeOffset UpdatedAt) : IVersionedDto, IHasResourceId
{
    Guid IHasResourceId.ResourceId => Id;
}

public sealed record OrderSummaryDto(
    Guid Id,
    int Version,
    int OrderNumber,
    DateOnly BusinessDate,
    OrderType OrderType,
    FulfillmentType FulfillmentType,
    string? CustomerName,
    string? TableLabel,
    string? TabNumber,
    Money TotalAmount,
    Money PaidAmount,
    Money Balance,
    bool IsPaid,
    ProductionStatus? ProductionStatus,
    CancellationStatus CancellationStatus,
    int ItemCount,
    int PendingAlertCount,
    DateTimeOffset CreatedAt) : IVersionedDto;

// ---------- Pedidos: comandos e consultas ----------

public sealed record AddonInput(Guid ProductId, int Quantity, string? Notes);

/// Item de `POST /orders` e de `POST /orders/{id}/items`. Preço e nomes nunca vêm do corpo (07 §7.8).
public sealed record OrderItemInput(
    OrderItemKind ItemKind,
    Guid? ParentOrderItemId,
    Guid? MarmitaSizeId,
    Guid? ProductId,
    int Quantity,
    IReadOnlyList<Guid>? MainDishProductIds,
    IReadOnlyList<Guid>? SideDishProductIds,
    string? Notes,
    IReadOnlyList<AddonInput>? Addons);

public sealed record WeighingInput(int WeightGrams);

public sealed record CreateOrderCommand(
    OrderType OrderType,
    FulfillmentType FulfillmentType,
    Guid? CustomerId,
    string? CustomerName,
    string? CustomerPhone,
    bool SaveCustomer,
    Guid? CustomerAddressId,
    DeliveryAddressDto? DeliveryAddress,
    Money? DeliveryFee,
    string? TableLabel,
    string? TabNumber,
    string? Notes,
    IReadOnlyList<OrderItemInput> Items,
    IReadOnlyList<WeighingInput>? Weighings);

public sealed record UpdateOrderRequest(
    Optional<string?> CustomerName,
    Optional<string?> CustomerPhone,
    Optional<FulfillmentType> FulfillmentType,
    Optional<DeliveryAddressDto?> DeliveryAddress,
    Optional<Money> DeliveryFee,
    Optional<string?> TableLabel,
    Optional<string?> TabNumber,
    Optional<string?> Notes);

public sealed record UpdateOrderCommand(Guid OrderId, UpdateOrderRequest Changes);

public sealed record AddOrderItemsRequest(IReadOnlyList<OrderItemInput> Items);

public sealed record AddOrderItemsCommand(Guid OrderId, AddOrderItemsRequest Request);

public sealed record UpdateOrderItemRequest(Optional<int> Quantity, Optional<string?> Notes);

public sealed record UpdateOrderItemCommand(Guid OrderId, Guid ItemId, UpdateOrderItemRequest Changes);

public sealed record RemoveOrderItemCommand(Guid OrderId, Guid ItemId);

public sealed record AddWeighingsRequest(IReadOnlyList<WeighingInput> Weighings);

public sealed record AddWeighingsCommand(Guid OrderId, AddWeighingsRequest Request);

public sealed record RemoveWeighingCommand(Guid OrderId, Guid WeighingId);

public sealed record ChangeProductionStatusRequest(ProductionStatus ProductionStatus);

public sealed record ChangeProductionStatusCommand(Guid OrderId, ChangeProductionStatusRequest Request);

public sealed record CancelOrderRequest(string Reason);

public sealed record CancelOrderCommand(Guid OrderId, CancelOrderRequest Request);

public sealed record GetOrderQuery(Guid OrderId);

public sealed record ListOrdersQuery(
    Guid? BusinessDayId,
    bool AllDays,
    OrderType? OrderType,
    bool? IsPaid,
    CancellationStatus? CancellationStatus,
    ProductionStatus? ProductionStatus,
    Guid? CustomerId,
    string? Search,
    int Limit,
    string? Cursor);

// ---------- Alertas, produção e histórico ----------

public sealed record OrderAlertDto(
    Guid EventId,
    OrderChangeKind ChangeKind,
    bool AffectsProduction,
    int OrderVersion,
    DateTimeOffset OccurredAt,
    AuthorDto Actor,
    string Summary,
    System.Text.Json.JsonElement Details);

public sealed record GetOrderAlertsQuery(Guid OrderId);

public sealed record AcknowledgeAlertsRequest(IReadOnlyList<Guid> EventIds);

public sealed record AcknowledgeAlertsCommand(Guid OrderId, AcknowledgeAlertsRequest Request);

public sealed record ProductionAddonDto(Guid Id, string Name, int Quantity, string? Notes);

public sealed record ProductionItemDto(
    Guid Id, string Name, string? Composition, int Quantity, string? Notes, bool RequiresPreparation, IReadOnlyList<ProductionAddonDto> Addons);

public sealed record ProductionOrderDto(
    Guid Id,
    int Version,
    int OrderNumber,
    OrderType OrderType,
    FulfillmentType FulfillmentType,
    string? Label,
    string? Notes,
    bool IsPaid,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ProductionItemDto> Items,
    int PendingAlertCount);

/// `groups` sempre com as três chaves OPEN, IN_PROGRESS e COMPLETED (06 §6.9).
public sealed record ProductionBoardDto(Guid BusinessDayId, IReadOnlyDictionary<string, IReadOnlyList<ProductionOrderDto>> Groups);

public sealed record GetProductionBoardQuery;

public sealed record OrderHistoryEntryDto(
    DateTimeOffset OccurredAt,
    string OperationType,
    string Origin,
    AuthorDto? Actor,
    System.Text.Json.JsonElement? Before,
    System.Text.Json.JsonElement? After);

public sealed record GetOrderHistoryQuery(Guid OrderId, int Limit, string? Cursor);

// ---------- Clientes ----------

public sealed record CustomerAddressDto(
    Guid Id, string? Label, string Street, string? Number, string? Complement, string? Neighborhood, string? Reference, bool IsDefault);

public sealed record CustomerDto(
    Guid Id,
    int Version,
    string Name,
    string? PhoneNumber,
    string? Notes,
    IReadOnlyList<CustomerAddressDto> Addresses,
    Money PendingAmount,
    int PendingOrderCount,
    DateTimeOffset? AnonymizedAt,
    DateTimeOffset CreatedAt) : IVersionedDto;

public sealed record CustomerAddressInput(
    string? Label, string Street, string? Number, string? Complement, string? Neighborhood, string? Reference, bool IsDefault);

public sealed record CreateCustomerCommand(string Name, string? PhoneNumber, string? Notes, IReadOnlyList<CustomerAddressInput>? Addresses);

public sealed record UpdateCustomerRequest(Optional<string> Name, Optional<string?> PhoneNumber, Optional<string?> Notes);

public sealed record UpdateCustomerCommand(Guid CustomerId, UpdateCustomerRequest Changes);

public sealed record AddCustomerAddressCommand(Guid CustomerId, CustomerAddressInput Address);

public sealed record UpdateCustomerAddressRequest(
    Optional<string?> Label,
    Optional<string> Street,
    Optional<string?> Number,
    Optional<string?> Complement,
    Optional<string?> Neighborhood,
    Optional<string?> Reference,
    Optional<bool> IsDefault);

public sealed record UpdateCustomerAddressCommand(Guid CustomerId, Guid AddressId, UpdateCustomerAddressRequest Changes);

public sealed record RemoveCustomerAddressCommand(Guid CustomerId, Guid AddressId);

public sealed record AnonymizeCustomerRequest(string Reason);

public sealed record AnonymizeCustomerCommand(Guid CustomerId, AnonymizeCustomerRequest Request);

public sealed record GetCustomerQuery(Guid CustomerId);

public sealed record SearchCustomersQuery(string? Search, int Limit, string? Cursor);

public sealed record CustomerRefDto(Guid Id, string Name);

public sealed record PendingOrderDto(
    Guid Id, int OrderNumber, DateOnly BusinessDate, OrderType OrderType, Money TotalAmount, Money PaidAmount, Money Balance, int Version);

public sealed record CustomerPendingOrdersDto(CustomerRefDto Customer, Money TotalPending, IReadOnlyList<PendingOrderDto> Orders);

public sealed record GetCustomerPendingOrdersQuery(Guid CustomerId);

// ---------- Portas ----------

/// Pagamentos do pedido para o DTO. Implementada pelo Payments; até lá, lista vazia.
public interface IOrderPaymentsReader
{
    Task<IReadOnlyList<OrderPaymentDto>> ListAsync(Guid orderId, CancellationToken cancellationToken);
}
