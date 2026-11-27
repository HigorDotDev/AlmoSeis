using AlmoSeis.Infrastructure.Audit;
using AlmoSeis.Infrastructure.Observability;
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

/// Onde os itens entram: a data do cardápio do dia, a tabela de preço, a modalidade e, em `POST /orders/{id}/items`, os
/// itens já gravados (o pai de um adicional incluído depois).
internal sealed record ItemResolution(DateOnly BusinessDate, PriceList PriceList, OrderType OrderType, IReadOnlyList<OrderItem>? Existing = null);

/// Transforma os itens do corpo em `NewItem` com preço, nome e composição do catálogo e do cardápio do dia
/// (ADR-0020), ou do cardápio fixo no pedido `A_LA_CARTE` (ADR-0058). Nada de preço ou nome vem do cliente. O preço sai
/// da tabela do pedido (ADR-0057): `SITE` usa o preço de site de quem vende no site (tamanho de marmita, bebida, prato,
/// tamanho de prato, tamanho de bebida, combo) e o de balcão no resto (Q-309); adicional e acréscimo de sabor, o mesmo (Q-308).
internal sealed class OrderItemResolver(ICatalogReader catalog, IMenuReader menus, IFixedMenuReader fixedMenu)
{
    public async Task<IReadOnlyList<NewItem>> ResolveAsync(
        IReadOnlyList<OrderItemInput> inputs, ItemResolution context, CancellationToken cancellationToken)
    {
        // Um tipo por pedido (Q-317, ADR-0058 item 3): o item de outra modalidade é recusado antes de ler cardápio.
        for (var i = 0; i < inputs.Count; i++)
        {
            var kind = inputs[i].ItemKind;
            var fits = inputs[i].ParentOrderItemId is null
                ? Order.KindFits(context.OrderType, kind)
                : kind == (context.OrderType == OrderType.ALaCarte ? OrderItemKind.GroupAddon : OrderItemKind.Addon);
            if (!fits)
            {
                throw BusinessException.Validation($"items[{i}].itemKind", Order.KindMismatchMessage);
            }
        }

        if (context.OrderType == OrderType.ALaCarte)
        {
            return new FixedMenuItems(await fixedMenu.LoadAsync(cancellationToken), context).Resolve(inputs);
        }

        var productIds = inputs
            .SelectMany(i => (i.MainDishProductIds ?? []).Concat(i.SideDishProductIds ?? []).Concat((i.Addons ?? []).Select(a => a.ProductId)))
            .Concat(inputs.Where(i => i.ProductId is not null).Select(i => i.ProductId!.Value))
            .Distinct()
            .ToList();
        var products = await catalog.GetProductsAsync(productIds, cancellationToken);
        var businessDate = context.BusinessDate;
        var priceList = context.PriceList;

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
            var addons = (input.Addons ?? []).Select((a, j) => Simple(OrderItemKind.Addon, null, a.ProductId, a.Quantity, a.Notes, products, $"{field}.addons[{j}]", priceList)).ToList();
            if (addons.Count > 0 && input.ItemKind is not (OrderItemKind.Marmita or OrderItemKind.Pf))
            {
                throw BusinessException.Validation($"{field}.addons", "Só marmita e PF têm adicionais.");
            }

            result.Add(input.ItemKind switch
            {
                OrderItemKind.Marmita => await MarmitaAsync(input, marmitaMenu!, products, addons, field, priceList, cancellationToken),
                OrderItemKind.Pf => Pf(input, pfMenu!, products, addons, field),
                _ => Simple(input.ItemKind, input.ParentOrderItemId, input.ProductId, input.Quantity, input.Notes, products, field, priceList),
            });
        }

        return result;
    }

    private async Task<NewItem> MarmitaAsync(
        OrderItemInput input, DailyMenu menu, IReadOnlyDictionary<Guid, CatalogProduct> products, IReadOnlyList<NewItem> addons,
        string field, PriceList priceList, CancellationToken cancellationToken)
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
            OrderItemKind.Marmita, null, null, size.Id, input.Quantity, size.PriceFor(priceList), Truncate($"Marmita {size.Name}", 80), Truncate(composition, 500),
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
        OrderItemKind kind, Guid? parentId, Guid? productId, int quantity, string? notes, IReadOnlyDictionary<Guid, CatalogProduct> products, string field,
        PriceList priceList)
    {
        var expected = kind == OrderItemKind.Beverage ? ProductKind.Beverage : ProductKind.Addon;
        if (productId is not { } id || !products.TryGetValue(id, out var product) || product.Kind != expected)
        {
            throw BusinessException.Validation($"{field}.productId", kind == OrderItemKind.Beverage ? "Escolha uma bebida do catálogo." : "Escolha um adicional do catálogo.");
        }

        EnsureSellable(product);
        return new NewItem(kind, parentId, id, null, quantity, product.PriceFor(priceList)!.Value, product.Name, null, product.RequiresPreparation, notes, [], []);
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

/// Cliente do pedido do app (07 §7.8): `customerId`; ou `saveCustomer` (marmita, com o endereço salvo, Q-313); ou, fora
/// do salão, o telefone informado vincula o cliente existente ou cria um só com nome e telefone, sem `CustomerAddress`
/// (ADR-0059 item 14, ADR-0024 complementado). O cadastro existente nunca muda: o pedido guarda o nome digitado (Q-314).
internal sealed class CustomerResolver(AppDbContext db, ITenantContext tenant, IIdGenerator ids, IAuditWriter audit, TimeProvider time)
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

            return new Resolved(customer.Id, command.CustomerName ?? customer.Name, OrderingFormats.NormalizePhone(command.CustomerPhone) ?? customer.PhoneNumber, address);
        }

        if (command.CustomerAddressId is not null)
        {
            throw BusinessException.Validation("customerAddressId", "Endereço salvo exige o cliente.");
        }

        var name = string.IsNullOrWhiteSpace(command.CustomerName) ? null : command.CustomerName.Trim();
        // Mesmo número em qualquer formato → o mesmo cliente (o `BrazilianPhone` da página, 06 §6.9).
        var phone = OrderingFormats.NormalizePhone(command.CustomerPhone);
        if (command.SaveCustomer)
        {
            if (name is null)
            {
                throw BusinessException.Validation("customerName", "Informe o nome para salvar o cliente.");
            }

            // "Salvar cliente" da marmita grava o endereço (Q-313). Telefone já cadastrado: o pedido vai para o cliente
            // existente, sem mudar o cadastro (06 §6.9).
            var addresses = command.DeliveryAddress is { } a
                ? new[] { new CustomerAddressInput(null, a.Street, a.Number, a.Complement, a.Neighborhood, a.Reference, IsDefault: true) }
                : [];
            if (phone is null)
            {
                var created = Customer.Create(ids.New(), tenant.TenantId!.Value, name, null, null, addresses, ids.New, time.GetUtcNow());
                db.Add(created);
                AuditCreated(created);
                return new Resolved(created.Id, name, null, command.DeliveryAddress);
            }

            return new Resolved(await FindOrCreateByPhoneAsync(name, phone, addresses, cancellationToken), name, phone, command.DeliveryAddress);
        }

        // Pedido por telefone (entrega ou retirada): vínculo automático, sem "salvar cliente" (ADR-0059 item 14).
        if (phone is not null && name is not null && command.FulfillmentType != FulfillmentType.DineIn)
        {
            return new Resolved(await FindOrCreateByPhoneAsync(name, phone, [], cancellationToken), name, phone, command.DeliveryAddress);
        }

        return new Resolved(null, command.CustomerName, phone, command.DeliveryAddress);
    }

    /// 05 §5.14 "Cliente pelo telefone": `INSERT … ON CONFLICT DO NOTHING RETURNING id`, e o `SELECT` quando não volta
    /// linha (o telefone já existe, ou outro atendente acabou de criá-lo: o ON CONFLICT esperou o commit dele). Um INSERT
    /// comum que batesse no `ux_customers_phone` abortaria a transação do pedido. A trava por telefone é a mesma do
    /// aceite do pedido do site (`WebOrderCustomerLinker`), que cria pelo EF: sem ela, o aceite estouraria o índice.
    private async Task<Guid> FindOrCreateByPhoneAsync(
        string name, string phone, IReadOnlyList<CustomerAddressInput> addresses, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId!.Value;
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"customer-phone:" + tenantId + ":" + phone}, 0))", cancellationToken);

        var id = ids.New();
        var now = time.GetUtcNow();
        var inserted = await db.Database
            .SqlQuery<Guid>(
                $"""
                INSERT INTO customers (id, tenant_id, name, phone_number, created_at)
                VALUES ({id}, {tenantId}, {name}, {phone}, {now})
                ON CONFLICT (tenant_id, phone_number) WHERE phone_number IS NOT NULL DO NOTHING
                RETURNING id AS "Value"
                """)
            .ToListAsync(cancellationToken);

        if (inserted.Count == 0)
        {
            return await db.Set<Customer>().AsNoTracking()
                .Where(c => c.PhoneNumber == phone)
                .Select(c => c.Id)
                .SingleAsync(cancellationToken);
        }

        // A linha já está no banco (versão 1). O agregado entra rastreado como alterado, sem mudar nada, só para o
        // UnitOfWork gravar o feed (`CUSTOMER`, versão 1) e o `CustomerChanged` no outbox, como num cadastro pelo EF; os
        // endereços do "salvar cliente" entram como novos.
        var customer = Customer.Create(id, tenantId, name, phone, null, addresses, ids.New, now);
        db.Attach(customer);
        db.Entry(customer).Property(c => c.Name).IsModified = true;
        foreach (var address in customer.Addresses)
        {
            db.Entry(address).State = EntityState.Added;
        }

        AuditCreated(customer);
        return id;
    }

    private void AuditCreated(Customer customer) =>
        audit.Add("CustomerCreated", customer.SyncEntityType, customer.Id, after: CustomerRules.AuditView(customer));
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
    /// `almoseis.orders.created` (07 §7.4): só depois do commit, para o pedido desfeito no rollback não contar. Pedido do
    /// app na criação; da página no aceite (é quando nasce para a cozinha, com `OrderCreated`).
    public static void CountCreated(IUnitOfWork unitOfWork, AlmoSeisMetrics metrics, Order order)
    {
        var origin = UpperSnakeEnumConverter<OrderOrigin>.ToText(order.Origin);
        var type = UpperSnakeEnumConverter<OrderType>.ToText(order.OrderType);
        unitOfWork.OnCommitted(() => metrics.OrderCreated(origin, type));
    }

    /// Módulo de cada modalidade (ADR-0054 item 12). A pergunta é sempre pelo módulo, nunca pelo comportamento.
    /// `A_LA_CARTE`: comanda no salão → `A_LA_CARTE`; entrega e retirada → `DELIVERY` (ADR-0058 item 1).
    public static TenantModule ModuleOf(OrderType orderType, FulfillmentType fulfillmentType = FulfillmentType.DineIn) => orderType switch
    {
        OrderType.Marmita => TenantModule.Marmita,
        OrderType.Pf => TenantModule.Pf,
        OrderType.Kilo => TenantModule.Kilo,
        OrderType.ALaCarte => fulfillmentType == FulfillmentType.DineIn ? TenantModule.ALaCarte : TenantModule.Delivery,
        _ => throw new ArgumentOutOfRangeException(nameof(orderType), orderType, null),
    };

    public static async Task<Order> LoadAsync(AppDbContext db, Guid orderId, CancellationToken cancellationToken) =>
        await db.Set<Order>()
            .Include(o => o.Items).ThenInclude(i => i.Components)
            .Include(o => o.Weighings)
            .AsSplitQuery()
            .SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken)
        ?? throw new NotFoundException("ORDER", orderId);

    /// Alterar a taxa de um pedido existente para um valor diferente da padrão exige `orders.delivery-fee.edit`
    /// (ADR-0021). Na criação, não: quem cria digita a taxa (Q-315).
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
            .Concat(weighings.Select(w => (Guid?)w.CreatedByEmployeeId))
            .Append(order.CreatedByEmployeeId)
            .Append(order.CanceledByEmployeeId)
            .Append(order.AcceptedByEmployeeId)
            .Append(order.RejectedByEmployeeId)
            .OfType<Guid>();
        var names = await employees.GetDisplayNamesAsync(authorIds, cancellationToken);
        AuthorDto Author(Guid id) => new(id, names.GetValueOrDefault(id, string.Empty));
        AuthorDto? MaybeAuthor(Guid? id) => id is { } value ? Author(value) : null;

        // Árvore do pedido (ADR-0058 item 9): primeiro nível → `addons` (ADDON/GROUP_ADDON) e, no COMBO, `parts`
        // (COMBO_PART, cada uma com os seus `addons`).
        IEnumerable<OrderItem> ChildrenOf(OrderItem parent) =>
            active.Where(a => a.ParentOrderItemId == parent.Id).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id);

        OrderItemDto Item(OrderItem i) => new(
            i.Id, i.ItemKind, i.MarmitaSizeId, i.ProductId, i.DishId, i.DishSizeId, i.BeverageSizeId, i.BeverageFlavorId, i.ComboId,
            i.DisplayName, i.SizeNameSnapshot, i.FlavorNameSnapshot, i.CompositionSnapshot,
            i.Components.OrderBy(c => c.Role).Select(c => new OrderItemComponentDto(c.ProductId, c.Role, c.NameSnapshot)).ToList(),
            i.Quantity, i.UnitPrice, i.LineTotal, i.RequiresPreparation, i.Notes,
            ChildrenOf(i).Where(a => a.ItemKind is OrderItemKind.Addon or OrderItemKind.GroupAddon)
                .Select(a => new OrderAddonDto(a.Id, a.ItemKind, a.ProductId, a.AddonOptionId, a.DisplayName, a.Quantity, a.UnitPrice, a.LineTotal,
                    a.RequiresPreparation, a.Notes, a.CreatedAt, MaybeAuthor(a.CreatedByEmployeeId)))
                .ToList(),
            ChildrenOf(i).Where(p => p.ItemKind == OrderItemKind.ComboPart).Select(Item).ToList(),
            i.CreatedAt, MaybeAuthor(i.CreatedByEmployeeId));

        var items = active.Where(i => i.ParentOrderItemId is null)
            .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
            .Select(Item)
            .ToList();

        var customer = order.CustomerId is not null || order.CustomerName is not null
            ? new OrderCustomerDto(order.CustomerId, order.CustomerName ?? string.Empty, order.CustomerPhone)
            : null;
        var cancellation = order.CanceledAt is { } canceledAt
            ? new CancellationDto(canceledAt, Author(order.CanceledByEmployeeId!.Value), order.CancellationReason!)
            : null;
        var rejection = order.RejectedAt is { } rejectedAt
            ? new RejectionDto(order.RejectionReasonCode!.Value, order.RejectionMessage, rejectedAt, MaybeAuthor(order.RejectedByEmployeeId))
            : null;

        return new OrderDto(
            order.Id, order.Version, order.OrderNumber, order.BusinessDayId, order.BusinessDate, order.OrderType, order.Origin, order.PriceList,
            order.FulfillmentType, customer, order.DeliveryAddress, order.TableLabel, order.TabNumber, order.Notes,
            order.ExpectedPaymentMethod, order.ChangeFor, order.ChangeForBelowTotal, order.RiskFlag,
            order.DeliveryFee, order.DeliveryFeeSource, order.DeliveryDistanceKm, order.ItemsTotal, order.WeighingsTotal, order.TotalAmount, order.PaidAmount, order.Balance, order.IsPaid,
            order.ProductionStatus, order.ProductionStatusChangedAt, order.CancellationStatus, cancellation,
            order.AcceptanceStatus, order.AcceptanceDeadline, order.AcceptedAt, MaybeAuthor(order.AcceptedByEmployeeId), order.EstimatedReadyMinutes,
            rejection, order.ExpiredAt, order.WithdrawnAt,
            order.FulfillmentStatus, order.FulfillmentStatusChangedAt, order.OutForDeliveryAt, order.DeliveredAt, order.PickedUpAt,
            order.IsReadyForPickup, items,
            weighings.Select(w => new WeighingDto(w.Id, w.WeightGrams.Value, w.PricePerKg, w.Amount, w.CreatedAt, Author(w.CreatedByEmployeeId))).ToList(),
            await payments.ListAsync(order.Id, cancellationToken),
            await alerts.CountForOrderAsync(order.Id, cancellationToken),
            order.CreatedAt, MaybeAuthor(order.CreatedByEmployeeId), order.UpdatedAt);
    }
}

/// Até o módulo Payments existir, o pedido não tem pagamentos.
internal sealed class NoOrderPayments : IOrderPaymentsReader
{
    public Task<IReadOnlyList<OrderPaymentDto>> ListAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OrderPaymentDto>>([]);
}
