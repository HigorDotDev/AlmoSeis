using System.Text.Json.Nodes;
using AlmoSeis.Api.Infrastructure;
using Shouldly;

namespace AlmoSeis.UnitTests.Modules;

public sealed class RealtimeGroupsTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    [Theory]
    [InlineData("OrderCreated", "PF", "orders,production")]
    [InlineData("OrderCreated", "KILO", "orders")]
    [InlineData("OrderPaymentRegistered", null, "orders,closing")]
    [InlineData("OrderPaymentConfirmed", null, "orders,production,closing")]
    [InlineData("MenuUpdated", null, "catalog")]
    [InlineData("BusinessDayOpened", null, "tenant")]
    [InlineData("Unknown", null, "")]
    public void Events_go_to_the_groups_of_06(string eventType, string? orderType, string expected)
    {
        var envelope = new JsonObject { ["data"] = new JsonObject { ["orderType"] = orderType } };

        var groups = RealtimeGroups.ForEvent(Tenant, eventType, envelope)
            .Select(g => g == $"tenant:{Tenant}" ? "tenant" : g.Split(':')[^1]);

        string.Join(',', groups).ShouldBe(expected);
    }
}
