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

    /// Bebidas e adicionais podem ser escondidos da página de pedidos (ADR-0044 item 5). Pratos seguem o cardápio do dia.
    public bool IsVisibleOnPage { get; private set; } = true;

    /// Preço de site da bebida (ADR-0057 item 5, Q-307): o `IsVisibleOnPage` é o "vende no site" dela. Só `BEVERAGE`
    /// (`ck_products_site_price`); adicional usa o mesmo preço no site (Q-308).
    public Money? SitePrice { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Product Create(
        Guid id, Guid tenantId, ProductKind kind, string name, string? description, string? category,
        Money? defaultPrice, bool? requiresPreparation, DateTimeOffset now, Money? sitePrice = null)
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
        product.ApplySitePrice(sitePrice is null ? default : new Optional<Money?>(sitePrice), creating: true);
        product.Raise(new CatalogChanged(product.Id, "PRODUCT", now));
        return product;
    }

    /// Aplica um PATCH. Só incrementa a versão se algo mudou de fato.
    /// `kindLocked`: o produto já foi vendido ou está em cardápio (06 §6.5).
    public void Apply(ProductChanges changes, bool kindLocked, DateTimeOffset now)
    {
        var before = Snapshot();
        var previousImage = ImageStorageKey;

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
        IsVisibleOnPage = changes.IsVisibleOnPage.GetValueOr(IsVisibleOnPage);

        EnsurePriceMatchesKind();
        ApplySitePrice(changes.SitePrice, creating: false);

        if (!Equals(Snapshot(), before))
        {
            UpdatedAt = now;
            Touch(new CatalogChanged(Id, "PRODUCT", now));
        }

        // Troca ou remoção: o arquivo anterior só é apagado depois do commit, pelo outbox (07 §7.13).
        if (previousImage is not null && previousImage != ImageStorageKey)
        {
            Touch(new ProductImageDiscarded(Id, previousImage, now));
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

    /// A API desta versão **sempre** grava `site_price` na bebida vendida no site (05 §5.17 0009, janela do deploy).
    /// Sem preço de site informado, vem o de balcão (ADR-0057 item 2), **também quando é `0.00`**: a bebida do
    /// restaurante pode ter preço de site `>= 0` (água da casa, cafezinho; revisão de 2026-11-23, I6, 05 §5.17 0012).
    /// Na edição, apagar o preço de site (`sitePrice: null`) da bebida visível continua recusado.
    private void ApplySitePrice(Optional<Money?> sitePrice, bool creating)
    {
        if (Kind != ProductKind.Beverage)
        {
            if (sitePrice is { HasValue: true, Value: not null })
            {
                throw BusinessException.Validation("sitePrice", "Só bebidas têm preço de site; adicionais usam o mesmo preço.");
            }

            SitePrice = null;
            return;
        }

        if (sitePrice is { HasValue: true, Value: { IsNegative: true } })
        {
            throw BusinessException.Validation("sitePrice", "O preço de site não pode ser negativo.");
        }

        SitePrice = sitePrice.GetValueOr(SitePrice);
        if (!IsVisibleOnPage || SitePrice is not null)
        {
            return;
        }

        if (sitePrice.HasValue && !creating)
        {
            throw BusinessException.Validation("sitePrice", "Informe o preço de site da bebida vendida no site.");
        }

        // A bebida sempre tem preço de balcão (EnsurePriceMatchesKind), e ele vale como preço de site mesmo em 0,00.
        SitePrice = DefaultPrice;
    }

    private object Snapshot() =>
        (Kind, Name, Description, Category, DefaultPrice, RequiresPreparation, ImageStorageKey, IsActive, IsAvailable, IsVisibleOnPage, SitePrice);

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
    Optional<bool> IsAvailable,
    Optional<bool> IsVisibleOnPage,
    Optional<Money?> SitePrice = default);

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

    /// "Vende no site" e preço de site do tamanho (ADR-0057 item 1). Desligada, o tamanho não aparece na página.
    public bool SellsOnSite { get; private set; }
    public Money? SitePrice { get; private set; }

    public static MarmitaSize Create(
        Guid id, Guid tenantId, string name, Money price, int sortOrder, DateTimeOffset now, bool sellsOnSite = false, Money? sitePrice = null)
    {
        var size = new MarmitaSize
        {
            Id = id, TenantId = tenantId, Name = name.Trim(), Price = price, SortOrder = sortOrder,
            SellsOnSite = sellsOnSite, SitePrice = SitePricing.Resolve(sellsOnSite, sitePrice, price),
        };
        size.Raise(new CatalogChanged(id, "MARMITA_SIZE", now));
        return size;
    }

    public void Apply(
        Optional<string> name, Optional<Money> price, Optional<int> sortOrder, Optional<bool> isActive, DateTimeOffset now,
        Optional<bool> sellsOnSite = default, Optional<Money?> sitePrice = default)
    {
        var before = (Name, Price, SortOrder, IsActive, SellsOnSite, SitePrice);
        Name = name.HasValue ? name.Value.Trim() : Name;
        Price = price.GetValueOr(Price);
        SortOrder = sortOrder.GetValueOr(SortOrder);
        IsActive = isActive.GetValueOr(IsActive);
        SellsOnSite = sellsOnSite.GetValueOr(SellsOnSite);
        SitePrice = SitePricing.Resolve(SellsOnSite, sitePrice.GetValueOr(SitePrice), Price);

        if ((Name, Price, SortOrder, IsActive, SellsOnSite, SitePrice) != before)
        {
            Touch(new CatalogChanged(Id, "MARMITA_SIZE", now));
        }
    }
}

/// Vira `CatalogUpdated` no grupo catalog (06 §6.14).
internal sealed record CatalogChanged(Guid EntityId, string EntityType, DateTimeOffset OccurredAt) : IDomainEvent;

/// Imagem substituída ou removida: o objeto antigo no R2 deve ser apagado depois do commit.
internal sealed record ProductImageDiscarded(Guid ProductId, string StorageKey, DateTimeOffset OccurredAt) : IDomainEvent;
