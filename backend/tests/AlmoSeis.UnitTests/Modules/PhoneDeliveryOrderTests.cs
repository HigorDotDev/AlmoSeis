using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Ordering.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using Shouldly;

namespace AlmoSeis.UnitTests.Modules;

/// Delivery por telefone (EP-003 fatia 4, ADR-0059, Q-312, Q-316): nome e telefone, forma prevista e "troco para" no
/// pedido do app. Exemplos do RF-312 (57,00 com troco para 100,00) e do ADR-0059 item 12 (47,00 com troco para 50,00).
public sealed class PhoneDeliveryOrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 19, 0, 0, TimeSpan.Zero);
    private static readonly Guid By = Guid.NewGuid();
    private const string Phone = "+5534999991234";
    private static readonly DeliveryAddressDto Address = new("Rua das Flores", "123", null, "Centro", null);

    [Theory]
    [InlineData(OrderType.Marmita, FulfillmentType.Delivery, true, true)]
    [InlineData(OrderType.Marmita, FulfillmentType.Pickup, true, false)]
    [InlineData(OrderType.ALaCarte, FulfillmentType.Delivery, true, true)]
    [InlineData(OrderType.ALaCarte, FulfillmentType.Pickup, true, true)]
    [InlineData(OrderType.ALaCarte, FulfillmentType.DineIn, false, false)]
    [InlineData(OrderType.Pf, FulfillmentType.DineIn, false, false)]
    [InlineData(OrderType.Kilo, FulfillmentType.DineIn, false, false)]
    public void Contact_requirements_follow_q312(OrderType type, FulfillmentType fulfillment, bool name, bool phone) =>
        Order.ContactRequired(type, fulfillment).ShouldBe((name, phone));

    [Fact]
    public void Marmita_delivery_without_phone_is_refused() =>
        Fields(() => Create(OrderType.Marmita, FulfillmentType.Delivery, phone: null, method: ExpectedPaymentMethod.Pix)).ShouldBe(["customerPhone"]);

    [Fact]
    public void Marmita_pickup_at_the_counter_keeps_the_phone_optional()
    {
        var order = Create(OrderType.Marmita, FulfillmentType.Pickup, phone: null);

        order.CustomerPhone.ShouldBeNull();
        order.ExpectedPaymentMethod.ShouldBeNull();
    }

    [Theory]
    [InlineData(FulfillmentType.Delivery)]
    [InlineData(FulfillmentType.Pickup)]
    public void A_la_carte_delivery_and_pickup_require_name_and_phone(FulfillmentType fulfillment)
    {
        var method = fulfillment == FulfillmentType.Delivery ? ExpectedPaymentMethod.Cash : (ExpectedPaymentMethod?)null;
        Fields(() => Create(OrderType.ALaCarte, fulfillment, name: null, method: method)).ShouldBe(["customerName"]);
        Fields(() => Create(OrderType.ALaCarte, fulfillment, phone: " ", method: method)).ShouldBe(["customerPhone"]);
    }

    [Fact]
    public void A_la_carte_dine_in_tab_needs_no_contact() =>
        Create(OrderType.ALaCarte, FulfillmentType.DineIn, name: null, phone: null).FulfillmentStatus.ShouldBeNull();

    [Theory]
    [InlineData(OrderType.Marmita)]
    [InlineData(OrderType.ALaCarte)]
    public void Delivery_requires_the_expected_payment_and_pickup_does_not(OrderType type)
    {
        Fields(() => Create(type, FulfillmentType.Delivery, method: null)).ShouldBe(["expectedPaymentMethod"]);
        Create(type, FulfillmentType.Pickup, method: null).ExpectedPaymentMethod.ShouldBeNull();
        Create(type, FulfillmentType.Pickup, method: ExpectedPaymentMethod.Card).ExpectedPaymentMethod.ShouldBe(ExpectedPaymentMethod.Card);
    }

    [Fact]
    public void Dine_in_with_expected_payment_is_refused() =>
        Fields(() => Create(OrderType.ALaCarte, FulfillmentType.DineIn, name: null, phone: null, method: ExpectedPaymentMethod.Pix))
            .ShouldBe(["expectedPaymentMethod"]);

    /// RF-312: combo 32,00 + açaí 20,00 + taxa manual 5,00 = 57,00; troco para 100,00 → o motoboy leva 43,00.
    [Fact]
    public void Change_for_100_on_a_57_order_is_recorded_and_nothing_is_paid()
    {
        var order = Create(OrderType.ALaCarte, FulfillmentType.Delivery, items: [32m, 20m], fee: 5m,
            method: ExpectedPaymentMethod.Cash, changeFor: 100m);

        order.TotalAmount.ShouldBe(Money.Of(57m));
        order.ChangeFor.ShouldBe(Money.Of(100m));
        (order.ChangeFor!.Value - order.TotalAmount).ShouldBe(Money.Of(43m));
        order.PaidAmount.ShouldBe(Money.Zero);
        order.Balance.ShouldBe(Money.Of(57m));
        order.IsPaid.ShouldBeFalse();
    }

    /// ADR-0059 item 12: 47,00 com troco para 50,00 passa; para 40,00, não; exatamente o total passa.
    [Theory]
    [InlineData(50, true)]
    [InlineData(47, true)]
    [InlineData(46.99, false)]
    [InlineData(40, false)]
    public void Change_for_must_be_at_least_the_total(decimal changeFor, bool accepted)
    {
        Order Act() => Create(OrderType.Marmita, FulfillmentType.Delivery, items: [42m], fee: 5m, method: ExpectedPaymentMethod.Cash, changeFor: changeFor);
        if (accepted)
        {
            Act().ChangeFor.ShouldBe(Money.Of(changeFor));
        }
        else
        {
            Fields(() => Act()).ShouldBe(["changeFor"]);
        }
    }

    [Theory]
    [InlineData(ExpectedPaymentMethod.Pix)]
    [InlineData(ExpectedPaymentMethod.Card)]
    public void Change_for_only_with_cash(ExpectedPaymentMethod method) =>
        Fields(() => Create(OrderType.Marmita, FulfillmentType.Delivery, method: method, changeFor: 100m)).ShouldBe(["changeFor"]);

    [Fact]
    public void Change_for_without_method_on_pickup_is_refused() =>
        Fields(() => Create(OrderType.Marmita, FulfillmentType.Pickup, method: null, changeFor: 100m)).ShouldBe(["changeFor"]);

    /// Q-315 no agregado: a taxa digitada na criação vale e grava a origem que o handler passou.
    [Fact]
    public void Typed_fee_at_creation_is_stored_as_manual()
    {
        var order = Create(OrderType.ALaCarte, FulfillmentType.Delivery, fee: 7m, method: ExpectedPaymentMethod.Pix, source: DeliveryFeeSource.Manual);

        order.DeliveryFee.ShouldBe(Money.Of(7m));
        order.DeliveryFeeSource.ShouldBe(DeliveryFeeSource.Manual);
        order.TotalAmount.ShouldBe(Money.Of(27m));
    }

    [Fact]
    public void Moving_a_la_carte_to_the_dining_room_clears_the_expected_payment()
    {
        var order = Create(OrderType.ALaCarte, FulfillmentType.Pickup, method: ExpectedPaymentMethod.Cash, changeFor: 50m);

        order.UpdateDetails(Changes(fulfillment: FulfillmentType.DineIn), Money.Of(5m), By, Guid.NewGuid, Now);

        order.FulfillmentType.ShouldBe(FulfillmentType.DineIn);
        order.ExpectedPaymentMethod.ShouldBeNull();
        order.ChangeFor.ShouldBeNull();
    }

    [Fact]
    public void Patch_cannot_clear_the_phone_of_a_phone_delivery_nor_turn_a_nameless_tab_into_a_delivery()
    {
        var delivery = Create(OrderType.ALaCarte, FulfillmentType.Delivery, method: ExpectedPaymentMethod.Pix);
        Fields(() => delivery.UpdateDetails(Changes(phone: new Optional<string?>(null)), Money.Of(5m), By, Guid.NewGuid, Now))
            .ShouldBe(["customerPhone"]);

        var tab = Create(OrderType.ALaCarte, FulfillmentType.DineIn, name: null, phone: null);
        Fields(() => tab.UpdateDetails(Changes(fulfillment: FulfillmentType.Pickup), Money.Of(5m), By, Guid.NewGuid, Now))
            .ShouldBe(["customerName"]);
    }

    private static OrderDetailsChanges Changes(FulfillmentType? fulfillment = null, Optional<string?> phone = default) => new(
        default, phone, fulfillment is { } f ? new Optional<FulfillmentType>(f) : default, default, default, default, default, default);

    private static Order Create(
        OrderType type, FulfillmentType fulfillment, string? name = "Carla Souza", string? phone = Phone, ExpectedPaymentMethod? method = null,
        decimal? changeFor = null, decimal[]? items = null, decimal fee = 5m, DeliveryFeeSource source = DeliveryFeeSource.FixedDefault)
    {
        var lines = (items ?? [20m]).Select(price => type == OrderType.Marmita
                ? new NewItem(OrderItemKind.Marmita, null, null, Guid.NewGuid(), 1, Money.Of(price), "Marmita M", "Frango", true, null, [], [])
                : new NewItem(OrderItemKind.FixedDish, null, null, null, 1, Money.Of(price), "X-Burguer", null, true, null, [], [], new FixedMenuRefs(DishId: Guid.NewGuid())))
            .ToList();
        var delivery = fulfillment == FulfillmentType.Delivery;
        return Order.Create(
            Guid.NewGuid(), Guid.NewGuid(),
            new NewOrder(
                type, fulfillment, null, name, phone, delivery ? Address : null, delivery ? Money.Of(fee) : Money.Zero,
                fulfillment == FulfillmentType.DineIn ? "5" : null, null, null, lines, [],
                delivery ? new DeliveryPricing(source) : null, method, changeFor is { } c ? Money.Of(c) : null),
            Guid.NewGuid(), new DateOnly(2026, 11, 22), 1, By, Guid.NewGuid, Now);
    }

    private static IEnumerable<string> Fields(Action action)
    {
        var ex = Should.Throw<BusinessException>(action);
        ex.ErrorCode.ShouldBe(ErrorCodes.ValidationFailed);
        return ((IReadOnlyDictionary<string, string[]>)ex.Extensions["errors"]!).Keys;
    }
}
