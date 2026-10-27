using AlmoSeis.SharedKernel.Errors;

namespace AlmoSeis.SharedKernel.Domain;

public abstract class AggregateRoot : Entity, IVersioned
{
    private readonly List<IDomainEvent> _domainEvents = [];
    private bool _touched;

    public int Version { get; private set; } = 1;

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    /// Registra uma mutação do agregado (inclusive dos filhos). A versão sobe uma única vez por
    /// unidade de trabalho; o EF usa o valor original no WHERE como token de concorrência (ADR-0034).
    protected void Touch(IDomainEvent? domainEvent = null)
    {
        if (!_touched)
        {
            Version++;
            _touched = true;
        }

        if (domainEvent is not null)
        {
            _domainEvents.Add(domainEvent);
        }
    }

    /// Evento de criação: o agregado nasce na versão 1, sem incremento.
    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    /// Confere a versão informada pelo cliente (If-Match).
    public void EnsureVersion(int expectedVersion)
    {
        if (expectedVersion != Version)
        {
            throw new VersionConflictException(SyncEntityTypeOrName(), Id, expectedVersion, Version);
        }
    }

    /// Chamado pelo UnitOfWork depois de persistir.
    public void ClearDomainEvents()
    {
        _domainEvents.Clear();
        _touched = false;
    }

    private string SyncEntityTypeOrName() =>
        this is ISyncTracked tracked ? tracked.SyncEntityType : GetType().Name.ToUpperInvariant();
}
