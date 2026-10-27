using AlmoSeis.Modules.Closing.Contracts;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;

namespace AlmoSeis.Modules.Closing.Domain;

/// Suprimento ou sangria (04 §4.9). Imutável: correção é outro movimento.
internal sealed class CashMovement : Entity, ITenantOwned, ISyncTracked
{
    private CashMovement()
    {
    }

    public Guid TenantId { get; private set; }
    public string SyncEntityType => "CASH_MOVEMENT";
    public Guid BusinessDayId { get; private set; }
    public CashMovementType MovementType { get; private set; }
    public Money Amount { get; private set; }
    public string Description { get; private set; } = null!;
    public Guid CreatedByEmployeeId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static CashMovement Register(
        Guid id, Guid tenantId, Guid businessDayId, CashMovementType type, Money amount, string description, Guid by, DateTimeOffset now)
    {
        if (amount <= Money.Zero)
        {
            throw BusinessException.Validation("amount", "O valor deve ser maior que zero.");
        }

        return new CashMovement
        {
            Id = id,
            TenantId = tenantId,
            BusinessDayId = businessDayId,
            MovementType = type,
            Amount = amount,
            Description = description.Trim(),
            CreatedByEmployeeId = by,
            CreatedAt = now,
        };
    }
}

/// Snapshot imutável do fechamento (ADR-0013, ADR-0014).
internal sealed class DailyClosing : Entity, ITenantOwned, ISyncTracked
{
    private readonly List<DailyClosingLine> _lines = [];

    private DailyClosing()
    {
    }

    public Guid TenantId { get; private set; }
    public string SyncEntityType => "DAILY_CLOSING";
    public Guid BusinessDayId { get; private set; }
    public DateOnly BusinessDate { get; private set; }
    public int OrderCount { get; private set; }
    public Money TotalSold { get; private set; }
    public Money DeliveryFeesTotal { get; private set; }
    public Money TotalReceived { get; private set; }
    public Money ReceivedFromPreviousDays { get; private set; }
    public Money TotalPending { get; private set; }
    public int CanceledCount { get; private set; }
    public Money CanceledTotal { get; private set; }
    public int ReversedCount { get; private set; }
    public Money ReversedTotal { get; private set; }
    public Money OpeningBalance { get; private set; }
    public Money CashReceived { get; private set; }
    public Money CashReversed { get; private set; }
    public Money Supplies { get; private set; }
    public Money Withdrawals { get; private set; }
    public Money ExpectedCash { get; private set; }
    public Money CountedCash { get; private set; }
    public Money CashDifference { get; private set; }
    public string? Justification { get; private set; }
    public Guid ClosedByEmployeeId { get; private set; }
    public DateTimeOffset ClosedAt { get; private set; }
    public IReadOnlyList<DailyClosingLine> Lines => _lines;

    /// RN-010.13: diferença de caixa ≠ 0 exige justificativa.
    public static DailyClosing Finalize(
        Guid id, Guid tenantId, Guid businessDayId, DateOnly businessDate, ClosingFigures f, Money countedCash, string? justification,
        Guid by, Func<Guid> newId, DateTimeOffset now)
    {
        if (countedCash.IsNegative)
        {
            throw BusinessException.Validation("countedCash", "O dinheiro contado não pode ser negativo.");
        }

        var difference = countedCash - f.ExpectedCash;
        var text = string.IsNullOrWhiteSpace(justification) ? null : justification.Trim();
        if (!difference.IsZero && (text is null || text.Length < 5))
        {
            throw new BusinessException(
                ErrorCodes.JustificationRequired,
                extensions: new Dictionary<string, object?> { ["expectedCash"] = f.ExpectedCash, ["cashDifference"] = difference });
        }

        var closing = new DailyClosing
        {
            Id = id,
            TenantId = tenantId,
            BusinessDayId = businessDayId,
            BusinessDate = businessDate,
            OrderCount = f.OrderCount,
            TotalSold = f.TotalSold,
            DeliveryFeesTotal = f.DeliveryFeesTotal,
            TotalReceived = f.TotalReceived,
            ReceivedFromPreviousDays = f.ReceivedFromPreviousDays,
            TotalPending = f.TotalPending,
            CanceledCount = f.CanceledCount,
            CanceledTotal = f.CanceledTotal,
            ReversedCount = f.ReversedCount,
            ReversedTotal = f.ReversedTotal,
            OpeningBalance = f.OpeningBalance,
            CashReceived = f.CashReceived,
            CashReversed = f.CashReversed,
            Supplies = f.Supplies,
            Withdrawals = f.Withdrawals,
            ExpectedCash = f.ExpectedCash,
            CountedCash = countedCash,
            CashDifference = difference,
            Justification = text,
            ClosedByEmployeeId = by,
            ClosedAt = now,
        };

        closing._lines.AddRange(f.ByOrderType.Select(l =>
            new DailyClosingLine(newId(), tenantId, id, ClosingDimension.OrderType, l.Key, l.Sold, l.Received, l.Pending, l.Reversed, l.Count)));
        closing._lines.AddRange(f.ByPaymentMethod.Select(l =>
            new DailyClosingLine(newId(), tenantId, id, ClosingDimension.PaymentMethod, l.Key, null, l.Received, null, l.Reversed, l.Count)));
        return closing;
    }
}

internal sealed class DailyClosingLine : Entity, ITenantOwned
{
    private DailyClosingLine()
    {
    }

    internal DailyClosingLine(
        Guid id, Guid tenantId, Guid closingId, ClosingDimension dimension, string key, Money? sold, Money received, Money? pending, Money reversed, int count)
    {
        Id = id;
        TenantId = tenantId;
        DailyClosingId = closingId;
        Dimension = dimension;
        Key = key;
        Sold = sold;
        Received = received;
        Pending = pending;
        Reversed = reversed;
        Count = count;
    }

    public Guid TenantId { get; private set; }
    public Guid DailyClosingId { get; private set; }
    public ClosingDimension Dimension { get; private set; }
    public string Key { get; private set; } = null!;
    public Money? Sold { get; private set; }
    public Money Received { get; private set; }
    public Money? Pending { get; private set; }
    public Money Reversed { get; private set; }
    public int Count { get; private set; }
}

/// Correção posterior: não altera o fechamento relacionado nem o caixa físico (ADR-0014).
internal sealed class ClosingAdjustment : Entity, ITenantOwned, ISyncTracked
{
    private ClosingAdjustment()
    {
    }

    public Guid TenantId { get; private set; }
    public string SyncEntityType => "CLOSING_ADJUSTMENT";
    public Guid RelatedClosingId { get; private set; }
    public Guid BusinessDayId { get; private set; }
    public AdjustmentType AdjustmentType { get; private set; }
    public Money Amount { get; private set; }
    public string Reason { get; private set; } = null!;
    public Guid CreatedByEmployeeId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static ClosingAdjustment Create(
        Guid id, Guid tenantId, Guid relatedClosingId, Guid businessDayId, AdjustmentType type, Money amount, string reason, Guid by, DateTimeOffset now)
    {
        if (amount.IsZero)
        {
            throw BusinessException.Validation("amount", "O ajuste não pode ser zero.");
        }

        if (reason.Trim().Length < 5)
        {
            throw BusinessException.Validation("reason", "Descreva o motivo com pelo menos 5 caracteres.");
        }

        return new ClosingAdjustment
        {
            Id = id,
            TenantId = tenantId,
            RelatedClosingId = relatedClosingId,
            BusinessDayId = businessDayId,
            AdjustmentType = type,
            Amount = amount,
            Reason = reason.Trim(),
            CreatedByEmployeeId = by,
            CreatedAt = now,
        };
    }
}

internal enum ClosingDimension
{
    OrderType,
    PaymentMethod,
}

// ---------- Cálculo (04 §4.9) ----------

/// Somas vindas do banco, por bloco. Chaves de texto: o Closing não referencia Ordering nem Payments (07 §7.1).
internal sealed record OrderTotalsRow(string OrderType, bool Canceled, int Count, Money Sold, Money DeliveryFees, Money Pending);

internal sealed record PaymentTotalsRow(string OrderType, string PaymentMethod, bool FromPreviousDay, int Count, Money Amount);

internal sealed record ReversalTotalsRow(string OrderType, string PaymentMethod, int Count, Money Amount);

internal sealed record ClosingInputs(
    Money OpeningBalance,
    IReadOnlyList<OrderTotalsRow> Orders,
    IReadOnlyList<PaymentTotalsRow> Payments,
    IReadOnlyList<ReversalTotalsRow> Reversals,
    Money Supplies,
    Money Withdrawals);

internal sealed record OrderTypeFigure(string Key, int Count, Money Sold, Money Received, Money Pending, Money Reversed);

internal sealed record PaymentMethodFigure(string Key, int Count, Money Received, Money Reversed);

internal sealed record ClosingFigures(
    int OrderCount,
    Money TotalSold,
    Money DeliveryFeesTotal,
    Money TotalReceived,
    Money ReceivedFromPreviousDays,
    Money TotalPending,
    int CanceledCount,
    Money CanceledTotal,
    int ReversedCount,
    Money ReversedTotal,
    IReadOnlyList<OrderTypeFigure> ByOrderType,
    IReadOnlyList<PaymentMethodFigure> ByPaymentMethod,
    Money OpeningBalance,
    Money CashReceived,
    Money CashReversed,
    Money Supplies,
    Money Withdrawals,
    Money ExpectedCash);

/// **Única** implementação das fórmulas do fechamento, usada pelo resumo parcial e pela finalização (07 §7.9).
internal static class ClosingCalculator
{
    public static readonly string[] OrderTypes = ["MARMITA", "PF", "KILO"];
    public static readonly string[] PaymentMethods = ["CASH", "PIX", "CARD"];

    public static ClosingFigures Compute(ClosingInputs i)
    {
        var active = i.Orders.Where(o => !o.Canceled).ToList();
        var canceled = i.Orders.Where(o => o.Canceled).ToList();

        var byOrderType = OrderTypes.Select(type =>
        {
            var orders = active.Where(o => o.OrderType == type).ToList();
            return new OrderTypeFigure(
                type,
                orders.Sum(o => o.Count),
                Money.Sum(orders.Select(o => o.Sold)),
                Money.Sum(i.Payments.Where(p => p.OrderType == type).Select(p => p.Amount)),
                Money.Sum(orders.Select(o => o.Pending)),
                Money.Sum(i.Reversals.Where(r => r.OrderType == type).Select(r => r.Amount)));
        }).ToList();

        var byPaymentMethod = PaymentMethods.Select(method => new PaymentMethodFigure(
            method,
            i.Payments.Where(p => p.PaymentMethod == method).Sum(p => p.Count),
            Money.Sum(i.Payments.Where(p => p.PaymentMethod == method).Select(p => p.Amount)),
            Money.Sum(i.Reversals.Where(r => r.PaymentMethod == method).Select(r => r.Amount)))).ToList();

        var cashReceived = byPaymentMethod.Single(m => m.Key == "CASH").Received;
        var cashReversed = byPaymentMethod.Single(m => m.Key == "CASH").Reversed;
        var expected = i.OpeningBalance + cashReceived + i.Supplies - i.Withdrawals - cashReversed;

        return new ClosingFigures(
            active.Sum(o => o.Count),
            Money.Sum(active.Select(o => o.Sold)),
            Money.Sum(active.Select(o => o.DeliveryFees)),
            Money.Sum(i.Payments.Select(p => p.Amount)),
            Money.Sum(i.Payments.Where(p => p.FromPreviousDay).Select(p => p.Amount)),
            Money.Sum(active.Select(o => o.Pending)),
            canceled.Sum(o => o.Count),
            Money.Sum(canceled.Select(o => o.Sold)),
            i.Reversals.Sum(r => r.Count),
            Money.Sum(i.Reversals.Select(r => r.Amount)),
            byOrderType,
            byPaymentMethod,
            i.OpeningBalance,
            cashReceived,
            cashReversed,
            i.Supplies,
            i.Withdrawals,
            expected);
    }
}
