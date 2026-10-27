using System.Text.Json;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;

namespace AlmoSeis.Modules.Ordering.Domain;

/// Pedido (04 §4.7, ADR-0005/0007/0020–0023). Totais derivados são recalculados aqui, nunca recebidos.
internal sealed class Order : AggregateRoot, ITenantOwned, ISyncTracked
{
    private static readonly JsonSerializerOptions DetailsJson = new(JsonSerializerDefaults.Web);

    private readonly List<OrderItem> _items = [];
    private readonly List<WeighingEntry> _weighings = [];
    private readonly List<OrderChangeEvent> _changeEvents = [];

    private Order()
    {
    }

    public Guid TenantId { get; private set; }
    public string SyncEntityType => "ORDER";
    public Guid BusinessDayId { get; private set; }
    public DateOnly BusinessDate { get; private set; }
    public int OrderNumber { get; private set; }
    public OrderType OrderType { get; private set; }
    public OrderOrigin Origin { get; private set; }
    public FulfillmentType FulfillmentType { get; private set; }
    public Guid? CustomerId { get; private set; }
    public string? CustomerName { get; private set; }
    public string? CustomerPhone { get; private set; }
    public DeliveryAddressDto? DeliveryAddress { get; private set; }
    public string? TableLabel { get; private set; }
    public string? TabNumber { get; private set; }
    public string? Notes { get; private set; }
    public Money DeliveryFee { get; private set; }
    public Money ItemsTotal { get; private set; }
    public Money WeighingsTotal { get; private set; }
    public Money TotalAmount { get; private set; }
    public Money PaidAmount { get; private set; }
    public bool IsPaid { get; private set; }
    public ProductionStatus? ProductionStatus { get; private set; }
    public DateTimeOffset? ProductionStatusChangedAt { get; private set; }
    public CancellationStatus CancellationStatus { get; private set; }
    public DateTimeOffset? CanceledAt { get; private set; }
    public Guid? CanceledByEmployeeId { get; private set; }
    public string? CancellationReason { get; private set; }
    public Guid CreatedByEmployeeId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<OrderItem> Items => _items;
    public IReadOnlyList<WeighingEntry> Weighings => _weighings;

    /// Só os eventos criados nesta unidade de trabalho (a coleção não é carregada do banco).
    public IReadOnlyList<OrderChangeEvent> ChangeEvents => _changeEvents;

    public Money Balance => TotalAmount - PaidAmount;

    /// "João Silva", "Mesa 5", "Comanda 47": rótulo do painel da cozinha e do `OrderCreated` (06 §6.9).
    public string? Label => OrderType == OrderType.Marmita
        ? CustomerName
        : TableLabel is not null ? $"Mesa {TableLabel}" : TabNumber is not null ? $"Comanda {TabNumber}" : CustomerName;

    public static Order Create(
        Guid id, Guid tenantId, NewOrder data, Guid businessDayId, DateOnly businessDate, int orderNumber,
        Guid createdBy, Func<Guid> newId, DateTimeOffset now)
    {
        EnsureFulfillment(data.OrderType, data.FulfillmentType);
        if (data.OrderType == OrderType.Marmita && string.IsNullOrWhiteSpace(data.CustomerName))
        {
            throw BusinessException.Validation("customerName", "Informe o nome do cliente da marmita.");
        }

        EnsureDelivery(data.FulfillmentType, data.DeliveryAddress, data.DeliveryFee);
        if (data.Weighings.Count > 0 && data.OrderType != OrderType.Kilo)
        {
            throw BusinessException.Validation("weighings", "Pesagens só existem em pedidos por quilo.");
        }

        var order = new Order
        {
            Id = id,
            TenantId = tenantId,
            BusinessDayId = businessDayId,
            BusinessDate = businessDate,
            OrderNumber = orderNumber,
            OrderType = data.OrderType,
            Origin = OrderOrigin.App,
            FulfillmentType = data.FulfillmentType,
            CustomerId = data.CustomerId,
            CustomerName = data.CustomerName,
            CustomerPhone = data.CustomerPhone,
            DeliveryAddress = data.DeliveryAddress,
            TableLabel = data.TableLabel,
            TabNumber = data.TabNumber,
            Notes = data.Notes,
            DeliveryFee = data.DeliveryFee,
            PaidAmount = Money.Zero,
            ProductionStatus = data.OrderType == OrderType.Kilo ? null : Contracts.ProductionStatus.Open,
            ProductionStatusChangedAt = data.OrderType == OrderType.Kilo ? null : now,
            CancellationStatus = CancellationStatus.Active,
            CreatedByEmployeeId = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };

        for (var i = 0; i < data.Items.Count; i++)
        {
            order.EnsureTopLevelKind(data.Items[i].Kind, $"items[{i}].itemKind");
            order.AddTree(data.Items[i], parent: null, createdBy, newId, now);
        }

        foreach (var weighing in data.Weighings)
        {
            order._weighings.Add(new WeighingEntry(newId(), tenantId, id, weighing, createdBy, now));
        }

        order.EnsureNotEmpty("items");
        order.Recalculate();
        order.Raise(new OrderCreated(id, orderNumber, order.OrderType, order.Label, now));
        return order;
    }

    /// PATCH /orders/{id}: dados do pedido, não itens. Pago → só notes, tableLabel e tabNumber (ADR-0007).
    public void UpdateDetails(OrderDetailsChanges changes, Money defaultDeliveryFee, Guid by, Func<Guid> newId, DateTimeOffset now)
    {
        EnsureNotCanceled();

        var financial = Differs(changes.CustomerName, CustomerName) || Differs(changes.CustomerPhone, CustomerPhone)
            || Differs(changes.FulfillmentType, FulfillmentType) || Differs(changes.DeliveryAddress, DeliveryAddress)
            || Differs(changes.DeliveryFee, DeliveryFee);
        if (financial && IsPaid)
        {
            throw new BusinessException(ErrorCodes.OrderAlreadyPaid);
        }

        var customerName = changes.CustomerName.GetValueOr(CustomerName);
        if (OrderType == OrderType.Marmita && string.IsNullOrWhiteSpace(customerName))
        {
            throw BusinessException.Validation("customerName", "Informe o nome do cliente da marmita.");
        }

        var fulfillment = changes.FulfillmentType.GetValueOr(FulfillmentType);
        EnsureFulfillment(OrderType, fulfillment);

        // Saindo de entrega, endereço e taxa somem; entrando, a taxa padrão vale se nenhuma foi enviada.
        var address = fulfillment == FulfillmentType.Delivery ? changes.DeliveryAddress.GetValueOr(DeliveryAddress) : null;
        var fee = fulfillment != FulfillmentType.Delivery
            ? changes.DeliveryFee.GetValueOr(Money.Zero)
            : changes.DeliveryFee.GetValueOr(FulfillmentType == FulfillmentType.Delivery ? DeliveryFee : defaultDeliveryFee);
        EnsureDelivery(fulfillment, address, fee);

        var before = new { FulfillmentType, DeliveryAddress, DeliveryFee, Notes };
        var notes = changes.Notes.GetValueOr(Notes);
        var fulfillmentChanged = fulfillment != FulfillmentType || address != DeliveryAddress || fee != DeliveryFee;
        var notesChanged = notes != Notes;
        var anyChange = fulfillmentChanged || notesChanged || customerName != CustomerName
            || changes.CustomerPhone.GetValueOr(CustomerPhone) != CustomerPhone
            || changes.TableLabel.GetValueOr(TableLabel) != TableLabel || changes.TabNumber.GetValueOr(TabNumber) != TabNumber;
        if (!anyChange)
        {
            return;
        }

        CustomerName = customerName;
        CustomerPhone = changes.CustomerPhone.GetValueOr(CustomerPhone);
        FulfillmentType = fulfillment;
        DeliveryAddress = address;
        DeliveryFee = fee;
        TableLabel = changes.TableLabel.GetValueOr(TableLabel);
        TabNumber = changes.TabNumber.GetValueOr(TabNumber);
        Notes = notes;
        Recalculate();
        Changed(now);

        if (fulfillmentChanged)
        {
            RecordChange(OrderChangeKind.FulfillmentChanged, false, new
            {
                fulfillmentBefore = UpperSnake(before.FulfillmentType),
                fulfillmentAfter = UpperSnake(FulfillmentType),
                deliveryFeeBefore = before.DeliveryFee.ToString(),
                deliveryFeeAfter = DeliveryFee.ToString(),
            }, by, newId, now);
        }

        if (notesChanged)
        {
            RecordChange(OrderChangeKind.OrderNotesChanged, HasProduction, new { notesBefore = before.Notes, notesAfter = Notes }, by, newId, now);
        }
    }

    /// POST /orders/{id}/items: vários itens numa só confirmação (RN-012.3).
    public IReadOnlyList<OrderItem> AddItems(IReadOnlyList<NewItem> items, Guid by, Func<Guid> newId, DateTimeOffset now)
    {
        EnsureEditable();
        var added = new List<OrderItem>();
        for (var i = 0; i < items.Count; i++)
        {
            var spec = items[i];
            if (spec.ParentOrderItemId is { } parentId)
            {
                var parent = _items.SingleOrDefault(x => x.Id == parentId && x.Status == LineStatus.Active && x.ParentOrderItemId is null);
                if (parent is null || spec.Kind != OrderItemKind.Addon || parent.ItemKind is not (OrderItemKind.Marmita or OrderItemKind.Pf))
                {
                    throw BusinessException.Validation($"items[{i}].parentOrderItemId", "Adicional vinculado só a uma marmita ou PF ativa deste pedido.");
                }

                added.AddRange(AddTree(spec, parent, by, newId, now));
            }
            else
            {
                EnsureTopLevelKind(spec.Kind, $"items[{i}].itemKind");
                added.AddRange(AddTree(spec, parent: null, by, newId, now));
            }
        }

        Recalculate();
        if (ProductionStatus == Contracts.ProductionStatus.Completed && added.Any(x => x.RequiresPreparation))
        {
            // RN-007.10: item com preparo em pedido pronto reabre a produção.
            ProductionStatus = Contracts.ProductionStatus.InProgress;
            ProductionStatusChangedAt = now;
        }

        Changed(now);
        foreach (var item in added)
        {
            RecordChange(OrderChangeKind.ItemAdded, item.RequiresPreparation, ItemDetails(item, null, item.Quantity), by, newId, now);
        }

        return added;
    }

    /// PATCH /orders/{id}/items/{itemId}: quantidade e observação na própria linha.
    public void ChangeItem(Guid itemId, Optional<int> quantity, Optional<string?> notes, Guid by, Func<Guid> newId, DateTimeOffset now)
    {
        EnsureEditable();
        var item = ActiveItem(itemId);
        var quantityBefore = item.Quantity;
        var notesBefore = item.Notes;
        var newQuantity = quantity.GetValueOr(item.Quantity);
        EnsureQuantity(newQuantity, "quantity");

        if (newQuantity == quantityBefore && notes.GetValueOr(notesBefore) == notesBefore)
        {
            return;
        }

        item.Change(newQuantity, notes.GetValueOr(notesBefore), ParentQuantity(item));
        foreach (var child in ChildrenOf(item))
        {
            child.Reprice(item.Quantity);
        }

        Recalculate();
        Changed(now);

        if (newQuantity != quantityBefore)
        {
            RecordChange(OrderChangeKind.QuantityChanged, item.RequiresPreparation, ItemDetails(item, quantityBefore, item.Quantity), by, newId, now);
        }

        if (item.Notes != notesBefore)
        {
            RecordChange(OrderChangeKind.ItemNotesChanged, true, new { itemId = item.Id, name = item.NameSnapshot, notesBefore, notesAfter = item.Notes }, by, newId, now);
        }
    }

    /// DELETE /orders/{id}/items/{itemId}: remoção lógica, com os adicionais filhos.
    public void RemoveItem(Guid itemId, Guid by, Func<Guid> newId, DateTimeOffset now)
    {
        EnsureEditable();
        var item = ActiveItem(itemId);
        var children = ChildrenOf(item).ToList();
        // Valida antes de mutar: o agregado nunca fica num estado inválido, mesmo sem rollback.
        if (!_items.Any(i => i.Status == LineStatus.Active && i != item && !children.Contains(i))
            && !_weighings.Any(w => w.Status == LineStatus.Active))
        {
            throw BusinessException.Validation("itemId", "O pedido precisa de pelo menos um item ou pesagem.");
        }

        item.Remove(by, now);
        foreach (var child in children)
        {
            child.Remove(by, now);
        }

        Recalculate();
        Changed(now);
        RecordChange(OrderChangeKind.ItemRemoved, item.RequiresPreparation, ItemDetails(item, item.Quantity, null), by, newId, now);
    }

    public IReadOnlyList<WeighingEntry> AddWeighings(IReadOnlyList<NewWeighing> weighings, Guid by, Func<Guid> newId, DateTimeOffset now)
    {
        EnsureEditable();
        if (OrderType != OrderType.Kilo)
        {
            throw BusinessException.Validation("weighings", "Pesagens só existem em pedidos por quilo.");
        }

        var added = weighings.Select(w => new WeighingEntry(newId(), TenantId, Id, w, by, now)).ToList();
        _weighings.AddRange(added);
        Recalculate();
        Changed(now);
        foreach (var weighing in added)
        {
            RecordChange(OrderChangeKind.WeighingAdded, false, WeighingDetails(weighing), by, newId, now);
        }

        return added;
    }

    public void RemoveWeighing(Guid weighingId, Guid by, Func<Guid> newId, DateTimeOffset now)
    {
        EnsureEditable();
        var weighing = _weighings.SingleOrDefault(w => w.Id == weighingId && w.Status == LineStatus.Active)
            ?? throw new NotFoundException("WEIGHING", weighingId);
        if (!_items.Any(i => i.Status == LineStatus.Active) && !_weighings.Any(w => w.Status == LineStatus.Active && w != weighing))
        {
            throw BusinessException.Validation("weighingId", "O pedido precisa de pelo menos um item ou pesagem.");
        }

        weighing.Remove(by, now);
        Recalculate();
        Changed(now);
        RecordChange(OrderChangeKind.WeighingRemoved, false, WeighingDetails(weighing), by, newId, now);
    }

    /// Qualquer transição entre os três estados, inclusive em pedido pago (RN-007.11). KILO não tem produção.
    public void ChangeProductionStatus(ProductionStatus to, DateTimeOffset now)
    {
        EnsureNotCanceled();
        if (OrderType == OrderType.Kilo)
        {
            throw BusinessException.Validation("productionStatus", "Pedido por quilo não tem status de produção.");
        }

        var from = ProductionStatus!.Value;
        if (from == to)
        {
            return;
        }

        ProductionStatus = to;
        ProductionStatusChangedAt = now;
        UpdatedAt = now;
        Touch(new OrderStatusChanged(Id, OrderNumber, from, to, now));
    }

    /// ADR-0022: só sem pagamentos válidos; o pedido cancelado é somente leitura.
    public void Cancel(string reason, Guid by, Func<Guid> newId, DateTimeOffset now)
    {
        EnsureNotCanceled();
        if (!PaidAmount.IsZero)
        {
            throw new BusinessException(ErrorCodes.OrderHasPayments);
        }

        if (reason.Trim().Length < 5)
        {
            throw BusinessException.Validation("reason", "Descreva o motivo com pelo menos 5 caracteres.");
        }

        CancellationStatus = CancellationStatus.Canceled;
        CanceledAt = now;
        CanceledByEmployeeId = by;
        CancellationReason = reason.Trim();
        UpdatedAt = now;
        Touch(new OrderCanceled(Id, OrderNumber, now));
        RecordChange(OrderChangeKind.OrderCanceled, HasProduction, new { reason = CancellationReason }, by, newId, now);
    }

    private bool HasProduction => ProductionStatus is not null;

    private List<OrderItem> AddTree(NewItem spec, OrderItem? parent, Guid by, Func<Guid> newId, DateTimeOffset now)
    {
        EnsureQuantity(spec.Quantity, "quantity");
        var item = new OrderItem(newId(), TenantId, Id, parent?.Id, spec, parent?.Quantity ?? 1, by, newId, now);
        var added = new List<OrderItem> { item };
        foreach (var addon in spec.Addons)
        {
            EnsureQuantity(addon.Quantity, "quantity");
            added.Add(new OrderItem(newId(), TenantId, Id, item.Id, addon, item.Quantity, by, newId, now));
        }

        _items.AddRange(added);
        return added;
    }

    private void EnsureTopLevelKind(OrderItemKind kind, string field)
    {
        // Marmita só em pedido de marmita, PF só em PF; bebida e adicional avulso em qualquer modalidade (RN-002.5).
        var ok = kind switch
        {
            OrderItemKind.Marmita => OrderType == OrderType.Marmita,
            OrderItemKind.Pf => OrderType == OrderType.Pf,
            _ => true,
        };
        if (!ok)
        {
            throw BusinessException.Validation(field, "Este tipo de item não combina com a modalidade do pedido.");
        }
    }

    private OrderItem ActiveItem(Guid itemId) =>
        _items.SingleOrDefault(i => i.Id == itemId && i.Status == LineStatus.Active) ?? throw new NotFoundException("ORDER_ITEM", itemId);

    private IEnumerable<OrderItem> ChildrenOf(OrderItem item) =>
        _items.Where(i => i.ParentOrderItemId == item.Id && i.Status == LineStatus.Active);

    private int ParentQuantity(OrderItem item) =>
        item.ParentOrderItemId is { } parentId ? _items.Single(i => i.Id == parentId).Quantity : 1;

    private void EnsureNotCanceled()
    {
        if (CancellationStatus == CancellationStatus.Canceled)
        {
            throw new BusinessException(ErrorCodes.OrderCanceled);
        }
    }

    /// Itens, pesagens, quantidades e taxa: bloqueados em pedido cancelado ou quitado (ADR-0007).
    private void EnsureEditable()
    {
        EnsureNotCanceled();
        if (IsPaid)
        {
            throw new BusinessException(ErrorCodes.OrderAlreadyPaid);
        }
    }

    private void EnsureNotEmpty(string field)
    {
        if (!_items.Any(i => i.Status == LineStatus.Active) && !_weighings.Any(w => w.Status == LineStatus.Active))
        {
            throw BusinessException.Validation(field, "O pedido precisa de pelo menos um item ou pesagem.");
        }
    }

    private void Recalculate()
    {
        ItemsTotal = Money.Sum(_items.Where(i => i.Status == LineStatus.Active).Select(i => i.LineTotal));
        WeighingsTotal = Money.Sum(_weighings.Where(w => w.Status == LineStatus.Active).Select(w => w.Amount));
        var total = ItemsTotal + WeighingsTotal + DeliveryFee;
        if (total < PaidAmount)
        {
            throw new BusinessException(ErrorCodes.TotalBelowPaidAmount);
        }

        TotalAmount = total;
        IsPaid = TotalAmount > Money.Zero && PaidAmount == TotalAmount;
    }

    private void Changed(DateTimeOffset now)
    {
        UpdatedAt = now;
        Touch(new OrderUpdated(Id, OrderNumber, OrderType, ProductionStatus, IsPaid, now));
    }

    /// Alerta de alteração (RF-008). Chamado depois do Touch: `OrderVersion` já é a versão nova.
    private void RecordChange(OrderChangeKind kind, bool affectsProduction, object details, Guid by, Func<Guid> newId, DateTimeOffset now)
    {
        var change = new OrderChangeEvent(newId(), TenantId, Id, Version, kind, affectsProduction,
            JsonSerializer.Serialize(details, DetailsJson), by, now);
        _changeEvents.Add(change);
        Touch(new OrderAlertCreated(Id, change.Id, OrderNumber, kind, affectsProduction, now));
    }

    private static object ItemDetails(OrderItem item, int? quantityBefore, int? quantityAfter) =>
        new { itemId = item.Id, name = item.NameSnapshot, quantityBefore, quantityAfter };

    private static object WeighingDetails(WeighingEntry weighing) =>
        new { weighingId = weighing.Id, weightGrams = weighing.WeightGrams.Value, amount = weighing.Amount.ToString() };

    private static void EnsureQuantity(int quantity, string field)
    {
        if (quantity is < 1 or > 99)
        {
            throw BusinessException.Validation(field, "A quantidade deve estar entre 1 e 99.");
        }
    }

    private static void EnsureFulfillment(OrderType type, FulfillmentType fulfillment)
    {
        var ok = type == OrderType.Marmita
            ? fulfillment is FulfillmentType.Delivery or FulfillmentType.Pickup
            : fulfillment == FulfillmentType.DineIn;
        if (!ok)
        {
            throw BusinessException.Validation("fulfillmentType", "Marmita é entrega ou retirada; PF e quilo são no salão.");
        }
    }

    private static void EnsureDelivery(FulfillmentType fulfillment, DeliveryAddressDto? address, Money fee)
    {
        if (fulfillment == FulfillmentType.Delivery && address is null)
        {
            throw BusinessException.Validation("deliveryAddress", "Informe o endereço de entrega.");
        }

        if (fulfillment != FulfillmentType.Delivery && (address is not null || !fee.IsZero))
        {
            throw BusinessException.Validation("deliveryFee", "Endereço e taxa de entrega só existem em entregas.");
        }

        if (fee.IsNegative)
        {
            throw BusinessException.Validation("deliveryFee", "A taxa de entrega não pode ser negativa.");
        }
    }

    private static bool Differs<T>(Optional<T> change, T current) => change.HasValue && !EqualityComparer<T>.Default.Equals(change.Value, current);

    private static string UpperSnake(FulfillmentType value) =>
        string.Concat(value.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : c.ToString())).ToUpperInvariant();
}

internal sealed class OrderItem : Entity, ITenantOwned
{
    private readonly List<OrderItemComponent> _components = [];

    private OrderItem()
    {
    }

    internal OrderItem(Guid id, Guid tenantId, Guid orderId, Guid? parentId, NewItem spec, int parentQuantity, Guid by, Func<Guid> newId, DateTimeOffset now)
    {
        Id = id;
        TenantId = tenantId;
        OrderId = orderId;
        ParentOrderItemId = parentId;
        ItemKind = spec.Kind;
        ProductId = spec.ProductId;
        MarmitaSizeId = spec.MarmitaSizeId;
        Quantity = spec.Quantity;
        UnitPrice = spec.UnitPrice;
        NameSnapshot = spec.Name;
        CompositionSnapshot = spec.Composition;
        RequiresPreparation = spec.RequiresPreparation;
        Notes = spec.Notes;
        Status = LineStatus.Active;
        CreatedByEmployeeId = by;
        CreatedAt = now;
        _components.AddRange(spec.Components.Select(c => new OrderItemComponent(newId(), tenantId, id, c)));
        Reprice(parentQuantity);
    }

    public Guid TenantId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid? ParentOrderItemId { get; private set; }
    public OrderItemKind ItemKind { get; private set; }
    public Guid? ProductId { get; private set; }
    public Guid? MarmitaSizeId { get; private set; }
    public int Quantity { get; private set; }
    public Money UnitPrice { get; private set; }
    public Money LineTotal { get; private set; }
    public string NameSnapshot { get; private set; } = null!;
    public string? CompositionSnapshot { get; private set; }
    public bool RequiresPreparation { get; private set; }
    public string? Notes { get; private set; }
    public LineStatus Status { get; private set; }
    public Guid CreatedByEmployeeId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? RemovedByEmployeeId { get; private set; }
    public DateTimeOffset? RemovedAt { get; private set; }
    public IReadOnlyList<OrderItemComponent> Components => _components;

    /// Adicional filho: `UnitPrice × Quantity × quantidade do pai` (04 §4.7).
    internal void Reprice(int parentQuantity) => LineTotal = UnitPrice * (Quantity * parentQuantity);

    internal void Change(int quantity, string? notes, int parentQuantity)
    {
        Quantity = quantity;
        Notes = notes;
        Reprice(parentQuantity);
    }

    internal void Remove(Guid by, DateTimeOffset now)
    {
        Status = LineStatus.Removed;
        RemovedByEmployeeId = by;
        RemovedAt = now;
    }
}

internal sealed class OrderItemComponent : Entity, ITenantOwned
{
    private OrderItemComponent()
    {
    }

    internal OrderItemComponent(Guid id, Guid tenantId, Guid orderItemId, NewComponent spec)
    {
        Id = id;
        TenantId = tenantId;
        OrderItemId = orderItemId;
        ProductId = spec.ProductId;
        Role = spec.Role;
        NameSnapshot = spec.Name;
    }

    public Guid TenantId { get; private set; }
    public Guid OrderItemId { get; private set; }
    public Guid ProductId { get; private set; }
    public ComponentRole Role { get; private set; }
    public string NameSnapshot { get; private set; } = null!;
}

internal sealed class WeighingEntry : Entity, ITenantOwned
{
    private WeighingEntry()
    {
    }

    internal WeighingEntry(Guid id, Guid tenantId, Guid orderId, NewWeighing spec, Guid by, DateTimeOffset now)
    {
        Id = id;
        TenantId = tenantId;
        OrderId = orderId;
        WeightGrams = spec.Grams;
        PricePerKg = spec.PricePerKg;
        Amount = KiloPricing.Amount(spec.Grams, spec.PricePerKg);
        Status = LineStatus.Active;
        CreatedByEmployeeId = by;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public Guid OrderId { get; private set; }
    public Grams WeightGrams { get; private set; }
    public Money PricePerKg { get; private set; }
    public Money Amount { get; private set; }
    public LineStatus Status { get; private set; }
    public Guid CreatedByEmployeeId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? RemovedByEmployeeId { get; private set; }
    public DateTimeOffset? RemovedAt { get; private set; }

    internal void Remove(Guid by, DateTimeOffset now)
    {
        Status = LineStatus.Removed;
        RemovedByEmployeeId = by;
        RemovedAt = now;
    }
}

/// Alteração relevante para quem acompanha o pedido (RF-008). Imutável.
internal sealed class OrderChangeEvent : Entity, ITenantOwned
{
    private OrderChangeEvent()
    {
    }

    internal OrderChangeEvent(
        Guid id, Guid tenantId, Guid orderId, int orderVersion, OrderChangeKind kind, bool affectsProduction, string details,
        Guid actor, DateTimeOffset now)
    {
        Id = id;
        TenantId = tenantId;
        OrderId = orderId;
        OrderVersion = orderVersion;
        ChangeKind = kind;
        AffectsProduction = affectsProduction;
        Details = details;
        ActorEmployeeId = actor;
        OccurredAt = now;
    }

    public Guid TenantId { get; private set; }
    public Guid OrderId { get; private set; }
    public int OrderVersion { get; private set; }
    public OrderChangeKind ChangeKind { get; private set; }
    public bool AffectsProduction { get; private set; }
    public string Details { get; private set; } = null!;
    public Guid ActorEmployeeId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
}

// ---------- Entradas já resolvidas pelo handler (preço e nomes do catálogo/cardápio) ----------

internal sealed record NewComponent(Guid ProductId, ComponentRole Role, string Name);

internal sealed record NewItem(
    OrderItemKind Kind,
    Guid? ParentOrderItemId,
    Guid? ProductId,
    Guid? MarmitaSizeId,
    int Quantity,
    Money UnitPrice,
    string Name,
    string? Composition,
    bool RequiresPreparation,
    string? Notes,
    IReadOnlyList<NewComponent> Components,
    IReadOnlyList<NewItem> Addons);

internal sealed record NewWeighing(Grams Grams, Money PricePerKg);

internal sealed record NewOrder(
    OrderType OrderType,
    FulfillmentType FulfillmentType,
    Guid? CustomerId,
    string? CustomerName,
    string? CustomerPhone,
    DeliveryAddressDto? DeliveryAddress,
    Money DeliveryFee,
    string? TableLabel,
    string? TabNumber,
    string? Notes,
    IReadOnlyList<NewItem> Items,
    IReadOnlyList<NewWeighing> Weighings);

internal sealed record OrderDetailsChanges(
    Optional<string?> CustomerName,
    Optional<string?> CustomerPhone,
    Optional<FulfillmentType> FulfillmentType,
    Optional<DeliveryAddressDto?> DeliveryAddress,
    Optional<Money> DeliveryFee,
    Optional<string?> TableLabel,
    Optional<string?> TabNumber,
    Optional<string?> Notes);

// ---------- Eventos de domínio (06 §6.14) ----------

internal sealed record OrderCreated(Guid OrderId, int OrderNumber, OrderType OrderType, string? Label, DateTimeOffset OccurredAt) : IDomainEvent;

internal sealed record OrderUpdated(Guid OrderId, int OrderNumber, OrderType OrderType, ProductionStatus? ProductionStatus, bool IsPaid, DateTimeOffset OccurredAt) : IDomainEvent;

internal sealed record OrderStatusChanged(Guid OrderId, int OrderNumber, ProductionStatus From, ProductionStatus To, DateTimeOffset OccurredAt) : IDomainEvent;

internal sealed record OrderCanceled(Guid OrderId, int OrderNumber, DateTimeOffset OccurredAt) : IDomainEvent;

internal sealed record OrderAlertCreated(Guid OrderId, Guid EventId, int OrderNumber, OrderChangeKind ChangeKind, bool AffectsProduction, DateTimeOffset OccurredAt) : IDomainEvent;
