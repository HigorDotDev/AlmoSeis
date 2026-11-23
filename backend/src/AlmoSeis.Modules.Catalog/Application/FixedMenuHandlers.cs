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

// Cardápio fixo (06 §6.5 "Cardápio fixo", ADR-0056/0057). Cadastro com `catalog.manage`, pausa com `catalog.availability`,
// as duas com [RequiresModule(FIXED_MENU)] no controller. Escritas de recurso existente com If-Match na versão da raiz.

// ---------- Categorias ----------

internal sealed class CreateDishCategoryHandler(AppDbContext db, ITenantContext tenant, IIdGenerator ids, IAuditWriter audit, TimeProvider time)
    : ICommandHandler<CreateDishCategoryCommand, DishCategoryDto>
{
    public async Task<DishCategoryDto> HandleAsync(CreateDishCategoryCommand command, CancellationToken cancellationToken)
    {
        await FixedMenuRules.EnsureCategoryNameAsync(db, command.Name, null, cancellationToken);
        var category = DishCategory.Create(ids.New(), tenant.TenantId!.Value, command.Name, command.SortOrder, time.GetUtcNow());
        db.Add(category);
        audit.Add(FixedMenuRules.Created, category.SyncEntityType, category.Id, after: FixedMenuRules.AuditView(category));
        return FixedMenuMapping.ToDto(category);
    }
}

internal sealed class UpdateDishCategoryHandler(AppDbContext db, IConcurrencyContext concurrency, IAuditWriter audit, TimeProvider time)
    : ICommandHandler<UpdateDishCategoryCommand, DishCategoryDto>
{
    public async Task<DishCategoryDto> HandleAsync(UpdateDishCategoryCommand command, CancellationToken cancellationToken)
    {
        var category = await db.Set<DishCategory>().SingleOrDefaultAsync(c => c.Id == command.DishCategoryId, cancellationToken)
            ?? throw new NotFoundException(DishCategory.EntityType, command.DishCategoryId);
        category.EnsureVersion(concurrency.ExpectedVersion);

        var changes = command.Changes;
        var finalActive = changes.IsActive.GetValueOr(category.IsActive);
        if (FixedMenuRules.NameNeedsCheck(finalActive, category.IsActive, category.Name, changes.Name.GetValueOr(category.Name)))
        {
            await FixedMenuRules.EnsureCategoryNameAsync(db, changes.Name.GetValueOr(category.Name), category.Id, cancellationToken);
        }

        // Categoria inativa com prato ativo deixaria o prato sem aba no app.
        if (category.IsActive && !finalActive
            && await db.Set<Dish>().AnyAsync(d => d.DishCategoryId == category.Id && d.IsActive, cancellationToken))
        {
            throw BusinessException.Validation("isActive", "Mova ou inative os pratos desta categoria antes.");
        }

        var before = FixedMenuRules.AuditView(category);
        if (category.Apply(changes.Name, changes.SortOrder, changes.IsActive, time.GetUtcNow()))
        {
            audit.Add(FixedMenuRules.Updated, category.SyncEntityType, category.Id, before: before, after: FixedMenuRules.AuditView(category));
        }

        return FixedMenuMapping.ToDto(category);
    }
}

// ---------- Pratos ----------

internal sealed class CreateDishHandler(
    AppDbContext db, ITenantContext tenant, IIdGenerator ids, FixedMenuLoader loader, IAuditWriter audit, IObjectStorage storage, TimeProvider time)
    : ICommandHandler<CreateDishCommand, DishDto>
{
    public async Task<DishDto> HandleAsync(CreateDishCommand command, CancellationToken cancellationToken)
    {
        await FixedMenuRules.EnsureActiveCategoryAsync(db, command.DishCategoryId, "dishCategoryId", cancellationToken);
        await FixedMenuRules.EnsureDishNameAsync(db, command.Name, null, cancellationToken);

        var dish = Dish.Create(
            ids.New(), tenant.TenantId!.Value,
            new DishData(
                command.DishCategoryId, command.Name, command.Description, command.RequiresPreparation ?? true, command.HasSizes,
                command.Price, command.SellsOnSite, command.SitePrice, command.SortOrder, FixedMenuSpecs.Sizes(command.Sizes)),
            ids.New, time.GetUtcNow());
        db.Add(dish);

        audit.Add(FixedMenuRules.Created, dish.SyncEntityType, dish.Id, after: FixedMenuRules.AuditView(dish));
        return FixedMenuMapping.ToDto(dish, await loader.AddonGroupsAsync(cancellationToken), storage);
    }
}

internal sealed class UpdateDishHandler(
    AppDbContext db, ITenantContext tenant, IIdGenerator ids, IConcurrencyContext concurrency, FixedMenuLoader loader, IAuditWriter audit,
    IObjectStorage storage, TimeProvider time) : ICommandHandler<UpdateDishCommand, DishDto>
{
    public async Task<DishDto> HandleAsync(UpdateDishCommand command, CancellationToken cancellationToken)
    {
        var dish = await FixedMenuLoads.DishAsync(db, command.DishId, cancellationToken);
        dish.EnsureVersion(concurrency.ExpectedVersion);

        var changes = command.Changes;
        await FixedMenuRules.EnsureImageKeyAsync(
            storage, tenant.TenantId!.Value, Dish.EntityType, dish.Id, dish.ImageStorageKey, changes.ImageStorageKey, cancellationToken);
        if (changes.DishCategoryId is { HasValue: true, Value: var categoryId } && categoryId != dish.DishCategoryId)
        {
            await FixedMenuRules.EnsureActiveCategoryAsync(db, categoryId, "dishCategoryId", cancellationToken);
        }

        if (FixedMenuRules.NameNeedsCheck(changes.IsActive.GetValueOr(dish.IsActive), dish.IsActive, dish.Name, changes.Name.GetValueOr(dish.Name)))
        {
            await FixedMenuRules.EnsureDishNameAsync(db, changes.Name.GetValueOr(dish.Name), dish.Id, cancellationToken);
        }

        var before = FixedMenuRules.AuditView(dish);
        var (wasActive, hadSizes) = (dish.IsActive, dish.HasSizes);
        var activeSizes = dish.Sizes.Where(s => s.IsActive).Select(s => s.Id).ToList();

        var changed = dish.Apply(
            new DishChanges(
                changes.DishCategoryId, changes.Name, changes.Description, changes.RequiresPreparation, changes.HasSizes, changes.Price,
                changes.SellsOnSite, changes.SitePrice, changes.SortOrder, changes.IsActive, changes.ImageStorageKey,
                changes.Sizes.HasValue ? new Optional<IReadOnlyList<DishSizeSpec>>(FixedMenuSpecs.Sizes(changes.Sizes.Value)) : default),
            ids.New, time.GetUtcNow());

        if (changed)
        {
            await FixedMenuRules.EnsureNotUsedByActiveCombosAsync(
                db,
                wasActive && !dish.IsActive ? [dish.Id] : [],
                !hadSizes && dish.HasSizes ? [dish.Id] : [],
                activeSizes.Where(id => !dish.Sizes.Single(s => s.Id == id).IsActive).ToList(),
                [],
                cancellationToken);
            audit.Add(FixedMenuRules.Updated, dish.SyncEntityType, dish.Id, before: before, after: FixedMenuRules.AuditView(dish));
        }

        return FixedMenuMapping.ToDto(dish, await loader.AddonGroupsAsync(cancellationToken), storage);
    }
}

internal sealed class SetDishAvailabilityHandler(
    AppDbContext db, IConcurrencyContext concurrency, FixedMenuLoader loader, IAuditWriter audit, IObjectStorage storage, TimeProvider time)
    : ICommandHandler<SetDishAvailabilityCommand, DishDto>
{
    public async Task<DishDto> HandleAsync(SetDishAvailabilityCommand command, CancellationToken cancellationToken)
    {
        var dish = await FixedMenuLoads.DishAsync(db, command.DishId, cancellationToken);
        dish.EnsureVersion(concurrency.ExpectedVersion);

        var before = Pauses(dish);
        if (dish.SetAvailability(command.Request.IsPaused, FixedMenuSpecs.Pauses(command.Request.Sizes), time.GetUtcNow()))
        {
            audit.Add(FixedMenuRules.AvailabilityChanged, dish.SyncEntityType, dish.Id, before: before, after: Pauses(dish));
        }

        return FixedMenuMapping.ToDto(dish, await loader.AddonGroupsAsync(cancellationToken), storage);
    }

    private static object Pauses(Dish d) => new { d.Name, d.IsPaused, sizes = d.Sizes.Select(s => new { s.Id, s.Name, s.IsPaused }).ToList() };
}

// ---------- Bebidas ----------

internal sealed class CreateBeverageTypeHandler(AppDbContext db, ITenantContext tenant, IIdGenerator ids, IAuditWriter audit, TimeProvider time)
    : ICommandHandler<CreateBeverageTypeCommand, BeverageTypeDto>
{
    public async Task<BeverageTypeDto> HandleAsync(CreateBeverageTypeCommand command, CancellationToken cancellationToken)
    {
        await FixedMenuRules.EnsureBeverageTypeNameAsync(db, command.Name, null, cancellationToken);
        var type = BeverageType.Create(
            ids.New(), tenant.TenantId!.Value, command.Name, command.SortOrder, FixedMenuSpecs.BeverageSizes(command.Sizes), ids.New, time.GetUtcNow());
        db.Add(type);
        audit.Add(FixedMenuRules.Created, type.SyncEntityType, type.Id, after: FixedMenuRules.AuditView(type));
        return FixedMenuMapping.ToDto(type);
    }
}

internal sealed class UpdateBeverageTypeHandler(
    AppDbContext db, IIdGenerator ids, IConcurrencyContext concurrency, IAuditWriter audit, TimeProvider time)
    : ICommandHandler<UpdateBeverageTypeCommand, BeverageTypeDto>
{
    public async Task<BeverageTypeDto> HandleAsync(UpdateBeverageTypeCommand command, CancellationToken cancellationToken)
    {
        var type = await FixedMenuLoads.BeverageTypeAsync(db, command.BeverageTypeId, cancellationToken);
        type.EnsureVersion(concurrency.ExpectedVersion);

        var changes = command.Changes;
        if (FixedMenuRules.NameNeedsCheck(changes.IsActive.GetValueOr(type.IsActive), type.IsActive, type.Name, changes.Name.GetValueOr(type.Name)))
        {
            await FixedMenuRules.EnsureBeverageTypeNameAsync(db, changes.Name.GetValueOr(type.Name), type.Id, cancellationToken);
        }

        var before = FixedMenuRules.AuditView(type);
        var activeSizes = type.IsActive ? type.Sizes.Where(s => s.IsActive).Select(s => s.Id).ToList() : [];
        var changed = type.Apply(
            changes.Name, changes.SortOrder, changes.IsActive,
            changes.Sizes.HasValue ? new Optional<IReadOnlyList<BeverageSizeSpec>>(FixedMenuSpecs.BeverageSizes(changes.Sizes.Value)) : default,
            ids.New, time.GetUtcNow());

        if (changed)
        {
            // Tipo inativo tira todos os tamanhos dele de venda.
            var inactivated = activeSizes.Where(id => !type.IsActive || !type.Sizes.Single(s => s.Id == id).IsActive).ToList();
            await FixedMenuRules.EnsureNotUsedByActiveCombosAsync(db, [], [], [], inactivated, cancellationToken);
            audit.Add(FixedMenuRules.Updated, type.SyncEntityType, type.Id, before: before, after: FixedMenuRules.AuditView(type));
        }

        return FixedMenuMapping.ToDto(type);
    }
}

internal sealed class SetBeverageAvailabilityHandler(AppDbContext db, IConcurrencyContext concurrency, IAuditWriter audit, TimeProvider time)
    : ICommandHandler<SetBeverageAvailabilityCommand, BeverageTypeDto>
{
    public async Task<BeverageTypeDto> HandleAsync(SetBeverageAvailabilityCommand command, CancellationToken cancellationToken)
    {
        var type = await FixedMenuLoads.BeverageTypeAsync(db, command.BeverageTypeId, cancellationToken);
        type.EnsureVersion(concurrency.ExpectedVersion);

        var before = Pauses(type);
        if (type.SetAvailability(FixedMenuSpecs.Pauses(command.Request.Sizes), FixedMenuSpecs.Pauses(command.Request.Flavors), time.GetUtcNow()))
        {
            audit.Add(FixedMenuRules.AvailabilityChanged, type.SyncEntityType, type.Id, before: before, after: Pauses(type));
        }

        return FixedMenuMapping.ToDto(type);
    }

    private static object Pauses(BeverageType t) => new
    {
        t.Name,
        sizes = t.Sizes.Select(s => new { s.Id, s.Name, s.IsPaused, flavors = s.Flavors.Select(f => new { f.Id, f.Name, f.IsPaused }).ToList() }).ToList(),
    };
}

// ---------- Combos ----------

internal sealed class CreateComboHandler(
    AppDbContext db, ITenantContext tenant, IIdGenerator ids, FixedMenuLoader loader, IAuditWriter audit, IObjectStorage storage, TimeProvider time)
    : ICommandHandler<CreateComboCommand, ComboDto>
{
    public async Task<ComboDto> HandleAsync(CreateComboCommand command, CancellationToken cancellationToken)
    {
        await FixedMenuRules.EnsureComboNameAsync(db, command.Name, null, cancellationToken);
        var dishes = await loader.DishesAsync(cancellationToken);
        var beverages = await loader.BeverageTypesAsync(cancellationToken);

        var combo = Combo.Create(
            ids.New(), tenant.TenantId!.Value,
            new ComboData(
                command.Name, command.Description, command.Price, command.SellsOnSite, command.SitePrice, command.SortOrder,
                FixedMenuRules.ResolveComponents(command.Components ?? [], dishes, beverages)),
            ids.New, time.GetUtcNow());
        db.Add(combo);

        audit.Add(FixedMenuRules.Created, combo.SyncEntityType, combo.Id, after: FixedMenuRules.AuditView(combo));
        return FixedMenuMapping.ToDto(combo, new FixedMenuAvailability(dishes, beverages), storage);
    }
}

internal sealed class UpdateComboHandler(
    AppDbContext db, ITenantContext tenant, IIdGenerator ids, IConcurrencyContext concurrency, FixedMenuLoader loader, IAuditWriter audit,
    IObjectStorage storage, TimeProvider time) : ICommandHandler<UpdateComboCommand, ComboDto>
{
    public async Task<ComboDto> HandleAsync(UpdateComboCommand command, CancellationToken cancellationToken)
    {
        var combo = await FixedMenuLoads.ComboAsync(db, command.ComboId, cancellationToken);
        combo.EnsureVersion(concurrency.ExpectedVersion);

        var changes = command.Changes;
        await FixedMenuRules.EnsureImageKeyAsync(
            storage, tenant.TenantId!.Value, Combo.EntityType, combo.Id, combo.ImageStorageKey, changes.ImageStorageKey, cancellationToken);
        if (FixedMenuRules.NameNeedsCheck(changes.IsActive.GetValueOr(combo.IsActive), combo.IsActive, combo.Name, changes.Name.GetValueOr(combo.Name)))
        {
            await FixedMenuRules.EnsureComboNameAsync(db, changes.Name.GetValueOr(combo.Name), combo.Id, cancellationToken);
        }

        var dishes = await loader.DishesAsync(cancellationToken);
        var beverages = await loader.BeverageTypesAsync(cancellationToken);
        var components = changes.Components.HasValue
            ? new Optional<IReadOnlyList<ComboComponentSpec>>(FixedMenuRules.ResolveComponents(changes.Components.Value, dishes, beverages))
            : default;

        var before = FixedMenuRules.AuditView(combo);
        if (combo.Apply(
                new ComboChanges(
                    changes.Name, changes.Description, changes.Price, changes.SellsOnSite, changes.SitePrice, changes.SortOrder, changes.IsActive,
                    changes.ImageStorageKey, components),
                ids.New, time.GetUtcNow()))
        {
            audit.Add(FixedMenuRules.Updated, combo.SyncEntityType, combo.Id, before: before, after: FixedMenuRules.AuditView(combo));
        }

        return FixedMenuMapping.ToDto(combo, new FixedMenuAvailability(dishes, beverages), storage);
    }
}

internal sealed class SetComboAvailabilityHandler(
    AppDbContext db, IConcurrencyContext concurrency, FixedMenuLoader loader, IAuditWriter audit, IObjectStorage storage, TimeProvider time)
    : ICommandHandler<SetComboAvailabilityCommand, ComboDto>
{
    public async Task<ComboDto> HandleAsync(SetComboAvailabilityCommand command, CancellationToken cancellationToken)
    {
        var combo = await FixedMenuLoads.ComboAsync(db, command.ComboId, cancellationToken);
        combo.EnsureVersion(concurrency.ExpectedVersion);

        if (combo.SetPaused(command.Request.IsPaused, time.GetUtcNow()))
        {
            audit.Add(FixedMenuRules.AvailabilityChanged, combo.SyncEntityType, combo.Id,
                before: new { combo.Name, isPaused = !combo.IsPaused }, after: new { combo.Name, combo.IsPaused });
        }

        var availability = new FixedMenuAvailability(await loader.DishesAsync(cancellationToken), await loader.BeverageTypesAsync(cancellationToken));
        return FixedMenuMapping.ToDto(combo, availability, storage);
    }
}

// ---------- Grupos de adicionais ----------

internal sealed class CreateAddonGroupHandler(AppDbContext db, ITenantContext tenant, IIdGenerator ids, IAuditWriter audit, TimeProvider time)
    : ICommandHandler<CreateAddonGroupCommand, AddonGroupDto>
{
    public async Task<AddonGroupDto> HandleAsync(CreateAddonGroupCommand command, CancellationToken cancellationToken)
    {
        await FixedMenuRules.EnsureAddonGroupNameAsync(db, command.Name, null, cancellationToken);
        await FixedMenuRules.EnsureLinkTargetsAsync(db, command.DishCategoryIds, command.DishIds, cancellationToken);

        var group = AddonGroup.Create(
            ids.New(), tenant.TenantId!.Value, command.Name, command.SortOrder, FixedMenuSpecs.Options(command.Options),
            command.DishCategoryIds ?? [], command.DishIds ?? [], ids.New, time.GetUtcNow());
        db.Add(group);

        audit.Add(FixedMenuRules.Created, group.SyncEntityType, group.Id, after: FixedMenuRules.AuditView(group));
        return FixedMenuMapping.ToDto(group);
    }
}

internal sealed class UpdateAddonGroupHandler(AppDbContext db, IIdGenerator ids, IConcurrencyContext concurrency, IAuditWriter audit, TimeProvider time)
    : ICommandHandler<UpdateAddonGroupCommand, AddonGroupDto>
{
    public async Task<AddonGroupDto> HandleAsync(UpdateAddonGroupCommand command, CancellationToken cancellationToken)
    {
        var group = await FixedMenuLoads.AddonGroupAsync(db, command.AddonGroupId, cancellationToken);
        group.EnsureVersion(concurrency.ExpectedVersion);

        var changes = command.Changes;
        if (FixedMenuRules.NameNeedsCheck(changes.IsActive.GetValueOr(group.IsActive), group.IsActive, group.Name, changes.Name.GetValueOr(group.Name)))
        {
            await FixedMenuRules.EnsureAddonGroupNameAsync(db, changes.Name.GetValueOr(group.Name), group.Id, cancellationToken);
        }

        await FixedMenuRules.EnsureLinkTargetsAsync(
            db, changes.DishCategoryIds.HasValue ? changes.DishCategoryIds.Value : null, changes.DishIds.HasValue ? changes.DishIds.Value : null,
            cancellationToken);

        var before = FixedMenuRules.AuditView(group);
        if (group.Apply(
                changes.Name, changes.SortOrder, changes.IsActive,
                changes.Options.HasValue ? new Optional<IReadOnlyList<AddonOptionSpec>>(FixedMenuSpecs.Options(changes.Options.Value)) : default,
                changes.DishCategoryIds, changes.DishIds, ids.New, time.GetUtcNow()))
        {
            audit.Add(FixedMenuRules.Updated, group.SyncEntityType, group.Id, before: before, after: FixedMenuRules.AuditView(group));
        }

        return FixedMenuMapping.ToDto(group);
    }
}

internal sealed class SetAddonAvailabilityHandler(AppDbContext db, IConcurrencyContext concurrency, IAuditWriter audit, TimeProvider time)
    : ICommandHandler<SetAddonAvailabilityCommand, AddonGroupDto>
{
    public async Task<AddonGroupDto> HandleAsync(SetAddonAvailabilityCommand command, CancellationToken cancellationToken)
    {
        var group = await FixedMenuLoads.AddonGroupAsync(db, command.AddonGroupId, cancellationToken);
        group.EnsureVersion(concurrency.ExpectedVersion);

        var before = Pauses(group);
        if (group.SetAvailability(FixedMenuSpecs.Pauses(command.Request.Options), time.GetUtcNow()))
        {
            audit.Add(FixedMenuRules.AvailabilityChanged, group.SyncEntityType, group.Id, before: before, after: Pauses(group));
        }

        return FixedMenuMapping.ToDto(group);
    }

    private static object Pauses(AddonGroup g) => new { g.Name, options = g.Options.Select(o => new { o.Id, o.Name, o.IsPaused }).ToList() };
}

// ---------- Imagem de prato e combo (como a do produto, 06 §6.5) ----------

internal sealed class RequestFixedMenuImageUploadHandler(AppDbContext db, ITenantContext tenant, IObjectStorage storage, TimeProvider time)
    : ICommandHandler<RequestFixedMenuImageUploadCommand, ImageUploadDto>
{
    public async Task<ImageUploadDto> HandleAsync(RequestFixedMenuImageUploadCommand command, CancellationToken cancellationToken)
    {
        var exists = command.EntityType == Combo.EntityType
            ? await db.Set<Combo>().AnyAsync(c => c.Id == command.EntityId, cancellationToken)
            : await db.Set<Dish>().AnyAsync(d => d.Id == command.EntityId, cancellationToken);
        if (!exists)
        {
            throw new NotFoundException(command.EntityType, command.EntityId);
        }

        var request = command.Request;
        ProductImages.EnsureAllowed(request.ContentType, request.SizeBytes, "contentType", "sizeBytes");
        var suffix = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3));
        var key = FixedMenuRules.ImagePrefix(tenant.TenantId!.Value, command.EntityType, command.EntityId)
            + $"{time.GetUtcNow():yyyyMMddHHmmss}-{suffix}.{ProductImages.Extensions[request.ContentType]}";
        var upload = storage.CreateUploadUrl(key, request.ContentType);
        return new ImageUploadDto(upload.Url, upload.Method, upload.Headers, key, upload.ExpiresAt);
    }
}

// ---------- Leitura ----------

internal sealed class GetFixedMenuQueryHandler(FixedMenuLoader loader, IObjectStorage storage) : IQueryHandler<GetFixedMenuQuery, FixedMenuDto>
{
    public async Task<FixedMenuDto> HandleAsync(GetFixedMenuQuery query, CancellationToken cancellationToken) =>
        FixedMenuMapping.ToDto(await loader.LoadAsync(cancellationToken), storage, query.IncludeInactive);
}

internal sealed class GetDishCategoryQueryHandler(FixedMenuLoader loader) : IQueryHandler<GetDishCategoryQuery, DishCategoryDto>
{
    public async Task<DishCategoryDto> HandleAsync(GetDishCategoryQuery query, CancellationToken cancellationToken) =>
        FixedMenuMapping.ToDto((await loader.LoadAsync(cancellationToken)).Categories.SingleOrDefault(c => c.Id == query.Id)
            ?? throw new NotFoundException(DishCategory.EntityType, query.Id));
}

internal sealed class GetDishQueryHandler(FixedMenuLoader loader, IObjectStorage storage) : IQueryHandler<GetDishQuery, DishDto>
{
    public async Task<DishDto> HandleAsync(GetDishQuery query, CancellationToken cancellationToken)
    {
        var data = await loader.LoadAsync(cancellationToken);
        var dish = data.Dishes.SingleOrDefault(d => d.Id == query.Id) ?? throw new NotFoundException(Dish.EntityType, query.Id);
        return FixedMenuMapping.ToDto(dish, data.AddonGroups, storage);
    }
}

internal sealed class GetBeverageTypeQueryHandler(FixedMenuLoader loader) : IQueryHandler<GetBeverageTypeQuery, BeverageTypeDto>
{
    public async Task<BeverageTypeDto> HandleAsync(GetBeverageTypeQuery query, CancellationToken cancellationToken) =>
        FixedMenuMapping.ToDto((await loader.LoadAsync(cancellationToken)).BeverageTypes.SingleOrDefault(t => t.Id == query.Id)
            ?? throw new NotFoundException(BeverageType.EntityType, query.Id));
}

internal sealed class GetComboQueryHandler(FixedMenuLoader loader, IObjectStorage storage) : IQueryHandler<GetComboQuery, ComboDto>
{
    public async Task<ComboDto> HandleAsync(GetComboQuery query, CancellationToken cancellationToken)
    {
        var data = await loader.LoadAsync(cancellationToken);
        var combo = data.Combos.SingleOrDefault(c => c.Id == query.Id) ?? throw new NotFoundException(Combo.EntityType, query.Id);
        return FixedMenuMapping.ToDto(combo, data.Availability, storage);
    }
}

internal sealed class GetAddonGroupQueryHandler(FixedMenuLoader loader) : IQueryHandler<GetAddonGroupQuery, AddonGroupDto>
{
    public async Task<AddonGroupDto> HandleAsync(GetAddonGroupQuery query, CancellationToken cancellationToken) =>
        FixedMenuMapping.ToDto((await loader.LoadAsync(cancellationToken)).AddonGroups.SingleOrDefault(g => g.Id == query.Id)
            ?? throw new NotFoundException(AddonGroup.EntityType, query.Id));
}

/// `current` do 409 das cinco raízes (06 §6.1): o mesmo DTO do GET.
internal sealed class DishCategoryCurrentReader(IQueryHandler<GetDishCategoryQuery, DishCategoryDto> query) : ICurrentResourceReader
{
    public string EntityType => DishCategory.EntityType;

    public async Task<IVersionedDto?> ReadAsync(Guid entityId, CancellationToken cancellationToken) =>
        await query.HandleAsync(new GetDishCategoryQuery(entityId), cancellationToken);
}

internal sealed class DishCurrentReader(IQueryHandler<GetDishQuery, DishDto> query) : ICurrentResourceReader
{
    public string EntityType => Dish.EntityType;

    public async Task<IVersionedDto?> ReadAsync(Guid entityId, CancellationToken cancellationToken) =>
        await query.HandleAsync(new GetDishQuery(entityId), cancellationToken);
}

internal sealed class BeverageTypeCurrentReader(IQueryHandler<GetBeverageTypeQuery, BeverageTypeDto> query) : ICurrentResourceReader
{
    public string EntityType => BeverageType.EntityType;

    public async Task<IVersionedDto?> ReadAsync(Guid entityId, CancellationToken cancellationToken) =>
        await query.HandleAsync(new GetBeverageTypeQuery(entityId), cancellationToken);
}

internal sealed class ComboCurrentReader(IQueryHandler<GetComboQuery, ComboDto> query) : ICurrentResourceReader
{
    public string EntityType => Combo.EntityType;

    public async Task<IVersionedDto?> ReadAsync(Guid entityId, CancellationToken cancellationToken) =>
        await query.HandleAsync(new GetComboQuery(entityId), cancellationToken);
}

internal sealed class AddonGroupCurrentReader(IQueryHandler<GetAddonGroupQuery, AddonGroupDto> query) : ICurrentResourceReader
{
    public string EntityType => AddonGroup.EntityType;

    public async Task<IVersionedDto?> ReadAsync(Guid entityId, CancellationToken cancellationToken) =>
        await query.HandleAsync(new GetAddonGroupQuery(entityId), cancellationToken);
}

/// Porta `IFixedMenuReader` (Contracts): o mesmo carregamento e a mesma disponibilidade do `GET /fixed-menu`.
internal sealed class FixedMenuReader(FixedMenuLoader loader, IObjectStorage storage) : IFixedMenuReader
{
    public async Task<FixedMenuSnapshot> LoadAsync(CancellationToken cancellationToken) =>
        FixedMenuMapping.ToSnapshot(await loader.LoadAsync(cancellationToken));

    public async Task<FixedMenuDto> LoadMenuAsync(CancellationToken cancellationToken) =>
        FixedMenuMapping.ToDto(await loader.LoadAsync(cancellationToken), storage, includeInactive: false);
}

/// Agregados rastreados para escrita, com os filhos.
internal static class FixedMenuLoads
{
    public static async Task<Dish> DishAsync(AppDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<Dish>().Include(d => d.Sizes).SingleOrDefaultAsync(d => d.Id == id, ct)
        ?? throw new NotFoundException(Dish.EntityType, id);

    public static async Task<BeverageType> BeverageTypeAsync(AppDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<BeverageType>().Include(t => t.Sizes).ThenInclude(s => s.Flavors).AsSplitQuery().SingleOrDefaultAsync(t => t.Id == id, ct)
        ?? throw new NotFoundException(BeverageType.EntityType, id);

    public static async Task<Combo> ComboAsync(AppDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<Combo>().Include(c => c.Components).SingleOrDefaultAsync(c => c.Id == id, ct)
        ?? throw new NotFoundException(Combo.EntityType, id);

    public static async Task<AddonGroup> AddonGroupAsync(AppDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<AddonGroup>().Include(g => g.Options).Include(g => g.Links).AsSplitQuery().SingleOrDefaultAsync(g => g.Id == id, ct)
        ?? throw new NotFoundException(AddonGroup.EntityType, id);
}

/// Corpo da API → especificações do domínio (o formato já passou pelos validators).
internal static class FixedMenuSpecs
{
    public static IReadOnlyList<DishSizeSpec> Sizes(IReadOnlyList<DishSizeInput>? inputs) =>
        (inputs ?? []).Select(s => new DishSizeSpec(s.Id, s.Name, s.Price, s.SellsOnSite, s.SitePrice, s.SortOrder)).ToList();

    public static IReadOnlyList<BeverageSizeSpec> BeverageSizes(IReadOnlyList<BeverageSizeInput>? inputs) =>
        (inputs ?? []).Select(s => new BeverageSizeSpec(
                s.Id, s.Name, s.Price, s.SellsOnSite, s.SitePrice, s.SortOrder,
                (s.Flavors ?? []).Select(f => new BeverageFlavorSpec(f.Id, f.Name, f.ExtraPrice, f.SortOrder)).ToList()))
            .ToList();

    public static IReadOnlyList<AddonOptionSpec> Options(IReadOnlyList<AddonOptionInput>? inputs) =>
        (inputs ?? []).Select(o => new AddonOptionSpec(o.Id, o.Name, o.Price, o.SortOrder)).ToList();

    public static IReadOnlyList<(Guid Id, bool IsPaused)> Pauses(IReadOnlyList<PauseInput>? inputs) =>
        (inputs ?? []).Select(p => (p.Id, p.IsPaused)).ToList();
}
