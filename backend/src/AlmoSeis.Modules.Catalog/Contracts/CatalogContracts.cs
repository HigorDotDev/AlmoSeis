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
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt) : IVersionedDto;

public sealed record MarmitaSizeDto(Guid Id, int Version, string Name, Money Price, int SortOrder, bool IsActive) : IVersionedDto;

// ---------- Comandos e consultas ----------

public sealed record CreateProductCommand(
    ProductKind Kind,
    string Name,
    string? Description,
    string? Category,
    Money? DefaultPrice,
    bool? RequiresPreparation);

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
    Optional<bool> IsAvailable);

public sealed record UpdateProductCommand(Guid ProductId, UpdateProductRequest Changes);

public sealed record GetProductQuery(Guid ProductId);

public sealed record ListProductsQuery(ProductKind? Kind, bool IncludeInactive);

public sealed record CreateMarmitaSizeCommand(string Name, Money Price, int SortOrder);

public sealed record UpdateMarmitaSizeRequest(Optional<string> Name, Optional<Money> Price, Optional<int> SortOrder, Optional<bool> IsActive);

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
    bool IsAvailable);

public sealed record CatalogMarmitaSize(Guid Id, string Name, Money Price, bool IsActive);

/// Leitura do catálogo por outros módulos (Menu, Ordering), sempre no tenant corrente.
public interface ICatalogReader
{
    Task<IReadOnlyDictionary<Guid, CatalogProduct>> GetProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);

    Task<CatalogMarmitaSize?> GetMarmitaSizeAsync(Guid marmitaSizeId, CancellationToken cancellationToken);
}

/// Implementado pelos módulos que referenciam produtos (Menu, Ordering). Produto em uso não muda de `kind`.
public interface IProductUsageCheck
{
    Task<bool> IsInUseAsync(Guid productId, CancellationToken cancellationToken);
}
