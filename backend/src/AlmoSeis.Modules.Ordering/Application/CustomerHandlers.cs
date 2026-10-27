using AlmoSeis.Infrastructure.Audit;
using AlmoSeis.Infrastructure.Concurrency;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Ordering.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Modules.Ordering.Application;

/// Auditoria de cliente só com nome e contagem de endereços: sem telefone nem endereço (ADR-0024).
internal static class CustomerRules
{
    public static async Task<Customer> LoadAsync(AppDbContext db, Guid customerId, CancellationToken cancellationToken) =>
        await db.Set<Customer>().Include(c => c.Addresses).SingleOrDefaultAsync(c => c.Id == customerId, cancellationToken)
        ?? throw new NotFoundException("CUSTOMER", customerId);

    /// Telefone é único por tenant (ux_customers_phone) → 409 com o cliente existente em `current` (06 §6.8).
    public static async Task EnsurePhoneFreeAsync(
        AppDbContext db, string? phone, Guid? self, CustomerDtoBuilder dtos, CancellationToken cancellationToken)
    {
        if (phone is null)
        {
            return;
        }

        var existing = await db.Set<Customer>().AsNoTracking().Include(c => c.Addresses)
            .SingleOrDefaultAsync(c => c.PhoneNumber == phone && c.Id != self, cancellationToken);
        if (existing is not null)
        {
            throw new BusinessException(
                ErrorCodes.PhoneAlreadyRegistered,
                "Já existe um cliente com este telefone.",
                new Dictionary<string, object?> { ["current"] = await dtos.BuildAsync(existing, cancellationToken) });
        }
    }

    public static object AuditView(Customer c) => new { c.Name, addressCount = c.Addresses.Count, anonymized = c.AnonymizedAt is not null };
}

internal sealed class CreateCustomerHandler(
    AppDbContext db,
    ITenantContext tenant,
    IIdGenerator ids,
    IAuditWriter audit,
    CustomerDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<CreateCustomerCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(CreateCustomerCommand command, CancellationToken cancellationToken)
    {
        await CustomerRules.EnsurePhoneFreeAsync(db, command.PhoneNumber, null, dtos, cancellationToken);
        var customer = Customer.Create(
            ids.New(), tenant.TenantId!.Value, command.Name, command.PhoneNumber, command.Notes, command.Addresses ?? [], ids.New, time.GetUtcNow());
        db.Add(customer);
        audit.Add("CustomerCreated", customer.SyncEntityType, customer.Id, after: CustomerRules.AuditView(customer));
        return await dtos.BuildAsync(customer, cancellationToken);
    }
}

internal sealed class UpdateCustomerHandler(
    AppDbContext db,
    IConcurrencyContext concurrency,
    IAuditWriter audit,
    CustomerDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<UpdateCustomerCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(UpdateCustomerCommand command, CancellationToken cancellationToken)
    {
        var customer = await CustomerRules.LoadAsync(db, command.CustomerId, cancellationToken);
        customer.EnsureVersion(concurrency.ExpectedVersion);

        var c = command.Changes;
        if (c.PhoneNumber.HasValue && c.PhoneNumber.Value != customer.PhoneNumber)
        {
            await CustomerRules.EnsurePhoneFreeAsync(db, c.PhoneNumber.Value, customer.Id, dtos, cancellationToken);
        }

        var before = CustomerRules.AuditView(customer);
        var version = customer.Version;
        customer.Update(c.Name, c.PhoneNumber, c.Notes, time.GetUtcNow());
        if (customer.Version != version)
        {
            audit.Add("CustomerUpdated", customer.SyncEntityType, customer.Id, before: before, after: CustomerRules.AuditView(customer));
        }

        return await dtos.BuildAsync(customer, cancellationToken);
    }
}

internal sealed class AddCustomerAddressHandler(
    AppDbContext db,
    IConcurrencyContext concurrency,
    IIdGenerator ids,
    IAuditWriter audit,
    CustomerDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<AddCustomerAddressCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(AddCustomerAddressCommand command, CancellationToken cancellationToken)
    {
        var customer = await CustomerRules.LoadAsync(db, command.CustomerId, cancellationToken);
        customer.EnsureVersion(concurrency.ExpectedVersion);
        customer.AddAddress(ids.New(), command.Address, time.GetUtcNow());
        audit.Add("CustomerUpdated", customer.SyncEntityType, customer.Id, after: CustomerRules.AuditView(customer));
        return await dtos.BuildAsync(customer, cancellationToken);
    }
}

internal sealed class UpdateCustomerAddressHandler(
    AppDbContext db,
    IConcurrencyContext concurrency,
    IAuditWriter audit,
    CustomerDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<UpdateCustomerAddressCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(UpdateCustomerAddressCommand command, CancellationToken cancellationToken)
    {
        var customer = await CustomerRules.LoadAsync(db, command.CustomerId, cancellationToken);
        customer.EnsureVersion(concurrency.ExpectedVersion);
        var c = command.Changes;
        var version = customer.Version;
        customer.UpdateAddress(
            command.AddressId,
            new AddressChanges(c.Label, c.Street, c.Number, c.Complement, c.Neighborhood, c.Reference, c.IsDefault),
            time.GetUtcNow());
        if (customer.Version != version)
        {
            audit.Add("CustomerUpdated", customer.SyncEntityType, customer.Id, after: CustomerRules.AuditView(customer));
        }

        return await dtos.BuildAsync(customer, cancellationToken);
    }
}

internal sealed class RemoveCustomerAddressHandler(
    AppDbContext db,
    IConcurrencyContext concurrency,
    IAuditWriter audit,
    CustomerDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<RemoveCustomerAddressCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(RemoveCustomerAddressCommand command, CancellationToken cancellationToken)
    {
        var customer = await CustomerRules.LoadAsync(db, command.CustomerId, cancellationToken);
        customer.EnsureVersion(concurrency.ExpectedVersion);
        customer.RemoveAddress(command.AddressId, time.GetUtcNow());
        audit.Add("CustomerUpdated", customer.SyncEntityType, customer.Id, after: CustomerRules.AuditView(customer));
        return await dtos.BuildAsync(customer, cancellationToken);
    }
}

/// ADR-0024 §3: anonimiza o cadastro e os snapshots dos pedidos dele; valores financeiros ficam.
internal sealed class AnonymizeCustomerHandler(
    AppDbContext db,
    IConcurrencyContext concurrency,
    IAuditWriter audit,
    CustomerDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<AnonymizeCustomerCommand, CustomerDto>
{
    /// `ck_orders_delivery` exige endereço em pedido de entrega: o snapshot vira um marcador sem dado pessoal.
    private const string AnonymizedAddress = """{"street":"Endereço anonimizado","number":null,"complement":null,"neighborhood":null,"reference":null}""";

    public async Task<CustomerDto> HandleAsync(AnonymizeCustomerCommand command, CancellationToken cancellationToken)
    {
        var customer = await CustomerRules.LoadAsync(db, command.CustomerId, cancellationToken);
        customer.EnsureVersion(concurrency.ExpectedVersion);
        if (command.Request.Reason.Trim().Length < 5)
        {
            throw BusinessException.Validation("reason", "Descreva o motivo com pelo menos 5 caracteres.");
        }

        customer.Anonymize(time.GetUtcNow());
        var tenantId = db.Tenant.TenantId!.Value;
        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE orders
               SET customer_name = {Customer.AnonymizedName},
                   customer_phone = NULL,
                   delivery_address = CASE WHEN delivery_address IS NULL THEN NULL ELSE {AnonymizedAddress}::jsonb END
             WHERE tenant_id = {tenantId} AND customer_id = {customer.Id}
            """,
            cancellationToken);

        audit.Add("CustomerAnonymized", customer.SyncEntityType, customer.Id, after: new { reason = command.Request.Reason.Trim() });
        return await dtos.BuildAsync(customer, cancellationToken);
    }
}

internal sealed class GetCustomerQueryHandler(AppDbContext db, CustomerDtoBuilder dtos) : IQueryHandler<GetCustomerQuery, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(GetCustomerQuery query, CancellationToken cancellationToken)
    {
        var customer = await db.Set<Customer>().AsNoTracking().Include(c => c.Addresses)
            .SingleOrDefaultAsync(c => c.Id == query.CustomerId, cancellationToken)
            ?? throw new NotFoundException("CUSTOMER", query.CustomerId);
        return await dtos.BuildAsync(customer, cancellationToken);
    }
}

internal sealed class CustomerCurrentReader(IQueryHandler<GetCustomerQuery, CustomerDto> query) : ICurrentResourceReader
{
    public string EntityType => "CUSTOMER";

    public async Task<IVersionedDto?> ReadAsync(Guid entityId, CancellationToken cancellationToken) =>
        await query.HandleAsync(new GetCustomerQuery(entityId), cancellationToken);
}

/// GET /customers?search=: nome ou telefone. Ordem por id (cadastro mais recente primeiro).
internal sealed class SearchCustomersQueryHandler(AppDbContext db, CustomerDtoBuilder dtos)
    : IQueryHandler<SearchCustomersQuery, PageDto<CustomerDto>>
{
    public async Task<PageDto<CustomerDto>> HandleAsync(SearchCustomersQuery query, CancellationToken cancellationToken)
    {
        Paging.EnsureLimit(query.Limit);
        var customers = db.Set<Customer>().AsNoTracking().Include(c => c.Addresses).Where(c => c.AnonymizedAt == null);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            var pattern = $"%{Paging.EscapeLike(term)}%";
            var digits = new string(term.Where(char.IsDigit).ToArray());
            customers = digits.Length >= 4
                ? customers.Where(c => EF.Functions.ILike(c.Name, pattern, "\\") || (c.PhoneNumber != null && c.PhoneNumber.Contains(digits)))
                : customers.Where(c => EF.Functions.ILike(c.Name, pattern, "\\"));
        }

        if (query.Cursor is { } cursor)
        {
            var after = Paging.DecodeCursor(cursor);
            customers = customers.Where(c => c.Id.CompareTo(after) < 0);
        }

        var rows = await customers.OrderByDescending(c => c.Id).Take(query.Limit + 1).ToListAsync(cancellationToken);
        var items = await dtos.BuildManyAsync(rows.Take(query.Limit).ToList(), cancellationToken);
        return new PageDto<CustomerDto>(items, rows.Count > query.Limit ? Paging.EncodeCursor(items[^1].Id) : null);
    }
}

/// GET /customers/{id}/pending-orders: o fiado (ADR-0021) — pedidos ativos com saldo, de qualquer data.
internal sealed class GetCustomerPendingOrdersQueryHandler(AppDbContext db)
    : IQueryHandler<GetCustomerPendingOrdersQuery, CustomerPendingOrdersDto>
{
    public async Task<CustomerPendingOrdersDto> HandleAsync(GetCustomerPendingOrdersQuery query, CancellationToken cancellationToken)
    {
        var customer = await db.Set<Customer>().AsNoTracking()
            .Where(c => c.Id == query.CustomerId)
            .Select(c => new CustomerRefDto(c.Id, c.Name))
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("CUSTOMER", query.CustomerId);

        var orders = await CustomerDtoBuilder.Pending(db).Where(o => o.CustomerId == query.CustomerId)
            .OrderBy(o => o.BusinessDate).ThenBy(o => o.OrderNumber)
            .Select(o => new { o.Id, o.OrderNumber, o.BusinessDate, o.OrderType, o.TotalAmount, o.PaidAmount, o.Version })
            .ToListAsync(cancellationToken);

        var items = orders.Select(o => new PendingOrderDto(o.Id, o.OrderNumber, o.BusinessDate, o.OrderType, o.TotalAmount, o.PaidAmount, o.TotalAmount - o.PaidAmount, o.Version)).ToList();
        return new CustomerPendingOrdersDto(customer, Money.Sum(items.Select(i => i.Balance)), items);
    }
}

/// Monta o `Customer` de 06 §6.8 com o fiado calculado dos pedidos.
internal sealed class CustomerDtoBuilder(AppDbContext db)
{
    public static IQueryable<Order> Pending(AppDbContext db) =>
        db.Set<Order>().AsNoTracking().Where(o => !o.IsPaid && o.CancellationStatus == CancellationStatus.Active);

    public async Task<CustomerDto> BuildAsync(Customer customer, CancellationToken cancellationToken) =>
        (await BuildManyAsync([customer], cancellationToken))[0];

    public async Task<IReadOnlyList<CustomerDto>> BuildManyAsync(IReadOnlyList<Customer> customers, CancellationToken cancellationToken)
    {
        var ids = customers.Select(c => c.Id).ToArray();
        var pending = (await Pending(db).Where(o => o.CustomerId != null && ids.Contains(o.CustomerId!.Value))
                .Select(o => new { CustomerId = o.CustomerId!.Value, o.TotalAmount, o.PaidAmount })
                .ToListAsync(cancellationToken))
            .GroupBy(o => o.CustomerId)
            .ToDictionary(g => g.Key, g => (Amount: Money.Sum(g.Select(o => o.TotalAmount - o.PaidAmount)), Count: g.Count()));

        return customers
            .Select(c => new CustomerDto(
                c.Id, c.Version, c.Name, c.PhoneNumber, c.Notes,
                c.Addresses.OrderByDescending(a => a.IsDefault).ThenBy(a => a.Id)
                    .Select(a => new CustomerAddressDto(a.Id, a.Label, a.Street, a.Number, a.Complement, a.Neighborhood, a.Reference, a.IsDefault))
                    .ToList(),
                pending.TryGetValue(c.Id, out var p) ? p.Amount : Money.Zero,
                pending.TryGetValue(c.Id, out var q) ? q.Count : 0,
                c.AnonymizedAt, c.CreatedAt))
            .ToList();
    }
}
