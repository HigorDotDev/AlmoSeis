using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Menu.Contracts;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Ordering.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using AlmoSeis.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Modules.Ordering.Application;

/// Transforma os itens do corpo em `NewItem` com preço, nome e composição do catálogo e do cardápio do dia
/// (ADR-0020). Nada de preço ou nome vem do cliente.
internal sealed class OrderItemResolver(ICatalogReader catalog, IMenuReader menus)
{
    public async Task<IReadOnlyList<NewItem>> ResolveAsync(
        IReadOnlyList<OrderItemInput> inputs, DateOnly businessDate, CancellationToken cancellationToken)
    {
        var productIds = inputs
            .SelectMany(i => (i.MainDishProductIds ?? []).Concat(i.SideDishProductIds ?? []).Concat((i.Addons ?? []).Select(a => a.ProductId)))
            .Concat(inputs.Where(i => i.ProductId is not null).Select(i => i.ProductId!.Value))
            .Distinct()
            .ToList();
        var products = await catalog.GetProductsAsync(productIds, cancellationToken);

        var marmitaMenu = inputs.Any(i => i.ItemKind == OrderItemKind.Marmita)
            ? await DailyMenuAsync(MenuType.Marmita, businessDate, cancellationToken)
            : null;
        var pfMenu = inputs.Any(i => i.ItemKind == OrderItemKind.Pf)
            ? await DailyMenuAsync(MenuType.Pf, businessDate, cancellationToken)
            : null;

        var result = new List<NewItem>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var field = $"items[{i}]";
            var addons = (input.Addons ?? []).Select((a, j) => Simple(OrderItemKind.Addon, null, a.ProductId, a.Quantity, a.Notes, products, $"{field}.addons[{j}]")).ToList();
            if (addons.Count > 0 && input.ItemKind is not (OrderItemKind.Marmita or OrderItemKind.Pf))
            {
                throw BusinessException.Validation($"{field}.addons", "Só marmita e PF têm adicionais.");
            }

            result.Add(input.ItemKind switch
            {
                OrderItemKind.Marmita => await MarmitaAsync(input, marmitaMenu!, products, addons, field, cancellationToken),
                OrderItemKind.Pf => Pf(input, pfMenu!, products, addons, field),
                _ => Simple(input.ItemKind, input.ParentOrderItemId, input.ProductId, input.Quantity, input.Notes, products, field),
            });
        }

        return result;
    }

    private async Task<NewItem> MarmitaAsync(
        OrderItemInput input, DailyMenu menu, IReadOnlyDictionary<Guid, CatalogProduct> products, IReadOnlyList<NewItem> addons,
        string field, CancellationToken cancellationToken)
    {
        var size = input.MarmitaSizeId is { } sizeId ? await catalog.GetMarmitaSizeAsync(sizeId, cancellationToken) : null;
        if (size is not { IsActive: true })
        {
            throw BusinessException.Validation($"{field}.marmitaSizeId", "Escolha um tamanho de marmita ativo.");
        }

        var mains = input.MainDishProductIds ?? [];
        if (mains.Count == 0)
        {
            throw BusinessException.Validation($"{field}.mainDishProductIds", "A marmita precisa de pelo menos um prato principal.");
        }

        var components = mains.Select(id => Component(id, ComponentRole.MainDish, MenuSection.MainDish))
            .Concat((input.SideDishProductIds ?? []).Select(id => Component(id, ComponentRole.SideDish, MenuSection.SideDish)))
            .ToList();
        var composition = string.Join(", ", components.Where(c => c.Role == ComponentRole.MainDish).Select(c => c.Name));
        var sides = components.Where(c => c.Role == ComponentRole.SideDish).Select(c => c.Name).ToList();
        if (sides.Count > 0)
        {
            composition += " · " + string.Join(", ", sides);
        }

        return new NewItem(
            OrderItemKind.Marmita, null, null, size.Id, input.Quantity, size.Price, Truncate($"Marmita {size.Name}", 80), Truncate(composition, 500),
            RequiresPreparation: true, input.Notes, components, addons);

        NewComponent Component(Guid productId, ComponentRole role, MenuSection section)
        {
            var product = EnsureOnMenu(menu, productId, section, products);
            return new NewComponent(productId, role, product.Name);
        }
    }

    private static NewItem Pf(
        OrderItemInput input, DailyMenu menu, IReadOnlyDictionary<Guid, CatalogProduct> products, IReadOnlyList<NewItem> addons, string field)
    {
        if (input.ProductId is not { } productId)
        {
            throw BusinessException.Validation($"{field}.productId", "Escolha o prato do PF.");
        }

        var product = EnsureOnMenu(menu, productId, MenuSection.MainDish, products);
        var price = menu.Items.Single(i => i.ProductId == productId).PfPrice!.Value;
        return new NewItem(
            OrderItemKind.Pf, null, productId, null, input.Quantity, price, Truncate($"PF {product.Name}", 80), null,
            RequiresPreparation: true, input.Notes, [], addons);
    }

    /// Bebida ou adicional: preço do produto, em qualquer modalidade (RN-002.5).
    private static NewItem Simple(
        OrderItemKind kind, Guid? parentId, Guid? productId, int quantity, string? notes, IReadOnlyDictionary<Guid, CatalogProduct> products, string field)
    {
        var expected = kind == OrderItemKind.Beverage ? ProductKind.Beverage : ProductKind.Addon;
        if (productId is not { } id || !products.TryGetValue(id, out var product) || product.Kind != expected)
        {
            throw BusinessException.Validation($"{field}.productId", kind == OrderItemKind.Beverage ? "Escolha uma bebida do catálogo." : "Escolha um adicional do catálogo.");
        }

        EnsureSellable(product);
        return new NewItem(kind, parentId, id, null, quantity, product.DefaultPrice!.Value, product.Name, null, product.RequiresPreparation, notes, [], []);
    }

    private static CatalogProduct EnsureOnMenu(DailyMenu menu, Guid productId, MenuSection section, IReadOnlyDictionary<Guid, CatalogProduct> products)
    {
        var item = menu.Items.SingleOrDefault(i => i.ProductId == productId && i.Section == section);
        products.TryGetValue(productId, out var product);
        if (item is not { IsAvailable: true } || product is null)
        {
            throw new BusinessException(ErrorCodes.ProductUnavailable, $"{product?.Name ?? "Produto"} não está disponível no cardápio de hoje.");
        }

        EnsureSellable(product);
        return product;
    }

    private static void EnsureSellable(CatalogProduct product)
    {
        if (!product.IsActive || !product.IsAvailable)
        {
            throw new BusinessException(ErrorCodes.ProductUnavailable, $"{product.Name} está indisponível.");
        }
    }

    private async Task<DailyMenu> DailyMenuAsync(MenuType menuType, DateOnly date, CancellationToken cancellationToken) =>
        await menus.GetDailyAsync(menuType, date, cancellationToken)
        ?? throw new BusinessException(
            ErrorCodes.MenuNotFound,
            $"Ainda não há cardápio de {(menuType == MenuType.Marmita ? "marmitas" : "PF")} para hoje.",
            new Dictionary<string, object?> { ["menuType"] = menuType == MenuType.Marmita ? "MARMITA" : "PF", ["businessDate"] = date });

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// `customerId`, ou `saveCustomer` → busca por telefone → cria ou reaproveita (07 §7.8). Resolve o endereço em snapshot.
internal sealed class CustomerResolver(AppDbContext db, ITenantContext tenant, IIdGenerator ids, TimeProvider time)
{
    public sealed record Resolved(Guid? CustomerId, string? Name, string? Phone, DeliveryAddressDto? Address);

    public async Task<Resolved> ResolveAsync(CreateOrderCommand command, CancellationToken cancellationToken)
    {
        if (command.CustomerAddressId is not null && command.DeliveryAddress is not null)
        {
            throw BusinessException.Validation("customerAddressId", "Informe o endereço salvo ou um endereço novo, não os dois.");
        }

        if (command.CustomerId is { } customerId)
        {
            var customer = await db.Set<Customer>().Include(c => c.Addresses).SingleOrDefaultAsync(c => c.Id == customerId, cancellationToken);
            if (customer is null || customer.AnonymizedAt is not null)
            {
                throw BusinessException.Validation("customerId", "Cliente não encontrado.");
            }

            var address = command.DeliveryAddress;
            if (command.CustomerAddressId is { } addressId)
            {
                address = customer.Addresses.SingleOrDefault(a => a.Id == addressId)?.ToDelivery()
                    ?? throw BusinessException.Validation("customerAddressId", "Endereço não pertence a este cliente.");
            }

            return new Resolved(customer.Id, command.CustomerName ?? customer.Name, command.CustomerPhone ?? customer.PhoneNumber, address);
        }

        if (command.CustomerAddressId is not null)
        {
            throw BusinessException.Validation("customerAddressId", "Endereço salvo exige o cliente.");
        }

        if (!command.SaveCustomer)
        {
            return new Resolved(null, command.CustomerName, command.CustomerPhone, command.DeliveryAddress);
        }

        if (string.IsNullOrWhiteSpace(command.CustomerName))
        {
            throw BusinessException.Validation("customerName", "Informe o nome para salvar o cliente.");
        }

        // Telefone já cadastrado: o pedido vai para o cliente existente (06 §6.9).
        if (command.CustomerPhone is { } phone
            && await db.Set<Customer>().AsNoTracking().SingleOrDefaultAsync(c => c.PhoneNumber == phone, cancellationToken) is { } existing)
        {
            return new Resolved(existing.Id, command.CustomerName, phone, command.DeliveryAddress);
        }

        var addresses = command.DeliveryAddress is { } a
            ? new[] { new CustomerAddressInput(null, a.Street, a.Number, a.Complement, a.Neighborhood, a.Reference, IsDefault: true) }
            : [];
        var created = Customer.Create(ids.New(), tenant.TenantId!.Value, command.CustomerName, command.CustomerPhone, null, addresses, ids.New, time.GetUtcNow());
        db.Add(created);
        return new Resolved(created.Id, command.CustomerName, command.CustomerPhone, command.DeliveryAddress);
    }
}

/// SQL de 05 §5.14: número sequencial por data operacional (ADR-0039).
internal sealed class OrderNumberGenerator(AppDbContext db, ITenantContext tenant)
{
    public async Task<int> NextAsync(DateOnly businessDate, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId!.Value;
        return (await db.Database
            .SqlQuery<int>(
                $"""
                INSERT INTO business_date_counters (tenant_id, business_date, last_order_number)
                VALUES ({tenantId}, {businessDate}, 1)
                ON CONFLICT (tenant_id, business_date)
                DO UPDATE SET last_order_number = business_date_counters.last_order_number + 1
                RETURNING last_order_number AS "Value"
                """)
            .ToListAsync(cancellationToken))
            .Single();
    }
}

internal static class OrderRules
{
    public static async Task<Order> LoadAsync(AppDbContext db, Guid orderId, CancellationToken cancellationToken) =>
        await db.Set<Order>()
            .Include(o => o.Items).ThenInclude(i => i.Components)
            .Include(o => o.Weighings)
            .AsSplitQuery()
            .SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken)
        ?? throw new NotFoundException("ORDER", orderId);

    /// Taxa diferente da padrão exige `orders.delivery-fee.edit` (ADR-0021).
    public static void EnsureCanSetDeliveryFee(IPermissionChecker permissions, Money fee, Money defaultFee)
    {
        if (fee != defaultFee && !permissions.Has(Permissions.Orders.EditDeliveryFee))
        {
            throw new BusinessException(
                ErrorCodes.PermissionDenied,
                extensions: new Dictionary<string, object?> { ["permission"] = Permissions.Orders.EditDeliveryFee });
        }
    }

    public static IReadOnlyList<NewWeighing> Weighings(IReadOnlyList<WeighingInput> inputs, Money? kiloPrice)
    {
        if (inputs.Count == 0)
        {
            return [];
        }

        if (kiloPrice is not { } price)
        {
            throw new BusinessException(ErrorCodes.KiloPriceNotConfigured);
        }

        return inputs.Select((w, i) => w.WeightGrams is < Grams.Min or > Grams.Max
                ? throw BusinessException.Validation($"weighings[{i}].weightGrams", $"O peso deve estar entre {Grams.Min} e {Grams.Max} gramas.")
                : new NewWeighing(Grams.Of(w.WeightGrams), price))
            .ToList();
    }

    /// Valores da auditoria do pedido: sem telefone nem endereço (ADR-0024).
    public static object Totals(Order o) => new { totalAmount = o.TotalAmount.ToString(), paidAmount = o.PaidAmount.ToString() };
}

/// Monta o `Order` de 06 §6.9.
internal sealed class OrderDtoBuilder(IEmployeeDirectory employees, IOrderPaymentsReader payments, AlertCounter alerts)
{
    public async Task<OrderDto> BuildAsync(Order order, CancellationToken cancellationToken)
    {
        var active = order.Items.Where(i => i.Status == LineStatus.Active).ToList();
        var weighings = order.Weighings.Where(w => w.Status == LineStatus.Active).OrderBy(w => w.CreatedAt).ToList();
        var authorIds = active.Select(i => i.CreatedByEmployeeId)
            .Concat(weighings.Select(w => w.CreatedByEmployeeId))
            .Append(order.CreatedByEmployeeId)
            .Concat(order.CanceledByEmployeeId is { } c ? [c] : []);
        var names = await employees.GetDisplayNamesAsync(authorIds, cancellationToken);
        AuthorDto Author(Guid id) => new(id, names.GetValueOrDefault(id, string.Empty));

        var items = active.Where(i => i.ParentOrderItemId is null)
            .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
            .Select(i => new OrderItemDto(
                i.Id, i.ItemKind, i.MarmitaSizeId, i.ProductId, i.NameSnapshot, i.CompositionSnapshot,
                i.Components.OrderBy(c => c.Role).Select(c => new OrderItemComponentDto(c.ProductId, c.Role, c.NameSnapshot)).ToList(),
                i.Quantity, i.UnitPrice, i.LineTotal, i.RequiresPreparation, i.Notes,
                active.Where(a => a.ParentOrderItemId == i.Id).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
                    .Select(a => new OrderAddonDto(a.Id, a.ItemKind, a.ProductId!.Value, a.NameSnapshot, a.Quantity, a.UnitPrice, a.LineTotal,
                        a.RequiresPreparation, a.Notes, a.CreatedAt, Author(a.CreatedByEmployeeId)))
                    .ToList(),
                i.CreatedAt, Author(i.CreatedByEmployeeId)))
            .ToList();

        var customer = order.CustomerId is not null || order.CustomerName is not null
            ? new OrderCustomerDto(order.CustomerId, order.CustomerName ?? string.Empty, order.CustomerPhone)
            : null;
        var cancellation = order.CanceledAt is { } canceledAt
            ? new CancellationDto(canceledAt, Author(order.CanceledByEmployeeId!.Value), order.CancellationReason!)
            : null;

        return new OrderDto(
            order.Id, order.Version, order.OrderNumber, order.BusinessDayId, order.BusinessDate, order.OrderType, order.Origin,
            order.FulfillmentType, customer, order.DeliveryAddress, order.TableLabel, order.TabNumber, order.Notes,
            order.DeliveryFee, order.ItemsTotal, order.WeighingsTotal, order.TotalAmount, order.PaidAmount, order.Balance, order.IsPaid,
            order.ProductionStatus, order.ProductionStatusChangedAt, order.CancellationStatus, cancellation, items,
            weighings.Select(w => new WeighingDto(w.Id, w.WeightGrams.Value, w.PricePerKg, w.Amount, w.CreatedAt, Author(w.CreatedByEmployeeId))).ToList(),
            await payments.ListAsync(order.Id, cancellationToken),
            await alerts.CountForOrderAsync(order.Id, cancellationToken),
            order.CreatedAt, Author(order.CreatedByEmployeeId), order.UpdatedAt);
    }
}

/// Até o módulo Payments existir, o pedido não tem pagamentos.
internal sealed class NoOrderPayments : IOrderPaymentsReader
{
    public Task<IReadOnlyList<OrderPaymentDto>> ListAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OrderPaymentDto>>([]);
}
