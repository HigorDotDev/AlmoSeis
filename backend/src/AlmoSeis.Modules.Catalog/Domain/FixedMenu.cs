using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;

namespace AlmoSeis.Modules.Catalog.Domain;

// Cardápio fixo (ADR-0056, ADR-0057; 04 §4.5.1; 05 §5.17 0009). Cinco agregados [V][S]: DishCategory, Dish (com DishSize),
// BeverageType (com BeverageSize → BeverageFlavor), Combo (com ComboComponent) e AddonGroup (com AddonOption e
// AddonGroupLink). Os filhos não têm versão: toda mudança neles, inclusive a pausa, sobe a versão da raiz.

/// "Vende no site" e preço de site (ADR-0057 itens 1 e 2): ligada sem preço de site → vem o preço de balcão.
/// Desligada, o preço de site fica guardado (volta preenchido se a chave for religada).
internal static class SitePricing
{
    public static Money? Resolve(bool sellsOnSite, Money? sitePrice, Money counterPrice) =>
        sellsOnSite && sitePrice is null ? counterPrice : sitePrice;
}

/// Categoria de pratos (ADR-0056 item 5): "Lanches", "Açaí". Ordenável pelo `SortOrder`.
internal sealed class DishCategory : AggregateRoot, ITenantOwned, ISyncTracked
{
    public const string EntityType = "DISH_CATEGORY";

    private DishCategory()
    {
    }

    public Guid TenantId { get; private set; }
    public string SyncEntityType => EntityType;
    public string Name { get; private set; } = null!;
    public int SortOrder { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static DishCategory Create(Guid id, Guid tenantId, string name, int sortOrder, DateTimeOffset now)
    {
        var category = new DishCategory
        {
            Id = id, TenantId = tenantId, Name = name.Trim(), SortOrder = sortOrder, CreatedAt = now, UpdatedAt = now,
        };
        category.Raise(new CatalogChanged(id, EntityType, now));
        return category;
    }

    public bool Apply(Optional<string> name, Optional<int> sortOrder, Optional<bool> isActive, DateTimeOffset now)
    {
        var before = (Name, SortOrder, IsActive);
        Name = name.HasValue ? name.Value.Trim() : Name;
        SortOrder = sortOrder.GetValueOr(SortOrder);
        IsActive = isActive.GetValueOr(IsActive);
        if ((Name, SortOrder, IsActive) == before)
        {
            return false;
        }

        UpdatedAt = now;
        Touch(new CatalogChanged(Id, EntityType, now));
        return true;
    }
}

/// Tamanho do prato já validado no formato (id nulo = novo).
internal sealed record DishSizeSpec(Guid? Id, string Name, Money Price, bool SellsOnSite, Money? SitePrice, int SortOrder);

internal sealed record DishData(
    Guid DishCategoryId,
    string Name,
    string? Description,
    bool RequiresPreparation,
    bool HasSizes,
    Money? Price,
    bool SellsOnSite,
    Money? SitePrice,
    int SortOrder,
    IReadOnlyList<DishSizeSpec> Sizes);

internal sealed record DishChanges(
    Optional<Guid> DishCategoryId,
    Optional<string> Name,
    Optional<string?> Description,
    Optional<bool> RequiresPreparation,
    Optional<bool> HasSizes,
    Optional<Money?> Price,
    Optional<bool> SellsOnSite,
    Optional<Money?> SitePrice,
    Optional<int> SortOrder,
    Optional<bool> IsActive,
    Optional<string?> ImageStorageKey,
    Optional<IReadOnlyList<DishSizeSpec>> Sizes);

/// Prato do cardápio fixo (ADR-0056 itens 6 a 8): preço único ou tamanhos do próprio prato ("Tamanho?").
internal sealed class Dish : AggregateRoot, ITenantOwned, ISyncTracked
{
    public const string EntityType = "DISH";

    private readonly List<DishSize> _sizes = [];

    private Dish()
    {
    }

    public Guid TenantId { get; private set; }
    public string SyncEntityType => EntityType;
    public Guid DishCategoryId { get; private set; }
    public string Name { get; private set; } = null!;
    public string? Description { get; private set; }
    public string? ImageStorageKey { get; private set; }
    public bool RequiresPreparation { get; private set; } = true;
    public bool HasSizes { get; private set; }
    public Money? Price { get; private set; }
    public bool SellsOnSite { get; private set; }
    public Money? SitePrice { get; private set; }
    public int SortOrder { get; private set; }
    public bool IsActive { get; private set; } = true;
    public bool IsPaused { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyList<DishSize> Sizes => _sizes;

    public static Dish Create(Guid id, Guid tenantId, DishData data, Func<Guid> newId, DateTimeOffset now)
    {
        var dish = new Dish
        {
            Id = id,
            TenantId = tenantId,
            DishCategoryId = data.DishCategoryId,
            Name = data.Name.Trim(),
            Description = Text.Clean(data.Description),
            RequiresPreparation = data.RequiresPreparation,
            SortOrder = data.SortOrder,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dish.ApplyPricing(data.HasSizes, Opt(data.Price), data.SellsOnSite, Opt(data.SitePrice), data.Sizes, newId);
        dish.Raise(new CatalogChanged(id, EntityType, now));
        return dish;

        static Optional<Money?> Opt(Money? value) => new(value);
    }

    /// PATCH: só os campos presentes mudam. `Sizes` presente substitui a lista: tamanhos fora dela são inativados (item 8),
    /// nunca apagados. Devolve se algo mudou (a versão sobe uma vez).
    public bool Apply(DishChanges changes, Func<Guid> newId, DateTimeOffset now)
    {
        var before = State();
        var previousImage = ImageStorageKey;

        DishCategoryId = changes.DishCategoryId.GetValueOr(DishCategoryId);
        Name = changes.Name.HasValue ? changes.Name.Value.Trim() : Name;
        Description = changes.Description.HasValue ? Text.Clean(changes.Description.Value) : Description;
        RequiresPreparation = changes.RequiresPreparation.GetValueOr(RequiresPreparation);
        SortOrder = changes.SortOrder.GetValueOr(SortOrder);
        IsActive = changes.IsActive.GetValueOr(IsActive);
        ImageStorageKey = changes.ImageStorageKey.GetValueOr(ImageStorageKey);

        var hasSizes = changes.HasSizes.GetValueOr(HasSizes);
        var price = changes.Price.HasValue ? changes.Price : (hasSizes ? new Optional<Money?>(null) : new Optional<Money?>(Price));
        var sellsOnSite = changes.SellsOnSite.GetValueOr(hasSizes ? false : SellsOnSite);
        var sitePrice = changes.SitePrice.HasValue ? changes.SitePrice : new Optional<Money?>(hasSizes ? null : SitePrice);
        var sizes = changes.Sizes.HasValue
            ? changes.Sizes.Value
            : hasSizes ? null : (IReadOnlyList<DishSizeSpec>)[];
        ApplyPricing(hasSizes, price, sellsOnSite, sitePrice, sizes, newId, sizesExplicit: changes.Sizes.HasValue);

        if (State() == before)
        {
            return false;
        }

        UpdatedAt = now;
        Touch(new CatalogChanged(Id, EntityType, now));
        if (previousImage is not null && previousImage != ImageStorageKey)
        {
            Touch(new FixedMenuImageDiscarded(EntityType, Id, previousImage, now));
        }

        return true;
    }

    /// Pausa do prato e dos tamanhos (ADR-0056 itens 21 e 22): fica até alguém reativar.
    public bool SetAvailability(bool? isPaused, IReadOnlyList<(Guid Id, bool IsPaused)> sizes, DateTimeOffset now)
    {
        var before = State();
        for (var i = 0; i < sizes.Count; i++)
        {
            var size = _sizes.SingleOrDefault(s => s.Id == sizes[i].Id)
                ?? throw BusinessException.Validation($"sizes[{i}].id", "Tamanho não pertence a este prato.");
            size.SetPaused(sizes[i].IsPaused);
        }

        IsPaused = isPaused ?? IsPaused;
        if (State() == before)
        {
            return false;
        }

        UpdatedAt = now;
        Touch(new CatalogChanged(Id, EntityType, now));
        return true;
    }

    /// Espelha `ck_dishes_price` e `ck_dishes_site_price` (05 §5.17 0009) com a mensagem no campo da API.
    private void ApplyPricing(
        bool hasSizes, Optional<Money?> price, bool sellsOnSite, Optional<Money?> sitePrice, IReadOnlyList<DishSizeSpec>? sizes,
        Func<Guid> newId, bool sizesExplicit = true)
    {
        if (hasSizes)
        {
            if (price.Value is not null)
            {
                throw BusinessException.Validation("price", "Prato com tamanho não tem preço único: o preço fica em cada tamanho.");
            }

            if (sellsOnSite || sitePrice.Value is not null)
            {
                throw BusinessException.Validation("sellsOnSite", "Prato com tamanho vende no site por tamanho.");
            }

            if (sizes is not null)
            {
                ReplaceSizes(sizes, newId);
            }

            if (!_sizes.Any(s => s.IsActive))
            {
                throw BusinessException.Validation("sizes", "Cadastre pelo menos um tamanho ativo.");
            }

            (HasSizes, Price, SellsOnSite, SitePrice) = (true, null, false, null);
            return;
        }

        if (price.Value is not { } single)
        {
            throw BusinessException.Validation("price", "Informe o preço do prato.");
        }

        if (sizesExplicit && sizes is { Count: > 0 })
        {
            throw BusinessException.Validation("sizes", "Ligue \"Tamanho?\" para cadastrar tamanhos.");
        }

        // Desligar "Tamanho?" inativa os tamanhos (ADR-0056 item 8): o histórico fica no snapshot dos pedidos.
        foreach (var size in _sizes)
        {
            size.Deactivate();
        }

        (HasSizes, Price, SellsOnSite, SitePrice) = (false, single, sellsOnSite, SitePricing.Resolve(sellsOnSite, sitePrice.Value, single));
    }

    private void ReplaceSizes(IReadOnlyList<DishSizeSpec> specs, Func<Guid> newId)
    {
        Children.EnsureUniqueNames(specs.Select(s => s.Name).ToList(), "sizes", "Já existe um tamanho com este nome.");
        var kept = new HashSet<Guid>();
        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            if (spec.Id is { } id)
            {
                var existing = _sizes.SingleOrDefault(s => s.Id == id)
                    ?? throw BusinessException.Validation($"sizes[{i}].id", "Tamanho não pertence a este prato.");
                existing.Apply(spec);
                kept.Add(id);
            }
            else
            {
                var created = new DishSize(newId(), TenantId, Id, spec);
                _sizes.Add(created);
                kept.Add(created.Id);
            }
        }

        foreach (var size in _sizes.Where(s => !kept.Contains(s.Id)))
        {
            size.Deactivate();
        }
    }

    private string State() => string.Join(
        '|',
        new object?[] { DishCategoryId, Name, Description, ImageStorageKey, RequiresPreparation, HasSizes, Price, SellsOnSite, SitePrice, SortOrder, IsActive, IsPaused }
            .Concat(_sizes.OrderBy(s => s.Id).Select(s => (object)s.State())));
}

internal sealed class DishSize : Entity, ITenantOwned
{
    private DishSize()
    {
    }

    internal DishSize(Guid id, Guid tenantId, Guid dishId, DishSizeSpec spec)
    {
        Id = id;
        TenantId = tenantId;
        DishId = dishId;
        Apply(spec);
    }

    public Guid TenantId { get; private set; }
    public Guid DishId { get; private set; }
    public string Name { get; private set; } = null!;
    public Money Price { get; private set; }
    public bool SellsOnSite { get; private set; }
    public Money? SitePrice { get; private set; }
    public int SortOrder { get; private set; }
    public bool IsActive { get; private set; } = true;
    public bool IsPaused { get; private set; }

    internal void Apply(DishSizeSpec spec)
    {
        Name = spec.Name.Trim();
        Price = spec.Price;
        SellsOnSite = spec.SellsOnSite;
        SitePrice = SitePricing.Resolve(spec.SellsOnSite, spec.SitePrice, spec.Price);
        SortOrder = spec.SortOrder;
        IsActive = true;
    }

    internal void Deactivate() => IsActive = false;

    internal void SetPaused(bool paused) => IsPaused = paused;

    internal string State() => $"{Id}:{Name}:{Price}:{SellsOnSite}:{SitePrice}:{SortOrder}:{IsActive}:{IsPaused}";
}

internal sealed record BeverageFlavorSpec(Guid? Id, string Name, Money ExtraPrice, int SortOrder);

internal sealed record BeverageSizeSpec(
    Guid? Id, string Name, Money Price, bool SellsOnSite, Money? SitePrice, int SortOrder, IReadOnlyList<BeverageFlavorSpec> Flavors);

/// Tipo de bebida → tamanho com preço → sabor do tamanho (ADR-0056 itens 9 a 12, Q-305).
internal sealed class BeverageType : AggregateRoot, ITenantOwned, ISyncTracked
{
    public const string EntityType = "BEVERAGE_TYPE";

    private readonly List<BeverageSize> _sizes = [];

    private BeverageType()
    {
    }

    public Guid TenantId { get; private set; }
    public string SyncEntityType => EntityType;
    public string Name { get; private set; } = null!;
    public int SortOrder { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyList<BeverageSize> Sizes => _sizes;

    public static BeverageType Create(
        Guid id, Guid tenantId, string name, int sortOrder, IReadOnlyList<BeverageSizeSpec> sizes, Func<Guid> newId, DateTimeOffset now)
    {
        var type = new BeverageType
        {
            Id = id, TenantId = tenantId, Name = name.Trim(), SortOrder = sortOrder, CreatedAt = now, UpdatedAt = now,
        };
        type.ReplaceSizes(sizes, newId);
        type.Raise(new CatalogChanged(id, EntityType, now));
        return type;
    }

    public bool Apply(
        Optional<string> name, Optional<int> sortOrder, Optional<bool> isActive, Optional<IReadOnlyList<BeverageSizeSpec>> sizes,
        Func<Guid> newId, DateTimeOffset now)
    {
        var before = State();
        Name = name.HasValue ? name.Value.Trim() : Name;
        SortOrder = sortOrder.GetValueOr(SortOrder);
        IsActive = isActive.GetValueOr(IsActive);
        if (sizes.HasValue)
        {
            ReplaceSizes(sizes.Value, newId);
        }

        return Changed(before, now);
    }

    /// Pausa de tamanhos e sabores ("acabou a Coca lata"), sem tocar nos outros tamanhos (Q-305).
    public bool SetAvailability(
        IReadOnlyList<(Guid Id, bool IsPaused)> sizes, IReadOnlyList<(Guid Id, bool IsPaused)> flavors, DateTimeOffset now)
    {
        var before = State();
        for (var i = 0; i < sizes.Count; i++)
        {
            var size = _sizes.SingleOrDefault(s => s.Id == sizes[i].Id)
                ?? throw BusinessException.Validation($"sizes[{i}].id", "Tamanho não pertence a este tipo de bebida.");
            size.SetPaused(sizes[i].IsPaused);
        }

        for (var i = 0; i < flavors.Count; i++)
        {
            var flavor = _sizes.SelectMany(s => s.Flavors).SingleOrDefault(f => f.Id == flavors[i].Id)
                ?? throw BusinessException.Validation($"flavors[{i}].id", "Sabor não pertence a este tipo de bebida.");
            flavor.SetPaused(flavors[i].IsPaused);
        }

        return Changed(before, now);
    }

    private bool Changed(string before, DateTimeOffset now)
    {
        if (State() == before)
        {
            return false;
        }

        UpdatedAt = now;
        Touch(new CatalogChanged(Id, EntityType, now));
        return true;
    }

    private void ReplaceSizes(IReadOnlyList<BeverageSizeSpec> specs, Func<Guid> newId)
    {
        Children.EnsureUniqueNames(specs.Select(s => s.Name).ToList(), "sizes", "Já existe um tamanho com este nome.");
        var kept = new HashSet<Guid>();
        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            BeverageSize size;
            if (spec.Id is { } id)
            {
                size = _sizes.SingleOrDefault(s => s.Id == id)
                    ?? throw BusinessException.Validation($"sizes[{i}].id", "Tamanho não pertence a este tipo de bebida.");
                size.Apply(spec);
            }
            else
            {
                size = new BeverageSize(newId(), TenantId, Id, spec);
                _sizes.Add(size);
            }

            size.ReplaceFlavors(spec.Flavors, newId, $"sizes[{i}].flavors");
            kept.Add(size.Id);
        }

        foreach (var size in _sizes.Where(s => !kept.Contains(s.Id)))
        {
            size.Deactivate();
        }
    }

    private string State() => string.Join(
        '|', new object[] { Name, SortOrder, IsActive }.Concat(_sizes.OrderBy(s => s.Id).Select(s => (object)s.State())));
}

internal sealed class BeverageSize : Entity, ITenantOwned
{
    private readonly List<BeverageFlavor> _flavors = [];

    private BeverageSize()
    {
    }

    internal BeverageSize(Guid id, Guid tenantId, Guid beverageTypeId, BeverageSizeSpec spec)
    {
        Id = id;
        TenantId = tenantId;
        BeverageTypeId = beverageTypeId;
        Apply(spec);
    }

    public Guid TenantId { get; private set; }
    public Guid BeverageTypeId { get; private set; }
    public string Name { get; private set; } = null!;
    public Money Price { get; private set; }
    public bool SellsOnSite { get; private set; }
    public Money? SitePrice { get; private set; }
    public int SortOrder { get; private set; }
    public bool IsActive { get; private set; } = true;
    public bool IsPaused { get; private set; }
    public IReadOnlyList<BeverageFlavor> Flavors => _flavors;

    internal void Apply(BeverageSizeSpec spec)
    {
        Name = spec.Name.Trim();
        Price = spec.Price;
        SellsOnSite = spec.SellsOnSite;
        SitePrice = SitePricing.Resolve(spec.SellsOnSite, spec.SitePrice, spec.Price);
        SortOrder = spec.SortOrder;
        IsActive = true;
    }

    /// A lista enviada substitui a do tamanho: sabores fora dela são inativados.
    internal void ReplaceFlavors(IReadOnlyList<BeverageFlavorSpec> specs, Func<Guid> newId, string field)
    {
        Children.EnsureUniqueNames(specs.Select(s => s.Name).ToList(), field, "Já existe um sabor com este nome.");
        var kept = new HashSet<Guid>();
        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            if (spec.Id is { } id)
            {
                var existing = _flavors.SingleOrDefault(f => f.Id == id)
                    ?? throw BusinessException.Validation($"{field}[{i}].id", "Sabor não pertence a este tamanho.");
                existing.Apply(spec);
                kept.Add(id);
            }
            else
            {
                var created = new BeverageFlavor(newId(), TenantId, Id, spec);
                _flavors.Add(created);
                kept.Add(created.Id);
            }
        }

        foreach (var flavor in _flavors.Where(f => !kept.Contains(f.Id)))
        {
            flavor.Deactivate();
        }
    }

    internal void Deactivate() => IsActive = false;

    internal void SetPaused(bool paused) => IsPaused = paused;

    internal string State() =>
        $"{Id}:{Name}:{Price}:{SellsOnSite}:{SitePrice}:{SortOrder}:{IsActive}:{IsPaused}[" +
        string.Join(',', _flavors.OrderBy(f => f.Id).Select(f => f.State())) + "]";
}

internal sealed class BeverageFlavor : Entity, ITenantOwned
{
    private BeverageFlavor()
    {
    }

    internal BeverageFlavor(Guid id, Guid tenantId, Guid beverageSizeId, BeverageFlavorSpec spec)
    {
        Id = id;
        TenantId = tenantId;
        BeverageSizeId = beverageSizeId;
        Apply(spec);
    }

    public Guid TenantId { get; private set; }
    public Guid BeverageSizeId { get; private set; }
    public string Name { get; private set; } = null!;
    public Money ExtraPrice { get; private set; }
    public int SortOrder { get; private set; }
    public bool IsActive { get; private set; } = true;
    public bool IsPaused { get; private set; }

    internal void Apply(BeverageFlavorSpec spec)
    {
        Name = spec.Name.Trim();
        ExtraPrice = spec.ExtraPrice;
        SortOrder = spec.SortOrder;
        IsActive = true;
    }

    internal void Deactivate() => IsActive = false;

    internal void SetPaused(bool paused) => IsPaused = paused;

    internal string State() => $"{Id}:{Name}:{ExtraPrice}:{SortOrder}:{IsActive}:{IsPaused}";
}

/// Componente já conferido contra o cadastro pelo handler (prato com o tamanho do próprio prato; tamanho de bebida).
internal sealed record ComboComponentSpec(ComboComponentKind Kind, Guid? DishId, Guid? DishSizeId, Guid? BeverageSizeId, int Quantity);

internal sealed record ComboData(
    string Name, string? Description, Money Price, bool SellsOnSite, Money? SitePrice, int SortOrder, IReadOnlyList<ComboComponentSpec> Components);

internal sealed record ComboChanges(
    Optional<string> Name,
    Optional<string?> Description,
    Optional<Money> Price,
    Optional<bool> SellsOnSite,
    Optional<Money?> SitePrice,
    Optional<int> SortOrder,
    Optional<bool> IsActive,
    Optional<string?> ImageStorageKey,
    Optional<IReadOnlyList<ComboComponentSpec>> Components);

/// Combo fixo com preço fechado (ADR-0056 itens 13 a 16). A disponibilidade é derivada dos componentes (item 23).
internal sealed class Combo : AggregateRoot, ITenantOwned, ISyncTracked
{
    public const string EntityType = "COMBO";

    private readonly List<ComboComponent> _components = [];

    private Combo()
    {
    }

    public Guid TenantId { get; private set; }
    public string SyncEntityType => EntityType;
    public string Name { get; private set; } = null!;
    public string? Description { get; private set; }
    public string? ImageStorageKey { get; private set; }
    public Money Price { get; private set; }
    public bool SellsOnSite { get; private set; }
    public Money? SitePrice { get; private set; }
    public int SortOrder { get; private set; }
    public bool IsActive { get; private set; } = true;
    public bool IsPaused { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyList<ComboComponent> Components => _components;

    public static Combo Create(Guid id, Guid tenantId, ComboData data, Func<Guid> newId, DateTimeOffset now)
    {
        var combo = new Combo
        {
            Id = id,
            TenantId = tenantId,
            Name = data.Name.Trim(),
            Description = Text.Clean(data.Description),
            Price = data.Price,
            SellsOnSite = data.SellsOnSite,
            SitePrice = SitePricing.Resolve(data.SellsOnSite, data.SitePrice, data.Price),
            SortOrder = data.SortOrder,
            CreatedAt = now,
            UpdatedAt = now,
        };
        combo.ReplaceComponents(data.Components, newId);
        combo.Raise(new CatalogChanged(id, EntityType, now));
        return combo;
    }

    public bool Apply(ComboChanges changes, Func<Guid> newId, DateTimeOffset now)
    {
        var before = State();
        var previousImage = ImageStorageKey;

        Name = changes.Name.HasValue ? changes.Name.Value.Trim() : Name;
        Description = changes.Description.HasValue ? Text.Clean(changes.Description.Value) : Description;
        Price = changes.Price.GetValueOr(Price);
        SellsOnSite = changes.SellsOnSite.GetValueOr(SellsOnSite);
        SitePrice = SitePricing.Resolve(SellsOnSite, changes.SitePrice.GetValueOr(SitePrice), Price);
        SortOrder = changes.SortOrder.GetValueOr(SortOrder);
        IsActive = changes.IsActive.GetValueOr(IsActive);
        ImageStorageKey = changes.ImageStorageKey.GetValueOr(ImageStorageKey);
        if (changes.Components.HasValue && !SameComponents(changes.Components.Value))
        {
            ReplaceComponents(changes.Components.Value, newId);
        }

        if (State() == before)
        {
            return false;
        }

        UpdatedAt = now;
        Touch(new CatalogChanged(Id, EntityType, now));
        if (previousImage is not null && previousImage != ImageStorageKey)
        {
            Touch(new FixedMenuImageDiscarded(EntityType, Id, previousImage, now));
        }

        return true;
    }

    public bool SetPaused(bool paused, DateTimeOffset now)
    {
        if (IsPaused == paused)
        {
            return false;
        }

        IsPaused = paused;
        UpdatedAt = now;
        Touch(new CatalogChanged(Id, EntityType, now));
        return true;
    }

    /// Os componentes não têm valor histórico (o pedido grava o snapshot): a edição os troca (05 §5.17 0009).
    private void ReplaceComponents(IReadOnlyList<ComboComponentSpec> specs, Func<Guid> newId)
    {
        if (specs.Count == 0)
        {
            throw BusinessException.Validation("components", "O combo precisa de pelo menos um item.");
        }

        _components.Clear();
        for (var i = 0; i < specs.Count; i++)
        {
            _components.Add(new ComboComponent(newId(), TenantId, Id, specs[i], i));
        }
    }

    private bool SameComponents(IReadOnlyList<ComboComponentSpec> specs) =>
        specs.Count == _components.Count
        && _components.OrderBy(c => c.SortOrder).Select(c => c.Spec()).SequenceEqual(specs);

    private string State() => string.Join(
        '|',
        new object?[] { Name, Description, ImageStorageKey, Price, SellsOnSite, SitePrice, SortOrder, IsActive, IsPaused }
            .Concat(_components.OrderBy(c => c.SortOrder).Select(c => (object)c.Spec())));
}

internal sealed class ComboComponent : Entity, ITenantOwned
{
    private ComboComponent()
    {
    }

    internal ComboComponent(Guid id, Guid tenantId, Guid comboId, ComboComponentSpec spec, int sortOrder)
    {
        Id = id;
        TenantId = tenantId;
        ComboId = comboId;
        Kind = spec.Kind;
        DishId = spec.DishId;
        DishSizeId = spec.DishSizeId;
        BeverageSizeId = spec.BeverageSizeId;
        Quantity = spec.Quantity;
        SortOrder = sortOrder;
    }

    public Guid TenantId { get; private set; }
    public Guid ComboId { get; private set; }
    public ComboComponentKind Kind { get; private set; }
    public Guid? DishId { get; private set; }
    public Guid? DishSizeId { get; private set; }
    public Guid? BeverageSizeId { get; private set; }
    public int Quantity { get; private set; }
    public int SortOrder { get; private set; }

    internal ComboComponentSpec Spec() => new(Kind, DishId, DishSizeId, BeverageSizeId, Quantity);
}

internal sealed record AddonOptionSpec(Guid? Id, string Name, Money Price, int SortOrder);

/// Grupo de adicionais do cardápio fixo (ADR-0056 itens 17 a 20), ligado a categorias e a pratos.
internal sealed class AddonGroup : AggregateRoot, ITenantOwned, ISyncTracked
{
    public const string EntityType = "ADDON_GROUP";

    private readonly List<AddonOption> _options = [];
    private readonly List<AddonGroupLink> _links = [];

    private AddonGroup()
    {
    }

    public Guid TenantId { get; private set; }
    public string SyncEntityType => EntityType;
    public string Name { get; private set; } = null!;
    public int SortOrder { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyList<AddonOption> Options => _options;
    public IReadOnlyList<AddonGroupLink> Links => _links;

    public static AddonGroup Create(
        Guid id, Guid tenantId, string name, int sortOrder, IReadOnlyList<AddonOptionSpec> options,
        IReadOnlyList<Guid> dishCategoryIds, IReadOnlyList<Guid> dishIds, Func<Guid> newId, DateTimeOffset now)
    {
        var group = new AddonGroup
        {
            Id = id, TenantId = tenantId, Name = name.Trim(), SortOrder = sortOrder, CreatedAt = now, UpdatedAt = now,
        };
        group.ReplaceOptions(options, newId);
        group.ReplaceLinks(dishCategoryIds, dishIds, newId);
        group.Raise(new CatalogChanged(id, EntityType, now));
        return group;
    }

    public bool Apply(
        Optional<string> name, Optional<int> sortOrder, Optional<bool> isActive, Optional<IReadOnlyList<AddonOptionSpec>> options,
        Optional<IReadOnlyList<Guid>> dishCategoryIds, Optional<IReadOnlyList<Guid>> dishIds, Func<Guid> newId, DateTimeOffset now)
    {
        var before = State();
        Name = name.HasValue ? name.Value.Trim() : Name;
        SortOrder = sortOrder.GetValueOr(SortOrder);
        IsActive = isActive.GetValueOr(IsActive);
        if (options.HasValue)
        {
            ReplaceOptions(options.Value, newId);
        }

        if (dishCategoryIds.HasValue || dishIds.HasValue)
        {
            ReplaceLinks(
                dishCategoryIds.GetValueOr(_links.Where(l => l.DishCategoryId is not null).Select(l => l.DishCategoryId!.Value).ToList()),
                dishIds.GetValueOr(_links.Where(l => l.DishId is not null).Select(l => l.DishId!.Value).ToList()),
                newId);
        }

        return Changed(before, now);
    }

    /// "Acabou o bacon" (ADR-0056 item 21, recomendação).
    public bool SetAvailability(IReadOnlyList<(Guid Id, bool IsPaused)> options, DateTimeOffset now)
    {
        var before = State();
        for (var i = 0; i < options.Count; i++)
        {
            var option = _options.SingleOrDefault(o => o.Id == options[i].Id)
                ?? throw BusinessException.Validation($"options[{i}].id", "Opção não pertence a este grupo.");
            option.SetPaused(options[i].IsPaused);
        }

        return Changed(before, now);
    }

    private bool Changed(string before, DateTimeOffset now)
    {
        if (State() == before)
        {
            return false;
        }

        UpdatedAt = now;
        Touch(new CatalogChanged(Id, EntityType, now));
        return true;
    }

    private void ReplaceOptions(IReadOnlyList<AddonOptionSpec> specs, Func<Guid> newId)
    {
        Children.EnsureUniqueNames(specs.Select(s => s.Name).ToList(), "options", "Já existe uma opção com este nome.");
        var kept = new HashSet<Guid>();
        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            if (spec.Id is { } id)
            {
                var existing = _options.SingleOrDefault(o => o.Id == id)
                    ?? throw BusinessException.Validation($"options[{i}].id", "Opção não pertence a este grupo.");
                existing.Apply(spec);
                kept.Add(id);
            }
            else
            {
                var created = new AddonOption(newId(), TenantId, Id, spec);
                _options.Add(created);
                kept.Add(created.Id);
            }
        }

        foreach (var option in _options.Where(o => !kept.Contains(o.Id)))
        {
            option.Deactivate();
        }
    }

    /// Vínculos sem valor histórico: os que saíram são apagados, os que ficam mantêm o id (05 §5.17 0009).
    private void ReplaceLinks(IReadOnlyList<Guid> dishCategoryIds, IReadOnlyList<Guid> dishIds, Func<Guid> newId)
    {
        var categories = dishCategoryIds.Distinct().ToHashSet();
        var dishes = dishIds.Distinct().ToHashSet();
        _links.RemoveAll(l => l.DishCategoryId is { } c ? !categories.Contains(c) : !dishes.Contains(l.DishId!.Value));
        foreach (var category in categories.Where(c => _links.All(l => l.DishCategoryId != c)))
        {
            _links.Add(new AddonGroupLink(newId(), TenantId, Id, category, null));
        }

        foreach (var dish in dishes.Where(d => _links.All(l => l.DishId != d)))
        {
            _links.Add(new AddonGroupLink(newId(), TenantId, Id, null, dish));
        }
    }

    private string State() => string.Join(
        '|',
        new object[] { Name, SortOrder, IsActive }
            .Concat(_options.OrderBy(o => o.Id).Select(o => (object)o.State()))
            .Concat(_links.Select(l => (object)$"{l.DishCategoryId}/{l.DishId}").Order()));
}

internal sealed class AddonOption : Entity, ITenantOwned
{
    private AddonOption()
    {
    }

    internal AddonOption(Guid id, Guid tenantId, Guid addonGroupId, AddonOptionSpec spec)
    {
        Id = id;
        TenantId = tenantId;
        AddonGroupId = addonGroupId;
        Apply(spec);
    }

    public Guid TenantId { get; private set; }
    public Guid AddonGroupId { get; private set; }
    public string Name { get; private set; } = null!;
    public Money Price { get; private set; }
    public int SortOrder { get; private set; }
    public bool IsActive { get; private set; } = true;
    public bool IsPaused { get; private set; }

    internal void Apply(AddonOptionSpec spec)
    {
        Name = spec.Name.Trim();
        Price = spec.Price;
        SortOrder = spec.SortOrder;
        IsActive = true;
    }

    internal void Deactivate() => IsActive = false;

    internal void SetPaused(bool paused) => IsPaused = paused;

    internal string State() => $"{Id}:{Name}:{Price}:{SortOrder}:{IsActive}:{IsPaused}";
}

internal sealed class AddonGroupLink : Entity, ITenantOwned
{
    private AddonGroupLink()
    {
    }

    internal AddonGroupLink(Guid id, Guid tenantId, Guid addonGroupId, Guid? dishCategoryId, Guid? dishId)
    {
        Id = id;
        TenantId = tenantId;
        AddonGroupId = addonGroupId;
        DishCategoryId = dishCategoryId;
        DishId = dishId;
    }

    public Guid TenantId { get; private set; }
    public Guid AddonGroupId { get; private set; }
    public Guid? DishCategoryId { get; private set; }
    public Guid? DishId { get; private set; }
}

/// Imagem de prato ou combo substituída ou removida: o objeto antigo no R2 é apagado depois do commit (como o produto).
internal sealed record FixedMenuImageDiscarded(string EntityType, Guid EntityId, string StorageKey, DateTimeOffset OccurredAt) : IDomainEvent;

internal static class Children
{
    /// O nome é único entre os ativos do mesmo pai (`ux_*_active_name`): repetir na lista é erro no campo do item.
    public static void EnsureUniqueNames(IReadOnlyList<string> names, string field, string message)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Count; i++)
        {
            if (!seen.Add(names[i].Trim()))
            {
                throw BusinessException.Validation($"{field}[{i}].name", message);
            }
        }
    }
}

internal static class Text
{
    public static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
