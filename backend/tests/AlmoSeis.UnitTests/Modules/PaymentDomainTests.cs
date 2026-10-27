using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Ordering.Domain;
using AlmoSeis.Modules.Payments.Contracts;
using AlmoSeis.Modules.Payments.Domain;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using Shouldly;

namespace AlmoSeis.UnitTests.Modules;

public sealed class PaymentDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 27);

    [Fact]
    public void Cash_payment_records_the_change()
    {
        var payment = Register(PaymentMethod.Cash, 35m, cashReceived: 50m);

        payment.ChangeGiven.ShouldBe(Money.Of(15m));
        payment.Status.ShouldBe(PaymentStatus.Confirmed);
    }

    [Fact]
    public void Change_only_exists_in_cash_and_must_cover_the_amount()
    {
        Should.Throw<BusinessException>(() => Register(PaymentMethod.Pix, 10m, cashReceived: 20m)).ErrorCode.ShouldBe(ErrorCodes.ValidationFailed);
        Should.Throw<BusinessException>(() => Register(PaymentMethod.Cash, 10m, cashReceived: 5m)).ErrorCode.ShouldBe(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public void Reversal_copies_amount_and_method_and_happens_once()
    {
        var payment = Register(PaymentMethod.Card, 48m);
        var dayId = Guid.NewGuid();

        var reversal = payment.Reverse(Guid.NewGuid(), dayId, Today.AddDays(1), "Cobrado em duplicidade", Guid.NewGuid(), Now);

        reversal.Amount.ShouldBe(Money.Of(48m));
        reversal.PaymentMethod.ShouldBe(PaymentMethod.Card);
        reversal.BusinessDayId.ShouldBe(dayId);
        payment.Status.ShouldBe(PaymentStatus.Reversed);
        Should.Throw<BusinessException>(() => payment.Reverse(Guid.NewGuid(), dayId, Today, "De novo", Guid.NewGuid(), Now))
            .ErrorCode.ShouldBe(ErrorCodes.PaymentAlreadyReversed);
    }

    [Fact]
    public void Order_payment_settles_only_at_zero_balance_and_undo_unlocks()
    {
        var order = Order.Create(
            Guid.NewGuid(), Guid.NewGuid(),
            new NewOrder(OrderType.Pf, FulfillmentType.DineIn, null, null, null, null, Money.Zero, "5", null, null,
                [new NewItem(OrderItemKind.Pf, null, Guid.NewGuid(), null, 2, Money.Of(24m), "PF Frango", null, true, null, [], [])], []),
            Guid.NewGuid(), Today, 1, Guid.NewGuid(), Guid.NewGuid, Now);

        Should.Throw<BusinessException>(() => order.ApplyPayment(Money.Of(48.01m), Now)).ErrorCode.ShouldBe(ErrorCodes.PaymentExceedsBalance);
        order.ApplyPayment(Money.Of(20m), Now);
        order.IsPaid.ShouldBeFalse();
        order.ApplyPayment(Money.Of(28m), Now);
        order.IsPaid.ShouldBeTrue();
        order.Version.ShouldBe(2); // uma subida por unidade de trabalho

        order.UndoPayment(Money.Of(28m), Now);
        order.IsPaid.ShouldBeFalse();
        order.Balance.ShouldBe(Money.Of(28m));
    }

    private static Payment Register(PaymentMethod method, decimal amount, decimal? cashReceived = null) =>
        Payment.Register(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Today, Money.Of(amount), method,
            cashReceived is { } c ? Money.Of(c) : null, null, Guid.NewGuid(), Now);
}
