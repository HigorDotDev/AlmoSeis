using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;

namespace AlmoSeis.Modules.Catalog.Domain;

/// Produto do catálogo (04 §4.5, ADR-0020).
internal sealed class Product : AggregateRoot, ITenantOwned, ISyncTracked
{
    private Product()
    {
    }

    public Guid TenantId { get; private set; }
    public string SyncEntityType => "PRODUCT";
    public ProductKind Kind { get; private set; }
    public string Name { get; private set; } = null!;
    public string? Description { get; private set; }
    public string? Category { get; private set; }
    public Money? DefaultPrice { get; private set; }
    public bool RequiresPreparation { get; private set; }
    public string? ImageStorageKey { get; private set; }
    public bool IsActive { get; private set; } = true;
    public bool IsAvailable { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Product Create(
        Guid id, Guid tenantId, ProductKind kind, string name, string? description, string? category,
        Money? defaultPrice, bool? requiresPreparation, DateTimeOffset now)
    {
        var product = new Product
        {
            Id = id,
            TenantId = tenantId,
            Kind = kind,
            Name = name.Trim(),
            Description = Clean(description),
            Category = Clean(category),
            DefaultPrice = defaultPrice,
            // Padrão de 04 §4.5: pratos e adicionais vão para a produção, bebidas não.
            RequiresPreparation = requiresPreparation ?? kind != ProductKind.Beverage,
            CreatedAt = now,
            UpdatedAt = now,
        };
        product.EnsurePriceMatchesKind();
        product.Raise(new CatalogChanged(product.Id, "PRODUCT", now));
        return product;
    }

    /// Aplica um PATCH. Só incrementa a versão se algo mudou de fato.
    /// `kindLocked`: o produto já foi vendido ou está em cardápio (06 §6.5).
    public void Apply(ProductChanges changes, bool kindLocked, DateTimeOffset now)
    {
        var before = Snapshot();

        if (changes.Kind.HasValue && changes.Kind.Value != Kind)
        {
            if (kindLocked)
            {
                throw BusinessException.Validation("kind", "O tipo não pode mudar: o produto já foi vendido ou está em um cardápio.");
            }

            Kind = changes.Kind.Value;
        }

        Name = changes.Name.HasValue ? changes.Name.Value.Trim() : Name;
        Description = changes.Description.HasValue ? Clean(changes.Description.Value) : Description;
        Category = changes.Category.HasValue ? Clean(changes.Category.Value) : Category;
        DefaultPrice = changes.DefaultPrice.GetValueOr(DefaultPrice);
        RequiresPreparation = changes.RequiresPreparation.GetValueOr(RequiresPreparation);
        ImageStorageKey = changes.ImageStorageKey.GetValueOr(ImageStorageKey);
        IsActive = changes.IsActive.GetValueOr(IsActive);
        IsAvailable = changes.IsAvailable.GetValueOr(IsAvailable);

        EnsurePriceMatchesKind();

        if (!Equals(Snapshot(), before))
        {
            UpdatedAt = now;
            Touch(new CatalogChanged(Id, "PRODUCT", now));
        }
    }

    /// Espelha ck_products_price_by_kind (05 §5.5) com mensagem por campo.
    private void EnsurePriceMatchesKind()
    {
        if (Kind is ProductKind.Beverage or ProductKind.Addon && DefaultPrice is null)
        {
            throw BusinessException.Validation("defaultPrice", "Bebidas e adicionais precisam de preço.");
        }

        if (Kind == ProductKind.SideDish && DefaultPrice is not null)
        {
            throw BusinessException.Validation("defaultPrice", "Acompanhamentos não têm preço próprio.");
        }
    }

    private object Snapshot() =>
        (Kind, Name, Description, Category, DefaultPrice, RequiresPreparation, ImageStorageKey, IsActive, IsAvailable);

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// Campos do PATCH /products/{id}, já no vocabulário do domínio.
internal sealed record ProductChanges(
    Optional<ProductKind> Kind,
    Optional<string> Name,
    Optional<string?> Description,
    Optional<string?> Category,
    Optional<Money?> DefaultPrice,
    Optional<bool> RequiresPreparation,
    Optional<string?> ImageStorageKey,
    Optional<bool> IsActive,
    Optional<bool> IsAvailable);

/// Tamanho de marmita (P/M/G) com o preço da marmita (ADR-0020).
internal sealed class MarmitaSize : AggregateRoot, ITenantOwned, ISyncTracked
{
    private MarmitaSize()
    {
    }

    public Guid TenantId { get; private set; }
    public string SyncEntityType => "MARMITA_SIZE";
    public string Name { get; private set; } = null!;
    public Money Price { get; private set; }
    public int SortOrder { get; private set; }
    public bool IsActive { get; private set; } = true;

    public static MarmitaSize Create(Guid id, Guid tenantId, string name, Money price, int sortOrder, DateTimeOffset now)
    {
        var size = new MarmitaSize { Id = id, TenantId = tenantId, Name = name.Trim(), Price = price, SortOrder = sortOrder };
        size.Raise(new CatalogChanged(id, "MARMITA_SIZE", now));
        return size;
    }

    public void Apply(Optional<string> name, Optional<Money> price, Optional<int> sortOrder, Optional<bool> isActive, DateTimeOffset now)
    {
        var before = (Name, Price, SortOrder, IsActive);
        Name = name.HasValue ? name.Value.Trim() : Name;
        Price = price.GetValueOr(Price);
        SortOrder = sortOrder.GetValueOr(SortOrder);
        IsActive = isActive.GetValueOr(IsActive);

        if ((Name, Price, SortOrder, IsActive) != before)
        {
            Touch(new CatalogChanged(Id, "MARMITA_SIZE", now));
        }
    }
}

/// Vira `CatalogUpdated` no grupo catalog (06 §6.14).
internal sealed record CatalogChanged(Guid EntityId, string EntityType, DateTimeOffset OccurredAt) : IDomainEvent;
