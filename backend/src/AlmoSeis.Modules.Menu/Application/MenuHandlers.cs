using AlmoSeis.Infrastructure.Audit;
using AlmoSeis.Infrastructure.Concurrency;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Menu.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;
using MenuAggregate = AlmoSeis.Modules.Menu.Domain.Menu;
using MenuItemSpecs = AlmoSeis.Modules.Menu.Domain.MenuItemSpecs;
using MenuProduct = AlmoSeis.Modules.Menu.Domain.MenuProduct;

namespace AlmoSeis.Modules.Menu.Application;

internal sealed class CreateMenuHandler(
    AppDbContext db,
    ITenantContext tenant,
    IIdGenerator ids,
    ICatalogReader catalog,
    IAuditWriter audit,
    MenuDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<CreateMenuCommand, MenuDto>
{
    public async Task<MenuDto> HandleAsync(CreateMenuCommand command, CancellationToken cancellationToken)
    {
        await MenuRules.EnsureSlotFreeAsync(db, command.MenuType, command.Kind, command.Date, command.DayOfWeek, cancellationToken);

        var specs = MenuItemSpecs.Build(command.MenuType, command.Items, await MenuRules.ProductsAsync(catalog, command.Items, cancellationToken));
        var menu = MenuAggregate.Create(
            ids.New(), tenant.TenantId!.Value, command.MenuType, command.Kind, command.Date, command.DayOfWeek,
            sourceMenuId: null, tenant.EmployeeId, specs, ids.New, time.GetUtcNow());
        db.Add(menu);

        audit.Add("MenuCreated", menu.SyncEntityType, menu.Id, after: MenuRules.AuditView(menu));
        return await dtos.BuildAsync(menu, cancellationToken);
    }
}

internal sealed class ReplaceMenuItemsHandler(
    AppDbContext db,
    IConcurrencyContext concurrency,
    ICatalogReader catalog,
    IAuditWriter audit,
    IIdGenerator ids,
    MenuDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<ReplaceMenuItemsCommand, MenuDto>
{
    public async Task<MenuDto> HandleAsync(ReplaceMenuItemsCommand command, CancellationToken cancellationToken)
    {
        var menu = await MenuRules.LoadAsync(db, command.MenuId, cancellationToken);
        menu.EnsureVersion(concurrency.ExpectedVersion);

        var items = command.Request.Items;
        var specs = MenuItemSpecs.Build(menu.MenuType, items, await MenuRules.ProductsAsync(catalog, items, cancellationToken));
        var before = MenuRules.AuditView(menu);
        menu.ReplaceItems(specs, ids.New, time.GetUtcNow());

        audit.Add("MenuItemsReplaced", menu.SyncEntityType, menu.Id, before: before, after: MenuRules.AuditView(menu));
        return await dtos.BuildAsync(menu, cancellationToken);
    }
}

internal sealed class UpdateMenuItemHandler(
    AppDbContext db,
    IConcurrencyContext concurrency,
    IAuditWriter audit,
    MenuDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<UpdateMenuItemCommand, MenuDto>
{
    public async Task<MenuDto> HandleAsync(UpdateMenuItemCommand command, CancellationToken cancellationToken)
    {
        var menu = await MenuRules.LoadAsync(db, command.MenuId, cancellationToken);
        menu.EnsureVersion(concurrency.ExpectedVersion);

        var item = menu.Items.SingleOrDefault(i => i.Id == command.ItemId) ?? throw new NotFoundException("MENU_ITEM", command.ItemId);
        var before = new { item.ProductId, item.IsAvailable, pfPrice = item.PfPrice?.ToString() };
        var versionBefore = menu.Version;

        menu.UpdateItem(command.ItemId, command.Changes.IsAvailable, command.Changes.PfPrice, time.GetUtcNow());

        if (menu.Version != versionBefore)
        {
            audit.Add(
                "MenuItemUpdated", menu.SyncEntityType, menu.Id, before: before,
                after: new { item.ProductId, item.IsAvailable, pfPrice = item.PfPrice?.ToString() });
        }

        return await dtos.BuildAsync(menu, cancellationToken);
    }
}

internal sealed class CloneMenuHandler(
    AppDbContext db,
    ITenantContext tenant,
    IIdGenerator ids,
    ICatalogReader catalog,
    IAuditWriter audit,
    MenuDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<CloneMenuCommand, MenuDto>
{
    public async Task<MenuDto> HandleAsync(CloneMenuCommand command, CancellationToken cancellationToken)
    {
        var source = await MenuRules.LoadAsync(db, command.MenuId, cancellationToken);
        var request = command.Request;
        await MenuRules.EnsureSlotFreeAsync(db, source.MenuType, request.Kind, request.Date, request.DayOfWeek, cancellationToken);

        // Produto desativado depois do original não volta para a cópia (RN-002.6).
        var products = await catalog.GetProductsAsync(source.Items.Select(i => i.ProductId).ToList(), cancellationToken);
        var clone = source.CloneAs(
            ids.New(), request.Kind, request.Date, request.DayOfWeek, tenant.EmployeeId, ids.New, time.GetUtcNow(),
            keep: item => products.TryGetValue(item.ProductId, out var p) && p.IsActive);
        db.Add(clone);

        audit.Add("MenuCloned", clone.SyncEntityType, clone.Id, after: MenuRules.AuditView(clone));
        return await dtos.BuildAsync(clone, cancellationToken);
    }
}

internal sealed class GetMenuQueryHandler(AppDbContext db, MenuDtoBuilder dtos) : IQueryHandler<GetMenuQuery, MenuDto>
{
    public async Task<MenuDto> HandleAsync(GetMenuQuery query, CancellationToken cancellationToken)
    {
        var menu = await db.Set<MenuAggregate>().AsNoTracking().Include(m => m.Items)
            .SingleOrDefaultAsync(m => m.Id == query.MenuId, cancellationToken)
            ?? throw new NotFoundException("MENU", query.MenuId);
        return await dtos.BuildAsync(menu, cancellationToken);
    }
}

internal sealed class ListMenusQueryHandler(AppDbContext db, IEmployeeDirectory employees)
    : IQueryHandler<ListMenusQuery, ItemsDto<MenuSummaryDto>>
{
    private const int MaxItems = 100;

    public async Task<ItemsDto<MenuSummaryDto>> HandleAsync(ListMenusQuery query, CancellationToken cancellationToken)
    {
        var menus = db.Set<MenuAggregate>().AsNoTracking();
        if (query.MenuType is { } menuType)
        {
            menus = menus.Where(m => m.MenuType == menuType);
        }

        if (query.Kind is { } kind)
        {
            menus = menus.Where(m => m.Kind == kind);
        }

        if (query.From is { } from)
        {
            menus = menus.Where(m => m.Date >= from);
        }

        if (query.To is { } to)
        {
            menus = menus.Where(m => m.Date <= to);
        }

        var rows = await menus
            .OrderByDescending(m => m.Date).ThenBy(m => m.DayOfWeek).ThenBy(m => m.MenuType)
            .Take(MaxItems)
            .Select(m => new
            {
                m.Id, m.Version, m.MenuType, m.Kind, m.Date, m.DayOfWeek, m.SourceMenuId,
                ItemCount = m.Items.Count, m.CreatedAt, m.CreatedByEmployeeId, m.UpdatedAt,
            })
            .ToListAsync(cancellationToken);

        var names = await employees.GetDisplayNamesAsync(rows.Where(r => r.CreatedByEmployeeId is not null).Select(r => r.CreatedByEmployeeId!.Value), cancellationToken);
        return new ItemsDto<MenuSummaryDto>(rows
            .Select(r => new MenuSummaryDto(
                r.Id, r.Version, r.MenuType, r.Kind, r.Date, r.DayOfWeek, r.SourceMenuId, r.ItemCount, r.CreatedAt,
                MenuDtoBuilder.Author(r.CreatedByEmployeeId, names), r.UpdatedAt))
            .ToList());
    }
}

/// `current` do 409 das rotas [M] de cardápio.
internal sealed class MenuCurrentReader(IQueryHandler<GetMenuQuery, MenuDto> query) : ICurrentResourceReader
{
    public string EntityType => "MENU";

    public async Task<IVersionedDto?> ReadAsync(Guid entityId, CancellationToken cancellationToken) =>
        await query.HandleAsync(new GetMenuQuery(entityId), cancellationToken);
}

/// Monta o `Menu` de 06 §6.7: nome/descrição/imagem vêm do catálogo, o autor do Identity.
internal sealed class MenuDtoBuilder(ICatalogReader catalog, IEmployeeDirectory employees)
{
    public async Task<MenuDto> BuildAsync(MenuAggregate menu, CancellationToken cancellationToken)
    {
        var products = await catalog.GetProductsAsync(menu.Items.Select(i => i.ProductId).ToList(), cancellationToken);
        var names = menu.CreatedByEmployeeId is { } author
            ? await employees.GetDisplayNamesAsync([author], cancellationToken)
            : new Dictionary<Guid, string>();

        var items = menu.Items
            .OrderBy(i => i.Section).ThenBy(i => i.SortOrder)
            .Select(i =>
            {
                var product = products[i.ProductId];
                return new MenuItemDto(i.Id, i.ProductId, i.Section, product.Name, product.Description, product.ImageUrl, i.PfPrice, i.IsAvailable, i.SortOrder);
            })
            .ToList();

        return new MenuDto(
            menu.Id, menu.Version, menu.MenuType, menu.Kind, menu.Date, menu.DayOfWeek, menu.SourceMenuId, items,
            menu.CreatedAt, Author(menu.CreatedByEmployeeId, names), menu.UpdatedAt);
    }

    public static AuthorDto? Author(Guid? employeeId, IReadOnlyDictionary<Guid, string> names) =>
        employeeId is { } id ? new AuthorDto(id, names.GetValueOrDefault(id, string.Empty)) : null;
}

internal static class MenuRules
{
    public static async Task<MenuAggregate> LoadAsync(AppDbContext db, Guid menuId, CancellationToken cancellationToken) =>
        await db.Set<MenuAggregate>().Include(m => m.Items).SingleOrDefaultAsync(m => m.Id == menuId, cancellationToken)
        ?? throw new NotFoundException("MENU", menuId);

    /// ux_menus_daily / ux_menus_weekly (05 §5.6) → 409 MenuAlreadyExists com o id do existente.
    public static async Task EnsureSlotFreeAsync(
        AppDbContext db, MenuType menuType, MenuKind kind, DateOnly? date, int? dayOfWeek, CancellationToken cancellationToken)
    {
        var existing = await db.Set<MenuAggregate>().AsNoTracking()
            .Where(m => m.MenuType == menuType && m.Kind == kind)
            .Where(m => kind == MenuKind.Daily ? m.Date == date : m.DayOfWeek == dayOfWeek)
            .Select(m => (Guid?)m.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is { } id)
        {
            throw new BusinessException(
                ErrorCodes.MenuAlreadyExists,
                extensions: new Dictionary<string, object?> { ["existingMenuId"] = id });
        }
    }

    public static async Task<IReadOnlyDictionary<Guid, MenuProduct>> ProductsAsync(
        ICatalogReader catalog, IReadOnlyList<MenuItemInput> items, CancellationToken cancellationToken)
    {
        var products = await catalog.GetProductsAsync(items.Select(i => i.ProductId).Distinct().ToList(), cancellationToken);
        return products.ToDictionary(p => p.Key, p => new MenuProduct(p.Value.Id, p.Value.Kind, p.Value.DefaultPrice, p.Value.IsActive));
    }

    public static object AuditView(MenuAggregate m) => new
    {
        menuType = UpperSnakeEnumConverter<MenuType>.ToText(m.MenuType),
        kind = UpperSnakeEnumConverter<MenuKind>.ToText(m.Kind),
        m.Date,
        m.DayOfWeek,
        m.SourceMenuId,
        items = m.Items.Select(i => new { i.ProductId, i.IsAvailable, pfPrice = i.PfPrice?.ToString() }).ToList(),
    };
}
