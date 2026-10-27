using AlmoSeis.Modules.Closing.Contracts;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;

namespace AlmoSeis.Modules.Closing.Domain;

/// Expediente / sessão de caixa (04 §4.8, ADR-0019, ADR-0039). `BusinessDate` é a data local da
/// abertura e não muda se o expediente passar da meia-noite.
internal sealed class BusinessDay : AggregateRoot, ITenantOwned, ISyncTracked
{
    private BusinessDay()
    {
    }

    public Guid TenantId { get; private set; }
    public string SyncEntityType => "BUSINESS_DAY";
    public DateOnly BusinessDate { get; private set; }
    public BusinessDayStatus Status { get; private set; }
    public Money OpeningBalance { get; private set; }
    public Guid OpenedByEmployeeId { get; private set; }
    public DateTimeOffset OpenedAt { get; private set; }
    public Guid? ClosedByEmployeeId { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }

    public static BusinessDay Open(Guid id, Guid tenantId, DateOnly businessDate, Money openingBalance, Guid openedBy, DateTimeOffset now)
    {
        if (openingBalance.IsNegative)
        {
            throw BusinessException.Validation("openingBalance", "O saldo inicial não pode ser negativo.");
        }

        var day = new BusinessDay
        {
            Id = id,
            TenantId = tenantId,
            BusinessDate = businessDate,
            Status = BusinessDayStatus.Open,
            OpeningBalance = openingBalance,
            OpenedByEmployeeId = openedBy,
            OpenedAt = now,
        };
        day.Raise(new BusinessDayOpened(id, businessDate, now));
        return day;
    }

    /// Finalização do fechamento (ADR-0013): o expediente passa a CLOSED.
    public void Close(Guid dailyClosingId, Guid by, DateTimeOffset now)
    {
        EnsureOpen();
        Status = BusinessDayStatus.Closed;
        ClosedByEmployeeId = by;
        ClosedAt = now;
        Touch(new ClosingFinalized(Id, dailyClosingId, BusinessDate, now));
    }

    private void EnsureOpen()
    {
        if (Status != BusinessDayStatus.Open)
        {
            throw new BusinessException(ErrorCodes.ClosingAlreadyFinalized);
        }
    }
}

/// Vira `BusinessDayOpened` para todo o tenant (06 §6.14).
internal sealed record BusinessDayOpened(Guid BusinessDayId, DateOnly BusinessDate, DateTimeOffset OccurredAt) : IDomainEvent;

/// Vira `ClosingFinalized` para todo o tenant (06 §6.14).
internal sealed record ClosingFinalized(Guid BusinessDayId, Guid DailyClosingId, DateOnly BusinessDate, DateTimeOffset OccurredAt) : IDomainEvent;
