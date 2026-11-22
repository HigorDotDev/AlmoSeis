using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Storage;
using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.Modules.Catalog.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using AlmoSeis.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Modules.Catalog.Application;

/// O cardápio fixo inteiro do tenant (catálogo pequeno: cinco consultas, sem rastreamento).
internal sealed record FixedMenuData(
    IReadOnlyList<DishCategory> Categories,
    IReadOnlyList<Dish> Dishes,
    IReadOnlyList<BeverageType> BeverageTypes,
    IReadOnlyList<Combo> Combos,
    IReadOnlyList<AddonGroup> AddonGroups)
{
    public static readonly FixedMenuData Empty = new([], [], [], [], []);

    public FixedMenuAvailability Availability => new(Dishes, BeverageTypes);
}

internal sealed class FixedMenuLoader(AppDbContext db, ITenantModules modules)
{
    /// Sem o módulo `FIXED_MENU`, vazio (ADR-0054 item 14: leitura de cadastro de módulo ausente não dá erro).
    public async Task<FixedMenuData> LoadAsync(CancellationToken cancellationToken)
    {
        if (!await modules.HasAsync(TenantModule.FixedMenu, cancellationToken))
        {
            return FixedMenuData.Empty;
        }

        return new FixedMenuData(
            await db.Set<DishCategory>().AsNoTracking().ToListAsync(cancellationToken),
            await DishesAsync(cancellationToken),
            await BeverageTypesAsync(cancellationToken),
            await db.Set<Combo>().AsNoTracking().Include(c => c.Components).AsSplitQuery().ToListAsync(cancellationToken),
            await AddonGroupsAsync(cancellationToken));
    }

    public async Task<List<Dish>> DishesAsync(CancellationToken cancellationToken) =>
        await db.Set<Dish>().AsNoTracking().Include(d => d.Sizes).AsSplitQuery().ToListAsync(cancellationToken);

    public async Task<List<BeverageType>> BeverageTypesAsync(CancellationToken cancellationToken) =>
        await db.Set<BeverageType>().AsNoTracking().Include(t => t.Sizes).ThenInclude(s => s.Flavors).AsSplitQuery().ToListAsync(cancellationToken);

    public async Task<List<AddonGroup>> AddonGroupsAsync(CancellationToken cancellationToken) =>
        await db.Set<AddonGroup>().AsNoTracking().Include(g => g.Options).Include(g => g.Links).AsSplitQuery().ToListAsync(cancellationToken);
}

/// Formato de 06 §6.5 "Cardápio fixo". `includeInactive: false` tira os filhos inativos (tamanhos, sabores, opções).
internal static class FixedMenuMapping
{
    public static DishCategoryDto ToDto(DishCategory c) => new(c.Id, c.Version, c.Name, c.SortOrder, c.IsActive, c.CreatedAt, c.UpdatedAt);

    public static DishDto ToDto(Dish d, IEnumerable<AddonGroup> groups, IObjectStorage storage, bool includeInactive = true) => new(
        d.Id, d.Version, d.DishCategoryId, d.Name, d.Description, storage.PublicUrl(d.ImageStorageKey), d.RequiresPreparation, d.HasSizes,
        d.Price, d.SellsOnSite, d.SitePrice, d.SortOrder, d.IsActive, d.IsPaused, FixedMenuAvailability.IsSellable(d),
        d.Sizes.Where(s => includeInactive || s.IsActive).OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Select(s => new DishSizeDto(s.Id, s.Name, s.Price, s.SellsOnSite, s.SitePrice, s.SortOrder, s.IsActive, s.IsPaused, FixedMenuAvailability.IsSellable(d, s)))
            .ToList(),
        GroupsOf(d, groups).Select(g => g.Id).ToList(),
        d.CreatedAt, d.UpdatedAt);

    public static BeverageTypeDto ToDto(BeverageType t, bool includeInactive = true) => new(
        t.Id, t.Version, t.Name, t.SortOrder, t.IsActive,
        t.Sizes.Where(s => includeInactive || s.IsActive).OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Select(s => new BeverageSizeDto(
                s.Id, s.Name, s.Price, s.SellsOnSite, s.SitePrice, s.SortOrder, s.IsActive, s.IsPaused, FixedMenuAvailability.IsSellable(t, s),
                s.Flavors.Where(f => includeInactive || f.IsActive).OrderBy(f => f.SortOrder).ThenBy(f => f.Name)
                    .Select(f => new BeverageFlavorDto(f.Id, f.Name, f.ExtraPrice, f.SortOrder, f.IsActive, f.IsPaused))
                    .ToList()))
            .ToList(),
        t.CreatedAt, t.UpdatedAt);

    public static ComboDto ToDto(Combo c, FixedMenuAvailability availability, IObjectStorage storage) => new(
        c.Id, c.Version, c.Name, c.Description, storage.PublicUrl(c.ImageStorageKey), c.Price, c.SellsOnSite, c.SitePrice, c.SortOrder,
        c.IsActive, c.IsPaused, availability.IsSellable(c),
        c.Components.OrderBy(x => x.SortOrder).Select(x =>
        {
            var (name, sellable) = availability.Component(x);
            return new ComboComponentDto(x.Id, x.Kind, x.DishId, x.DishSizeId, x.BeverageSizeId, name, x.Quantity, x.SortOrder, sellable);
        }).ToList(),
        c.CreatedAt, c.UpdatedAt);

    public static AddonGroupDto ToDto(AddonGroup g, bool includeInactive = true) => new(
        g.Id, g.Version, g.Name, g.SortOrder, g.IsActive,
        g.Options.Where(o => includeInactive || o.IsActive).OrderBy(o => o.SortOrder).ThenBy(o => o.Name)
            .Select(o => new AddonOptionDto(o.Id, o.Name, o.Price, o.SortOrder, o.IsActive, o.IsPaused))
            .ToList(),
        g.Links.Where(l => l.DishCategoryId is not null).Select(l => l.DishCategoryId!.Value).Order().ToList(),
        g.Links.Where(l => l.DishId is not null).Select(l => l.DishId!.Value).Order().ToList(),
        g.CreatedAt, g.UpdatedAt);

    /// Adicionais do prato = grupos ativos da categoria dele ∪ grupos ligados ao próprio prato, sem repetir (item 18).
    public static IEnumerable<AddonGroup> GroupsOf(Dish dish, IEnumerable<AddonGroup> groups) =>
        groups.Where(g => g.IsActive && g.Links.Any(l => l.DishId == dish.Id || l.DishCategoryId == dish.DishCategoryId))
            .OrderBy(g => g.SortOrder).ThenBy(g => g.Name);

    public static FixedMenuDto ToDto(FixedMenuData data, IObjectStorage storage, bool includeInactive)
    {
        var availability = data.Availability;
        return new FixedMenuDto(
            data.Categories.Where(c => includeInactive || c.IsActive).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).Select(ToDto).ToList(),
            data.Dishes.Where(d => includeInactive || d.IsActive).OrderBy(d => d.SortOrder).ThenBy(d => d.Name)
                .Select(d => ToDto(d, data.AddonGroups, storage, includeInactive)).ToList(),
            data.BeverageTypes.Where(t => includeInactive || t.IsActive).OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
                .Select(t => ToDto(t, includeInactive)).ToList(),
            data.Combos.Where(c => includeInactive || c.IsActive).OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
                .Select(c => ToDto(c, availability, storage)).ToList(),
            data.AddonGroups.Where(g => includeInactive || g.IsActive).OrderBy(g => g.SortOrder).ThenBy(g => g.Name)
                .Select(g => ToDto(g, includeInactive)).ToList());
    }

    /// Snapshot da porta `IFixedMenuReader`: só itens ativos, com a disponibilidade e os preços de balcão e de site.
    public static FixedMenuSnapshot ToSnapshot(FixedMenuData data)
    {
        var availability = data.Availability;
        var dishes = data.Dishes.Where(d => d.IsActive).ToDictionary(
            d => d.Id,
            d => new FixedMenuDish(
                d.Id, d.DishCategoryId, d.Name, d.RequiresPreparation, d.HasSizes,
                d.Price is { } price ? new FixedMenuPrice(price, d.SellsOnSite, d.SitePrice) : null,
                FixedMenuAvailability.IsSellable(d),
                d.Sizes.Where(s => s.IsActive).ToDictionary(
                    s => s.Id,
                    s => new FixedMenuDishSize(s.Id, s.Name, new FixedMenuPrice(s.Price, s.SellsOnSite, s.SitePrice), FixedMenuAvailability.IsSellable(d, s))),
                GroupsOf(d, data.AddonGroups).SelectMany(g => g.Options.Where(o => o.IsActive).OrderBy(o => o.SortOrder).Select(o => o.Id)).Distinct().ToList()));
        var beverageSizes = data.BeverageTypes.Where(t => t.IsActive)
            .SelectMany(t => t.Sizes.Where(s => s.IsActive).Select(s => (Type: t, Size: s)))
            .ToDictionary(
                x => x.Size.Id,
                x => new FixedMenuBeverageSize(
                    x.Size.Id, x.Type.Id, x.Type.Name, x.Size.Name, new FixedMenuPrice(x.Size.Price, x.Size.SellsOnSite, x.Size.SitePrice),
                    FixedMenuAvailability.IsSellable(x.Type, x.Size),
                    x.Size.Flavors.Where(f => f.IsActive).ToDictionary(
                        f => f.Id, f => new FixedMenuBeverageFlavor(f.Id, f.Name, f.ExtraPrice, FixedMenuAvailability.IsSellable(f)))));
        var combos = data.Combos.Where(c => c.IsActive).ToDictionary(
            c => c.Id,
            c => new FixedMenuCombo(
                c.Id, c.Name, new FixedMenuPrice(c.Price, c.SellsOnSite, c.SitePrice), availability.IsSellable(c),
                c.Components.OrderBy(x => x.SortOrder).Select(x =>
                {
                    var (name, sellable) = availability.Component(x);
                    return new FixedMenuComboComponent(x.Kind, x.DishId, x.DishSizeId, x.BeverageSizeId, x.Quantity, name, sellable);
                }).ToList()));
        var options = data.AddonGroups.Where(g => g.IsActive)
            .SelectMany(g => g.Options.Where(o => o.IsActive).Select(o => (Group: g, Option: o)))
            .ToDictionary(
                x => x.Option.Id,
                x => new FixedMenuAddonOption(x.Option.Id, x.Group.Id, x.Option.Name, x.Option.Price, FixedMenuAvailability.IsSellable(x.Group, x.Option)));
        return new FixedMenuSnapshot(dishes, beverageSizes, combos, options);
    }
}

/// Regras do cadastro que dependem de outros agregados ou do banco.
internal static class FixedMenuRules
{
    public const string Created = "FixedMenuItemCreated";
    public const string Updated = "FixedMenuItemUpdated";
    public const string AvailabilityChanged = "FixedMenuAvailabilityChanged";

    /// `ux_*_active_name` (05 §5.17 0009): nome único entre os ativos, sem diferenciar maiúsculas.
    public static async Task EnsureCategoryNameAsync(AppDbContext db, string name, Guid? exceptId, CancellationToken ct)
    {
        var normalized = name.Trim().ToLowerInvariant();
        if (await db.Set<DishCategory>().AnyAsync(c => c.IsActive && c.Name.ToLower() == normalized && c.Id != exceptId, ct))
        {
            throw BusinessException.Validation("name", "Já existe uma categoria ativa com este nome.");
        }
    }

    public static async Task EnsureDishNameAsync(AppDbContext db, string name, Guid? exceptId, CancellationToken ct)
    {
        var normalized = name.Trim().ToLowerInvariant();
        if (await db.Set<Dish>().AnyAsync(d => d.IsActive && d.Name.ToLower() == normalized && d.Id != exceptId, ct))
        {
            throw BusinessException.Validation("name", "Já existe um prato ativo com este nome.");
        }
    }

    public static async Task EnsureBeverageTypeNameAsync(AppDbContext db, string name, Guid? exceptId, CancellationToken ct)
    {
        var normalized = name.Trim().ToLowerInvariant();
        if (await db.Set<BeverageType>().AnyAsync(t => t.IsActive && t.Name.ToLower() == normalized && t.Id != exceptId, ct))
        {
            throw BusinessException.Validation("name", "Já existe um tipo de bebida ativo com este nome.");
        }
    }

    public static async Task EnsureComboNameAsync(AppDbContext db, string name, Guid? exceptId, CancellationToken ct)
    {
        var normalized = name.Trim().ToLowerInvariant();
        if (await db.Set<Combo>().AnyAsync(c => c.IsActive && c.Name.ToLower() == normalized && c.Id != exceptId, ct))
        {
            throw BusinessException.Validation("name", "Já existe um combo ativo com este nome.");
        }
    }

    public static async Task EnsureAddonGroupNameAsync(AppDbContext db, string name, Guid? exceptId, CancellationToken ct)
    {
        var normalized = name.Trim().ToLowerInvariant();
        if (await db.Set<AddonGroup>().AnyAsync(g => g.IsActive && g.Name.ToLower() == normalized && g.Id != exceptId, ct))
        {
            throw BusinessException.Validation("name", "Já existe um grupo de adicionais ativo com este nome.");
        }
    }

    /// Só checa o nome quando ele vai ficar ativo com outro texto (ou quando o item volta a ficar ativo).
    public static bool NameNeedsCheck(bool finalActive, bool wasActive, string currentName, string finalName) =>
        finalActive && (!wasActive || !string.Equals(finalName.Trim(), currentName, StringComparison.OrdinalIgnoreCase));

    public static async Task EnsureActiveCategoryAsync(AppDbContext db, Guid categoryId, string field, CancellationToken ct)
    {
        if (!await db.Set<DishCategory>().AnyAsync(c => c.Id == categoryId && c.IsActive, ct))
        {
            throw BusinessException.Validation(field, "Escolha uma categoria ativa.");
        }
    }

    /// Componentes do combo conferidos contra o cadastro (ADR-0056 item 13): prato ativo com o tamanho **dele**
    /// (obrigatório se o prato tem tamanho, proibido se não tem) e tamanho de bebida ativo de tipo ativo.
    /// Item pausado pode entrar: o combo só fica fora de venda enquanto durar a pausa.
    public static IReadOnlyList<ComboComponentSpec> ResolveComponents(
        IReadOnlyList<ComboComponentInput> inputs, IReadOnlyList<Dish> dishes, IReadOnlyList<BeverageType> beverageTypes)
    {
        var dishById = dishes.ToDictionary(d => d.Id);
        var sizes = beverageTypes.SelectMany(t => t.Sizes.Select(s => (Type: t, Size: s))).ToDictionary(x => x.Size.Id);
        var result = new List<ComboComponentSpec>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var field = $"components[{i}]";
            if (input.Kind == ComboComponentKind.Beverage)
            {
                if (input.BeverageSizeId is not { } sizeId || !sizes.TryGetValue(sizeId, out var beverage)
                    || !beverage.Size.IsActive || !beverage.Type.IsActive)
                {
                    throw BusinessException.Validation($"{field}.beverageSizeId", "Escolha um tamanho de bebida ativo.");
                }

                result.Add(new ComboComponentSpec(ComboComponentKind.Beverage, null, null, sizeId, input.Quantity));
                continue;
            }

            if (input.DishId is not { } dishId || !dishById.TryGetValue(dishId, out var dish) || !dish.IsActive)
            {
                throw BusinessException.Validation($"{field}.dishId", "Escolha um prato ativo.");
            }

            if (dish.HasSizes)
            {
                if (input.DishSizeId is not { } dishSizeId || !dish.Sizes.Any(s => s.Id == dishSizeId && s.IsActive))
                {
                    throw BusinessException.Validation($"{field}.dishSizeId", $"Escolha um tamanho ativo de {dish.Name}.");
                }
            }
            else if (input.DishSizeId is not null)
            {
                throw BusinessException.Validation($"{field}.dishSizeId", $"{dish.Name} não tem tamanho.");
            }

            result.Add(new ComboComponentSpec(ComboComponentKind.Dish, dishId, input.DishSizeId, null, input.Quantity));
        }

        return result;
    }

    /// Item já vendido ou usado em combo não é apagado, é inativado; inativar o que um combo **ativo** usa → `409` com os
    /// combos, para o estabelecimento ajustar antes (ADR-0056 item 26). Vale para prato, tamanho de prato, tipo e tamanho
    /// de bebida, e para o prato que passa a ter tamanho (o combo fixou o prato sem tamanho).
    public static async Task EnsureNotUsedByActiveCombosAsync(
        AppDbContext db,
        IReadOnlyCollection<Guid> dishIds,
        IReadOnlyCollection<Guid> dishIdsWithoutSize,
        IReadOnlyCollection<Guid> dishSizeIds,
        IReadOnlyCollection<Guid> beverageSizeIds,
        CancellationToken ct)
    {
        if (dishIds.Count + dishIdsWithoutSize.Count + dishSizeIds.Count + beverageSizeIds.Count == 0)
        {
            return;
        }

        var combos = await db.Set<Combo>().AsNoTracking()
            .Where(c => c.IsActive && c.Components.Any(x =>
                (x.DishId != null && dishIds.Contains(x.DishId.Value))
                || (x.DishId != null && x.DishSizeId == null && dishIdsWithoutSize.Contains(x.DishId.Value))
                || (x.DishSizeId != null && dishSizeIds.Contains(x.DishSizeId.Value))
                || (x.BeverageSizeId != null && beverageSizeIds.Contains(x.BeverageSizeId.Value))))
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(ct);
        if (combos.Count == 0)
        {
            return;
        }

        throw new BusinessException(
            ErrorCodes.UsedInActiveCombos,
            $"Ajuste ou inative antes: {string.Join(", ", combos.Select(c => c.Name))}.",
            new Dictionary<string, object?> { ["combos"] = combos.Select(c => new { id = c.Id, name = c.Name }).ToList() });
    }

    public static async Task EnsureLinkTargetsAsync(
        AppDbContext db, IReadOnlyList<Guid>? categoryIds, IReadOnlyList<Guid>? dishIds, CancellationToken ct)
    {
        if (categoryIds is { Count: > 0 })
        {
            var active = await db.Set<DishCategory>().Where(c => categoryIds.Contains(c.Id) && c.IsActive).Select(c => c.Id).ToListAsync(ct);
            for (var i = 0; i < categoryIds.Count; i++)
            {
                if (!active.Contains(categoryIds[i]))
                {
                    throw BusinessException.Validation($"dishCategoryIds[{i}]", "Escolha uma categoria ativa.");
                }
            }
        }

        if (dishIds is { Count: > 0 })
        {
            var active = await db.Set<Dish>().Where(d => dishIds.Contains(d.Id) && d.IsActive).Select(d => d.Id).ToListAsync(ct);
            for (var i = 0; i < dishIds.Count; i++)
            {
                if (!active.Contains(dishIds[i]))
                {
                    throw BusinessException.Validation($"dishIds[{i}]", "Escolha um prato ativo.");
                }
            }
        }
    }

    /// Chave da imagem de prato (`d`) ou combo (`c`): `t/{tenant}/{d|c}/{id}-{aaaaMMddHHmmss}-{6 hex}.{ext}`.
    public static string ImagePrefix(Guid tenantId, string entityType, Guid id) =>
        $"t/{tenantId}/{(entityType == Combo.EntityType ? "c" : "d")}/{id}-";

    public static async Task EnsureImageKeyAsync(
        IObjectStorage storage, Guid tenantId, string entityType, Guid id, string? currentKey, Optional<string?> key, CancellationToken ct)
    {
        if (key is not { HasValue: true, Value: { } value } || value == currentKey)
        {
            return;
        }

        if (!value.StartsWith(ImagePrefix(tenantId, entityType, id), StringComparison.Ordinal))
        {
            throw BusinessException.Validation("imageStorageKey", "A imagem não pertence a este item.");
        }

        await ProductImages.EnsureUploadedAsync(storage, value, ct);
    }

    // ---------- Vistas da auditoria: nomes, preços e pausas; nunca a entidade inteira (ADR-0031) ----------

    public static object AuditView(DishCategory c) => new { c.Name, c.SortOrder, c.IsActive };

    public static object AuditView(Dish d) => new
    {
        d.Name, d.DishCategoryId, d.HasSizes, price = d.Price?.ToString(), d.SellsOnSite, sitePrice = d.SitePrice?.ToString(),
        d.IsActive, d.IsPaused,
        sizes = d.Sizes.Select(s => new { s.Id, s.Name, price = s.Price.ToString(), s.SellsOnSite, sitePrice = s.SitePrice?.ToString(), s.IsActive, s.IsPaused }).ToList(),
    };

    public static object AuditView(BeverageType t) => new
    {
        t.Name, t.IsActive,
        sizes = t.Sizes.Select(s => new
        {
            s.Id, s.Name, price = s.Price.ToString(), s.SellsOnSite, sitePrice = s.SitePrice?.ToString(), s.IsActive, s.IsPaused,
            flavors = s.Flavors.Select(f => new { f.Id, f.Name, extraPrice = f.ExtraPrice.ToString(), f.IsActive, f.IsPaused }).ToList(),
        }).ToList(),
    };

    public static object AuditView(Combo c) => new
    {
        c.Name, price = c.Price.ToString(), c.SellsOnSite, sitePrice = c.SitePrice?.ToString(), c.IsActive, c.IsPaused,
        components = c.Components.OrderBy(x => x.SortOrder).Select(x => new { x.DishId, x.DishSizeId, x.BeverageSizeId, x.Quantity }).ToList(),
    };

    public static object AuditView(AddonGroup g) => new
    {
        g.Name, g.IsActive,
        options = g.Options.Select(o => new { o.Id, o.Name, price = o.Price.ToString(), o.IsActive, o.IsPaused }).ToList(),
        dishCategoryIds = g.Links.Where(l => l.DishCategoryId is not null).Select(l => l.DishCategoryId).ToList(),
        dishIds = g.Links.Where(l => l.DishId is not null).Select(l => l.DishId).ToList(),
    };
}
