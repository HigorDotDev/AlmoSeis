using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;

namespace AlmoSeis.Modules.Payments.Contracts;

public enum PaymentMethod
{
    Cash,
    Pix,
    Card,
}

public enum PaymentStatus
{
    Confirmed,
    Reversed,
}

// ---------- DTOs (06 §6.10) ----------

/// `reversal` dentro do `Payment`.
public sealed record PaymentReversalSummaryDto(Guid Id, DateOnly BusinessDate, string Reason, DateTimeOffset CreatedAt, AuthorDto CreatedBy);

public sealed record PaymentDto(
    Guid Id,
    Guid OrderId,
    Guid BusinessDayId,
    DateOnly BusinessDate,
    Money Amount,
    PaymentMethod PaymentMethod,
    Money? CashReceived,
    Money? ChangeGiven,
    string? Label,
    PaymentStatus Status,
    DateTimeOffset PaidAt,
    AuthorDto CreatedBy,
    PaymentReversalSummaryDto? Reversal);

public sealed record PaymentReversalDto(
    Guid Id,
    Guid PaymentId,
    Guid OrderId,
    Guid BusinessDayId,
    DateOnly BusinessDate,
    Money Amount,
    PaymentMethod PaymentMethod,
    string Reason,
    DateTimeOffset CreatedAt,
    AuthorDto CreatedBy);

public sealed record RegisterPaymentResultDto(PaymentDto Payment, OrderBalanceDto Order) : IHasResourceId
{
    Guid IHasResourceId.ResourceId => Payment.Id;
}

public sealed record ReversePaymentResultDto(PaymentReversalDto Reversal, OrderBalanceDto Order) : IHasResourceId
{
    Guid IHasResourceId.ResourceId => Reversal.Id;
}

// ---------- Comandos e consultas ----------

public sealed record RegisterPaymentRequest(Money Amount, PaymentMethod PaymentMethod, Money? CashReceived, string? Label);

public sealed record RegisterPaymentCommand(Guid OrderId, RegisterPaymentRequest Request);

public sealed record ReversePaymentRequest(string Reason);

public sealed record ReversePaymentCommand(Guid PaymentId, ReversePaymentRequest Request);

public sealed record ListOrderPaymentsQuery(Guid OrderId);

public sealed record GetPaymentQuery(Guid PaymentId);
