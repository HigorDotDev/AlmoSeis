using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;

namespace AlmoSeis.Modules.Ordering.Domain;

/// Cliente do estabelecimento, base do fiado (04 §4.7, ADR-0021, ADR-0024).
internal sealed class Customer : AggregateRoot, ITenantOwned, ISyncTracked
{
    public const string AnonymizedName = "Cliente anonimizado";

    private readonly List<CustomerAddress> _addresses = [];

    private Customer()
    {
    }

    public Guid TenantId { get; private set; }
    public string SyncEntityType => "CUSTOMER";
    public string Name { get; private set; } = null!;
    public string? PhoneNumber { get; private set; }
    public string? Notes { get; private set; }
    public DateTimeOffset? AnonymizedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyList<CustomerAddress> Addresses => _addresses;

    public static Customer Create(
        Guid id, Guid tenantId, string name, string? phoneNumber, string? notes, IReadOnlyList<CustomerAddressInput> addresses,
        Func<Guid> newId, DateTimeOffset now)
    {
        var customer = new Customer
        {
            Id = id,
            TenantId = tenantId,
            Name = name.Trim(),
            PhoneNumber = phoneNumber,
            Notes = notes,
            CreatedAt = now,
        };

        foreach (var address in addresses)
        {
            customer.Add(newId(), address);
        }

        customer.EnsureSingleDefault();
        customer.Raise(new CustomerChanged(id, now));
        return customer;
    }

    public void Update(Optional<string> name, Optional<string?> phoneNumber, Optional<string?> notes, DateTimeOffset now)
    {
        EnsureNotAnonymized();
        var next = (name.GetValueOr(Name).Trim(), phoneNumber.GetValueOr(PhoneNumber), notes.GetValueOr(Notes));
        if (next == (Name, PhoneNumber, Notes))
        {
            return;
        }

        (Name, PhoneNumber, Notes) = next;
        Touch(new CustomerChanged(Id, now));
    }

    public CustomerAddress AddAddress(Guid id, CustomerAddressInput input, DateTimeOffset now)
    {
        EnsureNotAnonymized();
        var address = Add(id, input);
        EnsureSingleDefault();
        Touch(new CustomerChanged(Id, now));
        return address;
    }

    public void UpdateAddress(Guid addressId, AddressChanges changes, DateTimeOffset now)
    {
        EnsureNotAnonymized();
        var address = Address(addressId);
        var before = address.Snapshot();
        address.Apply(changes);
        if (changes.IsDefault is { HasValue: true, Value: true })
        {
            MakeDefault(address);
        }

        EnsureSingleDefault();
        if (!Equals(address.Snapshot(), before) || _addresses.Any(a => a != address && a.WasChanged))
        {
            Touch(new CustomerChanged(Id, now));
        }
    }

    public void RemoveAddress(Guid addressId, DateTimeOffset now)
    {
        EnsureNotAnonymized();
        _addresses.Remove(Address(addressId));
        EnsureSingleDefault();
        Touch(new CustomerChanged(Id, now));
    }

    /// ADR-0024: some com telefone, endereços e notas; o nome vira genérico. Irreversível.
    public void Anonymize(DateTimeOffset now)
    {
        EnsureNotAnonymized();
        Name = AnonymizedName;
        PhoneNumber = null;
        Notes = null;
        AnonymizedAt = now;
        _addresses.Clear();
        Touch(new CustomerChanged(Id, now));
    }

    private CustomerAddress Add(Guid id, CustomerAddressInput input)
    {
        var address = new CustomerAddress(id, TenantId, Id, input);
        _addresses.Add(address);
        if (input.IsDefault)
        {
            MakeDefault(address);
        }

        return address;
    }

    private CustomerAddress Address(Guid addressId) =>
        _addresses.SingleOrDefault(a => a.Id == addressId) ?? throw new NotFoundException("CUSTOMER_ADDRESS", addressId);

    private void MakeDefault(CustomerAddress address)
    {
        foreach (var other in _addresses.Where(a => a != address && a.IsDefault))
        {
            other.SetDefault(false);
        }

        address.SetDefault(true);
    }

    /// Com endereços, exatamente um é o padrão: o primeiro, se ninguém foi marcado.
    private void EnsureSingleDefault()
    {
        if (_addresses.Count > 0 && !_addresses.Any(a => a.IsDefault))
        {
            _addresses[0].SetDefault(true);
        }
    }

    private void EnsureNotAnonymized()
    {
        if (AnonymizedAt is not null)
        {
            throw BusinessException.Validation("customerId", "Cliente anonimizado não pode ser alterado.");
        }
    }
}

internal sealed class CustomerAddress : Entity, ITenantOwned
{
    private CustomerAddress()
    {
    }

    internal CustomerAddress(Guid id, Guid tenantId, Guid customerId, CustomerAddressInput input)
    {
        Id = id;
        TenantId = tenantId;
        CustomerId = customerId;
        Label = input.Label;
        Street = input.Street.Trim();
        Number = input.Number;
        Complement = input.Complement;
        Neighborhood = input.Neighborhood;
        Reference = input.Reference;
    }

    public Guid TenantId { get; private set; }
    public Guid CustomerId { get; private set; }
    public string? Label { get; private set; }
    public string Street { get; private set; } = null!;
    public string? Number { get; private set; }
    public string? Complement { get; private set; }
    public string? Neighborhood { get; private set; }
    public string? Reference { get; private set; }
    public bool IsDefault { get; private set; }

    internal bool WasChanged { get; private set; }

    /// Snapshot gravado no pedido.
    public DeliveryAddressDto ToDelivery() => new(Street, Number, Complement, Neighborhood, Reference);

    internal object Snapshot() => (Label, Street, Number, Complement, Neighborhood, Reference, IsDefault);

    internal void Apply(AddressChanges changes)
    {
        Label = changes.Label.GetValueOr(Label);
        Street = changes.Street.GetValueOr(Street).Trim();
        Number = changes.Number.GetValueOr(Number);
        Complement = changes.Complement.GetValueOr(Complement);
        Neighborhood = changes.Neighborhood.GetValueOr(Neighborhood);
        Reference = changes.Reference.GetValueOr(Reference);
    }

    internal void SetDefault(bool value)
    {
        WasChanged |= IsDefault != value;
        IsDefault = value;
    }
}

internal sealed record AddressChanges(
    Optional<string?> Label,
    Optional<string> Street,
    Optional<string?> Number,
    Optional<string?> Complement,
    Optional<string?> Neighborhood,
    Optional<string?> Reference,
    Optional<bool> IsDefault);

/// Cliente não tem evento de tempo real no 06 §6.14: só entra no feed de sincronização.
internal sealed record CustomerChanged(Guid CustomerId, DateTimeOffset OccurredAt) : IDomainEvent;
