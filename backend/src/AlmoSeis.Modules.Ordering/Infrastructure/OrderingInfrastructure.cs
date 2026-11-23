using System.Text.Json;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Ordering.Domain;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Modules.Ordering.Infrastructure;

/// Mapeamento de 05 §5.7 e §5.9.
internal sealed class OrderingModelConfiguration : IModuleModelConfiguration
{
    private static readonly JsonSerializerOptions AddressJson = new(JsonSerializerDefaults.Web);

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(b =>
        {
            b.ToTable("customers");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(80);
            b.Property(x => x.PhoneNumber).HasMaxLength(16);
            b.Property(x => x.Notes).HasMaxLength(300);
            b.HasMany(x => x.Addresses).WithOne().HasForeignKey(a => a.CustomerId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Addresses).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<CustomerAddress>(b =>
        {
            b.ToTable("customer_addresses");
            b.HasKey(x => x.Id);
            b.Ignore(x => x.WasChanged);
        });

        modelBuilder.Entity<Order>(b =>
        {
            b.ToTable("orders");
            b.HasKey(x => x.Id);
            b.Property(x => x.DeliveryAddress)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, AddressJson),
                    v => v == null ? null : JsonSerializer.Deserialize<DeliveryAddressDto>(v, AddressJson));
            b.Property(x => x.RejectionMessage).HasMaxLength(200);
            b.Property(x => x.DeliveryPricePerKm).HasPrecision(10, 2);
            b.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.OrderId);
            b.HasMany(x => x.Weighings).WithOne().HasForeignKey(w => w.OrderId);
            b.HasMany(x => x.ChangeEvents).WithOne().HasForeignKey(e => e.OrderId);
            b.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Navigation(x => x.Weighings).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Navigation(x => x.ChangeEvents).UsePropertyAccessMode(PropertyAccessMode.Field);
            // Relação "de ordenação": cliente criado junto com o pedido (saveCustomer) é inserido antes (07 §7.13).
            b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OrderItem>(b =>
        {
            b.ToTable("order_items");
            b.HasKey(x => x.Id);
            b.Property(x => x.NameSnapshot).HasMaxLength(80);
            b.Property(x => x.CompositionSnapshot).HasMaxLength(500);
            b.Property(x => x.Notes).HasMaxLength(200);
            b.Property(x => x.SizeNameSnapshot).HasMaxLength(30);
            b.Property(x => x.FlavorNameSnapshot).HasMaxLength(40);
            b.HasMany(x => x.Components).WithOne().HasForeignKey(c => c.OrderItemId);
            b.Navigation(x => x.Components).UsePropertyAccessMode(PropertyAccessMode.Field);
            // Adicional filho depois do pai (a FK real é composta com o pedido).
            b.HasOne<OrderItem>().WithMany().HasForeignKey(x => x.ParentOrderItemId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OrderItemComponent>(b =>
        {
            b.ToTable("order_item_components");
            b.HasKey(x => x.Id);
            b.Property(x => x.NameSnapshot).HasMaxLength(80);
        });

        modelBuilder.Entity<WeighingEntry>(b =>
        {
            b.ToTable("weighing_entries");
            b.HasKey(x => x.Id);
            b.Property(x => x.PricePerKg).HasPrecision(10, 2);
        });

        modelBuilder.Entity<OrderChangeEvent>(b =>
        {
            b.ToTable("order_change_events");
            b.HasKey(x => x.Id);
            b.Property(x => x.Details).HasColumnType("jsonb");
        });

        modelBuilder.Entity<AlertAcknowledgement>(b =>
        {
            b.ToTable("alert_acknowledgements");
            b.HasKey(x => new { x.OrderChangeEventId, x.TenantEmployeeId });
        });

        // Página de pedidos (05 §5.17, 0004). Relações "de ordenação": o pedido é inserido antes (07 §7.13).
        modelBuilder.Entity<OrderTrackingToken>(b =>
        {
            b.ToTable("order_tracking_tokens");
            b.HasKey(x => x.TokenHash);
            b.Property(x => x.TokenHash).HasMaxLength(64).IsFixedLength();
            b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WebOrderIntake>(b =>
        {
            b.ToTable("web_order_intakes");
            b.HasKey(x => x.OrderId);
            b.Property(x => x.IpHash).HasMaxLength(64).IsFixedLength();
            b.HasOne<Order>().WithOne().HasForeignKey<WebOrderIntake>(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        });

        // business_days é do Closing; aqui só a leitura de "expediente aberto" para a regra de alerta pendente.
        modelBuilder.Entity<OpenBusinessDayRow>(b =>
        {
            b.HasNoKey();
            b.ToSqlQuery("SELECT tenant_id, id FROM business_days WHERE status = 'OPEN'");
        });
    }
}

/// alert_acknowledgements: ciência de um alerta por funcionário (RN-008.7). Só leitura no EF; a escrita é SQL.
internal sealed class AlertAcknowledgement : ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid OrderChangeEventId { get; private set; }
    public Guid TenantEmployeeId { get; private set; }
    public DateTimeOffset AcknowledgedAt { get; private set; }
}

internal sealed class OpenBusinessDayRow : ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid Id { get; private set; }
}

/// Produto vendido não muda de `kind` (07 §7.13).
internal sealed class OrderProductUsageCheck(AppDbContext db) : IProductUsageCheck
{
    public async Task<bool> IsInUseAsync(Guid productId, CancellationToken cancellationToken) =>
        await db.Set<OrderItem>().AnyAsync(i => i.ProductId == productId, cancellationToken)
        || await db.Set<OrderItemComponent>().AnyAsync(c => c.ProductId == productId, cancellationToken);
}

// ---------- Tempo real (06 §6.14) ----------

internal sealed class OrderCreatedMapper(ITenantContext tenant) : IntegrationEventMapper<OrderCreated>
{
    protected override IEnumerable<IntegrationEnvelope> Map(OrderCreated e)
    {
        yield return new IntegrationEnvelope("OrderCreated", "ORDER", e.OrderId, tenant.EmployeeId,
            new { orderNumber = e.OrderNumber, orderType = UpperSnakeEnumConverter<OrderType>.ToText(e.OrderType), label = e.Label });
    }
}

internal sealed class OrderUpdatedMapper(ITenantContext tenant) : IntegrationEventMapper<OrderUpdated>
{
    protected override IEnumerable<IntegrationEnvelope> Map(OrderUpdated e)
    {
        yield return new IntegrationEnvelope("OrderUpdated", "ORDER", e.OrderId, tenant.EmployeeId, new
        {
            orderNumber = e.OrderNumber,
            orderType = UpperSnakeEnumConverter<OrderType>.ToText(e.OrderType),
            productionStatus = e.ProductionStatus is { } s ? UpperSnakeEnumConverter<ProductionStatus>.ToText(s) : null,
            isPaid = e.IsPaid,
        });
    }
}

internal sealed class OrderStatusChangedMapper(ITenantContext tenant) : IntegrationEventMapper<OrderStatusChanged>
{
    protected override IEnumerable<IntegrationEnvelope> Map(OrderStatusChanged e)
    {
        yield return new IntegrationEnvelope("OrderStatusChanged", "ORDER", e.OrderId, tenant.EmployeeId, new
        {
            orderNumber = e.OrderNumber,
            from = UpperSnakeEnumConverter<ProductionStatus>.ToText(e.From),
            to = UpperSnakeEnumConverter<ProductionStatus>.ToText(e.To),
        });
    }
}

internal sealed class OrderCanceledMapper(ITenantContext tenant) : IntegrationEventMapper<OrderCanceled>
{
    protected override IEnumerable<IntegrationEnvelope> Map(OrderCanceled e)
    {
        yield return new IntegrationEnvelope("OrderCanceled", "ORDER", e.OrderId, tenant.EmployeeId, new { orderNumber = e.OrderNumber });
    }
}

internal sealed class OrderAlertCreatedMapper(ITenantContext tenant) : IntegrationEventMapper<OrderAlertCreated>
{
    protected override IEnumerable<IntegrationEnvelope> Map(OrderAlertCreated e)
    {
        yield return new IntegrationEnvelope("OrderAlertCreated", "ORDER", e.OrderId, tenant.EmployeeId, new
        {
            orderNumber = e.OrderNumber,
            changeKind = UpperSnakeEnumConverter<OrderChangeKind>.ToText(e.ChangeKind),
            affectsProduction = e.AffectsProduction,
        });
    }
}

internal sealed class OrderPaymentRegisteredMapper(ITenantContext tenant) : IntegrationEventMapper<OrderPaymentRegistered>
{
    protected override IEnumerable<IntegrationEnvelope> Map(OrderPaymentRegistered e)
    {
        yield return new IntegrationEnvelope("OrderPaymentRegistered", "ORDER", e.OrderId, tenant.EmployeeId,
            new { orderNumber = e.OrderNumber, paidAmount = e.PaidAmount.ToString(), balance = e.Balance.ToString() });
    }
}

internal sealed class OrderPaymentConfirmedMapper(ITenantContext tenant) : IntegrationEventMapper<OrderPaymentConfirmed>
{
    protected override IEnumerable<IntegrationEnvelope> Map(OrderPaymentConfirmed e)
    {
        yield return new IntegrationEnvelope("OrderPaymentConfirmed", "ORDER", e.OrderId, tenant.EmployeeId, new { orderNumber = e.OrderNumber });
    }
}

internal sealed class OrderPaymentReversedMapper(ITenantContext tenant) : IntegrationEventMapper<OrderPaymentReversed>
{
    protected override IEnumerable<IntegrationEnvelope> Map(OrderPaymentReversed e)
    {
        yield return new IntegrationEnvelope("OrderPaymentReversed", "ORDER", e.OrderId, tenant.EmployeeId,
            new { orderNumber = e.OrderNumber, isPaid = e.IsPaid });
    }
}

/// Pedido do site aguardando aceite (ADR-0046 item 23): sem nome, telefone nem endereço.
internal sealed class OrderAwaitingAcceptanceMapper(ITenantContext tenant) : IntegrationEventMapper<OrderAwaitingAcceptance>
{
    protected override IEnumerable<IntegrationEnvelope> Map(OrderAwaitingAcceptance e)
    {
        yield return new IntegrationEnvelope("OrderAwaitingAcceptance", "ORDER", e.OrderId, tenant.EmployeeId, new
        {
            fulfillmentType = UpperSnakeEnumConverter<FulfillmentType>.ToText(e.FulfillmentType),
            marmitaCount = e.MarmitaCount,
            totalAmount = e.TotalAmount.ToString(),
            acceptanceDeadline = e.AcceptanceDeadline,
        });
    }
}

/// Push do pedido do site (ADR-0049 itens 2 e 8): o aviso na hora e o lembrete agendado (ADR-0046 item 15), mensagens
/// internas do outbox na mesma transação do envio. O lembrete só existe se cair antes do prazo de aceite; o consumidor
/// ainda confere, na hora de enviar, se o pedido continua aguardando.
internal sealed class WebOrderPushMapper(ITenantContext tenant, Microsoft.Extensions.Options.IOptions<AlmoSeis.Modules.Ordering.Application.WebOrderOptions> options)
    : IntegrationEventMapper<OrderAwaitingAcceptance>
{
    protected override IEnumerable<IntegrationEnvelope> Map(OrderAwaitingAcceptance e)
    {
        var fulfillmentType = UpperSnakeEnumConverter<FulfillmentType>.ToText(e.FulfillmentType);
        yield return new IntegrationEnvelope(WebOrderPushMessage.EventType, "ORDER", e.OrderId, tenant.EmployeeId, new
        {
            kind = WebOrderPushMessage.AwaitingAcceptance,
            fulfillmentType,
            marmitaCount = e.MarmitaCount,
        });

        if (options.Value.ReminderMinutes > 0 && e.OccurredAt.AddMinutes(options.Value.ReminderMinutes) is var remindAt && remindAt < e.AcceptanceDeadline)
        {
            yield return new IntegrationEnvelope(WebOrderPushMessage.EventType, "ORDER", e.OrderId, tenant.EmployeeId, new
            {
                kind = WebOrderPushMessage.Reminder,
                fulfillmentType,
                marmitaCount = e.MarmitaCount,
            }, remindAt);
        }
    }
}

/// Para o push (Notifications): o aceite ainda está em aberto?
internal sealed class WebOrderAcceptanceReader(AppDbContext db) : IWebOrderAcceptanceReader
{
    public async Task<WebOrderAcceptanceState?> GetAsync(Guid orderId, CancellationToken cancellationToken) =>
        await db.Set<Order>().AsNoTracking()
            .Where(o => o.Id == orderId)
            .Select(o => new WebOrderAcceptanceState(o.Id, o.AcceptanceStatus, o.AcceptanceDeadline))
            .SingleOrDefaultAsync(cancellationToken);
}

internal sealed class OrderAcceptedMapper(ITenantContext tenant) : IntegrationEventMapper<OrderAccepted>
{
    protected override IEnumerable<IntegrationEnvelope> Map(OrderAccepted e)
    {
        yield return new IntegrationEnvelope("OrderAccepted", "ORDER", e.OrderId, tenant.EmployeeId, new
        {
            orderNumber = e.OrderNumber,
            acceptedBy = e.AcceptedBy is { } id ? new { id, displayName = e.AcceptedByName ?? string.Empty } : null,
        });
    }
}

internal sealed class OrderRejectedMapper(ITenantContext tenant) : IntegrationEventMapper<OrderRejected>
{
    protected override IEnumerable<IntegrationEnvelope> Map(OrderRejected e)
    {
        yield return new IntegrationEnvelope("OrderRejected", "ORDER", e.OrderId, tenant.EmployeeId, new
        {
            acceptanceStatus = UpperSnakeEnumConverter<AcceptanceStatus>.ToText(e.AcceptanceStatus),
            reasonCode = e.ReasonCode is { } r ? UpperSnakeEnumConverter<RejectionReason>.ToText(r) : null,
        });
    }
}

internal sealed class OrderFulfillmentChangedMapper(ITenantContext tenant) : IntegrationEventMapper<OrderFulfillmentChanged>
{
    protected override IEnumerable<IntegrationEnvelope> Map(OrderFulfillmentChanged e)
    {
        yield return new IntegrationEnvelope("OrderFulfillmentChanged", "ORDER", e.OrderId, tenant.EmployeeId, new
        {
            orderNumber = e.OrderNumber,
            from = UpperSnakeEnumConverter<FulfillmentStatus>.ToText(e.From),
            to = UpperSnakeEnumConverter<FulfillmentStatus>.ToText(e.To),
        });
    }
}

internal sealed class OrderPaymentPort(AppDbContext db, TimeProvider time) : IOrderPaymentPort
{
    public async Task<OrderPaymentSnapshot> LoadForPaymentAsync(Guid orderId, int expectedVersion, CancellationToken cancellationToken)
    {
        var order = await LoadAsync(orderId, cancellationToken);
        order.EnsureVersion(expectedVersion);
        // Pedido não aceito não recebe pagamento manual (ADR-0046 item 4).
        order.EnsureAccepted();
        if (order.CancellationStatus == CancellationStatus.Canceled)
        {
            throw new BusinessException(ErrorCodes.OrderCanceled);
        }

        return new OrderPaymentSnapshot(order.Id, order.Version, order.OrderNumber!.Value, order.TotalAmount, order.PaidAmount, order.Balance, IsCanceled: false);
    }

    public async Task<OrderBalanceDto> ApplyPaymentAsync(Guid orderId, Money amount, CancellationToken cancellationToken)
    {
        var order = await LoadAsync(orderId, cancellationToken);
        order.ApplyPayment(amount, time.GetUtcNow());
        return Balance(order);
    }

    public async Task<OrderBalanceDto> UndoPaymentAsync(Guid orderId, Money amount, CancellationToken cancellationToken)
    {
        var order = await LoadAsync(orderId, cancellationToken);
        order.UndoPayment(amount, time.GetUtcNow());
        return Balance(order);
    }

    // Itens não são carregados: pagamento não recalcula total. FindAsync devolve a instância já rastreada.
    private async Task<Order> LoadAsync(Guid orderId, CancellationToken cancellationToken) =>
        await db.Set<Order>().FindAsync([orderId], cancellationToken) ?? throw new NotFoundException("ORDER", orderId);

    private static OrderBalanceDto Balance(Order o) => new(o.Id, o.Version, o.TotalAmount, o.PaidAmount, o.Balance, o.IsPaid);
}
