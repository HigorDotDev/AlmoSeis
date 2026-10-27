using AlmoSeis.Infrastructure.Audit;
using AlmoSeis.Infrastructure.Concurrency;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Catalog.Contracts;
using AlmoSeis.Modules.Catalog.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Modules.Catalog.Application;

internal sealed class CreateMarmitaSizeHandler(
    AppDbContext db,
    ITenantContext tenant,
    IIdGenerator ids,
    IAuditWriter audit,
    TimeProvider time) : ICommandHandler<CreateMarmitaSizeCommand, MarmitaSizeDto>
{
    public async Task<MarmitaSizeDto> HandleAsync(CreateMarmitaSizeCommand command, CancellationToken cancellationToken)
    {
        await MarmitaSizeRules.EnsureNameAvailableAsync(db, command.Name, exceptId: null, cancellationToken);

        var size = MarmitaSize.Create(ids.New(), tenant.TenantId!.Value, command.Name, command.Price, command.SortOrder, time.GetUtcNow());
        db.Add(size);

        audit.Add("MarmitaSizeCreated", size.SyncEntityType, size.Id, after: MarmitaSizeRules.AuditView(size));
        return MarmitaSizeRules.ToDto(size);
    }
}

internal sealed class UpdateMarmitaSizeHandler(
    AppDbContext db,
    IConcurrencyContext concurrency,
    IAuditWriter audit,
    TimeProvider time) : ICommandHandler<UpdateMarmitaSizeCommand, MarmitaSizeDto>
{
    public async Task<MarmitaSizeDto> HandleAsync(UpdateMarmitaSizeCommand command, CancellationToken cancellationToken)
    {
        var size = await db.Set<MarmitaSize>().SingleOrDefaultAsync(s => s.Id == command.MarmitaSizeId, cancellationToken)
            ?? throw new NotFoundException("MARMITA_SIZE", command.MarmitaSizeId);
        size.EnsureVersion(concurrency.ExpectedVersion);

        var changes = command.Changes;
        var finalName = changes.Name.GetValueOr(size.Name);
        if (changes.IsActive.GetValueOr(size.IsActive)
            && (!string.Equals(finalName.Trim(), size.Name, StringComparison.OrdinalIgnoreCase) || !size.IsActive))
        {
            await MarmitaSizeRules.EnsureNameAvailableAsync(db, finalName, size.Id, cancellationToken);
        }

        var before = MarmitaSizeRules.AuditView(size);
        size.Apply(changes.Name, changes.Price, changes.SortOrder, changes.IsActive, time.GetUtcNow());

        if (db.Entry(size).State == EntityState.Modified)
        {
            audit.Add("MarmitaSizeUpdated", size.SyncEntityType, size.Id, before: before, after: MarmitaSizeRules.AuditView(size));
        }

        return MarmitaSizeRules.ToDto(size);
    }
}

internal sealed class ListMarmitaSizesQueryHandler(AppDbContext db) : IQueryHandler<ListMarmitaSizesQuery, ItemsDto<MarmitaSizeDto>>
{
    public async Task<ItemsDto<MarmitaSizeDto>> HandleAsync(ListMarmitaSizesQuery query, CancellationToken cancellationToken)
    {
        var sizes = db.Set<MarmitaSize>().AsNoTracking();
        if (!query.IncludeInactive)
        {
            sizes = sizes.Where(s => s.IsActive);
        }

        var rows = await sizes.OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync(cancellationToken);
        return new ItemsDto<MarmitaSizeDto>(rows.Select(MarmitaSizeRules.ToDto).ToList());
    }
}

/// `current` do 409 de PATCH /marmita-sizes/{id}.
internal sealed class MarmitaSizeCurrentReader(AppDbContext db) : ICurrentResourceReader
{
    public string EntityType => "MARMITA_SIZE";

    public async Task<IVersionedDto?> ReadAsync(Guid entityId, CancellationToken cancellationToken)
    {
        var size = await db.Set<MarmitaSize>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == entityId, cancellationToken);
        return size is null ? null : MarmitaSizeRules.ToDto(size);
    }
}

internal static class MarmitaSizeRules
{
    /// ux_marmita_sizes_active_name (05 §5.5).
    public static async Task EnsureNameAvailableAsync(AppDbContext db, string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var normalized = name.Trim().ToLowerInvariant();
        if (await db.Set<MarmitaSize>().AnyAsync(s => s.IsActive && s.Name.ToLower() == normalized && s.Id != exceptId, cancellationToken))
        {
            throw BusinessException.Validation("name", "Já existe um tamanho ativo com este nome.");
        }
    }

    public static MarmitaSizeDto ToDto(MarmitaSize s) => new(s.Id, s.Version, s.Name, s.Price, s.SortOrder, s.IsActive);

    public static object AuditView(MarmitaSize s) => new { s.Name, price = s.Price.ToString(), s.SortOrder, s.IsActive };
}
