using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Modules.Storefront.Domain;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Modules.Storefront.Infrastructure;

/// Mapeamento de 05 §5.17 (0004).
internal sealed class StorefrontModelConfiguration : IModuleModelConfiguration
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StorefrontSettings>(b =>
        {
            b.ToTable("storefront_settings");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("tenant_id");
            b.Ignore(x => x.TenantId);
            b.Ignore(x => x.HasCompleteAddress);
            b.Ignore(x => x.DeliveryAvailableOnPage);
            b.Ignore(x => x.PublicAddress);
            b.Property(x => x.PublicName).HasMaxLength(80);
            b.Property(x => x.ContactPhone).HasMaxLength(16);
            b.Property(x => x.HoursText).HasMaxLength(120);
            b.Property(x => x.AddressStreet).HasMaxLength(120);
            b.Property(x => x.AddressNumber).HasMaxLength(10);
            b.Property(x => x.AddressComplement).HasMaxLength(60);
            b.Property(x => x.AddressNeighborhood).HasMaxLength(60);
            b.Property(x => x.AddressCity).HasMaxLength(80);
            b.Property(x => x.AddressState).HasMaxLength(2).IsFixedLength();
            b.Property(x => x.AddressZipCode).HasMaxLength(8).IsFixedLength();
            b.Property(x => x.Latitude).HasPrecision(9, 6);
            b.Property(x => x.Longitude).HasPrecision(9, 6);
            b.Property(x => x.PricePerKm).HasPrecision(10, 2);
            b.Property(x => x.MaxDeliveryDistanceKm).HasPrecision(4, 1);
        });

        modelBuilder.Entity<StoreSlug>(b =>
        {
            b.ToTable("store_slugs");
            b.HasKey(x => x.Slug);
            b.Property(x => x.Slug).HasMaxLength(40);
        });
    }
}
