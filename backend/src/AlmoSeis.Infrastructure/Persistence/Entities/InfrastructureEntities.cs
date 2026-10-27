using AlmoSeis.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlmoSeis.Infrastructure.Persistence.Entities;

/// audit_logs (05 §5.11). TenantId é anulável (ações de plataforma), por isso não é ITenantOwned:
/// as consultas do módulo Audit filtram o tenant explicitamente e a RLS é a segunda barreira.
public sealed class AuditLog
{
    public Guid Id { get; init; }
    public Guid? TenantId { get; init; }
    public required string Origin { get; init; }
    public Guid? ActorUserId { get; init; }
    public Guid? ActorEmployeeId { get; init; }
    public Guid? SharedDeviceId { get; init; }
    public required string OperationType { get; init; }
    public required string EntityType { get; init; }
    public Guid EntityId { get; init; }
    public Guid? OrderId { get; init; }
    public string? Before { get; init; }
    public string? After { get; init; }
    public required string CorrelationId { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
}

/// change_feed_entries (ADR-0032).
public sealed class ChangeFeedEntry : ITenantOwned
{
    public Guid TenantId { get; init; }
    public long Seq { get; init; }
    public required string EntityType { get; init; }
    public Guid EntityId { get; init; }
    public int EntityVersion { get; init; }
    public required string ChangeKind { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
}

/// outbox_messages (ADR-0031).
public sealed class OutboxMessage : ITenantOwned
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public required string EventType { get; init; }
    public required string Payload { get; init; }
    public long? Seq { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

/// idempotency_records (ADR-0033).
public sealed class IdempotencyRecord : ITenantOwned
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid Key { get; init; }
    public Guid UserId { get; init; }
    public required string Endpoint { get; init; }
    public required string RequestHash { get; init; }
    public required string Status { get; set; }
    public short? ResponseStatus { get; set; }
    public string? ResponseBody { get; set; }
    public Guid? ResourceId { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("audit_logs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Origin).HasMaxLength(20);
        b.Property(x => x.OperationType).HasMaxLength(60);
        b.Property(x => x.EntityType).HasMaxLength(40);
        b.Property(x => x.Before).HasColumnType("jsonb");
        b.Property(x => x.After).HasColumnType("jsonb");
        b.Property(x => x.CorrelationId).HasMaxLength(64);
    }
}

internal sealed class ChangeFeedEntryConfiguration : IEntityTypeConfiguration<ChangeFeedEntry>
{
    public void Configure(EntityTypeBuilder<ChangeFeedEntry> b)
    {
        b.ToTable("change_feed_entries");
        b.HasKey(x => new { x.TenantId, x.Seq });
        b.Property(x => x.EntityType).HasMaxLength(40);
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("outbox_messages");
        b.HasKey(x => x.Id);
        b.Property(x => x.EventType).HasMaxLength(80);
        b.Property(x => x.Payload).HasColumnType("jsonb");
        b.Property(x => x.LastError).HasMaxLength(500);
    }
}

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> b)
    {
        b.ToTable("idempotency_records");
        b.HasKey(x => x.Id);
        b.Property(x => x.Endpoint).HasMaxLength(120);
        b.Property(x => x.RequestHash).HasMaxLength(64).IsFixedLength();
        b.Property(x => x.ResponseBody).HasColumnType("jsonb");
    }
}
