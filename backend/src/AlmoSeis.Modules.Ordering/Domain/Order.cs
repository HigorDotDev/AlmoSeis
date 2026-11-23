using System.Text.Json;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Catalog.Contracts;
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
    /// Nulo enquanto o pedido não é aceito (ADR-0046 item 4); recusados, expirados e desistências nunca recebem.
    public int? OrderNumber { get; private set; }
    public OrderType OrderType { get; private set; }
    public OrderOrigin Origin { get; private set; }

    /// Tabela de preço (ADR-0057 item 7): `APP` → `COUNTER`, `WEB` → `SITE`. Imutável. A API desta versão sempre grava
    /// `SITE` no pedido da página (05 §5.17 0009, janela do deploy).
    public PriceList PriceList { get; private set; }
    public FulfillmentType FulfillmentType { get; private set; }
    public Guid? CustomerId { get; private set; }
    public string? CustomerName { get; private set; }
    public string? CustomerPhone { get; private set; }
    public DeliveryAddressDto? DeliveryAddress { get; private set; }
    public string? TableLabel { get; private set; }
    public string? TabNumber { get; private set; }
    public string? Notes { get; private set; }
    public Money DeliveryFee { get; private set; }

    /// Origem da taxa e snapshot da regra por distância (ADR-0048 item 20). Nulos fora de entrega.
    public DeliveryFeeSource? DeliveryFeeSource { get; private set; }
    public int? DeliveryDistanceMeters { get; private set; }
    public Money? DeliveryPricePerKm { get; private set; }
    public Money? DeliveryMinimumFee { get; private set; }

    /// "4.5" (uma casa, como a página mostrou) enquanto a taxa é a calculada pela distância.
    public string? DeliveryDistanceKm =>
        DeliveryFeeSource == Contracts.DeliveryFeeSource.Distance && DeliveryDistanceMeters is { } meters
            ? Math.Round(meters / 1000m, 1, MidpointRounding.AwayFromZero).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            : null;
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
    public AcceptanceStatus AcceptanceStatus { get; private set; }
    public DateTimeOffset? AcceptanceDeadline { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public Guid? AcceptedByEmployeeId { get; private set; }
    public int? EstimatedReadyMinutes { get; private set; }
    public RejectionReason? RejectionReasonCode { get; private set; }
    public string? RejectionMessage { get; private set; }
    public DateTimeOffset? RejectedAt { get; private set; }
    public Guid? RejectedByEmployeeId { get; private set; }
    public DateTimeOffset? ExpiredAt { get; private set; }
    public DateTimeOffset? WithdrawnAt { get; private set; }
    public FulfillmentStatus? FulfillmentStatus { get; private set; }
    public DateTimeOffset? FulfillmentStatusChangedAt { get; private set; }
    public DateTimeOffset? OutForDeliveryAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public DateTimeOffset? PickedUpAt { get; private set; }

    /// Pedido da página (ADR-0044 item 19): forma prevista e "troco para". Só informação para o entregador.
    public ExpectedPaymentMethod? ExpectedPaymentMethod { get; private set; }
    public Money? ChangeFor { get; private set; }

    /// `CAPTCHA_UNAVAILABLE`: o Turnstile não respondeu no envio (ADR-0045 item 12).
    public WebOrderRiskFlag? RiskFlag { get; private set; }

    /// Anonimização do pedido da página que não virou venda, 7 dias depois (ADR-0052 item 3).
    public DateTimeOffset? AnonymizedAt { get; private set; }

    /// Nulo só em pedido da página (`Origin = WEB`).
    public Guid? CreatedByEmployeeId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<OrderItem> Items => _items;
    public IReadOnlyList<WeighingEntry> Weighings => _weighings;

    /// Só os eventos criados nesta unidade de trabalho (a coleção não é carregada do banco).
    public IReadOnlyList<OrderChangeEvent> ChangeEvents => _changeEvents;

    public Money Balance => TotalAmount - PaidAmount;

    public bool IsAccepted => AcceptanceStatus == AcceptanceStatus.Accepted;

    /// "Pronto para retirar" é derivado, não gravado (ADR-0046 item 2).
    public bool IsReadyForPickup => IsReadyForPickupRule(FulfillmentType, ProductionStatus, FulfillmentStatus);

    /// `fulfillment` nulo em retirada aceita = `PENDING` (pedido gravado pela versão anterior da API, 05 §5.17 0003).
    public static bool IsReadyForPickupRule(FulfillmentType type, ProductionStatus? production, FulfillmentStatus? fulfillment) =>
        type == FulfillmentType.Pickup && production == Contracts.ProductionStatus.Completed
        && (fulfillment ?? Contracts.FulfillmentStatus.Pending) == Contracts.FulfillmentStatus.Pending;

    /// Texto da recusa (ADR-0046 item 12): 5 a 200 caracteres, obrigatório em `OTHER`.
    public const int RejectionMessageMin = 5;
    public const int RejectionMessageMax = 200;

    public static readonly int[] EstimatedMinutesOptions = [15, 30, 45, 60];

    /// "João Silva", "Mesa 5", "Comanda 47": rótulo do painel da cozinha e do `OrderCreated` (06 §6.9).
    public string? Label => OrderType == OrderType.Marmita
        ? CustomerName
        : TableLabel is not null ? $"Mesa {TableLabel}" : TabNumber is not null ? $"Comanda {TabNumber}" : CustomerName;

    public static Order Create(
        Guid id, Guid tenantId, NewOrder data, Guid businessDayId, DateOnly businessDate, int orderNumber,
        Guid createdBy, Func<Guid> newId, DateTimeOffset now)
    {
        EnsureFulfillment(data.OrderType, data.FulfillmentType);
        EnsureCustomerContact(data.OrderType, data.FulfillmentType, data.CustomerName, data.CustomerPhone, checkName: true, checkPhone: true);
        EnsureDelivery(data.FulfillmentType, data.DeliveryAddress, data.DeliveryFee);
        EnsureExpectedPayment(data.FulfillmentType, data.ExpectedPaymentMethod);
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
            PriceList = PriceList.Counter,
            FulfillmentType = data.FulfillmentType,
            CustomerId = data.CustomerId,
            CustomerName = data.CustomerName,
            CustomerPhone = data.CustomerPhone,
            DeliveryAddress = data.DeliveryAddress,
            TableLabel = data.TableLabel,
            TabNumber = data.TabNumber,
            Notes = data.Notes,
            DeliveryFee = data.DeliveryFee,
            // Forma prevista e "troco para" (ADR-0059 item 5): só informação; nenhum pagamento nasce aqui (item 8).
            ExpectedPaymentMethod = data.ExpectedPaymentMethod,
            ChangeFor = data.ChangeFor,
            PaidAmount = Money.Zero,
            ProductionStatus = data.OrderType == OrderType.Kilo ? null : Contracts.ProductionStatus.Open,
            ProductionStatusChangedAt = data.OrderType == OrderType.Kilo ? null : now,
            CancellationStatus = CancellationStatus.Active,
            // Todo pedido do app nasce aceito (ADR-0046 item 1): o aceite é a própria criação.
            AcceptanceStatus = AcceptanceStatus.Accepted,
            AcceptedAt = now,
            AcceptedByEmployeeId = createdBy,
            FulfillmentStatus = data.FulfillmentType == FulfillmentType.DineIn ? null : Contracts.FulfillmentStatus.Pending,
            FulfillmentStatusChangedAt = data.FulfillmentType == FulfillmentType.DineIn ? null : now,
            CreatedByEmployeeId = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };

        order.ApplyDeliveryPricing(data.FulfillmentType == FulfillmentType.Delivery ? data.Pricing ?? new DeliveryPricing(Contracts.DeliveryFeeSource.FixedDefault) : null);
        order.AddInitialLines(data, createdBy, newId, now);
        order.EnsureChangeFor();
        order.Raise(new OrderCreated(id, orderNumber, order.OrderType, order.Label, now));
        return order;
    }

    /// Pedido "pagar na entrega/retirada" da página (ADR-0046): sem número, sem produção, sem cliente vinculado e sem
    /// autor. Espera o aceite até `deadline`. Pertence ao expediente aberto no envio (item 5). `MARMITA` (restaurante) ou
    /// `A_LA_CARTE` (lanchonete, EP-003 fatia 5, ADR-0058 item 14), sempre entrega ou retirada.
    public static Order SubmitFromWeb(
        Guid id, Guid tenantId, NewOrder data, Guid businessDayId, DateOnly businessDate, DateTimeOffset deadline,
        Func<Guid> newId, DateTimeOffset now, WebOrderExtras? extras = null)
    {
        if (data.OrderType is not (OrderType.Marmita or OrderType.ALaCarte))
        {
            throw BusinessException.Validation("orderType", "A página de pedidos vende marmitas ou o cardápio da lanchonete.");
        }

        if (data.FulfillmentType is not (FulfillmentType.Delivery or FulfillmentType.Pickup))
        {
            throw BusinessException.Validation("fulfillmentType", "O pedido da página é entrega ou retirada.");
        }

        EnsureFulfillment(data.OrderType, data.FulfillmentType);
        if (string.IsNullOrWhiteSpace(data.CustomerName))
        {
            throw BusinessException.Validation("customerName", "Informe o nome do cliente.");
        }

        EnsureDelivery(data.FulfillmentType, data.DeliveryAddress, data.DeliveryFee);
        if (data.Weighings.Count > 0)
        {
            throw BusinessException.Validation("weighings", "Pesagens só existem em pedidos por quilo.");
        }

        if (deadline <= now)
        {
            throw new ArgumentOutOfRangeException(nameof(deadline), "O prazo de aceite precisa estar no futuro.");
        }

        var order = new Order
        {
            Id = id,
            TenantId = tenantId,
            BusinessDayId = businessDayId,
            BusinessDate = businessDate,
            OrderNumber = null,
            OrderType = data.OrderType,
            Origin = OrderOrigin.Web,
            PriceList = PriceList.Site,
            FulfillmentType = data.FulfillmentType,
            CustomerId = null,
            CustomerName = data.CustomerName,
            CustomerPhone = data.CustomerPhone,
            DeliveryAddress = data.DeliveryAddress,
            Notes = data.Notes,
            DeliveryFee = data.DeliveryFee,
            PaidAmount = Money.Zero,
            ProductionStatus = null,
            CancellationStatus = CancellationStatus.Active,
            AcceptanceStatus = AcceptanceStatus.AwaitingAcceptance,
            AcceptanceDeadline = deadline,
            ExpectedPaymentMethod = extras?.ExpectedPaymentMethod,
            ChangeFor = extras?.ChangeFor,
            RiskFlag = extras?.RiskFlag,
            CreatedByEmployeeId = null,
            CreatedAt = now,
            UpdatedAt = now,
        };

        order.ApplyDeliveryPricing(data.FulfillmentType == FulfillmentType.Delivery ? data.Pricing ?? new DeliveryPricing(Contracts.DeliveryFeeSource.FixedDefault) : null);
        order.AddInitialLines(data, by: null, newId, now);
        order.EnsureChangeFor();
        // `itemCount` (ADR-0058 item 14): unidades do primeiro nível (marmitas, pratos, combos e bebidas); `marmitaCount`
        // fica 0 no à la carte.
        var topLevel = order.Items.Where(i => i.ParentOrderItemId is null).ToList();
        order.Raise(new OrderAwaitingAcceptance(
            id, order.FulfillmentType, topLevel.Where(i => i.ItemKind == OrderItemKind.Marmita).Sum(i => i.Quantity),
            order.TotalAmount, deadline, now, topLevel.Sum(i => i.Quantity)));
        return order;
    }

    /// "Troco para R$ X" só em dinheiro e de pelo menos o total (ADR-0044 item 19; no app, ADR-0059 item 5). O mínimo é
    /// conferido na criação e quando o próprio "troco para" é editado (PATCH); uma edição de itens, quantidades ou taxa
    /// pode passar o total do "troco para" sem ser recusada (ADR-0059, revisão de 2026-11-23, I2): o pedido expõe o
    /// `ChangeForBelowTotal` para o app alertar.
    private void EnsureChangeFor() => EnsureChangeFor(ExpectedPaymentMethod, ChangeFor, TotalAmount, checkMinimum: true);

    private static void EnsureChangeFor(ExpectedPaymentMethod? method, Money? changeFor, Money total, bool checkMinimum)
    {
        if (changeFor is not { } change)
        {
            return;
        }

        if (method != Contracts.ExpectedPaymentMethod.Cash)
        {
            throw BusinessException.Validation("changeFor", "Troco só para pagamento em dinheiro.");
        }

        if (checkMinimum && (change < total || change.IsZero || change.IsNegative))
        {
            throw BusinessException.Validation("changeFor", $"O valor para troco precisa ser de pelo menos R$ {total}.");
        }
    }

    /// "Troco para" abaixo do total do pedido (ADR-0059, revisão de 2026-11-23, I2): o motoboy levaria troco a menos.
    /// Só informação, nunca recusa.
    public bool ChangeForBelowTotal => ChangeForBelowTotalRule(ChangeFor, TotalAmount);

    public static bool ChangeForBelowTotalRule(Money? changeFor, Money totalAmount) => changeFor is { } change && change < totalAmount;

    /// Forma prevista e "troco para" do PATCH (ADR-0059, revisão de 2026-11-23, I1), com as regras da criação: proibidos
    /// no salão (trocar para `DINE_IN` apaga os dois); trocar o atendimento ou enviar a forma confere a obrigatoriedade
    /// na entrega (Q-316), e um pedido de entrega antigo sem forma continua editável nos outros campos; "troco para" só
    /// com dinheiro, e o mínimo (o total depois deste PATCH) só quando ele mesmo muda. A forma que deixa de ser dinheiro
    /// sem `changeFor` no corpo leva o "troco para" guardado junto.
    private (ExpectedPaymentMethod? Method, Money? ChangeFor) ResolveExpectedPayment(
        OrderDetailsChanges changes, FulfillmentType fulfillment, Money total)
    {
        if (fulfillment == FulfillmentType.DineIn)
        {
            if (changes.ExpectedPaymentMethod is { HasValue: true, Value: not null })
            {
                throw BusinessException.Validation("expectedPaymentMethod", "Pedido no salão não tem forma de pagamento prevista.");
            }

            if (changes.ChangeFor is { HasValue: true, Value: not null })
            {
                throw BusinessException.Validation("changeFor", "Pedido no salão não tem troco previsto.");
            }

            return (null, null);
        }

        var method = changes.ExpectedPaymentMethod.GetValueOr(ExpectedPaymentMethod);
        if (fulfillment != FulfillmentType || changes.ExpectedPaymentMethod.HasValue)
        {
            EnsureExpectedPayment(fulfillment, method);
        }

        var changeFor = changes.ChangeFor.HasValue
            ? changes.ChangeFor.Value
            : method == Contracts.ExpectedPaymentMethod.Cash ? ChangeFor : null;
        EnsureChangeFor(method, changeFor, total, checkMinimum: Differs(changes.ChangeFor, ChangeFor));
        return (method, changeFor);
    }

    /// Pedido da página que não virou venda (`REJECTED`, `EXPIRED`, `WITHDRAWN`), 7 dias depois (ADR-0052 item 3): nome,
    /// telefone, endereço e observações saem; itens, valores e motivo ficam (métricas de recusa). Devolve false se não há
    /// o que anonimizar.
    public bool AnonymizeNotAccepted(DateTimeOffset now)
    {
        if (Origin != OrderOrigin.Web || AnonymizedAt is not null
            || AcceptanceStatus is not (AcceptanceStatus.Rejected or AcceptanceStatus.Expired or AcceptanceStatus.Withdrawn))
        {
            return false;
        }

        CustomerName = AnonymizedCustomerName;
        CustomerPhone = null;
        DeliveryAddress = DeliveryAddress is null ? null : new DeliveryAddressDto(AnonymizedAddressStreet, null, null, null, null);
        Notes = null;
        ChangeFor = null;
        foreach (var item in _items)
        {
            item.ClearNotes();
        }

        AnonymizedAt = now;
        Changed(now);
        return true;
    }

    public const string AnonymizedCustomerName = "Cliente anonimizado";

    /// Vencido pelo job ou com o prazo já passado, antes de o job rodar (ADR-0046 item 16).
    public bool HasAcceptanceExpired(DateTimeOffset now) =>
        AcceptanceStatus == AcceptanceStatus.Expired
        || (AcceptanceStatus == AcceptanceStatus.AwaitingAcceptance && AcceptanceDeadline is { } deadline && now >= deadline);

    /// Confere antes de o handler consumir um número do contador (ADR-0046 itens 9 e 16).
    public void EnsureAwaitingAcceptance(DateTimeOffset now)
    {
        switch (AcceptanceStatus)
        {
            case AcceptanceStatus.AwaitingAcceptance when AcceptanceDeadline is { } deadline && now >= deadline:
            case AcceptanceStatus.Expired:
                throw new BusinessException(ErrorCodes.WebOrderExpired);
            case AcceptanceStatus.AwaitingAcceptance:
                return;
            default:
                throw new BusinessException(
                    ErrorCodes.OrderNotAwaitingAcceptance,
                    extensions: new Dictionary<string, object?> { ["acceptanceStatus"] = UpperSnake(AcceptanceStatus) });
        }
    }

    /// Aceite (ADR-0046 item 7): número da data operacional, produção `OPEN`, atendimento `PENDING`, vínculo com o
    /// cliente. Para a cozinha, o pedido nasce aqui (`OrderCreated`). `by` nulo = aceite do sistema (Fase 2).
    public void Accept(int orderNumber, Guid? by, string? byName, Guid? customerId, int? estimatedReadyMinutes, DateTimeOffset now)
    {
        EnsureAwaitingAcceptance(now);
        if (estimatedReadyMinutes is { } minutes && !EstimatedMinutesOptions.Contains(minutes))
        {
            throw BusinessException.Validation("estimatedReadyMinutes", "Escolha 15, 30, 45 ou 60 minutos.");
        }

        OrderNumber = orderNumber;
        AcceptanceStatus = AcceptanceStatus.Accepted;
        AcceptedAt = now;
        AcceptedByEmployeeId = by;
        EstimatedReadyMinutes = estimatedReadyMinutes;
        CustomerId = customerId;
        ProductionStatus = Contracts.ProductionStatus.Open;
        ProductionStatusChangedAt = now;
        FulfillmentStatus = Contracts.FulfillmentStatus.Pending;
        FulfillmentStatusChangedAt = now;
        UpdatedAt = now;
        Touch(new OrderAccepted(Id, orderNumber, by, byName, now));
        // No pedido do app o rótulo é o nome do cliente; o da página não leva dado pessoal em evento (item 23).
        Touch(new OrderCreated(Id, orderNumber, OrderType, Label: null, now));
    }

    /// Recusa pelo funcionário (ADR-0046 item 12).
    public void Reject(RejectionReason reason, string? message, Guid by, DateTimeOffset now)
    {
        EnsureAwaitingAcceptance(now);
        if (reason == RejectionReason.BusinessDayClosed)
        {
            throw BusinessException.Validation("reasonCode", "Escolha um motivo da lista.");
        }

        var text = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        if (reason == RejectionReason.Other && text is null)
        {
            throw BusinessException.Validation("message", "Explique o motivo para o cliente.");
        }

        if (text is { Length: < RejectionMessageMin or > RejectionMessageMax })
        {
            throw BusinessException.Validation("message", $"Use de {RejectionMessageMin} a {RejectionMessageMax} caracteres.");
        }

        SetRejected(reason, text, by, now);
    }

    /// Fechamento finalizado com o pedido ainda aguardando (ADR-0046 item 13): o sistema recusa, sem olhar o prazo.
    public void RejectBecauseBusinessDayClosed(DateTimeOffset now)
    {
        if (AcceptanceStatus != AcceptanceStatus.AwaitingAcceptance)
        {
            throw new InvalidOperationException("Só pedido aguardando aceite é recusado pelo fechamento.");
        }

        SetRejected(RejectionReason.BusinessDayClosed, null, null, now);
    }

    /// Job de expiração (ADR-0046 item 14). Devolve false quando não há o que expirar.
    public bool Expire(DateTimeOffset now)
    {
        if (AcceptanceStatus != AcceptanceStatus.AwaitingAcceptance || AcceptanceDeadline is not { } deadline || deadline > now)
        {
            return false;
        }

        AcceptanceStatus = AcceptanceStatus.Expired;
        ExpiredAt = now;
        UpdatedAt = now;
        Touch(new OrderRejected(Id, AcceptanceStatus.Expired, null, now));
        return true;
    }

    /// Desistência do cliente pela página (ADR-0046 item 17), enquanto aguarda aceite ou pagamento.
    public void Withdraw(DateTimeOffset now)
    {
        if (AcceptanceStatus != AcceptanceStatus.AwaitingPayment)
        {
            EnsureAwaitingAcceptance(now);
        }

        AcceptanceStatus = AcceptanceStatus.Withdrawn;
        WithdrawnAt = now;
        UpdatedAt = now;
        Touch(new OrderRejected(Id, AcceptanceStatus.Withdrawn, null, now));
    }

    /// Atendimento (ADR-0046 item 18). Independente do financeiro (ADR-0006): vale em pedido pago ou não.
    public void ChangeFulfillment(FulfillmentStatus to, DateTimeOffset now)
    {
        EnsureAccepted();
        EnsureNotCanceled();
        if (FulfillmentType == FulfillmentType.DineIn)
        {
            throw BusinessException.Validation("fulfillmentStatus", "Pedido no salão não tem status de entrega ou retirada.");
        }

        // Nulo em entrega/retirada aceita = `PENDING`: pedido gravado pela versão anterior da API (05 §5.17 0003).
        var from = FulfillmentStatus ?? Contracts.FulfillmentStatus.Pending;

        if (from == to)
        {
            return;
        }

        var allowed = (FulfillmentType, from, to) switch
        {
            (FulfillmentType.Delivery, Contracts.FulfillmentStatus.Pending, Contracts.FulfillmentStatus.OutForDelivery) => true,
            (FulfillmentType.Delivery, Contracts.FulfillmentStatus.OutForDelivery, Contracts.FulfillmentStatus.Delivered) => true,
            (FulfillmentType.Delivery, Contracts.FulfillmentStatus.OutForDelivery, Contracts.FulfillmentStatus.Pending) => true, // voltou sem entregar
            (FulfillmentType.Delivery, Contracts.FulfillmentStatus.Delivered, Contracts.FulfillmentStatus.OutForDelivery) => true, // correção
            (FulfillmentType.Pickup, Contracts.FulfillmentStatus.Pending, Contracts.FulfillmentStatus.PickedUp) => true,
            (FulfillmentType.Pickup, Contracts.FulfillmentStatus.PickedUp, Contracts.FulfillmentStatus.Pending) => true, // correção
            _ => false,
        };
        if (!allowed)
        {
            throw BusinessException.Validation(
                "fulfillmentStatus",
                $"Não é possível passar de {UpperSnake(from)} para {UpperSnake(to)} neste pedido.");
        }

        // Sair ou ser retirado exige a produção concluída; "Concluir e marcar" conclui antes, na mesma transação.
        if (from == Contracts.FulfillmentStatus.Pending && ProductionStatus != Contracts.ProductionStatus.Completed)
        {
            throw new BusinessException(ErrorCodes.ProductionNotCompleted);
        }

        switch (to)
        {
            case Contracts.FulfillmentStatus.OutForDelivery:
                OutForDeliveryAt = from == Contracts.FulfillmentStatus.Pending ? now : OutForDeliveryAt;
                DeliveredAt = null;
                break;
            case Contracts.FulfillmentStatus.Delivered:
                DeliveredAt = now;
                break;
            case Contracts.FulfillmentStatus.PickedUp:
                PickedUpAt = now;
                break;
            default:
                OutForDeliveryAt = null;
                DeliveredAt = null;
                PickedUpAt = null;
                break;
        }

        FulfillmentStatus = to;
        FulfillmentStatusChangedAt = now;
        UpdatedAt = now;
        Touch(new OrderFulfillmentChanged(Id, OrderNumber, from, to, now));
    }

    private void SetRejected(RejectionReason reason, string? message, Guid? by, DateTimeOffset now)
    {
        AcceptanceStatus = AcceptanceStatus.Rejected;
        RejectionReasonCode = reason;
        RejectionMessage = message;
        RejectedAt = now;
        RejectedByEmployeeId = by;
        UpdatedAt = now;
        Touch(new OrderRejected(Id, AcceptanceStatus.Rejected, reason, now));
    }

    private void AddInitialLines(NewOrder data, Guid? by, Func<Guid> newId, DateTimeOffset now)
    {
        for (var i = 0; i < data.Items.Count; i++)
        {
            EnsureTopLevelKind(data.Items[i].Kind, $"items[{i}].itemKind");
            AddTree(data.Items[i], parent: null, by, newId, now, $"items[{i}]");
        }

        foreach (var weighing in data.Weighings)
        {
            // Pesagem só no app (quilo): sempre há funcionário.
            _weighings.Add(new WeighingEntry(newId(), TenantId, Id, weighing, by!.Value, now));
        }

        EnsureNotEmpty("items");
        Recalculate();
    }

    /// PATCH /orders/{id}: dados do pedido, não itens. Pago → só notes, tableLabel e tabNumber (ADR-0007).
    public void UpdateDetails(OrderDetailsChanges changes, Money defaultDeliveryFee, Guid by, Func<Guid> newId, DateTimeOffset now)
    {
        EnsureAccepted();
        EnsureNotCanceled();

        // Forma prevista e "troco para" (ADR-0059, revisão de 2026-11-23, I1) também só enquanto não quitado.
        var financial = Differs(changes.CustomerName, CustomerName) || Differs(changes.CustomerPhone, CustomerPhone)
            || Differs(changes.FulfillmentType, FulfillmentType) || Differs(changes.DeliveryAddress, DeliveryAddress)
            || Differs(changes.DeliveryFee, DeliveryFee)
            || Differs(changes.ExpectedPaymentMethod, ExpectedPaymentMethod) || Differs(changes.ChangeFor, ChangeFor);
        if (financial && IsPaid)
        {
            throw new BusinessException(ErrorCodes.OrderAlreadyPaid);
        }

        var customerName = changes.CustomerName.GetValueOr(CustomerName);
        var customerPhone = changes.CustomerPhone.GetValueOr(CustomerPhone);
        var fulfillment = changes.FulfillmentType.GetValueOr(FulfillmentType);
        EnsureFulfillment(OrderType, fulfillment);

        // Nome e telefone (ADR-0059, Q-312): trocar o atendimento confere os dois; senão, só o campo que mudou, para
        // um pedido antigo sem telefone continuar editável (a regra é da criação, 05 §5.17 0011).
        var fulfillmentTypeChanged = fulfillment != FulfillmentType;
        EnsureCustomerContact(
            OrderType, fulfillment, customerName, customerPhone,
            checkName: fulfillmentTypeChanged || customerName != CustomerName || OrderType == OrderType.Marmita,
            checkPhone: fulfillmentTypeChanged || customerPhone != CustomerPhone);
        if (fulfillment != FulfillmentType && IsDispatched)
        {
            // Entrega que saiu (ou retirada feita) não vira o outro tipo: corrija o atendimento antes.
            throw new BusinessException(ErrorCodes.OrderAlreadyDispatched);
        }

        // Saindo de entrega, endereço e taxa somem; entrando, a taxa padrão vale se nenhuma foi enviada.
        var address = fulfillment == FulfillmentType.Delivery ? changes.DeliveryAddress.GetValueOr(DeliveryAddress) : null;
        var fee = fulfillment != FulfillmentType.Delivery
            ? changes.DeliveryFee.GetValueOr(Money.Zero)
            : changes.DeliveryFee.GetValueOr(FulfillmentType == FulfillmentType.Delivery ? DeliveryFee : defaultDeliveryFee);
        EnsureDelivery(fulfillment, address, fee);
        var (method, changeFor) = ResolveExpectedPayment(changes, fulfillment, ItemsTotal + WeighingsTotal + fee);

        var before = new { FulfillmentType, DeliveryAddress, DeliveryFee, Notes };
        var notes = changes.Notes.GetValueOr(Notes);
        var fulfillmentChanged = fulfillment != FulfillmentType || address != DeliveryAddress || fee != DeliveryFee;
        var notesChanged = notes != Notes;
        var anyChange = fulfillmentChanged || notesChanged || customerName != CustomerName
            || changes.CustomerPhone.GetValueOr(CustomerPhone) != CustomerPhone
            || changes.TableLabel.GetValueOr(TableLabel) != TableLabel || changes.TabNumber.GetValueOr(TabNumber) != TabNumber
            || method != ExpectedPaymentMethod || changeFor != ChangeFor;
        if (!anyChange)
        {
            return;
        }

        if (fulfillmentChanged)
        {
            // Taxa ou endereço mexidos no app: a taxa deixa de ser a calculada pela página (o snapshot da distância fica
            // como histórico). Fora de entrega, sem origem (ADR-0048 item 19).
            var stayingDelivery = FulfillmentType == FulfillmentType.Delivery;
            var source = fee == defaultDeliveryFee ? Contracts.DeliveryFeeSource.FixedDefault : Contracts.DeliveryFeeSource.Manual;
            ApplyDeliveryPricing(fulfillment != FulfillmentType.Delivery
                ? null
                : stayingDelivery
                    ? new DeliveryPricing(source, DeliveryDistanceMeters, DeliveryPricePerKm, DeliveryMinimumFee)
                    : new DeliveryPricing(source));
        }

        CustomerName = customerName;
        CustomerPhone = customerPhone;
        ExpectedPaymentMethod = method;
        ChangeFor = changeFor;
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
        if (IsDispatched)
        {
            // ADR-0046 item 19 (EP-002 Q-16): depois que saiu, o atendente cria outro pedido.
            throw new BusinessException(ErrorCodes.OrderAlreadyDispatched);
        }
        var added = new List<OrderItem>();
        for (var i = 0; i < items.Count; i++)
        {
            var spec = items[i];
            if (spec.ParentOrderItemId is { } parentId)
            {
                // Incluído depois: ADDON numa marmita ou PF; GROUP_ADDON num prato avulso ou numa parte de prato do
                // combo (ADR-0058 item 10). Nunca uma parte nova num combo já registrado.
                var parent = _items.SingleOrDefault(x => x.Id == parentId && x.Status == LineStatus.Active);
                if (parent is null || !(spec.Kind is OrderItemKind.Addon or OrderItemKind.GroupAddon) || !ChildFits(parent, spec.Kind))
                {
                    throw BusinessException.Validation(
                        $"items[{i}].parentOrderItemId",
                        "Adicional vinculado só a uma marmita, PF ou prato ativo deste pedido.");
                }

                added.AddRange(AddTree(spec, parent, by, newId, now, $"items[{i}]"));
            }
            else
            {
                EnsureTopLevelKind(spec.Kind, $"items[{i}].itemKind");
                added.AddRange(AddTree(spec, parent: null, by, newId, now, $"items[{i}]"));
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

        // A parte do combo tem a quantidade do componente; muda-se a do COMBO (ADR-0058 item 10). O combo não vai à
        // cozinha: a observação fica em cada prato dele.
        if (item.ItemKind == OrderItemKind.ComboPart && newQuantity != quantityBefore)
        {
            throw BusinessException.Validation("quantity", "A quantidade da parte segue o combo: altere a quantidade do combo.");
        }

        if (item.ItemKind == OrderItemKind.Combo && notes.GetValueOr(notesBefore) != notesBefore)
        {
            throw BusinessException.Validation("notes", "A observação vai em cada prato do combo.");
        }

        item.Change(newQuantity, notes.GetValueOr(notesBefore), AncestorQuantity(item));
        RepriceDescendants(item);

        Recalculate();
        Changed(now);

        if (newQuantity != quantityBefore)
        {
            RecordChange(OrderChangeKind.QuantityChanged, AffectsProduction(item), ItemDetails(item, quantityBefore, item.Quantity), by, newId, now);
        }

        if (item.Notes != notesBefore)
        {
            RecordChange(OrderChangeKind.ItemNotesChanged, true, new { itemId = item.Id, name = item.DisplayName, notesBefore, notesAfter = item.Notes }, by, newId, now);
        }
    }

    /// DELETE /orders/{id}/items/{itemId}: remoção lógica, com os adicionais filhos.
    public void RemoveItem(Guid itemId, Guid by, Func<Guid> newId, DateTimeOffset now)
    {
        EnsureEditable();
        var item = ActiveItem(itemId);
        if (item.ItemKind == OrderItemKind.ComboPart)
        {
            // Uma parte não sai sozinha: remove-se o combo inteiro (ADR-0058 item 10).
            throw BusinessException.Validation("itemId", "Uma parte do combo não é removida sozinha: remova o combo.");
        }

        // O combo leva as partes e os adicionais delas; o prato e a marmita, os adicionais.
        var children = DescendantsOf(item).ToList();
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
        RecordChange(OrderChangeKind.ItemRemoved, item.RequiresPreparation || children.Any(c => c.RequiresPreparation), ItemDetails(item, item.Quantity, null), by, newId, now);
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
        EnsureAccepted();
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
        // Pedido não aceito não é cancelado: ele nunca foi venda (ADR-0046 item 4); a recusa é o caminho.
        EnsureAccepted();
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

    /// Chamado só pelo Payments, via IOrderPaymentPort (07 §7.8). Vale para pedido de expediente já fechado (fiado).
    public void ApplyPayment(Money amount, DateTimeOffset now)
    {
        EnsureAccepted();
        EnsureNotCanceled();
        if (amount > Balance)
        {
            throw new BusinessException(
                ErrorCodes.PaymentExceedsBalance,
                extensions: new Dictionary<string, object?> { ["balance"] = Balance });
        }

        var wasPaid = IsPaid;
        PaidAmount += amount;
        IsPaid = TotalAmount > Money.Zero && PaidAmount == TotalAmount;
        UpdatedAt = now;
        Touch(new OrderPaymentRegistered(Id, OrderNumber, PaidAmount, Balance, now));
        if (IsPaid && !wasPaid)
        {
            // Só quando quita (ADR-0021).
            Raise(new OrderPaymentConfirmed(Id, OrderNumber, now));
        }
    }

    /// Estorno: o saldo volta e, se sobrar, o pedido destrava a edição (ADR-0022 §4).
    public void UndoPayment(Money amount, DateTimeOffset now)
    {
        if (amount > PaidAmount)
        {
            throw new InvalidOperationException("Estorno maior que o valor pago.");
        }

        PaidAmount -= amount;
        IsPaid = TotalAmount > Money.Zero && PaidAmount == TotalAmount;
        UpdatedAt = now;
        Touch(new OrderPaymentReversed(Id, OrderNumber, IsPaid, now));
    }

    /// ADR-0024 §3: snapshot do cliente anonimizado junto com o cadastro, em qualquer estado do pedido (pago ou
    /// cancelado). Valores financeiros ficam. Sobe a versão para o feed levar a mudança aos aparelhos.
    public void AnonymizeCustomer(string anonymizedName, DateTimeOffset now)
    {
        // ck_orders_delivery exige endereço em entrega: fica um marcador sem dado pessoal.
        var address = DeliveryAddress is null ? null : new DeliveryAddressDto(AnonymizedAddressStreet, null, null, null, null);
        if (CustomerName == anonymizedName && CustomerPhone is null && DeliveryAddress == address)
        {
            return;
        }

        CustomerName = anonymizedName;
        CustomerPhone = null;
        DeliveryAddress = address;
        Changed(now);
    }

    public const string AnonymizedAddressStreet = "Endereço anonimizado";

    private bool HasProduction => ProductionStatus is not null;

    private bool IsDispatched => FulfillmentStatus is Contracts.FulfillmentStatus.OutForDelivery
        or Contracts.FulfillmentStatus.Delivered or Contracts.FulfillmentStatus.PickedUp;

    /// A linha e os filhos (e os netos, no combo: COMBO → COMBO_PART → GROUP_ADDON, ADR-0058 item 9). O valor de cada
    /// filho usa a quantidade de todos os antepassados.
    private List<OrderItem> AddTree(NewItem spec, OrderItem? parent, Guid? by, Func<Guid> newId, DateTimeOffset now, string field)
    {
        EnsureQuantity(spec.Quantity, "quantity");
        if (parent is not null && !ChildFits(parent, spec.Kind))
        {
            throw BusinessException.Validation(field, "Este item não pode ficar dentro do item escolhido.");
        }

        var ancestors = parent is null ? 1 : parent.Quantity * AncestorQuantity(parent);
        var item = new OrderItem(newId(), TenantId, Id, parent, spec, ancestors, by, newId, now);
        _items.Add(item);
        var added = new List<OrderItem> { item };
        foreach (var child in spec.Addons)
        {
            added.AddRange(AddTree(child, item, by, newId, now, field));
        }

        return added;
    }

    /// Um tipo por pedido (Q-317), e o tipo decide de onde vêm os itens (ADR-0058 item 3): marmita só em pedido de
    /// marmita, PF só em PF; bebida e adicional do catálogo em marmita, PF e quilo (RN-002.5); o cardápio fixo só em
    /// `A_LA_CARTE`. Parte de combo e adicional de grupo nunca ficam no primeiro nível.
    public static bool KindFits(OrderType orderType, OrderItemKind kind) => kind switch
    {
        OrderItemKind.Marmita => orderType == OrderType.Marmita,
        OrderItemKind.Pf => orderType == OrderType.Pf,
        OrderItemKind.Beverage or OrderItemKind.Addon => orderType != OrderType.ALaCarte,
        OrderItemKind.FixedDish or OrderItemKind.FixedBeverage or OrderItemKind.Combo => orderType == OrderType.ALaCarte,
        _ => false,
    };

    public const string KindMismatchMessage = "Este tipo de item não combina com a modalidade do pedido.";

    private void EnsureTopLevelKind(OrderItemKind kind, string field)
    {
        if (!KindFits(OrderType, kind))
        {
            throw BusinessException.Validation(field, KindMismatchMessage);
        }
    }

    /// As combinações do `ck_order_items_parent` (05 §5.17 0010): ADDON sob marmita/PF; COMBO_PART sob COMBO;
    /// GROUP_ADDON sob prato avulso ou parte **de prato** (`ck_order_items_parent_dish`).
    private static bool ChildFits(OrderItem parent, OrderItemKind kind) => kind switch
    {
        OrderItemKind.Addon => parent.ItemKind is OrderItemKind.Marmita or OrderItemKind.Pf,
        OrderItemKind.ComboPart => parent.ItemKind == OrderItemKind.Combo,
        OrderItemKind.GroupAddon => parent.ItemKind == OrderItemKind.FixedDish
            || (parent.ItemKind == OrderItemKind.ComboPart && parent.DishId is not null),
        _ => false,
    };

    private OrderItem ActiveItem(Guid itemId) =>
        _items.SingleOrDefault(i => i.Id == itemId && i.Status == LineStatus.Active) ?? throw new NotFoundException("ORDER_ITEM", itemId);

    private IEnumerable<OrderItem> ChildrenOf(OrderItem item) =>
        _items.Where(i => i.ParentOrderItemId == item.Id && i.Status == LineStatus.Active);

    private IEnumerable<OrderItem> DescendantsOf(OrderItem item) =>
        ChildrenOf(item).SelectMany(c => DescendantsOf(c).Prepend(c));

    /// Produto das quantidades dos antepassados (1 no primeiro nível).
    private int AncestorQuantity(OrderItem item)
    {
        var quantity = 1;
        for (var current = item; current.ParentOrderItemId is { } parentId;)
        {
            current = _items.Single(i => i.Id == parentId);
            quantity *= current.Quantity;
        }

        return quantity;
    }

    private void RepriceDescendants(OrderItem item)
    {
        var ancestors = item.Quantity * AncestorQuantity(item);
        foreach (var child in ChildrenOf(item))
        {
            child.Reprice(ancestors);
            RepriceDescendants(child);
        }
    }

    /// O combo não vai à cozinha, mas as partes dele vão.
    private bool AffectsProduction(OrderItem item) => item.RequiresPreparation || DescendantsOf(item).Any(c => c.RequiresPreparation);

    /// Pedido não aceito: sem edição, produção, pagamento, cancelamento nem atendimento (ADR-0046 item 4).
    public void EnsureAccepted()
    {
        if (!IsAccepted)
        {
            throw new BusinessException(
                ErrorCodes.OrderNotAccepted,
                extensions: new Dictionary<string, object?> { ["acceptanceStatus"] = UpperSnake(AcceptanceStatus) });
        }
    }

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
        EnsureAccepted();
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
        new { itemId = item.Id, name = item.DisplayName, quantityBefore, quantityAfter };

    private static object WeighingDetails(WeighingEntry weighing) =>
        new { weighingId = weighing.Id, weightGrams = weighing.WeightGrams.Value, amount = weighing.Amount.ToString() };

    private static void EnsureQuantity(int quantity, string field)
    {
        if (quantity is < 1 or > 99)
        {
            throw BusinessException.Validation(field, "A quantidade deve estar entre 1 e 99.");
        }
    }

    /// ADR-0058 item 2: marmita é entrega ou retirada; PF e quilo, salão; à la carte, os três.
    private static void EnsureFulfillment(OrderType type, FulfillmentType fulfillment)
    {
        var ok = type switch
        {
            OrderType.Marmita => fulfillment is FulfillmentType.Delivery or FulfillmentType.Pickup,
            OrderType.Pf or OrderType.Kilo => fulfillment == FulfillmentType.DineIn,
            OrderType.ALaCarte => true,
            _ => false,
        };
        if (!ok)
        {
            throw BusinessException.Validation("fulfillmentType", "Marmita é entrega ou retirada; PF e quilo são no salão.");
        }
    }

    private void ApplyDeliveryPricing(DeliveryPricing? pricing)
    {
        if (pricing is { Source: Contracts.DeliveryFeeSource.Distance } && (pricing.DistanceMeters is null || pricing.PricePerKm is null || pricing.MinimumFee is null))
        {
            throw new ArgumentException("Taxa por distância exige metros, preço por km e mínimo.", nameof(pricing));
        }

        DeliveryFeeSource = pricing?.Source;
        DeliveryDistanceMeters = pricing?.DistanceMeters;
        DeliveryPricePerKm = pricing?.PricePerKm;
        DeliveryMinimumFee = pricing?.MinimumFee;
    }

    /// Nome e telefone do pedido do app (ADR-0059 itens 1 e 2, Q-312): marmita sempre com nome e, na entrega, com
    /// telefone (a retirada é o balcão); à la carte de entrega ou retirada (o delivery por telefone) com os dois. A
    /// comanda do salão e o PF/quilo não exigem nada.
    public static (bool Name, bool Phone) ContactRequired(OrderType type, FulfillmentType fulfillment) => type switch
    {
        OrderType.Marmita => (true, fulfillment == FulfillmentType.Delivery),
        OrderType.ALaCarte when fulfillment != FulfillmentType.DineIn => (true, true),
        _ => (false, false),
    };

    private static void EnsureCustomerContact(
        OrderType type, FulfillmentType fulfillment, string? name, string? phone, bool checkName, bool checkPhone)
    {
        var (nameRequired, phoneRequired) = ContactRequired(type, fulfillment);
        if (checkName && nameRequired && string.IsNullOrWhiteSpace(name))
        {
            throw BusinessException.Validation(
                "customerName", type == OrderType.Marmita ? "Informe o nome do cliente da marmita." : "Informe o nome do cliente.");
        }

        if (checkPhone && phoneRequired && string.IsNullOrWhiteSpace(phone))
        {
            throw BusinessException.Validation(
                "customerPhone", fulfillment == FulfillmentType.Delivery ? "Informe o telefone do cliente da entrega." : "Informe o telefone do cliente.");
        }
    }

    /// Forma prevista (ADR-0059 item 5, Q-316): obrigatória na entrega, opcional na retirada e proibida no salão.
    private static void EnsureExpectedPayment(FulfillmentType fulfillment, ExpectedPaymentMethod? method)
    {
        if (fulfillment == FulfillmentType.DineIn && method is not null)
        {
            throw BusinessException.Validation("expectedPaymentMethod", "Pedido no salão não tem forma de pagamento prevista.");
        }

        if (fulfillment == FulfillmentType.Delivery && method is null)
        {
            throw BusinessException.Validation("expectedPaymentMethod", "Informe como o cliente vai pagar na entrega.");
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

    private static string UpperSnake<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        string.Concat(value.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : c.ToString())).ToUpperInvariant();
}

internal sealed class OrderItem : Entity, ITenantOwned
{
    private readonly List<OrderItemComponent> _components = [];

    private OrderItem()
    {
    }

    /// `ancestorsQuantity`: produto das quantidades do pai e do avô (1 no primeiro nível).
    internal OrderItem(Guid id, Guid tenantId, Guid orderId, OrderItem? parent, NewItem spec, int ancestorsQuantity, Guid? by, Func<Guid> newId, DateTimeOffset now)
    {
        Id = id;
        TenantId = tenantId;
        OrderId = orderId;
        ParentOrderItemId = parent?.Id;
        // A API desta versão sempre grava o tipo do pai (05 §5.17 0010, janela do deploy); o prato do pai só no GROUP_ADDON.
        ParentItemKind = parent?.ItemKind;
        ParentDishId = spec.Kind == OrderItemKind.GroupAddon ? parent?.DishId : null;
        ItemKind = spec.Kind;
        ProductId = spec.ProductId;
        MarmitaSizeId = spec.MarmitaSizeId;
        var fixedRefs = spec.Fixed ?? FixedMenuRefs.None;
        DishId = fixedRefs.DishId;
        DishSizeId = fixedRefs.DishSizeId;
        BeverageSizeId = fixedRefs.BeverageSizeId;
        BeverageFlavorId = fixedRefs.BeverageFlavorId;
        ComboId = fixedRefs.ComboId;
        AddonOptionId = fixedRefs.AddonOptionId;
        SizeNameSnapshot = fixedRefs.SizeName;
        FlavorNameSnapshot = fixedRefs.FlavorName;
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
        Reprice(ancestorsQuantity);
    }

    public Guid TenantId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid? ParentOrderItemId { get; private set; }

    /// Cópia do tipo e do prato do pai, para os CHECKs da hierarquia (05 §5.17 0010). Nulos no primeiro nível; o
    /// `ADDON` gravado pela API anterior na janela do deploy pode estar sem o tipo do pai.
    public OrderItemKind? ParentItemKind { get; private set; }
    public Guid? ParentDishId { get; private set; }
    public OrderItemKind ItemKind { get; private set; }
    public Guid? ProductId { get; private set; }
    public Guid? MarmitaSizeId { get; private set; }

    // Cardápio fixo (ADR-0058 item 8): cada tipo com as suas referências (`ck_order_items_refs`).
    public Guid? DishId { get; private set; }
    public Guid? DishSizeId { get; private set; }
    public Guid? BeverageSizeId { get; private set; }
    public Guid? BeverageFlavorId { get; private set; }
    public Guid? ComboId { get; private set; }
    public Guid? AddonOptionId { get; private set; }

    /// Snapshot do tamanho e do sabor, exatamente quando há tamanho e sabor (`ck_order_items_snapshots`).
    public string? SizeNameSnapshot { get; private set; }
    public string? FlavorNameSnapshot { get; private set; }

    /// "Refrigerante Lata 350 ml · Coca-Cola Zero", "Açaí 500 ml", "Marmita M": nome base + tamanho + sabor.
    public string DisplayName => DisplayNameOf(NameSnapshot, SizeNameSnapshot, FlavorNameSnapshot);

    public static string DisplayNameOf(string name, string? size, string? flavor) =>
        name + (size is null ? string.Empty : " " + size) + (flavor is null ? string.Empty : " · " + flavor);
    public int Quantity { get; private set; }
    public Money UnitPrice { get; private set; }
    public Money LineTotal { get; private set; }
    public string NameSnapshot { get; private set; } = null!;
    public string? CompositionSnapshot { get; private set; }
    public bool RequiresPreparation { get; private set; }
    public string? Notes { get; private set; }
    public LineStatus Status { get; private set; }

    /// Nulo nos itens montados pelo cliente na página (`Origin = WEB`).
    public Guid? CreatedByEmployeeId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? RemovedByEmployeeId { get; private set; }
    public DateTimeOffset? RemovedAt { get; private set; }
    public IReadOnlyList<OrderItemComponent> Components => _components;

    /// Filho: `UnitPrice × Quantity × quantidade do pai × quantidade do avô` (04 §4.7, ADR-0058 item 9).
    internal void Reprice(int ancestorsQuantity) => LineTotal = UnitPrice * (Quantity * ancestorsQuantity);

    internal void ClearNotes() => Notes = null;

    internal void Change(int quantity, string? notes, int ancestorsQuantity)
    {
        Quantity = quantity;
        Notes = notes;
        Reprice(ancestorsQuantity);
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
    IReadOnlyList<NewItem> Addons,
    FixedMenuRefs? Fixed = null);

/// Referências e snapshots do cardápio fixo (ADR-0058 item 8). `Addons` do `NewItem` são os filhos: os `GROUP_ADDON`
/// do prato, ou as `COMBO_PART` do combo (com os `GROUP_ADDON` delas).
internal sealed record FixedMenuRefs(
    Guid? DishId = null,
    Guid? DishSizeId = null,
    Guid? BeverageSizeId = null,
    Guid? BeverageFlavorId = null,
    Guid? ComboId = null,
    Guid? AddonOptionId = null,
    string? SizeName = null,
    string? FlavorName = null)
{
    public static readonly FixedMenuRefs None = new();
}

internal sealed record NewWeighing(Grams Grams, Money PricePerKg);

/// Dados do pedido da página fora do `NewOrder` (ADR-0044 item 19, ADR-0045 item 12).
internal sealed record WebOrderExtras(ExpectedPaymentMethod? ExpectedPaymentMethod, Money? ChangeFor, WebOrderRiskFlag? RiskFlag);

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
    IReadOnlyList<NewWeighing> Weighings,
    DeliveryPricing? Pricing = null,
    ExpectedPaymentMethod? ExpectedPaymentMethod = null,
    Money? ChangeFor = null);

/// Origem da taxa de entrega e snapshot da regra por distância (ADR-0048 item 20).
internal sealed record DeliveryPricing(DeliveryFeeSource Source, int? DistanceMeters = null, Money? PricePerKm = null, Money? MinimumFee = null);

internal sealed record OrderDetailsChanges(
    Optional<string?> CustomerName,
    Optional<string?> CustomerPhone,
    Optional<FulfillmentType> FulfillmentType,
    Optional<DeliveryAddressDto?> DeliveryAddress,
    Optional<Money> DeliveryFee,
    Optional<string?> TableLabel,
    Optional<string?> TabNumber,
    Optional<string?> Notes,
    Optional<ExpectedPaymentMethod?> ExpectedPaymentMethod = default,
    Optional<Money?> ChangeFor = default);

// ---------- Eventos de domínio (06 §6.14) ----------

internal sealed record OrderCreated(Guid OrderId, int OrderNumber, OrderType OrderType, string? Label, DateTimeOffset OccurredAt) : IDomainEvent;

/// `ItemCount`: unidades do primeiro nível do pedido (marmitas, pratos, combos e bebidas, ADR-0058 item 14).
internal sealed record OrderAwaitingAcceptance(
    Guid OrderId, FulfillmentType FulfillmentType, int MarmitaCount, Money TotalAmount, DateTimeOffset AcceptanceDeadline, DateTimeOffset OccurredAt,
    int ItemCount = 0)
    : IDomainEvent;

internal sealed record OrderAccepted(Guid OrderId, int OrderNumber, Guid? AcceptedBy, string? AcceptedByName, DateTimeOffset OccurredAt) : IDomainEvent;

/// Recusado, expirado ou desistência: o pedido sai de tudo (ADR-0046 item 23).
internal sealed record OrderRejected(Guid OrderId, AcceptanceStatus AcceptanceStatus, RejectionReason? ReasonCode, DateTimeOffset OccurredAt) : IDomainEvent;

internal sealed record OrderFulfillmentChanged(Guid OrderId, int? OrderNumber, FulfillmentStatus From, FulfillmentStatus To, DateTimeOffset OccurredAt) : IDomainEvent;

internal sealed record OrderUpdated(Guid OrderId, int? OrderNumber, OrderType OrderType, ProductionStatus? ProductionStatus, bool IsPaid, DateTimeOffset OccurredAt) : IDomainEvent;

internal sealed record OrderStatusChanged(Guid OrderId, int? OrderNumber, ProductionStatus From, ProductionStatus To, DateTimeOffset OccurredAt) : IDomainEvent;

internal sealed record OrderCanceled(Guid OrderId, int? OrderNumber, DateTimeOffset OccurredAt) : IDomainEvent;

internal sealed record OrderAlertCreated(Guid OrderId, Guid EventId, int? OrderNumber, OrderChangeKind ChangeKind, bool AffectsProduction, DateTimeOffset OccurredAt) : IDomainEvent;

internal sealed record OrderPaymentRegistered(Guid OrderId, int? OrderNumber, Money PaidAmount, Money Balance, DateTimeOffset OccurredAt) : IDomainEvent;

internal sealed record OrderPaymentConfirmed(Guid OrderId, int? OrderNumber, DateTimeOffset OccurredAt) : IDomainEvent;

internal sealed record OrderPaymentReversed(Guid OrderId, int? OrderNumber, bool IsPaid, DateTimeOffset OccurredAt) : IDomainEvent;
