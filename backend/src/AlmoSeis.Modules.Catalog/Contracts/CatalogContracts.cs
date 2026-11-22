using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;

namespace AlmoSeis.Modules.Catalog.Contracts;

public enum ProductKind
{
    MainDish,
    SideDish,
    Beverage,
    Addon,
}

// ---------- DTOs (06 §6.5) ----------

public sealed record ProductDto(
    Guid Id,
    int Version,
    ProductKind Kind,
    string Name,
    string? Description,
    string? Category,
    Money? DefaultPrice,
    bool RequiresPreparation,
    string? ImageUrl,
    bool IsActive,
    bool IsAvailable,
    bool IsVisibleOnPage,
    Money? SitePrice,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt) : IVersionedDto;

public sealed record MarmitaSizeDto(
    Guid Id, int Version, string Name, Money Price, int SortOrder, bool IsActive, bool SellsOnSite, Money? SitePrice) : IVersionedDto;

// ---------- Comandos e consultas ----------

public sealed record CreateProductCommand(
    ProductKind Kind,
    string Name,
    string? Description,
    string? Category,
    Money? DefaultPrice,
    bool? RequiresPreparation,
    Money? SitePrice = null);

/// Corpo do PATCH /products/{id}: só os campos presentes mudam.
public sealed record UpdateProductRequest(
    Optional<ProductKind> Kind,
    Optional<string> Name,
    Optional<string?> Description,
    Optional<string?> Category,
    Optional<Money?> DefaultPrice,
    Optional<bool> RequiresPreparation,
    Optional<string?> ImageStorageKey,
    Optional<bool> IsActive,
    Optional<bool> IsAvailable,
    Optional<bool> IsVisibleOnPage = default,
    Optional<Money?> SitePrice = default);

public sealed record UpdateProductCommand(Guid ProductId, UpdateProductRequest Changes);

/// POST /products/{id}/image-upload (06 §6.5).
public sealed record RequestImageUploadRequest(string ContentType, long SizeBytes);

public sealed record RequestImageUploadCommand(Guid ProductId, RequestImageUploadRequest Request);

public sealed record ImageUploadDto(string UploadUrl, string Method, IReadOnlyDictionary<string, string> Headers, string StorageKey, DateTimeOffset ExpiresAt);

public sealed record GetProductQuery(Guid ProductId);

public sealed record ListProductsQuery(ProductKind? Kind, bool IncludeInactive);

public sealed record CreateMarmitaSizeCommand(string Name, Money Price, int SortOrder, bool SellsOnSite = false, Money? SitePrice = null);

public sealed record UpdateMarmitaSizeRequest(
    Optional<string> Name,
    Optional<Money> Price,
    Optional<int> SortOrder,
    Optional<bool> IsActive,
    Optional<bool> SellsOnSite = default,
    Optional<Money?> SitePrice = default);

public sealed record UpdateMarmitaSizeCommand(Guid MarmitaSizeId, UpdateMarmitaSizeRequest Changes);

public sealed record ListMarmitaSizesQuery(bool IncludeInactive);

// ---------- Ports (07 §7.7) ----------

public sealed record CatalogProduct(
    Guid Id,
    ProductKind Kind,
    string Name,
    string? Description,
    Money? DefaultPrice,
    bool RequiresPreparation,
    string? ImageUrl,
    bool IsActive,
    bool IsAvailable,
    bool IsVisibleOnPage = true,
    Money? SitePrice = null)
{
    /// Preço da tabela do pedido (ADR-0057 itens 5 a 8): bebida vendida no site (`IsVisibleOnPage`) com preço de site
    /// num pedido `SITE` → o de site; o resto, o de balcão. Adicional: o mesmo preço (Q-308). Bebida visível ainda sem
    /// preço de site (gravada pela API anterior na janela do deploy da 0009) → o de balcão, como a página mostra.
    public Money? PriceFor(PriceList priceList) =>
        priceList == PriceList.Site && Kind == ProductKind.Beverage && IsVisibleOnPage && SitePrice is { } site ? site : DefaultPrice;
}

public sealed record CatalogMarmitaSize(Guid Id, string Name, Money Price, bool IsActive, int SortOrder = 0, bool SellsOnSite = false, Money? SitePrice = null)
{
    /// Pedido `SITE` → preço de site quando o tamanho vende no site; senão o de balcão (ADR-0057 itens 6 e 8, Q-309).
    public Money PriceFor(PriceList priceList) => priceList == PriceList.Site && SellsOnSite && SitePrice is { } site ? site : Price;
}

/// Leitura do catálogo por outros módulos (Menu, Ordering), sempre no tenant corrente.
public interface ICatalogReader
{
    Task<IReadOnlyDictionary<Guid, CatalogProduct>> GetProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);

    Task<CatalogMarmitaSize?> GetMarmitaSizeAsync(Guid marmitaSizeId, CancellationToken cancellationToken);

    /// Página de pedidos (ADR-0044 item 5): produtos ativos dos tipos pedidos, na ordem do nome.
    Task<IReadOnlyList<CatalogProduct>> ListActiveAsync(IReadOnlyCollection<ProductKind> kinds, CancellationToken cancellationToken);

    /// Tamanhos ativos, na ordem de `sortOrder`.
    Task<IReadOnlyList<CatalogMarmitaSize>> ListActiveMarmitaSizesAsync(CancellationToken cancellationToken);
}

/// Implementado pelos módulos que referenciam produtos (Menu, Ordering). Produto em uso não muda de `kind`.
public interface IProductUsageCheck
{
    Task<bool> IsInUseAsync(Guid productId, CancellationToken cancellationToken);
}
