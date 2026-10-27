using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Ordering.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using Shouldly;

namespace AlmoSeis.UnitTests.Modules;

public sealed class OrderDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid By = Guid.NewGuid();

    [Fact]
    public void Addon_line_total_follows_the_parent_quantity()
    {
        var order = Pf(quantity: 2, addonQuantity: 1);

        var pf = order.Items.Single(i => i.ParentOrderItemId is null);
        order.Items.Single(i => i.ParentOrderItemId == pf.Id).LineTotal.ShouldBe(Money.Of(4m));
        order.ItemsTotal.ShouldBe(Money.Of(52m));

        order.ChangeItem(pf.Id, new Optional<int>(3), default, By, Guid.NewGuid, Now);

        order.Items.Single(i => i.ParentOrderItemId == pf.Id).LineTotal.ShouldBe(Money.Of(6m));
        order.TotalAmount.ShouldBe(Money.Of(78m));
        order.Version.ShouldBe(2);
    }

    [Fact]
    public void Creation_does_not_record_change_events_but_edits_do_with_the_new_version()
    {
        var order = Pf(quantity: 1, addonQuantity: 0);
        order.ChangeEvents.ShouldBeEmpty();

        order.ChangeItem(order.Items[0].Id, default, new Optional<string?>("Sem salada"), By, Guid.NewGuid, Now);

        var change = order.ChangeEvents.ShouldHaveSingleItem();
        change.ChangeKind.ShouldBe(OrderChangeKind.ItemNotesChanged);
        change.OrderVersion.ShouldBe(2);
        change.AffectsProduction.ShouldBeTrue();
    }

    [Fact]
    public void Removing_the_last_item_is_rejected()
    {
        var order = Pf(quantity: 1, addonQuantity: 0);

        var ex = Should.Throw<BusinessException>(() => order.RemoveItem(order.Items[0].Id, By, Guid.NewGuid, Now));

        ex.ErrorCode.ShouldBe(ErrorCodes.ValidationFailed);
        order.Items[0].Status.ShouldBe(LineStatus.Active);
    }

    [Fact]
    public void Kilo_order_has_no_production_and_rejects_status_changes()
    {
        var order = Order.Create(
            Guid.NewGuid(), Guid.NewGuid(),
            new NewOrder(OrderType.Kilo, FulfillmentType.DineIn, null, "Mesa 3", null, null, Money.Zero, "3", "47", null, [],
                [new NewWeighing(Grams.Of(452), Money.Of(59.90m))]),
            Guid.NewGuid(), new DateOnly(2026, 10, 27), 1, By, Guid.NewGuid, Now);

        order.ProductionStatus.ShouldBeNull();
        order.TotalAmount.ShouldBe(Money.Of(27.07m));
        order.Label.ShouldBe("Mesa 3");
        Should.Throw<BusinessException>(() => order.ChangeProductionStatus(ProductionStatus.Completed, Now));
    }

    [Fact]
    public void Cancel_needs_a_reason_and_makes_the_order_read_only()
    {
        var order = Pf(quantity: 1, addonQuantity: 0);

        Should.Throw<BusinessException>(() => order.Cancel("ops", By, Guid.NewGuid, Now)).ErrorCode.ShouldBe(ErrorCodes.ValidationFailed);
        order.Cancel("Cliente desistiu", By, Guid.NewGuid, Now);

        order.CancellationStatus.ShouldBe(CancellationStatus.Canceled);
        order.ChangeEvents.ShouldHaveSingleItem().ChangeKind.ShouldBe(OrderChangeKind.OrderCanceled);
        Should.Throw<BusinessException>(() => order.ChangeItem(order.Items[0].Id, new Optional<int>(2), default, By, Guid.NewGuid, Now))
            .ErrorCode.ShouldBe(ErrorCodes.OrderCanceled);
    }

    private static Order Pf(int quantity, int addonQuantity)
    {
        var addons = addonQuantity > 0
            ? new[] { new NewItem(OrderItemKind.Addon, null, Guid.NewGuid(), null, addonQuantity, Money.Of(2m), "Ovo frito", null, true, null, [], []) }
            : [];
        var pf = new NewItem(OrderItemKind.Pf, null, Guid.NewGuid(), null, quantity, Money.Of(24m), "PF Frango", null, true, null, [], addons);
        return Order.Create(
            Guid.NewGuid(), Guid.NewGuid(),
            new NewOrder(OrderType.Pf, FulfillmentType.DineIn, null, null, null, null, Money.Zero, "5", null, null, [pf], []),
            Guid.NewGuid(), new DateOnly(2026, 10, 27), 1, By, Guid.NewGuid, Now);
    }
}
