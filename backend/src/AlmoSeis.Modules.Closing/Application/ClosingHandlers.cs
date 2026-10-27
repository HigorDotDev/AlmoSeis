using System.Buffers.Text;
using System.Globalization;
using AlmoSeis.Infrastructure.Audit;
using AlmoSeis.Infrastructure.Concurrency;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Closing.Contracts;
using AlmoSeis.Modules.Closing.Domain;
using AlmoSeis.Modules.Closing.Infrastructure;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Modules.Closing.Application;

/// POST /closing/cash-movements [I]: suprimento ou sangria no expediente aberto.
internal sealed class RegisterCashMovementHandler(
    AppDbContext db,
    ITenantContext tenant,
    IIdGenerator ids,
    IAuditWriter audit,
    ClosingDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<RegisterCashMovementCommand, CashMovementDto>
{
    public async Task<CashMovementDto> HandleAsync(RegisterCashMovementCommand command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId!.Value;

        // Sobe a versão do expediente de forma atômica: quem confirmar o fechamento com o resumo de antes recebe 409
        // (06 §6.11), mas duas movimentações simultâneas não conflitam entre si.
        var dayIds = await db.Database.SqlQuery<Guid>(
            $"""
            UPDATE business_days SET version = version + 1
             WHERE tenant_id = {tenantId} AND status = 'OPEN'
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);
        if (dayIds is not [var dayId])
        {
            throw new BusinessException(ErrorCodes.BusinessDayRequired);
        }

        var movement = CashMovement.Register(
            ids.New(), tenantId, dayId, command.MovementType, command.Amount, command.Description, tenant.EmployeeId!.Value, time.GetUtcNow());
        db.Add(movement);
        audit.Add("CashMovementRegistered", movement.SyncEntityType, movement.Id, after: new
        {
            movementType = UpperSnakeEnumConverter<CashMovementType>.ToText(movement.MovementType),
            amount = movement.Amount.ToString(),
            movement.Description,
        });
        return (await dtos.MovementsAsync([movement], cancellationToken))[0];
    }
}

internal sealed class ListCashMovementsQueryHandler(AppDbContext db, ClosingDtoBuilder dtos)
    : IQueryHandler<ListCashMovementsQuery, ItemsDto<CashMovementDto>>
{
    public async Task<ItemsDto<CashMovementDto>> HandleAsync(ListCashMovementsQuery query, CancellationToken cancellationToken)
    {
        var dayId = query.BusinessDayId ?? await ClosingRules.OpenDayIdAsync(db, cancellationToken);
        if (dayId is null)
        {
            return new ItemsDto<CashMovementDto>([]);
        }

        var movements = await db.Set<CashMovement>().AsNoTracking()
            .Where(m => m.BusinessDayId == dayId)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .ToListAsync(cancellationToken);
        return new ItemsDto<CashMovementDto>(await dtos.MovementsAsync(movements, cancellationToken));
    }
}

/// GET /closing/summary: expediente aberto → cálculo ao vivo; fechado → snapshot (RN-010.1).
internal sealed class GetClosingSummaryQueryHandler(AppDbContext db, ClosingInputsQuery inputs, ClosingDtoBuilder dtos, TimeProvider time)
    : IQueryHandler<GetClosingSummaryQuery, ClosingSummaryDto>
{
    public async Task<ClosingSummaryDto> HandleAsync(GetClosingSummaryQuery query, CancellationToken cancellationToken)
    {
        var dayId = query.BusinessDayId ?? await ClosingRules.OpenDayIdAsync(db, cancellationToken)
            ?? throw new BusinessException(ErrorCodes.NoOpenBusinessDay);
        var day = await db.Set<BusinessDay>().AsNoTracking().SingleOrDefaultAsync(d => d.Id == dayId, cancellationToken)
            ?? throw new NotFoundException("BUSINESS_DAY", dayId);

        if (day.Status == BusinessDayStatus.Closed)
        {
            var closing = await ClosingRules.LoadClosingAsync(db, c => c.BusinessDayId == day.Id, cancellationToken);
            return await dtos.FinalAsync(closing, day, cancellationToken);
        }

        var figures = ClosingCalculator.Compute(await inputs.LoadAsync(day.Id, day.OpeningBalance, cancellationToken));
        var adjustments = await db.Set<ClosingAdjustment>().AsNoTracking().Where(a => a.BusinessDayId == day.Id).ToListAsync(cancellationToken);
        return await dtos.LiveAsync(day, figures, adjustments, time.GetUtcNow(), cancellationToken);
    }
}

/// POST /closing/finalize [I][M] (07 §7.9): FOR UPDATE no expediente faz pagamentos e movimentações em curso
/// terminarem antes do cálculo, e as próximas esperarem o fechamento.
internal sealed class FinalizeClosingHandler(
    AppDbContext db,
    ITenantContext tenant,
    IIdGenerator ids,
    IConcurrencyContext concurrency,
    ClosingInputsQuery inputs,
    IAuditWriter audit,
    ClosingDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<FinalizeClosingCommand, ClosingSummaryDto>
{
    public async Task<ClosingSummaryDto> HandleAsync(FinalizeClosingCommand command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId!.Value;
        var locked = await db.Database.SqlQuery<Guid>(
            $"SELECT id AS \"Value\" FROM business_days WHERE tenant_id = {tenantId} AND id = {command.BusinessDayId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        if (locked.Count == 0)
        {
            throw new NotFoundException("BUSINESS_DAY", command.BusinessDayId);
        }

        var day = await db.Set<BusinessDay>().SingleAsync(d => d.Id == command.BusinessDayId, cancellationToken);
        if (day.Status == BusinessDayStatus.Closed)
        {
            throw new BusinessException(ErrorCodes.ClosingAlreadyFinalized);
        }

        day.EnsureVersion(concurrency.ExpectedVersion);

        var now = time.GetUtcNow();
        var figures = ClosingCalculator.Compute(await inputs.LoadAsync(day.Id, day.OpeningBalance, cancellationToken));
        var closing = DailyClosing.Finalize(
            ids.New(), tenantId, day.Id, day.BusinessDate, figures, command.CountedCash, command.Justification,
            tenant.EmployeeId!.Value, ids.New, now);
        db.Add(closing);
        day.Close(closing.Id, tenant.EmployeeId!.Value, now);

        audit.Add("ClosingFinalized", closing.SyncEntityType, closing.Id, after: new
        {
            closing.BusinessDayId,
            totalSold = closing.TotalSold.ToString(),
            totalReceived = closing.TotalReceived.ToString(),
            expectedCash = closing.ExpectedCash.ToString(),
            countedCash = closing.CountedCash.ToString(),
            cashDifference = closing.CashDifference.ToString(),
            closing.Justification,
        });
        return await dtos.FinalAsync(closing, day, cancellationToken);
    }
}

/// POST /closing/{id}/adjustments [I]: registrado no expediente aberto, sem alterar o fechamento original (ADR-0014).
internal sealed class CreateClosingAdjustmentHandler(
    AppDbContext db,
    ITenantContext tenant,
    IIdGenerator ids,
    IAuditWriter audit,
    ClosingDtoBuilder dtos,
    TimeProvider time) : ICommandHandler<CreateClosingAdjustmentCommand, ClosingAdjustmentDto>
{
    public async Task<ClosingAdjustmentDto> HandleAsync(CreateClosingAdjustmentCommand command, CancellationToken cancellationToken)
    {
        var dayId = await ClosingRules.OpenDayIdAsync(db, cancellationToken) ?? throw new BusinessException(ErrorCodes.BusinessDayRequired);
        if (!await db.Set<DailyClosing>().AnyAsync(c => c.Id == command.DailyClosingId, cancellationToken))
        {
            throw new NotFoundException("DAILY_CLOSING", command.DailyClosingId);
        }

        var r = command.Request;
        var adjustment = ClosingAdjustment.Create(
            ids.New(), tenant.TenantId!.Value, command.DailyClosingId, dayId, r.AdjustmentType, r.Amount, r.Reason, tenant.EmployeeId!.Value, time.GetUtcNow());
        db.Add(adjustment);
        audit.Add("ClosingAdjustmentCreated", adjustment.SyncEntityType, adjustment.Id, after: new
        {
            adjustment.RelatedClosingId,
            adjustmentType = UpperSnakeEnumConverter<AdjustmentType>.ToText(adjustment.AdjustmentType),
            amount = adjustment.Amount.ToString(),
            adjustment.Reason,
        });
        return (await dtos.AdjustmentsAsync([adjustment], cancellationToken))[0];
    }
}

internal sealed class GetClosingQueryHandler(AppDbContext db, ClosingDtoBuilder dtos) : IQueryHandler<GetClosingQuery, ClosingSummaryDto>
{
    public async Task<ClosingSummaryDto> HandleAsync(GetClosingQuery query, CancellationToken cancellationToken)
    {
        var closing = await ClosingRules.LoadClosingAsync(db, c => c.Id == query.DailyClosingId, cancellationToken);
        var day = await db.Set<BusinessDay>().AsNoTracking().SingleAsync(d => d.Id == closing.BusinessDayId, cancellationToken);
        return await dtos.FinalAsync(closing, day, cancellationToken);
    }
}

/// GET /closing/history: cada expediente fechado, do mais recente para o mais antigo (ADR-0039 §4).
internal sealed class ListClosingHistoryQueryHandler(AppDbContext db, IEmployeeDirectory employees)
    : IQueryHandler<ListClosingHistoryQuery, PageDto<ClosingHistoryItemDto>>
{
    public async Task<PageDto<ClosingHistoryItemDto>> HandleAsync(ListClosingHistoryQuery query, CancellationToken cancellationToken)
    {
        ClosingPaging.EnsureLimit(query.Limit);
        var closings = db.Set<DailyClosing>().AsNoTracking();
        if (query.From is { } from)
        {
            closings = closings.Where(c => c.BusinessDate >= from);
        }

        if (query.To is { } to)
        {
            closings = closings.Where(c => c.BusinessDate <= to);
        }

        if (query.Cursor is { } cursor)
        {
            var after = ClosingPaging.DecodeCursor(cursor);
            closings = closings.Where(c => c.Id.CompareTo(after) < 0);
        }

        var rows = await closings.OrderByDescending(c => c.Id).Take(query.Limit + 1)
            .Select(c => new
            {
                c.Id, c.BusinessDayId, c.BusinessDate, c.TotalSold, c.TotalReceived, c.TotalPending, c.CashDifference, c.ClosedAt, c.ClosedByEmployeeId,
                AdjustmentCount = db.Set<ClosingAdjustment>().Count(a => a.RelatedClosingId == c.Id),
            })
            .ToListAsync(cancellationToken);
        var page = rows.Take(query.Limit).ToList();
        var names = await employees.GetDisplayNamesAsync(page.Select(r => r.ClosedByEmployeeId), cancellationToken);

        var items = page.Select(r => new ClosingHistoryItemDto(
                r.Id, r.BusinessDayId, r.BusinessDate, r.TotalSold, r.TotalReceived, r.TotalPending, r.CashDifference, r.ClosedAt,
                new AuthorDto(r.ClosedByEmployeeId, names.GetValueOrDefault(r.ClosedByEmployeeId, string.Empty)), r.AdjustmentCount))
            .ToList();
        return new PageDto<ClosingHistoryItemDto>(items, rows.Count > query.Limit ? ClosingPaging.EncodeCursor(items[^1].DailyClosingId) : null);
    }
}

/// GET /closing/monthly: soma dos fechamentos finalizados do mês (RN-010).
internal sealed class GetMonthlyClosingQueryHandler(AppDbContext db) : IQueryHandler<GetMonthlyClosingQuery, MonthlyClosingDto>
{
    public async Task<MonthlyClosingDto> HandleAsync(GetMonthlyClosingQuery query, CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(query.Month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
        {
            throw BusinessException.Validation("month", "Use o formato AAAA-MM, ex.: 2026-10.");
        }

        var last = first.AddMonths(1).AddDays(-1);
        var closings = await db.Set<DailyClosing>().AsNoTracking().Include(c => c.Lines)
            .Where(c => c.BusinessDate >= first && c.BusinessDate <= last)
            .OrderBy(c => c.BusinessDate).ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);
        var lines = closings.SelectMany(c => c.Lines).ToList();

        return new MonthlyClosingDto(
            query.Month,
            closings.Count,
            Money.Sum(closings.Select(c => c.TotalSold)),
            Money.Sum(closings.Select(c => c.TotalReceived)),
            Money.Sum(closings.Select(c => c.TotalPending)),
            Money.Sum(closings.Select(c => c.CashDifference)),
            ClosingCalculator.OrderTypes.Select(t =>
            {
                var of = lines.Where(l => l.Dimension == ClosingDimension.OrderType && l.Key == t).ToList();
                return new MonthlyOrderTypeDto(t, Money.Sum(of.Select(l => l.Sold ?? Money.Zero)), Money.Sum(of.Select(l => l.Received)));
            }).ToList(),
            ClosingCalculator.PaymentMethods.Select(m => new MonthlyPaymentMethodDto(
                m, Money.Sum(lines.Where(l => l.Dimension == ClosingDimension.PaymentMethod && l.Key == m).Select(l => l.Received)))).ToList(),
            closings.GroupBy(c => c.BusinessDate)
                .Select(g => new MonthlyDayDto(g.Key, g.Select(c => c.Id).ToList(), Money.Sum(g.Select(c => c.TotalSold)), Money.Sum(g.Select(c => c.TotalReceived))))
                .ToList());
    }
}

internal static class ClosingRules
{
    public static async Task<Guid?> OpenDayIdAsync(AppDbContext db, CancellationToken cancellationToken) =>
        await db.Set<BusinessDay>().AsNoTracking()
            .Where(d => d.Status == BusinessDayStatus.Open)
            .Select(d => (Guid?)d.Id)
            .SingleOrDefaultAsync(cancellationToken);

    public static async Task<DailyClosing> LoadClosingAsync(
        AppDbContext db, System.Linq.Expressions.Expression<Func<DailyClosing, bool>> filter, CancellationToken cancellationToken) =>
        await db.Set<DailyClosing>().AsNoTracking().Include(c => c.Lines).SingleOrDefaultAsync(filter, cancellationToken)
        ?? throw new NotFoundException("DAILY_CLOSING");
}

/// Monta o resumo/fechamento de 06 §6.11 a partir do cálculo ao vivo ou do snapshot.
internal sealed class ClosingDtoBuilder(AppDbContext db, IEmployeeDirectory employees)
{
    public async Task<ClosingSummaryDto> LiveAsync(
        BusinessDay day, ClosingFigures f, IReadOnlyList<ClosingAdjustment> adjustments, DateTimeOffset now, CancellationToken cancellationToken) =>
        new(
            Day(day), false, null, now, f.OrderCount, f.TotalSold, f.DeliveryFeesTotal, f.TotalReceived, f.ReceivedFromPreviousDays, f.TotalPending,
            new CountTotalDto(f.CanceledCount, f.CanceledTotal), new CountTotalDto(f.ReversedCount, f.ReversedTotal),
            f.ByOrderType.Select(l => new OrderTypeLineDto(l.Key, l.Count, l.Sold, l.Received, l.Pending, l.Reversed)).ToList(),
            f.ByPaymentMethod.Select(l => new PaymentMethodLineDto(l.Key, l.Count, l.Received, l.Reversed)).ToList(),
            new ClosingCashDto(f.OpeningBalance, f.CashReceived, f.CashReversed, f.Supplies, f.Withdrawals, f.ExpectedCash, null, null, null),
            await AdjustmentsAsync(adjustments, cancellationToken), null, null);

    /// Snapshot: ajustes que corrigem **este** fechamento ("corrigido por", ADR-0014).
    public async Task<ClosingSummaryDto> FinalAsync(DailyClosing c, BusinessDay day, CancellationToken cancellationToken)
    {
        var adjustments = await db.Set<ClosingAdjustment>().AsNoTracking().Where(a => a.RelatedClosingId == c.Id).ToListAsync(cancellationToken);
        var names = await employees.GetDisplayNamesAsync([c.ClosedByEmployeeId], cancellationToken);
        Money M(Money? m) => m ?? Money.Zero;

        return new ClosingSummaryDto(
            Day(day), true, c.Id, c.ClosedAt, c.OrderCount, c.TotalSold, c.DeliveryFeesTotal, c.TotalReceived, c.ReceivedFromPreviousDays,
            c.TotalPending, new CountTotalDto(c.CanceledCount, c.CanceledTotal), new CountTotalDto(c.ReversedCount, c.ReversedTotal),
            ClosingCalculator.OrderTypes.Select(t => c.Lines.Single(l => l.Dimension == ClosingDimension.OrderType && l.Key == t))
                .Select(l => new OrderTypeLineDto(l.Key, l.Count, M(l.Sold), l.Received, M(l.Pending), l.Reversed)).ToList(),
            ClosingCalculator.PaymentMethods.Select(m => c.Lines.Single(l => l.Dimension == ClosingDimension.PaymentMethod && l.Key == m))
                .Select(l => new PaymentMethodLineDto(l.Key, l.Count, l.Received, l.Reversed)).ToList(),
            new ClosingCashDto(c.OpeningBalance, c.CashReceived, c.CashReversed, c.Supplies, c.Withdrawals, c.ExpectedCash, c.CountedCash,
                c.CashDifference, c.Justification),
            await AdjustmentsAsync(adjustments, cancellationToken),
            c.ClosedAt,
            new AuthorDto(c.ClosedByEmployeeId, names.GetValueOrDefault(c.ClosedByEmployeeId, string.Empty)));
    }

    public async Task<IReadOnlyList<CashMovementDto>> MovementsAsync(IReadOnlyList<CashMovement> movements, CancellationToken cancellationToken)
    {
        var names = await employees.GetDisplayNamesAsync(movements.Select(m => m.CreatedByEmployeeId), cancellationToken);
        return movements.Select(m => new CashMovementDto(
            m.Id, m.BusinessDayId, m.MovementType, m.Amount, m.Description, m.CreatedAt,
            new AuthorDto(m.CreatedByEmployeeId, names.GetValueOrDefault(m.CreatedByEmployeeId, string.Empty)))).ToList();
    }

    public async Task<IReadOnlyList<ClosingAdjustmentDto>> AdjustmentsAsync(IReadOnlyList<ClosingAdjustment> adjustments, CancellationToken cancellationToken)
    {
        var names = await employees.GetDisplayNamesAsync(adjustments.Select(a => a.CreatedByEmployeeId), cancellationToken);
        return adjustments.OrderBy(a => a.CreatedAt).Select(a => new ClosingAdjustmentDto(
            a.Id, a.RelatedClosingId, a.BusinessDayId, a.AdjustmentType, a.Amount, a.Reason, a.CreatedAt,
            new AuthorDto(a.CreatedByEmployeeId, names.GetValueOrDefault(a.CreatedByEmployeeId, string.Empty)))).ToList();
    }

    private static ClosingBusinessDayDto Day(BusinessDay d) => new(d.Id, d.Version, d.BusinessDate, d.Status);
}

/// Cursor opaco (06 §6.0): base64url do último id (UUID v7) da página.
internal static class ClosingPaging
{
    public const int MaxLimit = 100;

    public static void EnsureLimit(int limit)
    {
        if (limit is < 1 or > MaxLimit)
        {
            throw BusinessException.Validation("limit", $"Use um limite entre 1 e {MaxLimit}.");
        }
    }

    public static string EncodeCursor(Guid id) => Base64Url.EncodeToString(id.ToByteArray());

    public static Guid DecodeCursor(string cursor)
    {
        try
        {
            var bytes = Base64Url.DecodeFromChars(cursor);
            return bytes.Length == 16 ? new Guid(bytes) : throw new FormatException();
        }
        catch (FormatException)
        {
            throw new BusinessException(ErrorCodes.MalformedRequest, "Cursor inválido.");
        }
    }
}

internal sealed class RegisterCashMovementCommandValidator : AbstractValidator<RegisterCashMovementCommand>
{
    public RegisterCashMovementCommandValidator()
    {
        RuleFor(x => x.MovementType).IsInEnum();
        RuleFor(x => x.Description).NotEmpty().WithMessage("Descreva a movimentação.").MaximumLength(200);
    }
}

internal sealed class FinalizeClosingCommandValidator : AbstractValidator<FinalizeClosingCommand>
{
    public FinalizeClosingCommandValidator()
    {
        RuleFor(x => x.BusinessDayId).NotEmpty();
        RuleFor(x => x.Justification).MaximumLength(500);
    }
}

internal sealed class CreateClosingAdjustmentRequestValidator : AbstractValidator<CreateClosingAdjustmentRequest>
{
    public CreateClosingAdjustmentRequestValidator()
    {
        RuleFor(x => x.AdjustmentType).IsInEnum();
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Informe o motivo.").MaximumLength(500);
    }
}
