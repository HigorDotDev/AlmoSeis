using AlmoSeis.Modules.Menu.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;

namespace AlmoSeis.Modules.Closing.Contracts;

public enum BusinessDayStatus
{
    Open,
    Closed,
}

// ---------- DTOs (06 §6.6) ----------

public sealed record BusinessDayDto(
    Guid Id,
    int Version,
    DateOnly BusinessDate,
    BusinessDayStatus Status,
    Money OpeningBalance,
    DateTimeOffset OpenedAt,
    AuthorDto OpenedBy,
    DateTimeOffset? ClosedAt,
    AuthorDto? ClosedBy,
    Guid? DailyClosingId) : IVersionedDto;

public sealed record OpenBusinessDayResultDto(BusinessDayDto BusinessDay, IReadOnlyList<MaterializedMenuDto> MaterializedMenus)
    : IHasResourceId
{
    Guid IHasResourceId.ResourceId => BusinessDay.Id;
}

// ---------- Comandos e consultas ----------

public sealed record OpenBusinessDayCommand(Money OpeningBalance, bool ConfirmSameDate);

public sealed record GetCurrentBusinessDayQuery;

public sealed record GetBusinessDayQuery(Guid BusinessDayId);

public sealed record ListBusinessDaysQuery(DateOnly? From, DateOnly? To, int Limit, string? Cursor);

// ---------- Portas (07 §7.9) ----------

public sealed record BusinessDayRef(Guid Id, DateOnly BusinessDate, int Version);

/// Expediente aberto para as escritas que o exigem (ADR-0019). Lê com `FOR SHARE`: a finalização do
/// fechamento (`FOR UPDATE`) espera as escritas em curso, e vice-versa.
public interface IBusinessDayReader
{
    Task<BusinessDayRef?> GetOpenAsync(CancellationToken cancellationToken);
}

// ---------- Caixa e fechamento (06 §6.11) ----------

public enum CashMovementType
{
    Supply,
    Withdrawal,
}

public enum AdjustmentType
{
    CashCountCorrection,
    Other,
}

public sealed record CashMovementDto(
    Guid Id, Guid BusinessDayId, CashMovementType MovementType, Money Amount, string Description, DateTimeOffset CreatedAt, AuthorDto CreatedBy)
    : IHasResourceId
{
    Guid IHasResourceId.ResourceId => Id;
}

public sealed record ClosingBusinessDayDto(Guid Id, int Version, DateOnly BusinessDate, BusinessDayStatus Status);

public sealed record CountTotalDto(int Count, Money Total);

public sealed record OrderTypeLineDto(string OrderType, int Count, Money Sold, Money Received, Money Pending, Money Reversed);

public sealed record PaymentMethodLineDto(string PaymentMethod, int Count, Money Received, Money Reversed);

public sealed record ClosingCashDto(
    Money OpeningBalance,
    Money CashReceived,
    Money CashReversed,
    Money Supplies,
    Money Withdrawals,
    Money ExpectedCash,
    Money? CountedCash,
    Money? CashDifference,
    string? Justification);

public sealed record ClosingAdjustmentDto(
    Guid Id, Guid RelatedClosingId, Guid BusinessDayId, AdjustmentType AdjustmentType, Money Amount, string Reason, DateTimeOffset CreatedAt, AuthorDto CreatedBy)
    : IHasResourceId
{
    Guid IHasResourceId.ResourceId => Id;
}

/// Resumo parcial (`isFinal: false`) e `DailyClosing` (`isFinal: true`) têm o mesmo formato; `closedAt`/`closedBy`
/// só são preenchidos no fechamento finalizado.
public sealed record ClosingSummaryDto(
    ClosingBusinessDayDto BusinessDay,
    bool IsFinal,
    Guid? DailyClosingId,
    DateTimeOffset ComputedAt,
    int OrderCount,
    Money TotalSold,
    Money DeliveryFeesTotal,
    Money TotalReceived,
    Money ReceivedFromPreviousDays,
    Money TotalPending,
    CountTotalDto Canceled,
    CountTotalDto Reversed,
    IReadOnlyList<OrderTypeLineDto> ByOrderType,
    IReadOnlyList<PaymentMethodLineDto> ByPaymentMethod,
    ClosingCashDto Cash,
    IReadOnlyList<ClosingAdjustmentDto> Adjustments,
    DateTimeOffset? ClosedAt,
    AuthorDto? ClosedBy) : IHasResourceId
{
    Guid IHasResourceId.ResourceId => DailyClosingId ?? BusinessDay.Id;
}

public sealed record ClosingHistoryItemDto(
    Guid DailyClosingId, Guid BusinessDayId, DateOnly BusinessDate, Money TotalSold, Money TotalReceived, Money TotalPending,
    Money CashDifference, DateTimeOffset ClosedAt, AuthorDto ClosedBy, int AdjustmentCount);

public sealed record MonthlyOrderTypeDto(string OrderType, Money Sold, Money Received);

public sealed record MonthlyPaymentMethodDto(string PaymentMethod, Money Received);

public sealed record MonthlyDayDto(DateOnly BusinessDate, IReadOnlyList<Guid> DailyClosingIds, Money TotalSold, Money TotalReceived);

public sealed record MonthlyClosingDto(
    string Month,
    int ClosingCount,
    Money TotalSold,
    Money TotalReceived,
    Money TotalPendingAtClosings,
    Money CashDifferenceTotal,
    IReadOnlyList<MonthlyOrderTypeDto> ByOrderType,
    IReadOnlyList<MonthlyPaymentMethodDto> ByPaymentMethod,
    IReadOnlyList<MonthlyDayDto> Days);

public sealed record RegisterCashMovementCommand(CashMovementType MovementType, Money Amount, string Description);

public sealed record ListCashMovementsQuery(Guid? BusinessDayId);

public sealed record GetClosingSummaryQuery(Guid? BusinessDayId);

public sealed record FinalizeClosingCommand(Guid BusinessDayId, Money CountedCash, string? Justification);

public sealed record CreateClosingAdjustmentRequest(AdjustmentType AdjustmentType, Money Amount, string Reason);

public sealed record CreateClosingAdjustmentCommand(Guid DailyClosingId, CreateClosingAdjustmentRequest Request);

public sealed record GetClosingQuery(Guid DailyClosingId);

public sealed record ListClosingHistoryQuery(DateOnly? From, DateOnly? To, int Limit, string? Cursor);

public sealed record GetMonthlyClosingQuery(string Month);
