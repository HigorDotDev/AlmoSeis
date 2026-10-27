using System.Text.Json;
using AlmoSeis.Infrastructure.Persistence.Entities;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Infrastructure.Persistence;

public interface IUnitOfWork
{
    /// Etapa 3 do ciclo de escrita (07 §7.3): feed, outbox e SaveChanges, dentro da transação corrente.
    Task CommitAsync(CancellationToken cancellationToken);
}

/// Envelope de tempo real de 06 §6.14, montado pelos mappers de cada módulo.
/// `seq` e `entityVersion` vêm da entrada do feed da mesma transação.
public sealed record IntegrationEnvelope(
    string EventType,
    string EntityType,
    Guid EntityId,
    Guid? ActorEmployeeId,
    object? Data);

/// Converte um evento de domínio em envelope(s) para o outbox (ADR-0031).
public interface IIntegrationEventMapper
{
    Type EventType { get; }

    IEnumerable<IntegrationEnvelope> Map(IDomainEvent domainEvent);
}

public abstract class IntegrationEventMapper<TEvent> : IIntegrationEventMapper
    where TEvent : IDomainEvent
{
    public Type EventType => typeof(TEvent);

    public IEnumerable<IntegrationEnvelope> Map(IDomainEvent domainEvent) => Map((TEvent)domainEvent);

    protected abstract IEnumerable<IntegrationEnvelope> Map(TEvent domainEvent);
}

internal sealed class UnitOfWork(
    AppDbContext db,
    IEnumerable<IIntegrationEventMapper> mappers,
    IIdGenerator ids,
    TimeProvider time) : IUnitOfWork
{
    internal static readonly JsonSerializerOptions EnvelopeJson = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<Type, IIntegrationEventMapper[]> _mappers =
        mappers.GroupBy(m => m.EventType).ToDictionary(g => g.Key, g => g.ToArray());

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        // Captura antes do primeiro SaveChanges, que zera os estados.
        var synced = db.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .Select(e => e.Entity)
            .OfType<ISyncTracked>()
            .Cast<object>()
            .OfType<ITenantOwned>()
            .ToList();

        var aggregates = db.ChangeTracker.Entries<AggregateRoot>().Select(e => e.Entity).ToList();
        var events = aggregates.SelectMany(a => a.DomainEvents).ToList();

        // 1º save: grava o estado (inclusive contadores de um tenant recém-criado no autocadastro).
        await SaveAsync(cancellationToken);

        if (synced.Count > 0 || events.Count > 0)
        {
            var seqByEntity = await WriteChangeFeedAsync(synced, cancellationToken);
            WriteOutbox(events, seqByEntity);
            await SaveAsync(cancellationToken);
        }

        foreach (var aggregate in aggregates)
        {
            aggregate.ClearDomainEvents();
        }
    }

    private async Task<Dictionary<Guid, (Guid TenantId, long Seq, int Version)>> WriteChangeFeedAsync(
        List<ITenantOwned> synced, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, (Guid, long, int)>();
        var now = time.GetUtcNow();

        foreach (var group in synced.GroupBy(e => e.TenantId))
        {
            var count = group.Count();

            // Uma trava de linha por tenant e por transação: a ordem de commit acompanha o seq (ADR-0032).
            // ToListAsync (e não SingleAsync): UPDATE ... RETURNING não aceita composição (LIMIT).
            var lastSeq = (await db.Database
                .SqlQuery<long>($"UPDATE tenant_sync_counters SET last_seq = last_seq + {count} WHERE tenant_id = {group.Key} RETURNING last_seq AS \"Value\"")
                .ToListAsync(cancellationToken))
                .Single();

            var seq = lastSeq - count;
            foreach (var entity in group)
            {
                seq++;
                var id = ((Entity)entity).Id;
                var version = entity is IVersioned versioned ? versioned.Version : 1;
                db.Add(new ChangeFeedEntry
                {
                    TenantId = group.Key,
                    Seq = seq,
                    EntityType = ((ISyncTracked)entity).SyncEntityType,
                    EntityId = id,
                    EntityVersion = version,
                    ChangeKind = "UPSERT",
                    OccurredAt = now,
                });
                result[id] = (group.Key, seq, version);
            }
        }

        return result;
    }

    private void WriteOutbox(List<IDomainEvent> events, Dictionary<Guid, (Guid TenantId, long Seq, int Version)> seqByEntity)
    {
        foreach (var domainEvent in events)
        {
            if (!_mappers.TryGetValue(domainEvent.GetType(), out var eventMappers))
            {
                continue;
            }

            foreach (var envelope in eventMappers.SelectMany(m => m.Map(domainEvent)))
            {
                if (!seqByEntity.TryGetValue(envelope.EntityId, out var feed))
                {
                    throw new InvalidOperationException(
                        $"Evento {envelope.EventType} referencia {envelope.EntityType} {envelope.EntityId}, que não foi alterado nesta transação.");
                }

                var eventId = ids.New();
                db.Add(new OutboxMessage
                {
                    Id = eventId,
                    TenantId = feed.TenantId,
                    EventType = envelope.EventType,
                    Seq = feed.Seq,
                    OccurredAt = domainEvent.OccurredAt,
                    Payload = JsonSerializer.Serialize(
                        new
                        {
                            eventId,
                            eventType = envelope.EventType,
                            seq = feed.Seq,
                            occurredAt = domainEvent.OccurredAt,
                            entityType = envelope.EntityType,
                            entityId = envelope.EntityId,
                            entityVersion = feed.Version,
                            actorEmployeeId = envelope.ActorEmployeeId,
                            data = envelope.Data,
                        },
                        EnvelopeJson),
                });
            }
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var entry = ex.Entries.FirstOrDefault(e => e.Entity is AggregateRoot);
            if (entry?.Entity is not AggregateRoot aggregate)
            {
                throw;
            }

            var databaseValues = await entry.GetDatabaseValuesAsync(cancellationToken);
            var current = databaseValues?.GetValue<int>(nameof(AggregateRoot.Version)) ?? -1;
            var expected = (int)entry.Property(nameof(AggregateRoot.Version)).OriginalValue!;
            var entityType = aggregate is ISyncTracked tracked ? tracked.SyncEntityType : aggregate.GetType().Name.ToUpperInvariant();
            throw new VersionConflictException(entityType, aggregate.Id, expected, current);
        }
    }
}
