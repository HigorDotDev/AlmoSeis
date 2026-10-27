using System.Buffers.Text;
using System.Text.Json;
using AlmoSeis.Infrastructure.Audit;
using AlmoSeis.Infrastructure.Concurrency;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Persistence.Entities;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Closing.Contracts;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Ordering.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using AlmoSeis.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Modules.Ordering.Application;

/// POST /orders [I] (06 §6.9).
internal sealed class CreateOrderHandler(
    AppDbContext db,
    ITenantContext tenant,
    IIdGenerator ids,
    IBusinessDayReader businessDays,
    ITenantSettingsReader settings,
    IPermissionChecker permissions,
    OrderItemResolver items,
    CustomerResolver customers,
    OrderNumberGenerator numbers,
    IAuditWriter audit,
    OrderDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<CreateOrderCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(CreateOrderCommand command, CancellationToken cancellationToken)
    {
        var day = await businessDays.GetOpenAsync(cancellationToken) ?? throw new BusinessException(ErrorCodes.BusinessDayRequired);
        var config = await settings.GetAsync(cancellationToken);

        // Taxa nula = padrão (só em entrega). Valor diferente do padrão exige permissão (ADR-0021).
        Money fee;
        if (command.FulfillmentType == FulfillmentType.Delivery)
        {
            fee = command.DeliveryFee ?? config.DefaultDeliveryFee;
            OrderRules.EnsureCanSetDeliveryFee(permissions, fee, config.DefaultDeliveryFee);
        }
        else
        {
            fee = command.DeliveryFee ?? Money.Zero;
        }

        var weighings = OrderRules.Weighings(command.Weighings ?? [], config.KiloPricePerKg);
        var resolvedItems = await items.ResolveAsync(command.Items, day.BusinessDate, cancellationToken);
        var customer = await customers.ResolveAsync(command, cancellationToken);
        var number = await numbers.NextAsync(day.BusinessDate, cancellationToken);

        var order = Order.Create(
            ids.New(), tenant.TenantId!.Value,
            new NewOrder(
                command.OrderType, command.FulfillmentType, customer.CustomerId, customer.Name, customer.Phone, customer.Address, fee,
                command.TableLabel, command.TabNumber, command.Notes, resolvedItems, weighings),
            day.Id, day.BusinessDate, number, tenant.EmployeeId!.Value, ids.New, time.GetUtcNow());
        db.Add(order);

        audit.Add("OrderCreated", order.SyncEntityType, order.Id, order.Id, after: new
        {
            order.OrderNumber,
            orderType = UpperSnakeEnumConverter<OrderType>.ToText(order.OrderType),
            itemCount = order.Items.Count,
            weighingCount = order.Weighings.Count,
            deliveryFee = order.DeliveryFee.ToString(),
            totalAmount = order.TotalAmount.ToString(),
            order.CustomerId,
        });
        return await dtos.BuildAsync(order, cancellationToken);
    }
}

/// PATCH /orders/{id} [M].
internal sealed class UpdateOrderHandler(
    AppDbContext db,
    ITenantContext tenant,
    IIdGenerator ids,
    IConcurrencyContext concurrency,
    ITenantSettingsReader settings,
    IPermissionChecker permissions,
    IAuditWriter audit,
    OrderDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<UpdateOrderCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(UpdateOrderCommand command, CancellationToken cancellationToken)
    {
        var order = await OrderRules.LoadAsync(db, command.OrderId, cancellationToken);
        order.EnsureVersion(concurrency.ExpectedVersion);

        var c = command.Changes;
        var defaultFee = (await settings.GetAsync(cancellationToken)).DefaultDeliveryFee;
        if (c.DeliveryFee.HasValue && c.DeliveryFee.Value != order.DeliveryFee)
        {
            OrderRules.EnsureCanSetDeliveryFee(permissions, c.DeliveryFee.Value, defaultFee);
        }

        var before = Details(order);
        var version = order.Version;
        order.UpdateDetails(
            new OrderDetailsChanges(c.CustomerName, c.CustomerPhone, c.FulfillmentType, c.DeliveryAddress, c.DeliveryFee, c.TableLabel, c.TabNumber, c.Notes),
            defaultFee, tenant.EmployeeId!.Value, ids.New, time.GetUtcNow());

        if (order.Version != version)
        {
            audit.Add("OrderDetailsUpdated", order.SyncEntityType, order.Id, order.Id, before, Details(order));
        }

        return await dtos.BuildAsync(order, cancellationToken);
    }

    // Sem telefone nem endereço na auditoria (ADR-0024).
    private static object Details(Order o) => new
    {
        fulfillmentType = UpperSnakeEnumConverter<FulfillmentType>.ToText(o.FulfillmentType),
        deliveryFee = o.DeliveryFee.ToString(),
        o.TableLabel,
        o.TabNumber,
        o.Notes,
        totalAmount = o.TotalAmount.ToString(),
    };
}

/// POST /orders/{id}/items [I][M].
internal sealed class AddOrderItemsHandler(
    AppDbContext db,
    ITenantContext tenant,
    IIdGenerator ids,
    IConcurrencyContext concurrency,
    OrderItemResolver resolver,
    IAuditWriter audit,
    OrderDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<AddOrderItemsCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(AddOrderItemsCommand command, CancellationToken cancellationToken)
    {
        var order = await OrderRules.LoadAsync(db, command.OrderId, cancellationToken);
        order.EnsureVersion(concurrency.ExpectedVersion);

        // O cardápio é o da data do pedido, não o de hoje.
        var specs = await resolver.ResolveAsync(command.Request.Items, order.BusinessDate, cancellationToken);
        var added = order.AddItems(specs, tenant.EmployeeId!.Value, ids.New, time.GetUtcNow());

        foreach (var item in added)
        {
            audit.Add("OrderItemAdded", order.SyncEntityType, order.Id, order.Id, after: new
            {
                itemId = item.Id, name = item.NameSnapshot, item.Quantity, lineTotal = item.LineTotal.ToString(), orderTotal = order.TotalAmount.ToString(),
            });
        }

        return await dtos.BuildAsync(order, cancellationToken);
    }
}

/// PATCH /orders/{id}/items/{itemId} [M].
internal sealed class UpdateOrderItemHandler(
    AppDbContext db,
    ITenantContext tenant,
    IIdGenerator ids,
    IConcurrencyContext concurrency,
    IAuditWriter audit,
    OrderDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<UpdateOrderItemCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(UpdateOrderItemCommand command, CancellationToken cancellationToken)
    {
        var order = await OrderRules.LoadAsync(db, command.OrderId, cancellationToken);
        order.EnsureVersion(concurrency.ExpectedVersion);

        var item = order.Items.SingleOrDefault(i => i.Id == command.ItemId) ?? throw new NotFoundException("ORDER_ITEM", command.ItemId);
        var before = new { itemId = item.Id, name = item.NameSnapshot, item.Quantity, item.Notes, lineTotal = item.LineTotal.ToString(), orderTotal = order.TotalAmount.ToString() };
        var version = order.Version;
        order.ChangeItem(command.ItemId, command.Changes.Quantity, command.Changes.Notes, tenant.EmployeeId!.Value, ids.New, time.GetUtcNow());

        if (order.Version != version)
        {
            audit.Add("OrderItemChanged", order.SyncEntityType, order.Id, order.Id, before, new
            {
                itemId = item.Id, name = item.NameSnapshot, item.Quantity, item.Notes, lineTotal = item.LineTotal.ToString(), orderTotal = order.TotalAmount.ToString(),
            });
        }

        return await dtos.BuildAsync(order, cancellationToken);
    }
}

/// DELETE /orders/{id}/items/{itemId} [M].
internal sealed class RemoveOrderItemHandler(
    AppDbContext db,
    ITenantContext tenant,
    IIdGenerator ids,
    IConcurrencyContext concurrency,
    IAuditWriter audit,
    OrderDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<RemoveOrderItemCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(RemoveOrderItemCommand command, CancellationToken cancellationToken)
    {
        var order = await OrderRules.LoadAsync(db, command.OrderId, cancellationToken);
        order.EnsureVersion(concurrency.ExpectedVersion);

        var item = order.Items.SingleOrDefault(i => i.Id == command.ItemId) ?? throw new NotFoundException("ORDER_ITEM", command.ItemId);
        order.RemoveItem(command.ItemId, tenant.EmployeeId!.Value, ids.New, time.GetUtcNow());

        audit.Add("OrderItemRemoved", order.SyncEntityType, order.Id, order.Id,
            before: new { itemId = item.Id, name = item.NameSnapshot, item.Quantity, lineTotal = item.LineTotal.ToString() },
            after: new { orderTotal = order.TotalAmount.ToString() });
        return await dtos.BuildAsync(order, cancellationToken);
    }
}

/// POST /orders/{id}/weighings [I][M]: preço/kg vigente agora (snapshot, RN-006.3).
internal sealed class AddWeighingsHandler(
    AppDbContext db,
    ITenantContext tenant,
    IIdGenerator ids,
    IConcurrencyContext concurrency,
    ITenantSettingsReader settings,
    IAuditWriter audit,
    OrderDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<AddWeighingsCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(AddWeighingsCommand command, CancellationToken cancellationToken)
    {
        var order = await OrderRules.LoadAsync(db, command.OrderId, cancellationToken);
        order.EnsureVersion(concurrency.ExpectedVersion);

        if (command.Request.Weighings.Count == 0)
        {
            throw BusinessException.Validation("weighings", "Informe pelo menos uma pesagem.");
        }

        var weighings = OrderRules.Weighings(command.Request.Weighings, (await settings.GetAsync(cancellationToken)).KiloPricePerKg);
        var added = order.AddWeighings(weighings, tenant.EmployeeId!.Value, ids.New, time.GetUtcNow());

        foreach (var w in added)
        {
            audit.Add("WeighingAdded", order.SyncEntityType, order.Id, order.Id, after: new
            {
                weighingId = w.Id, weightGrams = w.WeightGrams.Value, pricePerKg = w.PricePerKg.ToString(), amount = w.Amount.ToString(), orderTotal = order.TotalAmount.ToString(),
            });
        }

        return await dtos.BuildAsync(order, cancellationToken);
    }
}

/// DELETE /orders/{id}/weighings/{weighingId} [M].
internal sealed class RemoveWeighingHandler(
    AppDbContext db,
    ITenantContext tenant,
    IIdGenerator ids,
    IConcurrencyContext concurrency,
    IAuditWriter audit,
    OrderDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<RemoveWeighingCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(RemoveWeighingCommand command, CancellationToken cancellationToken)
    {
        var order = await OrderRules.LoadAsync(db, command.OrderId, cancellationToken);
        order.EnsureVersion(concurrency.ExpectedVersion);

        order.RemoveWeighing(command.WeighingId, tenant.EmployeeId!.Value, ids.New, time.GetUtcNow());
        var w = order.Weighings.Single(x => x.Id == command.WeighingId);
        audit.Add("WeighingRemoved", order.SyncEntityType, order.Id, order.Id,
            before: new { weighingId = w.Id, weightGrams = w.WeightGrams.Value, amount = w.Amount.ToString() },
            after: new { orderTotal = order.TotalAmount.ToString() });
        return await dtos.BuildAsync(order, cancellationToken);
    }
}

/// PATCH /orders/{id}/status [M].
internal sealed class ChangeProductionStatusHandler(
    AppDbContext db,
    IConcurrencyContext concurrency,
    IAuditWriter audit,
    OrderDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<ChangeProductionStatusCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(ChangeProductionStatusCommand command, CancellationToken cancellationToken)
    {
        var order = await OrderRules.LoadAsync(db, command.OrderId, cancellationToken);
        order.EnsureVersion(concurrency.ExpectedVersion);

        var from = order.ProductionStatus;
        order.ChangeProductionStatus(command.Request.ProductionStatus, time.GetUtcNow());
        if (order.ProductionStatus != from)
        {
            audit.Add("ProductionStatusChanged", order.SyncEntityType, order.Id, order.Id,
                before: new { productionStatus = Text(from) }, after: new { productionStatus = Text(order.ProductionStatus) });
        }

        return await dtos.BuildAsync(order, cancellationToken);
    }

    private static string? Text(ProductionStatus? status) => status is { } s ? UpperSnakeEnumConverter<ProductionStatus>.ToText(s) : null;
}

/// POST /orders/{id}/cancel [M] (ADR-0022).
internal sealed class CancelOrderHandler(
    AppDbContext db,
    ITenantContext tenant,
    IIdGenerator ids,
    IConcurrencyContext concurrency,
    IAuditWriter audit,
    OrderDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<CancelOrderCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(CancelOrderCommand command, CancellationToken cancellationToken)
    {
        var order = await OrderRules.LoadAsync(db, command.OrderId, cancellationToken);
        order.EnsureVersion(concurrency.ExpectedVersion);

        order.Cancel(command.Request.Reason, tenant.EmployeeId!.Value, ids.New, time.GetUtcNow());
        audit.Add("OrderCanceled", order.SyncEntityType, order.Id, order.Id,
            before: new { cancellationStatus = "ACTIVE", totalAmount = order.TotalAmount.ToString() },
            after: new { cancellationStatus = "CANCELED", reason = order.CancellationReason });
        return await dtos.BuildAsync(order, cancellationToken);
    }
}

internal sealed class GetOrderQueryHandler(AppDbContext db, OrderDtoBuilder dtos) : IQueryHandler<GetOrderQuery, OrderDto>
{
    public async Task<OrderDto> HandleAsync(GetOrderQuery query, CancellationToken cancellationToken)
    {
        var order = await db.Set<Order>().AsNoTracking()
            .Include(o => o.Items).ThenInclude(i => i.Components)
            .Include(o => o.Weighings)
            .AsSplitQuery()
            .SingleOrDefaultAsync(o => o.Id == query.OrderId, cancellationToken)
            ?? throw new NotFoundException("ORDER", query.OrderId);
        return await dtos.BuildAsync(order, cancellationToken);
    }
}

/// `current` do 409 nas rotas [M] de pedido.
internal sealed class OrderCurrentReader(IQueryHandler<GetOrderQuery, OrderDto> query) : ICurrentResourceReader
{
    public string EntityType => "ORDER";

    public async Task<IVersionedDto?> ReadAsync(Guid entityId, CancellationToken cancellationToken) =>
        await query.HandleAsync(new GetOrderQuery(entityId), cancellationToken);
}

/// GET /orders: padrão = expediente aberto; `allDays` ignora o expediente. Mais recentes primeiro (id UUID v7).
internal sealed class ListOrdersQueryHandler(AppDbContext db, IBusinessDayReader businessDays, AlertCounter alerts)
    : IQueryHandler<ListOrdersQuery, PageDto<OrderSummaryDto>>
{
    public async Task<PageDto<OrderSummaryDto>> HandleAsync(ListOrdersQuery query, CancellationToken cancellationToken)
    {
        Paging.EnsureLimit(query.Limit);
        var orders = db.Set<Order>().AsNoTracking();

        if (!query.AllDays)
        {
            var dayId = query.BusinessDayId ?? (await businessDays.GetOpenAsync(cancellationToken))?.Id;
            if (dayId is null)
            {
                return new PageDto<OrderSummaryDto>([], null);
            }

            orders = orders.Where(o => o.BusinessDayId == dayId);
        }

        if (query.OrderType is { } type)
        {
            orders = orders.Where(o => o.OrderType == type);
        }

        if (query.IsPaid is { } isPaid)
        {
            orders = orders.Where(o => o.IsPaid == isPaid);
        }

        if (query.CancellationStatus is { } cancellation)
        {
            orders = orders.Where(o => o.CancellationStatus == cancellation);
        }

        if (query.ProductionStatus is { } production)
        {
            orders = orders.Where(o => o.ProductionStatus == production);
        }

        if (query.CustomerId is { } customerId)
        {
            orders = orders.Where(o => o.CustomerId == customerId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{Paging.EscapeLike(query.Search.Trim())}%";
            orders = orders.Where(o => EF.Functions.ILike(o.CustomerName ?? "", pattern, "\\")
                || EF.Functions.ILike(o.TableLabel ?? "", pattern, "\\") || EF.Functions.ILike(o.TabNumber ?? "", pattern, "\\"));
        }

        if (query.Cursor is { } cursor)
        {
            var after = Paging.DecodeCursor(cursor);
            orders = orders.Where(o => o.Id.CompareTo(after) < 0);
        }

        var rows = await orders.OrderByDescending(o => o.Id).Take(query.Limit + 1)
            .Select(o => new
            {
                o.Id, o.Version, o.OrderNumber, o.BusinessDate, o.OrderType, o.FulfillmentType, o.CustomerName, o.TableLabel, o.TabNumber,
                o.TotalAmount, o.PaidAmount, o.IsPaid, o.ProductionStatus, o.CancellationStatus, o.CreatedAt,
                ItemCount = o.Items.Count(i => i.Status == LineStatus.Active),
            })
            .ToListAsync(cancellationToken);

        var page = rows.Take(query.Limit).ToList();
        var counts = await alerts.CountForOrdersAsync(page.Select(r => r.Id).ToList(), cancellationToken);
        var items = page
            .Select(r => new OrderSummaryDto(
                r.Id, r.Version, r.OrderNumber, r.BusinessDate, r.OrderType, r.FulfillmentType, r.CustomerName, r.TableLabel, r.TabNumber,
                r.TotalAmount, r.PaidAmount, r.TotalAmount - r.PaidAmount, r.IsPaid, r.ProductionStatus, r.CancellationStatus, r.ItemCount,
                counts.GetValueOrDefault(r.Id), r.CreatedAt))
            .ToList();
        return new PageDto<OrderSummaryDto>(items, rows.Count > query.Limit ? Paging.EncodeCursor(items[^1].Id) : null);
    }
}

/// GET /production/orders: MARMITA e PF ativos do expediente aberto, do mais antigo ao mais novo (RN-007.4/7.5).
internal sealed class GetProductionBoardQueryHandler(AppDbContext db, IBusinessDayReader businessDays, AlertCounter alerts)
    : IQueryHandler<GetProductionBoardQuery, ProductionBoardDto>
{
    public async Task<ProductionBoardDto> HandleAsync(GetProductionBoardQuery query, CancellationToken cancellationToken)
    {
        var day = await businessDays.GetOpenAsync(cancellationToken) ?? throw new BusinessException(ErrorCodes.BusinessDayRequired);
        var orders = await db.Set<Order>().AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.BusinessDayId == day.Id && o.OrderType != OrderType.Kilo && o.CancellationStatus == CancellationStatus.Active)
            .OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
            .ToListAsync(cancellationToken);
        var counts = await alerts.CountForOrdersAsync(orders.Select(o => o.Id).ToList(), cancellationToken);

        var board = orders.Select(o =>
        {
            var active = o.Items.Where(i => i.Status == LineStatus.Active).ToList();
            var items = active.Where(i => i.ParentOrderItemId is null).OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
                .Select(i => new ProductionItemDto(
                    i.Id, i.NameSnapshot, i.CompositionSnapshot, i.Quantity, i.Notes, i.RequiresPreparation,
                    active.Where(a => a.ParentOrderItemId == i.Id).Select(a => new ProductionAddonDto(a.Id, a.NameSnapshot, a.Quantity, a.Notes)).ToList()))
                .ToList();
            return (Status: o.ProductionStatus!.Value, Dto: new ProductionOrderDto(
                o.Id, o.Version, o.OrderNumber, o.OrderType, o.FulfillmentType, o.Label, o.Notes, o.IsPaid, o.CreatedAt, items, counts.GetValueOrDefault(o.Id)));
        }).ToList();

        var groups = Enum.GetValues<ProductionStatus>().ToDictionary(
            s => UpperSnakeEnumConverter<ProductionStatus>.ToText(s),
            s => (IReadOnlyList<ProductionOrderDto>)board.Where(b => b.Status == s).Select(b => b.Dto).ToList());
        return new ProductionBoardDto(day.Id, groups);
    }
}

/// GET /orders/{id}/history: a auditoria do pedido, do mais recente ao mais antigo. Inclui pagamentos e estornos,
/// que gravam o `orderId` na auditoria.
internal sealed class GetOrderHistoryQueryHandler(AppDbContext db, IEmployeeDirectory employees)
    : IQueryHandler<GetOrderHistoryQuery, PageDto<OrderHistoryEntryDto>>
{
    public async Task<PageDto<OrderHistoryEntryDto>> HandleAsync(GetOrderHistoryQuery query, CancellationToken cancellationToken)
    {
        Paging.EnsureLimit(query.Limit);
        if (!await db.Set<Order>().AnyAsync(o => o.Id == query.OrderId, cancellationToken))
        {
            throw new NotFoundException("ORDER", query.OrderId);
        }

        // audit_logs não é ITenantOwned (tenant anulável): o filtro de tenant é explícito, e a RLS é a segunda barreira.
        var tenantId = db.Tenant.TenantId;
        var logs = db.Set<AuditLog>().AsNoTracking().Where(a => a.TenantId == tenantId && a.OrderId == query.OrderId);
        if (query.Cursor is { } cursor)
        {
            var after = Paging.DecodeCursor(cursor);
            logs = logs.Where(a => a.Id.CompareTo(after) < 0);
        }

        var rows = await logs.OrderByDescending(a => a.Id).Take(query.Limit + 1).ToListAsync(cancellationToken);
        var page = rows.Take(query.Limit).ToList();
        var names = await employees.GetDisplayNamesAsync(page.Where(r => r.ActorEmployeeId is not null).Select(r => r.ActorEmployeeId!.Value), cancellationToken);

        var items = page.Select(r => new OrderHistoryEntryDto(
                r.OccurredAt, r.OperationType, r.Origin,
                r.ActorEmployeeId is { } actor ? new AuthorDto(actor, names.GetValueOrDefault(actor, string.Empty)) : null,
                Json(r.Before), Json(r.After)))
            .ToList();
        return new PageDto<OrderHistoryEntryDto>(items, rows.Count > query.Limit ? Paging.EncodeCursor(page[^1].Id) : null);
    }

    private static JsonElement? Json(string? value) => value is null ? null : JsonDocument.Parse(value).RootElement.Clone();
}

/// Paginação por cursor opaco (06 §6.0): base64url do último id (UUID v7) da página.
internal static class Paging
{
    public const int MaxLimit = 100;

    public static void EnsureLimit(int limit)
    {
        if (limit is < 1 or > MaxLimit)
        {
            throw BusinessException.Validation("limit", $"Use um limite entre 1 e {MaxLimit}.");
        }
    }

    public static string EncodeCursor(Guid id) => Base64Url.EncodeToString(id.ToByteArray());

    public static Guid DecodeCursor(string cursor)
    {
        try
        {
            var bytes = Base64Url.DecodeFromChars(cursor);
            return bytes.Length == 16 ? new Guid(bytes) : throw new FormatException();
        }
        catch (FormatException)
        {
            throw new BusinessException(ErrorCodes.MalformedRequest, "Cursor inválido.");
        }
    }

    public static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
