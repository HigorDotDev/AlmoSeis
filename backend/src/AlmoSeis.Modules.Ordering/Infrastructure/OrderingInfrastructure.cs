using System.Text.Json;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Ordering.Domain;
using AlmoSeis.SharedKernel.Domain;
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
