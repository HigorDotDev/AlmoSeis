using AlmoSeis.Modules.Payments.Contracts;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;

namespace AlmoSeis.Modules.Payments.Domain;

/// Pagamento (04 §4.8, ADR-0021). Imutável, exceto `CONFIRMED → REVERSED`. Sem coluna de versão (05 §5.10):
/// a concorrência é a do pedido (If-Match = versão do pedido).
internal sealed class Payment : Entity, ITenantOwned, ISyncTracked
{
    private Payment()
    {
    }

    public Guid TenantId { get; private set; }
    public string SyncEntityType => "PAYMENT";
    public Guid OrderId { get; private set; }
    public Guid BusinessDayId { get; private set; }
    public DateOnly BusinessDate { get; private set; }
    public Money Amount { get; private set; }
    public PaymentMethod PaymentMethod { get; private set; }
    public Money? CashReceived { get; private set; }
    public Money? ChangeGiven { get; private set; }
    public string? Label { get; private set; }
    public PaymentStatus Status { get; private set; }
    public DateTimeOffset PaidAt { get; private set; }
    public Guid CreatedByEmployeeId { get; private set; }

    /// Troco só em dinheiro: `changeGiven = cashReceived − amount` (ADR-0021). O caixa conta só o `amount`.
    public static Payment Register(
        Guid id, Guid tenantId, Guid orderId, Guid businessDayId, DateOnly businessDate, Money amount, PaymentMethod method,
        Money? cashReceived, string? label, Guid by, DateTimeOffset now)
    {
        if (amount <= Money.Zero)
        {
            throw BusinessException.Validation("amount", "O valor deve ser maior que zero.");
        }

        if (cashReceived is not null && method != PaymentMethod.Cash)
        {
            throw BusinessException.Validation("cashReceived", "Valor recebido e troco só existem em dinheiro.");
        }

        if (cashReceived is { } received && received < amount)
        {
            throw BusinessException.Validation("cashReceived", "O valor recebido não cobre o pagamento.");
        }

        return new Payment
        {
            Id = id,
            TenantId = tenantId,
            OrderId = orderId,
            BusinessDayId = businessDayId,
            BusinessDate = businessDate,
            Amount = amount,
            PaymentMethod = method,
            CashReceived = cashReceived,
            ChangeGiven = cashReceived is { } r ? r - amount : null,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            Status = PaymentStatus.Confirmed,
            PaidAt = now,
            CreatedByEmployeeId = by,
        };
    }

    /// Estorno integral, registrado no expediente **aberto** (ADR-0014, ADR-0022 §3).
    public PaymentReversal Reverse(Guid reversalId, Guid businessDayId, DateOnly businessDate, string reason, Guid by, DateTimeOffset now)
    {
        if (Status == PaymentStatus.Reversed)
        {
            throw new BusinessException(ErrorCodes.PaymentAlreadyReversed);
        }

        if (reason.Trim().Length < 5)
        {
            throw BusinessException.Validation("reason", "Descreva o motivo com pelo menos 5 caracteres.");
        }

        Status = PaymentStatus.Reversed;
        return new PaymentReversal(reversalId, TenantId, this, businessDayId, businessDate, reason.Trim(), by, now);
    }
}

/// Estorno (04 §4.8). Imutável; um por pagamento.
internal sealed class PaymentReversal : Entity, ITenantOwned, ISyncTracked
{
    private PaymentReversal()
    {
    }

    internal PaymentReversal(Guid id, Guid tenantId, Payment payment, Guid businessDayId, DateOnly businessDate, string reason, Guid by, DateTimeOffset now)
    {
        Id = id;
        TenantId = tenantId;
        PaymentId = payment.Id;
        OrderId = payment.OrderId;
        BusinessDayId = businessDayId;
        BusinessDate = businessDate;
        Amount = payment.Amount;
        PaymentMethod = payment.PaymentMethod;
        Reason = reason;
        CreatedByEmployeeId = by;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public string SyncEntityType => "PAYMENT_REVERSAL";
    public Guid PaymentId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid BusinessDayId { get; private set; }
    public DateOnly BusinessDate { get; private set; }
    public Money Amount { get; private set; }
    public PaymentMethod PaymentMethod { get; private set; }
    public string Reason { get; private set; } = null!;
    public Guid CreatedByEmployeeId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
