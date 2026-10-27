using AlmoSeis.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlmoSeis.Infrastructure.Persistence.Entities;

/// tenant_sync_counters (ADR-0032). Criado no autocadastro; incrementado pelo UnitOfWork via SQL.
public sealed class TenantSyncCounter : ITenantOwned
{
    public Guid TenantId { get; init; }

    public long LastSeq { get; init; }
}

internal sealed class TenantSyncCounterConfiguration : IEntityTypeConfiguration<TenantSyncCounter>
{
    public void Configure(EntityTypeBuilder<TenantSyncCounter> b)
    {
        b.ToTable("tenant_sync_counters");
        b.HasKey(x => x.TenantId);
    }
}
