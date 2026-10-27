using AlmoSeis.Infrastructure.Audit;
using AlmoSeis.Infrastructure.Concurrency;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Storage;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.Modules.Catalog.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Modules.Catalog.Application;

internal sealed class CreateProductHandler(
    AppDbContext db,
    ITenantContext tenant,
    IIdGenerator ids,
    IAuditWriter audit,
    IObjectStorage storage,
    TimeProvider time) : ICommandHandler<CreateProductCommand, ProductDto>
{
    public async Task<ProductDto> HandleAsync(CreateProductCommand command, CancellationToken cancellationToken)
    {
        await ProductRules.EnsureNameAvailableAsync(db, command.Kind, command.Name, exceptId: null, cancellationToken);

        var product = Product.Create(
            ids.New(), tenant.TenantId!.Value, command.Kind, command.Name, command.Description, command.Category,
            command.DefaultPrice, command.RequiresPreparation, time.GetUtcNow());
        db.Add(product);

        audit.Add("ProductCreated", product.SyncEntityType, product.Id, after: ProductRules.AuditView(product));
        return ProductMapping.ToDto(product, storage);
    }
}

internal sealed class UpdateProductHandler(
    AppDbContext db,
    ITenantContext tenant,
    IConcurrencyContext concurrency,
    IEnumerable<IProductUsageCheck> usageChecks,
    IAuditWriter audit,
    IObjectStorage storage,
    TimeProvider time) : ICommandHandler<UpdateProductCommand, ProductDto>
{
    public async Task<ProductDto> HandleAsync(UpdateProductCommand command, CancellationToken cancellationToken)
    {
        var product = await db.Set<Product>().SingleOrDefaultAsync(p => p.Id == command.ProductId, cancellationToken)
            ?? throw new NotFoundException("PRODUCT", command.ProductId);
        product.EnsureVersion(concurrency.ExpectedVersion);

        var changes = command.Changes;
        if (changes.ImageStorageKey is { HasValue: true, Value: { } key }
            && !key.StartsWith($"t/{tenant.TenantId}/p/{product.Id}", StringComparison.Ordinal))
        {
            throw BusinessException.Validation("imageStorageKey", "A imagem não pertence a este produto.");
        }

        if (changes.ImageStorageKey is { HasValue: true, Value: { } uploadedKey } && uploadedKey != product.ImageStorageKey)
        {
            await ProductImages.EnsureUploadedAsync(storage, uploadedKey, cancellationToken);
        }

        var kindLocked = false;
        if (changes.Kind.HasValue && changes.Kind.Value != product.Kind)
        {
            foreach (var check in usageChecks)
            {
                kindLocked |= await check.IsInUseAsync(product.Id, cancellationToken);
            }
        }

        var finalKind = changes.Kind.GetValueOr(product.Kind);
        var finalName = changes.Name.GetValueOr(product.Name);
        if (changes.IsActive.GetValueOr(product.IsActive)
            && (finalKind != product.Kind || !string.Equals(finalName.Trim(), product.Name, StringComparison.OrdinalIgnoreCase) || !product.IsActive))
        {
            await ProductRules.EnsureNameAvailableAsync(db, finalKind, finalName, product.Id, cancellationToken);
        }

        var before = ProductRules.AuditView(product);
        product.Apply(
            new ProductChanges(
                changes.Kind, changes.Name, changes.Description, changes.Category, changes.DefaultPrice,
                changes.RequiresPreparation, changes.ImageStorageKey, changes.IsActive, changes.IsAvailable),
            kindLocked,
            time.GetUtcNow());

        if (db.Entry(product).State == EntityState.Modified)
        {
            audit.Add("ProductUpdated", product.SyncEntityType, product.Id, before: before, after: ProductRules.AuditView(product));
        }

        return ProductMapping.ToDto(product, storage);
    }
}

internal sealed class GetProductQueryHandler(AppDbContext db, IObjectStorage storage) : IQueryHandler<GetProductQuery, ProductDto>
{
    public async Task<ProductDto> HandleAsync(GetProductQuery query, CancellationToken cancellationToken)
    {
        var product = await db.Set<Product>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == query.ProductId, cancellationToken)
            ?? throw new NotFoundException("PRODUCT", query.ProductId);
        return ProductMapping.ToDto(product, storage);
    }
}

internal sealed class ListProductsQueryHandler(AppDbContext db, IObjectStorage storage)
    : IQueryHandler<ListProductsQuery, ItemsDto<ProductDto>>
{
    public async Task<ItemsDto<ProductDto>> HandleAsync(ListProductsQuery query, CancellationToken cancellationToken)
    {
        var products = db.Set<Product>().AsNoTracking();
        if (query.Kind is { } kind)
        {
            products = products.Where(p => p.Kind == kind);
        }

        if (!query.IncludeInactive)
        {
            products = products.Where(p => p.IsActive);
        }

        var rows = await products.OrderBy(p => p.Kind).ThenBy(p => p.Category).ThenBy(p => p.Name).ToListAsync(cancellationToken);
        return new ItemsDto<ProductDto>(rows.Select(p => ProductMapping.ToDto(p, storage)).ToList());
    }
}

/// `current` do 409 de PATCH /products/{id}.
internal sealed class ProductCurrentReader(IQueryHandler<GetProductQuery, ProductDto> query) : ICurrentResourceReader
{
    public string EntityType => "PRODUCT";

    public async Task<IVersionedDto?> ReadAsync(Guid entityId, CancellationToken cancellationToken) =>
        await query.HandleAsync(new GetProductQuery(entityId), cancellationToken);
}

internal static class ProductRules
{
    /// ux_products_active_name (05 §5.5): nome único por tipo entre os ativos, sem diferenciar maiúsculas.
    public static async Task EnsureNameAvailableAsync(AppDbContext db, ProductKind kind, string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var normalized = name.Trim().ToLowerInvariant();
        var taken = await db.Set<Product>().AnyAsync(
            p => p.IsActive && p.Kind == kind && p.Name.ToLower() == normalized && p.Id != exceptId,
            cancellationToken);
        if (taken)
        {
            throw BusinessException.Validation("name", "Já existe um produto ativo com este nome.");
        }
    }

    public static object AuditView(Product p) => new
    {
        kind = UpperSnakeEnumConverter<ProductKind>.ToText(p.Kind),
        p.Name,
        defaultPrice = p.DefaultPrice?.ToString(),
        p.RequiresPreparation,
        p.IsActive,
        p.IsAvailable,
    };
}

internal static class ProductMapping
{
    public static ProductDto ToDto(Product p, IObjectStorage storage) => new(
        p.Id, p.Version, p.Kind, p.Name, p.Description, p.Category, p.DefaultPrice, p.RequiresPreparation,
        storage.PublicUrl(p.ImageStorageKey), p.IsActive, p.IsAvailable, p.CreatedAt, p.UpdatedAt);
}

/// POST /products/{id}/image-upload: URL pré-assinada de PUT direto no R2 (06 §6.5). Nada é gravado aqui: o app envia
/// o arquivo e depois grava `imageStorageKey` no PATCH, que confere o objeto.
internal sealed class RequestImageUploadHandler(AppDbContext db, ITenantContext tenant, IObjectStorage storage, TimeProvider time)
    : ICommandHandler<RequestImageUploadCommand, ImageUploadDto>
{
    public async Task<ImageUploadDto> HandleAsync(RequestImageUploadCommand command, CancellationToken cancellationToken)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == command.ProductId, cancellationToken))
        {
            throw new NotFoundException("PRODUCT", command.ProductId);
        }

        var request = command.Request;
        ProductImages.EnsureAllowed(request.ContentType, request.SizeBytes, "contentType", "sizeBytes");

        // Chave nova a cada pedido: a imagem antiga continua válida até o PATCH trocar.
        var suffix = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3));
        var key = $"t/{tenant.TenantId}/p/{command.ProductId}-{time.GetUtcNow():yyyyMMddHHmmss}-{suffix}.{ProductImages.Extensions[request.ContentType]}";
        var upload = storage.CreateUploadUrl(key, request.ContentType);
        return new ImageUploadDto(upload.Url, upload.Method, upload.Headers, key, upload.ExpiresAt);
    }
}

/// Regras da imagem de produto (06 §6.5): JPEG, PNG ou WebP, até 2 MB.
internal static class ProductImages
{
    public const long MaxBytes = 2 * 1024 * 1024;

    public static readonly IReadOnlyDictionary<string, string> Extensions = new Dictionary<string, string>
    {
        ["image/jpeg"] = "jpg",
        ["image/png"] = "png",
        ["image/webp"] = "webp",
    };

    public static void EnsureAllowed(string? contentType, long sizeBytes, string typeField, string sizeField)
    {
        if (contentType is null || !Extensions.ContainsKey(contentType))
        {
            throw BusinessException.Validation(typeField, "Use uma imagem JPEG, PNG ou WebP.");
        }

        if (sizeBytes is <= 0 or > MaxBytes)
        {
            throw BusinessException.Validation(sizeField, "A imagem deve ter até 2 MB.");
        }
    }

    /// A URL pré-assinada não limita o tamanho: o objeto é conferido no bucket antes de a chave ser aceita.
    /// Sem R2 configurado (desenvolvimento e testes), não há o que conferir.
    public static async Task EnsureUploadedAsync(IObjectStorage storage, string key, CancellationToken cancellationToken)
    {
        if (!storage.IsConfigured)
        {
            return;
        }

        var stored = await storage.GetAsync(key, cancellationToken)
            ?? throw BusinessException.Validation("imageStorageKey", "Envie a imagem antes de gravá-la no produto.");
        EnsureAllowed(stored.ContentType, stored.SizeBytes, "imageStorageKey", "imageStorageKey");
    }
}
