using AlmoSeis.Infrastructure.Audit;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Closing.Contracts;
using AlmoSeis.Modules.Closing.Domain;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Menu.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Modules.Closing.Application;

/// POST /business-days [I] (06 §6.6): um expediente aberto por vez (ADR-0019), confirmação para reabrir
/// na mesma data (ADR-0039) e materialização do cardápio semanal na mesma transação (ADR-0038).
internal sealed class OpenBusinessDayHandler(
    AppDbContext db,
    ITenantContext tenant,
    IIdGenerator ids,
    ITenantCalendar calendar,
    IMenuMaterializer materializer,
    IAuditWriter audit,
    BusinessDayDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<OpenBusinessDayCommand, OpenBusinessDayResultDto>
{
    public async Task<OpenBusinessDayResultDto> HandleAsync(OpenBusinessDayCommand command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId!.Value;

        // Duas aberturas simultâneas: a segunda espera a primeira terminar e responde 409 com o expediente dela,
        // em vez de estourar ux_business_days_open no commit.
        var lockKey = $"business-day-open:{tenantId}";
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);

        var open = await db.Set<BusinessDay>().AsNoTracking().SingleOrDefaultAsync(d => d.Status == BusinessDayStatus.Open, cancellationToken);
        if (open is not null)
        {
            throw new BusinessException(
                ErrorCodes.BusinessDayAlreadyOpen,
                extensions: new Dictionary<string, object?> { ["current"] = await dtos.BuildAsync(open, cancellationToken) });
        }

        var today = await calendar.TodayAsync(cancellationToken);
        if (!command.ConfirmSameDate
            && await db.Set<BusinessDay>().AnyAsync(d => d.BusinessDate == today && d.Status == BusinessDayStatus.Closed, cancellationToken))
        {
            throw new BusinessException(ErrorCodes.BusinessDayClosedToday);
        }

        var day = BusinessDay.Open(ids.New(), tenantId, today, command.OpeningBalance, tenant.EmployeeId!.Value, time.GetUtcNow());
        db.Add(day);
        audit.Add(
            "BusinessDayOpened", day.SyncEntityType, day.Id,
            after: new { day.BusinessDate, openingBalance = day.OpeningBalance.ToString(), command.ConfirmSameDate });

        var menus = await materializer.MaterializeForAsync(today, cancellationToken);
        return new OpenBusinessDayResultDto(await dtos.BuildAsync(day, cancellationToken), menus);
    }
}

internal sealed class GetCurrentBusinessDayQueryHandler(AppDbContext db, BusinessDayDtoBuilder dtos)
    : IQueryHandler<GetCurrentBusinessDayQuery, BusinessDayDto>
{
    public async Task<BusinessDayDto> HandleAsync(GetCurrentBusinessDayQuery query, CancellationToken cancellationToken)
    {
        var open = await db.Set<BusinessDay>().AsNoTracking().SingleOrDefaultAsync(d => d.Status == BusinessDayStatus.Open, cancellationToken)
            ?? throw new BusinessException(ErrorCodes.NoOpenBusinessDay);
        return await dtos.BuildAsync(open, cancellationToken);
    }
}

internal sealed class GetBusinessDayQueryHandler(AppDbContext db, BusinessDayDtoBuilder dtos)
    : IQueryHandler<GetBusinessDayQuery, BusinessDayDto>
{
    public async Task<BusinessDayDto> HandleAsync(GetBusinessDayQuery query, CancellationToken cancellationToken)
    {
        var day = await db.Set<BusinessDay>().AsNoTracking().SingleOrDefaultAsync(d => d.Id == query.BusinessDayId, cancellationToken)
            ?? throw new NotFoundException("BUSINESS_DAY", query.BusinessDayId);
        return await dtos.BuildAsync(day, cancellationToken);
    }
}

/// GET /business-days: do mais recente para o mais antigo. O id é UUID v7 (ordem de abertura), então o
/// cursor é o último id da página.
internal sealed class ListBusinessDaysQueryHandler(AppDbContext db, BusinessDayDtoBuilder dtos)
    : IQueryHandler<ListBusinessDaysQuery, PageDto<BusinessDayDto>>
{
    public const int MaxLimit = 100;

    public async Task<PageDto<BusinessDayDto>> HandleAsync(ListBusinessDaysQuery query, CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > MaxLimit)
        {
            throw BusinessException.Validation("limit", $"Use um limite entre 1 e {MaxLimit}.");
        }

        var days = db.Set<BusinessDay>().AsNoTracking();
        if (query.From is { } from)
        {
            days = days.Where(d => d.BusinessDate >= from);
        }

        if (query.To is { } to)
        {
            days = days.Where(d => d.BusinessDate <= to);
        }

        if (query.Cursor is { } cursor)
        {
            var after = ClosingPaging.DecodeCursor(cursor);
            days = days.Where(d => d.Id.CompareTo(after) < 0);
        }

        var page = await days.OrderByDescending(d => d.Id).Take(query.Limit + 1).ToListAsync(cancellationToken);
        var hasMore = page.Count > query.Limit;
        var items = await dtos.BuildManyAsync(page.Take(query.Limit).ToList(), cancellationToken);
        return new PageDto<BusinessDayDto>(items, hasMore ? ClosingPaging.EncodeCursor(items[^1].Id) : null);
    }
}

/// `current` do 409 nas rotas [M] do expediente (finalização do fechamento).
internal sealed class BusinessDayCurrentReader(IQueryHandler<GetBusinessDayQuery, BusinessDayDto> query) : ICurrentResourceReader
{
    public string EntityType => "BUSINESS_DAY";

    public async Task<IVersionedDto?> ReadAsync(Guid entityId, CancellationToken cancellationToken) =>
        await query.HandleAsync(new GetBusinessDayQuery(entityId), cancellationToken);
}

/// Monta o `BusinessDay` de 06 §6.6 com os autores do Identity.
internal sealed class BusinessDayDtoBuilder(AppDbContext db, IEmployeeDirectory employees)
{
    public async Task<BusinessDayDto> BuildAsync(BusinessDay day, CancellationToken cancellationToken) =>
        (await BuildManyAsync([day], cancellationToken))[0];

    public async Task<IReadOnlyList<BusinessDayDto>> BuildManyAsync(IReadOnlyList<BusinessDay> days, CancellationToken cancellationToken)
    {
        var authorIds = days.Select(d => d.OpenedByEmployeeId)
            .Concat(days.Where(d => d.ClosedByEmployeeId is not null).Select(d => d.ClosedByEmployeeId!.Value));
        var names = await employees.GetDisplayNamesAsync(authorIds, cancellationToken);
        var dayIds = days.Select(d => d.Id).ToArray();
        var closings = await db.Set<DailyClosing>().AsNoTracking()
            .Where(c => dayIds.Contains(c.BusinessDayId))
            .ToDictionaryAsync(c => c.BusinessDayId, c => c.Id, cancellationToken);

        return days
            .Select(d => new BusinessDayDto(
                d.Id, d.Version, d.BusinessDate, d.Status, d.OpeningBalance, d.OpenedAt,
                new AuthorDto(d.OpenedByEmployeeId, names.GetValueOrDefault(d.OpenedByEmployeeId, string.Empty)),
                d.ClosedAt,
                d.ClosedByEmployeeId is { } closedBy ? new AuthorDto(closedBy, names.GetValueOrDefault(closedBy, string.Empty)) : null,
                closings.TryGetValue(d.Id, out var closingId) ? closingId : null))
            .ToList();
    }
}
