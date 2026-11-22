using System.Text.Json;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Realtime;
using AlmoSeis.Infrastructure.Storage;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Catalog.Application;
using AlmoSeis.Modules.Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlmoSeis.Modules.Catalog.Infrastructure;

/// Mapeamento do cardápio fixo (05 §5.17 0009). As FKs reais são compostas com `tenant_id`; para o EF basta o id do pai
/// (relações de ordenação, 07 §7.13). Componentes de combo e vínculos de grupo saem da coleção → `DELETE` (únicos com
/// grant de `DELETE`); os demais filhos nunca saem da coleção, só são inativados.
internal static class FixedMenuModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DishCategory>(b =>
        {
            b.ToTable("dish_categories");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(40);
        });

        modelBuilder.Entity<Dish>(b =>
        {
            b.ToTable("dishes");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(80);
            b.Property(x => x.Description).HasMaxLength(300);
            b.Property(x => x.ImageStorageKey).HasMaxLength(200);
            b.HasOne<DishCategory>().WithMany().HasForeignKey(x => x.DishCategoryId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Sizes).WithOne().HasForeignKey(s => s.DishId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.Sizes).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<DishSize>(b =>
        {
            b.ToTable("dish_sizes");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(30);
        });

        modelBuilder.Entity<BeverageType>(b =>
        {
            b.ToTable("beverage_types");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(40);
            b.HasMany(x => x.Sizes).WithOne().HasForeignKey(s => s.BeverageTypeId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.Sizes).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<BeverageSize>(b =>
        {
            b.ToTable("beverage_sizes");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(30);
            b.HasMany(x => x.Flavors).WithOne().HasForeignKey(f => f.BeverageSizeId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.Flavors).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<BeverageFlavor>(b =>
        {
            b.ToTable("beverage_flavors");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(40);
        });

        modelBuilder.Entity<Combo>(b =>
        {
            b.ToTable("combos");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(80);
            b.Property(x => x.Description).HasMaxLength(300);
            b.Property(x => x.ImageStorageKey).HasMaxLength(200);
            b.HasMany(x => x.Components).WithOne().HasForeignKey(c => c.ComboId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Components).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<ComboComponent>(b =>
        {
            b.ToTable("combo_components");
            b.HasKey(x => x.Id);
            b.HasOne<Dish>().WithMany().HasForeignKey(x => x.DishId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<DishSize>().WithMany().HasForeignKey(x => x.DishSizeId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<BeverageSize>().WithMany().HasForeignKey(x => x.BeverageSizeId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AddonGroup>(b =>
        {
            b.ToTable("addon_groups");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(60);
            b.HasMany(x => x.Options).WithOne().HasForeignKey(o => o.AddonGroupId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.Options).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.HasMany(x => x.Links).WithOne().HasForeignKey(l => l.AddonGroupId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Links).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<AddonOption>(b =>
        {
            b.ToTable("addon_options");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(80);
        });

        modelBuilder.Entity<AddonGroupLink>(b =>
        {
            b.ToTable("addon_group_links");
            b.HasKey(x => x.Id);
            b.HasOne<DishCategory>().WithMany().HasForeignKey(x => x.DishCategoryId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Dish>().WithMany().HasForeignKey(x => x.DishId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// FixedMenuImageDiscarded → mensagem interna do outbox (não vai para o SignalR), como a imagem do produto.
internal sealed class FixedMenuImageDiscardedMapper(ITenantContext tenant) : IntegrationEventMapper<FixedMenuImageDiscarded>
{
    public const string EventTypeName = "FixedMenuImageDiscarded";

    protected override IEnumerable<IntegrationEnvelope> Map(FixedMenuImageDiscarded e)
    {
        yield return new IntegrationEnvelope(EventTypeName, e.EntityType, e.EntityId, tenant.EmployeeId, new { storageKey = e.StorageKey });
    }
}

/// Apaga no R2 a imagem que o prato ou o combo deixou de usar. Só chaves do próprio item e tenant.
internal sealed class FixedMenuImageCleanup(IObjectStorage storage, ILogger<FixedMenuImageCleanup> logger) : IOutboxConsumer
{
    public string EventType => FixedMenuImageDiscardedMapper.EventTypeName;

    public async Task HandleAsync(Guid tenantId, JsonElement envelope, CancellationToken cancellationToken)
    {
        var entityType = envelope.GetProperty("entityType").GetString() ?? string.Empty;
        var entityId = envelope.GetProperty("entityId").GetGuid();
        var key = envelope.GetProperty("data").GetProperty("storageKey").GetString();
        if (key is null || !key.StartsWith(FixedMenuRules.ImagePrefix(tenantId, entityType, entityId), StringComparison.Ordinal))
        {
            logger.LogWarning("Imagem descartada fora do prefixo do item {EntityId}: nada apagado.", entityId);
            return;
        }

        if (storage.IsConfigured)
        {
            await storage.DeleteAsync(key, cancellationToken);
        }
    }
}
