namespace AlmoSeis.SharedKernel.Domain;

public abstract class Entity
{
    public Guid Id { get; protected init; }
}

public interface ITenantOwned
{
    Guid TenantId { get; }
}

public interface IVersioned
{
    int Version { get; }
}

/// Entidades cujas alterações entram no feed de sincronização (ADR-0032).
public interface ISyncTracked
{
    /// Valor de `entityType` do feed: "ORDER", "PRODUCT"... (06 §6.12).
    string SyncEntityType { get; }
}

public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}
