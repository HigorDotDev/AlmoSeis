using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.Modules.Menu.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;

namespace AlmoSeis.Modules.Menu.Domain;

/// Cardápio do dia ou modelo semanal (04 §4.6, ADR-0038). Os itens só mudam pelo agregado.
internal sealed class Menu : AggregateRoot, ITenantOwned, ISyncTracked
{
    private readonly List<MenuItem> _items = [];

    private Menu()
    {
    }

    public Guid TenantId { get; private set; }
    public string SyncEntityType => "MENU";
    public MenuType MenuType { get; private set; }
    public MenuKind Kind { get; private set; }
    public DateOnly? Date { get; private set; }
    public int? DayOfWeek { get; private set; }
    public Guid? SourceMenuId { get; private set; }
    public Guid? CreatedByEmployeeId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyList<MenuItem> Items => _items;

    /// `createdBy` nulo = materializado pelo sistema (ADR-0038).
    public static Menu Create(
        Guid id, Guid tenantId, MenuType menuType, MenuKind kind, DateOnly? date, int? dayOfWeek, Guid? sourceMenuId,
        Guid? createdBy, IReadOnlyList<MenuItemSpec> items, Func<Guid> newId, DateTimeOffset now)
    {
        // Espelha ck_menus_kind (05 §5.6).
        if (kind == MenuKind.Daily && (date is null || dayOfWeek is not null))
        {
            throw BusinessException.Validation("date", "Cardápio do dia precisa de data (e não de dia da semana).");
        }

        if (kind == MenuKind.WeeklyTemplate && (date is not null || dayOfWeek is not (>= 1 and <= 7)))
        {
            throw BusinessException.Validation("dayOfWeek", "Modelo semanal precisa do dia da semana (1 = segunda … 7 = domingo) e não de data.");
        }

        var menu = new Menu
        {
            Id = id,
            TenantId = tenantId,
            MenuType = menuType,
            Kind = kind,
            Date = date,
            DayOfWeek = dayOfWeek,
            SourceMenuId = sourceMenuId,
            CreatedByEmployeeId = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };
        menu.SetItems(items, newId);
        menu.Raise(new MenuChanged(id, menuType, date, now));
        return menu;
    }

    /// PUT /menus/{id}/items: substitui a lista. Itens do mesmo produto mantêm o id.
    public void ReplaceItems(IReadOnlyList<MenuItemSpec> items, Func<Guid> newId, DateTimeOffset now)
    {
        SetItems(items, newId);
        Changed(now);
    }

    /// PATCH /menus/{id}/items/{itemId}: "acabou a costela" ou novo preço de PF.
    public void UpdateItem(Guid itemId, Optional<bool> isAvailable, Optional<Money?> pfPrice, DateTimeOffset now)
    {
        var item = _items.SingleOrDefault(i => i.Id == itemId) ?? throw new NotFoundException("MENU_ITEM", itemId);

        if (pfPrice.HasValue)
        {
            MenuItemSpecs.EnsurePfPrice(MenuType, item.Section, pfPrice.Value, "pfPrice");
        }

        var before = (item.IsAvailable, item.PfPrice);
        item.Update(isAvailable.GetValueOr(item.IsAvailable), pfPrice.GetValueOr(item.PfPrice));
        if ((item.IsAvailable, item.PfPrice) != before)
        {
            Changed(now);
        }
    }

    /// Cópia independente (RN-003.5): o original não muda e a cópia aponta para ele em `SourceMenuId`.
    public Menu CloneAs(
        Guid id, MenuKind kind, DateOnly? date, int? dayOfWeek, Guid? createdBy, Func<Guid> newId, DateTimeOffset now,
        Func<MenuItem, bool>? keep = null) =>
        Create(
            id, TenantId, MenuType, kind, date, dayOfWeek, Id, createdBy,
            _items.Where(keep ?? (_ => true)).Select(i => new MenuItemSpec(i.ProductId, i.Section, i.PfPrice, i.IsAvailable, i.SortOrder)).ToList(),
            newId, now);

    private void SetItems(IReadOnlyList<MenuItemSpec> specs, Func<Guid> newId)
    {
        _items.RemoveAll(i => specs.All(s => s.ProductId != i.ProductId));
        foreach (var spec in specs)
        {
            var existing = _items.SingleOrDefault(i => i.ProductId == spec.ProductId);
            if (existing is null)
            {
                _items.Add(new MenuItem(newId(), TenantId, Id, spec));
            }
            else
            {
                existing.Apply(spec);
            }
        }
    }

    private void Changed(DateTimeOffset now)
    {
        UpdatedAt = now;
        Touch(new MenuChanged(Id, MenuType, Date, now));
    }
}

internal sealed class MenuItem : Entity, ITenantOwned
{
    private MenuItem()
    {
    }

    internal MenuItem(Guid id, Guid tenantId, Guid menuId, MenuItemSpec spec)
    {
        Id = id;
        TenantId = tenantId;
        MenuId = menuId;
        ProductId = spec.ProductId;
        Apply(spec);
    }

    public Guid TenantId { get; private set; }
    public Guid MenuId { get; private set; }
    public Guid ProductId { get; private set; }
    public MenuSection Section { get; private set; }
    public Money? PfPrice { get; private set; }
    public bool IsAvailable { get; private set; }
    public int SortOrder { get; private set; }

    internal void Apply(MenuItemSpec spec)
    {
        Section = spec.Section;
        PfPrice = spec.PfPrice;
        IsAvailable = spec.IsAvailable;
        SortOrder = spec.SortOrder;
    }

    internal void Update(bool isAvailable, Money? pfPrice)
    {
        IsAvailable = isAvailable;
        PfPrice = pfPrice;
    }
}

/// Item já validado contra o catálogo.
internal sealed record MenuItemSpec(Guid ProductId, MenuSection Section, Money? PfPrice, bool IsAvailable, int SortOrder);

/// O que o cardápio precisa saber do produto (vem do ICatalogReader).
internal sealed record MenuProduct(Guid Id, ProductKind Kind, Money? DefaultPrice, bool IsActive);

/// Regras de item que dependem do produto (04 §4.6): section = kind, sem bebida/adicional,
/// pfPrice obrigatório só em PF + MAIN_DISH (padrão = preço do produto).
internal static class MenuItemSpecs
{
    public static IReadOnlyList<MenuItemSpec> Build(
        MenuType menuType, IReadOnlyList<MenuItemInput> inputs, IReadOnlyDictionary<Guid, MenuProduct> products)
    {
        var errors = new Dictionary<string, string[]>();
        var specs = new List<MenuItemSpec>();
        var seen = new HashSet<Guid>();

        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var field = $"items[{i}]";

            if (!seen.Add(input.ProductId))
            {
                errors[$"{field}.productId"] = ["Produto repetido no cardápio."];
                continue;
            }

            if (!products.TryGetValue(input.ProductId, out var product) || !product.IsActive)
            {
                errors[$"{field}.productId"] = ["Produto não encontrado ou inativo."];
                continue;
            }

            MenuSection section;
            switch (product.Kind)
            {
                case ProductKind.MainDish:
                    section = MenuSection.MainDish;
                    break;
                case ProductKind.SideDish:
                    section = MenuSection.SideDish;
                    break;
                default:
                    errors[$"{field}.productId"] = ["Bebidas e adicionais não entram no cardápio."];
                    continue;
            }

            var pfPrice = menuType == MenuType.Pf && section == MenuSection.MainDish
                ? input.PfPrice ?? product.DefaultPrice
                : input.PfPrice;
            if (PfPriceError(menuType, section, pfPrice) is { } message)
            {
                errors[$"{field}.pfPrice"] = [message];
                continue;
            }

            specs.Add(new MenuItemSpec(product.Id, section, pfPrice, input.IsAvailable, input.SortOrder));
        }

        return errors.Count > 0 ? throw BusinessException.Validation(errors) : specs;
    }

    public static void EnsurePfPrice(MenuType menuType, MenuSection section, Money? pfPrice, string field)
    {
        if (PfPriceError(menuType, section, pfPrice) is { } message)
        {
            throw BusinessException.Validation(field, message);
        }
    }

    private static string? PfPriceError(MenuType menuType, MenuSection section, Money? pfPrice)
    {
        var required = menuType == MenuType.Pf && section == MenuSection.MainDish;
        return (required, pfPrice) switch
        {
            (true, null) => "Informe o preço do PF deste prato.",
            (false, not null) => "Só pratos principais do cardápio PF têm preço.",
            _ => null,
        };
    }
}

/// Vira `MenuUpdated` no grupo catalog (06 §6.14).
internal sealed record MenuChanged(Guid MenuId, MenuType MenuType, DateOnly? Date, DateTimeOffset OccurredAt) : IDomainEvent;
