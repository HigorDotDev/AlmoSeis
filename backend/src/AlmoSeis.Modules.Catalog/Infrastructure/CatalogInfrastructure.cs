using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Storage;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.Modules.Catalog.Domain;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Modules.Catalog.Infrastructure;

/// Mapeamento de 05 §5.5.
internal sealed class CatalogModelConfiguration : IModuleModelConfiguration
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(b =>
        {
            b.ToTable("products");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(80);
            b.Property(x => x.Description).HasMaxLength(300);
            b.Property(x => x.Category).HasMaxLength(40);
            b.Property(x => x.ImageStorageKey).HasMaxLength(200);
        });

        modelBuilder.Entity<MarmitaSize>(b =>
        {
            b.ToTable("marmita_sizes");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(20);
        });
    }
}

internal sealed class CatalogReader(AppDbContext db, IObjectStorage storage) : ICatalogReader
{
    public async Task<IReadOnlyDictionary<Guid, CatalogProduct>> GetProductsAsync(
        IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        if (productIds.Count == 0)
        {
            return new Dictionary<Guid, CatalogProduct>();
        }

        var rows = await db.Set<Product>().AsNoTracking().Where(p => productIds.Contains(p.Id)).ToListAsync(cancellationToken);
        return rows.ToDictionary(
            p => p.Id,
            p => new CatalogProduct(
                p.Id, p.Kind, p.Name, p.Description, p.DefaultPrice, p.RequiresPreparation,
                storage.PublicUrl(p.ImageStorageKey), p.IsActive, p.IsAvailable));
    }

    public async Task<CatalogMarmitaSize?> GetMarmitaSizeAsync(Guid marmitaSizeId, CancellationToken cancellationToken) =>
        await db.Set<MarmitaSize>().AsNoTracking()
            .Where(s => s.Id == marmitaSizeId)
            .Select(s => new CatalogMarmitaSize(s.Id, s.Name, s.Price, s.IsActive))
            .SingleOrDefaultAsync(cancellationToken);
}

/// CatalogChanged → `CatalogUpdated` (06 §6.14).
internal sealed class CatalogChangedMapper(ITenantContext tenant) : IntegrationEventMapper<CatalogChanged>
{
    protected override IEnumerable<IntegrationEnvelope> Map(CatalogChanged domainEvent)
    {
        yield return new IntegrationEnvelope(
            "CatalogUpdated", domainEvent.EntityType, domainEvent.EntityId, tenant.EmployeeId, new { entityType = domainEvent.EntityType });
    }
}
