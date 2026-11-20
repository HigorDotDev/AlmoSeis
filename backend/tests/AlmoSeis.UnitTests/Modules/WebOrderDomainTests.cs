using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Ordering.Domain;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using Shouldly;

namespace AlmoSeis.UnitTests.Modules;

/// Aceite do pedido da página e atendimento (ADR-0046).
public sealed class WebOrderDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Deadline = Now.AddMinutes(10);
    private static readonly Guid By = Guid.NewGuid();

    [Fact]
    public void Web_order_waits_without_number_production_customer_or_author()
    {
        var order = Web(FulfillmentType.Delivery);

        order.Origin.ShouldBe(OrderOrigin.Web);
        order.AcceptanceStatus.ShouldBe(AcceptanceStatus.AwaitingAcceptance);
        order.OrderNumber.ShouldBeNull();
        order.ProductionStatus.ShouldBeNull();
        order.FulfillmentStatus.ShouldBeNull();
        order.CustomerId.ShouldBeNull();
        order.CreatedByEmployeeId.ShouldBeNull();
        order.Items.ShouldAllBe(i => i.CreatedByEmployeeId == null);
        // 2 × 20,00 + taxa 5,00 = 45,00
        order.TotalAmount.ShouldBe(Money.Of(45m));
        order.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<OrderAwaitingAcceptance>().MarmitaCount.ShouldBe(2);
    }

    [Fact]
    public void Not_accepted_order_cannot_be_edited_produced_paid_canceled_or_dispatched()
    {
        var order = Web(FulfillmentType.Pickup);

        Code(() => order.AddItems([Beverage()], By, Guid.NewGuid, Now)).ShouldBe(ErrorCodes.OrderNotAccepted);
        Code(() => order.ChangeProductionStatus(ProductionStatus.Completed, Now)).ShouldBe(ErrorCodes.OrderNotAccepted);
        Code(() => order.ApplyPayment(Money.Of(1m), Now)).ShouldBe(ErrorCodes.OrderNotAccepted);
        Code(() => order.Cancel("Cliente desistiu", By, Guid.NewGuid, Now)).ShouldBe(ErrorCodes.OrderNotAccepted);
        Code(() => order.ChangeFulfillment(FulfillmentStatus.PickedUp, Now)).ShouldBe(ErrorCodes.OrderNotAccepted);
        order.ChangeEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Accept_gives_number_production_fulfillment_and_customer_in_one_version()
    {
        var order = Web(FulfillmentType.Delivery);
        order.ClearDomainEvents();
        var customer = Guid.NewGuid();

        order.Accept(7, By, "Ana", customer, 30, Now.AddMinutes(2));

        order.Version.ShouldBe(2);
        order.AcceptanceStatus.ShouldBe(AcceptanceStatus.Accepted);
        order.OrderNumber.ShouldBe(7);
        order.ProductionStatus.ShouldBe(ProductionStatus.Open);
        order.FulfillmentStatus.ShouldBe(FulfillmentStatus.Pending);
        order.CustomerId.ShouldBe(customer);
        order.EstimatedReadyMinutes.ShouldBe(30);
        order.AcceptedAt.ShouldBe(Now.AddMinutes(2));
        order.DomainEvents.Select(e => e.GetType().Name).ShouldBe(["OrderAccepted", "OrderCreated"]);
        order.DomainEvents.OfType<OrderCreated>().Single().Label.ShouldBeNull();
        Code(() => order.Accept(8, By, "Ana", null, null, Now)).ShouldBe(ErrorCodes.OrderNotAwaitingAcceptance);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(90)]
    public void Estimated_time_is_one_of_the_options(int minutes) =>
        Code(() => Web(FulfillmentType.Pickup).Accept(1, By, "Ana", null, minutes, Now)).ShouldBe(ErrorCodes.ValidationFailed);

    [Fact]
    public void Deadline_closes_accept_reject_and_withdraw_and_the_job_expires()
    {
        var order = Web(FulfillmentType.Pickup);

        Code(() => order.Accept(1, By, "Ana", null, null, Deadline)).ShouldBe(ErrorCodes.WebOrderExpired);
        Code(() => order.Reject(RejectionReason.TooBusy, null, By, Deadline)).ShouldBe(ErrorCodes.WebOrderExpired);
        Code(() => order.Withdraw(Deadline)).ShouldBe(ErrorCodes.WebOrderExpired);
        order.Expire(Deadline.AddSeconds(-1)).ShouldBeFalse();

        order.Expire(Deadline).ShouldBeTrue();

        order.AcceptanceStatus.ShouldBe(AcceptanceStatus.Expired);
        order.ExpiredAt.ShouldBe(Deadline);
        order.OrderNumber.ShouldBeNull();
        order.Expire(Deadline.AddMinutes(1)).ShouldBeFalse();
        Code(() => order.Accept(1, By, "Ana", null, null, Now)).ShouldBe(ErrorCodes.WebOrderExpired);
    }

    [Fact]
    public void Reject_needs_a_listed_reason_and_text_in_other()
    {
        var order = Web(FulfillmentType.Pickup);

        Code(() => order.Reject(RejectionReason.Other, null, By, Now)).ShouldBe(ErrorCodes.ValidationFailed);
        Code(() => order.Reject(RejectionReason.Other, "abc", By, Now)).ShouldBe(ErrorCodes.ValidationFailed);
        Code(() => order.Reject(RejectionReason.TooBusy, new string('x', 201), By, Now)).ShouldBe(ErrorCodes.ValidationFailed);
        Code(() => order.Reject(RejectionReason.BusinessDayClosed, null, By, Now)).ShouldBe(ErrorCodes.ValidationFailed);

        order.Reject(RejectionReason.ItemUnavailable, "  A costela acabou.  ", By, Now);

        order.AcceptanceStatus.ShouldBe(AcceptanceStatus.Rejected);
        order.RejectionMessage.ShouldBe("A costela acabou.");
        order.RejectedByEmployeeId.ShouldBe(By);
        order.DomainEvents.OfType<OrderRejected>().Single().ReasonCode.ShouldBe(RejectionReason.ItemUnavailable);
        Code(() => order.Withdraw(Now)).ShouldBe(ErrorCodes.OrderNotAwaitingAcceptance);
    }

    [Fact]
    public void Closing_rejects_as_the_system_and_customer_can_withdraw()
    {
        var closed = Web(FulfillmentType.Pickup);
        closed.RejectBecauseBusinessDayClosed(Deadline.AddHours(1));
        closed.RejectionReasonCode.ShouldBe(RejectionReason.BusinessDayClosed);
        closed.RejectedByEmployeeId.ShouldBeNull();

        var withdrawn = Web(FulfillmentType.Pickup);
        withdrawn.Withdraw(Now);
        withdrawn.AcceptanceStatus.ShouldBe(AcceptanceStatus.Withdrawn);
        withdrawn.DomainEvents.OfType<OrderRejected>().Single().AcceptanceStatus.ShouldBe(AcceptanceStatus.Withdrawn);
    }

    [Fact]
    public void App_order_is_born_accepted_with_pending_fulfillment_only_outside_the_dining_room()
    {
        var delivery = App(FulfillmentType.Delivery);
        delivery.AcceptanceStatus.ShouldBe(AcceptanceStatus.Accepted);
        delivery.AcceptedAt.ShouldBe(Now);
        delivery.AcceptedByEmployeeId.ShouldBe(By);
        delivery.FulfillmentStatus.ShouldBe(FulfillmentStatus.Pending);

        var pf = Order.Create(
            Guid.NewGuid(), Guid.NewGuid(),
            new NewOrder(OrderType.Pf, FulfillmentType.DineIn, null, null, null, null, Money.Zero, "5", null, null,
                [new NewItem(OrderItemKind.Pf, null, Guid.NewGuid(), null, 1, Money.Of(24m), "PF Frango", null, true, null, [], [])], []),
            Guid.NewGuid(), new DateOnly(2026, 11, 20), 1, By, Guid.NewGuid, Now);
        pf.FulfillmentStatus.ShouldBeNull();
        Code(() => pf.ChangeFulfillment(FulfillmentStatus.PickedUp, Now)).ShouldBe(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public void Delivery_goes_out_only_with_production_completed_and_can_come_back()
    {
        var order = App(FulfillmentType.Delivery);

        Code(() => order.ChangeFulfillment(FulfillmentStatus.OutForDelivery, Now)).ShouldBe(ErrorCodes.ProductionNotCompleted);
        Code(() => order.ChangeFulfillment(FulfillmentStatus.Delivered, Now)).ShouldBe(ErrorCodes.ValidationFailed);
        Code(() => order.ChangeFulfillment(FulfillmentStatus.PickedUp, Now)).ShouldBe(ErrorCodes.ValidationFailed);

        order.ChangeProductionStatus(ProductionStatus.Completed, Now);
        order.ChangeFulfillment(FulfillmentStatus.OutForDelivery, Now.AddMinutes(1));
        order.OutForDeliveryAt.ShouldBe(Now.AddMinutes(1));

        // "Voltou sem entregar": volta a pendente e limpa o horário de saída.
        order.ChangeFulfillment(FulfillmentStatus.Pending, Now.AddMinutes(20));
        order.OutForDeliveryAt.ShouldBeNull();

        order.ChangeFulfillment(FulfillmentStatus.OutForDelivery, Now.AddMinutes(30));
        order.ChangeFulfillment(FulfillmentStatus.Delivered, Now.AddMinutes(45));
        order.DeliveredAt.ShouldBe(Now.AddMinutes(45));

        // Correção de toque errado: entregue → saiu.
        order.ChangeFulfillment(FulfillmentStatus.OutForDelivery, Now.AddMinutes(46));
        order.DeliveredAt.ShouldBeNull();
        order.OutForDeliveryAt.ShouldBe(Now.AddMinutes(30));
        order.Version.ShouldBe(2);
    }

    [Fact]
    public void Dispatched_order_does_not_take_new_items_but_unpaid_balance_stays()
    {
        var order = App(FulfillmentType.Delivery);
        order.ChangeProductionStatus(ProductionStatus.Completed, Now);
        order.ChangeFulfillment(FulfillmentStatus.OutForDelivery, Now);
        order.ChangeFulfillment(FulfillmentStatus.Delivered, Now);

        Code(() => order.AddItems([Beverage()], By, Guid.NewGuid, Now)).ShouldBe(ErrorCodes.OrderAlreadyDispatched);
        order.IsPaid.ShouldBeFalse();
        order.Balance.ShouldBe(Money.Of(45m));
    }

    [Fact]
    public void Ready_for_pickup_is_derived()
    {
        var order = App(FulfillmentType.Pickup);
        order.IsReadyForPickup.ShouldBeFalse();

        order.ChangeProductionStatus(ProductionStatus.Completed, Now);
        order.IsReadyForPickup.ShouldBeTrue();

        order.ChangeFulfillment(FulfillmentStatus.PickedUp, Now);
        order.IsReadyForPickup.ShouldBeFalse();
        order.PickedUpAt.ShouldBe(Now);
    }

    private static ErrorCode Code(Action action) => Should.Throw<BusinessException>(action).ErrorCode;

    private static NewItem Marmita(int quantity) =>
        new(OrderItemKind.Marmita, null, null, Guid.NewGuid(), quantity, Money.Of(20m), "Marmita M", "Frango", true, null, [], []);

    private static NewItem Beverage() =>
        new(OrderItemKind.Beverage, null, Guid.NewGuid(), null, 1, Money.Of(6m), "Coca-Cola 350ml", null, false, null, [], []);

    private static NewOrder Data(FulfillmentType fulfillment) => new(
        OrderType.Marmita, fulfillment, null, "Carla", "+5534999990000",
        fulfillment == FulfillmentType.Delivery ? new DeliveryAddressDto("Rua A", "1", null, null, null) : null,
        fulfillment == FulfillmentType.Delivery ? Money.Of(5m) : Money.Zero, null, null, null, [Marmita(2)], []);

    private static Order Web(FulfillmentType fulfillment) =>
        Order.SubmitFromWeb(Guid.NewGuid(), Guid.NewGuid(), Data(fulfillment), Guid.NewGuid(), new DateOnly(2026, 11, 20), Deadline, Guid.NewGuid, Now);

    private static Order App(FulfillmentType fulfillment) =>
        Order.Create(Guid.NewGuid(), Guid.NewGuid(), Data(fulfillment), Guid.NewGuid(), new DateOnly(2026, 11, 20), 1, By, Guid.NewGuid, Now);
}
