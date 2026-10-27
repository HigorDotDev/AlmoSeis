using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.Modules.Menu.Contracts;
using AlmoSeis.Modules.Menu.Domain;
using Microsoft.EntityFrameworkCore;
using MenuAggregate = AlmoSeis.Modules.Menu.Domain.Menu;

namespace AlmoSeis.Modules.Menu.Infrastructure;

/// Mapeamento de 05 §5.6.
internal sealed class MenuModelConfiguration : IModuleModelConfiguration
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MenuAggregate>(b =>
        {
            b.ToTable("menus");
            b.HasKey(x => x.Id);
            b.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.MenuId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<MenuItem>(b =>
        {
            b.ToTable("menu_items");
            b.HasKey(x => x.Id);
            // Item removido do PUT é DELETE; um novo item do mesmo produto nunca é criado (o agregado reaproveita o id).
        });
    }
}

/// Produto em cardápio não muda de `kind` (a section do item é o kind do produto).
internal sealed class MenuProductUsageCheck(AppDbContext db) : IProductUsageCheck
{
    public Task<bool> IsInUseAsync(Guid productId, CancellationToken cancellationToken) =>
        db.Set<MenuItem>().AnyAsync(i => i.ProductId == productId, cancellationToken);
}

/// MenuChanged → `MenuUpdated` (06 §6.14).
internal sealed class MenuChangedMapper(ITenantContext tenant) : IntegrationEventMapper<MenuChanged>
{
    protected override IEnumerable<IntegrationEnvelope> Map(MenuChanged domainEvent)
    {
        yield return new IntegrationEnvelope(
            "MenuUpdated", "MENU", domainEvent.MenuId, tenant.EmployeeId,
            new { menuType = UpperSnakeEnumConverter<MenuType>.ToText(domainEvent.MenuType), date = domainEvent.Date });
    }
}

internal sealed class MenuReader(AppDbContext db) : IMenuReader
{
    public async Task<DailyMenu?> GetDailyAsync(MenuType menuType, DateOnly date, CancellationToken cancellationToken)
    {
        var menu = await db.Set<MenuAggregate>().AsNoTracking().Include(m => m.Items)
            .SingleOrDefaultAsync(m => m.MenuType == menuType && m.Kind == MenuKind.Daily && m.Date == date, cancellationToken);
        return menu is null
            ? null
            : new DailyMenu(menu.Id, menu.MenuType, date, menu.Items.Select(i => new DailyMenuItem(i.ProductId, i.Section, i.PfPrice, i.IsAvailable)).ToList());
    }
}
