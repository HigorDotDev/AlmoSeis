using AlmoSeis.Modules.Closing.Domain;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using Shouldly;

namespace AlmoSeis.UnitTests.Modules;

public sealed class ClosingCalculatorTests
{
    private static Money M(decimal v) => Money.Of(v);

    /// Exemplo do épico reproduzido em 06 §6.11.
    [Fact]
    public void Epic_example_closes()
    {
        var inputs = new ClosingInputs(
            M(100m),
            [
                new("MARMITA", false, 41, M(1250m), M(85m), M(150m)),
                new("PF", false, 30, M(870m), M(0m), M(70m)),
                new("KILO", false, 25, M(1530m), M(0m), M(0m)),
                new("MARMITA", true, 1, M(25m), M(0m), M(25m)),
            ],
            [
                new("MARMITA", "CASH", false, 15, M(300m)),
                new("MARMITA", "PIX", false, 20, M(765m)),
                new("MARMITA", "PIX", true, 1, M(35m)),
                new("PF", "CASH", false, 10, M(250m)),
                new("PF", "PIX", false, 10, M(350m)),
                new("PF", "CARD", false, 8, M(200m)),
                new("KILO", "CASH", false, 13, M(300m)),
                new("KILO", "PIX", false, 13, M(530m)),
                new("KILO", "CARD", false, 13, M(700m)),
            ],
            [],
            M(0m),
            M(200m));

        var f = ClosingCalculator.Compute(inputs);

        f.OrderCount.ShouldBe(96);
        f.TotalSold.ShouldBe(M(3650m));
        f.DeliveryFeesTotal.ShouldBe(M(85m));
        f.TotalReceived.ShouldBe(M(3430m));
        f.ReceivedFromPreviousDays.ShouldBe(M(35m));
        f.TotalPending.ShouldBe(M(220m));
        f.CanceledCount.ShouldBe(1);
        f.CanceledTotal.ShouldBe(M(25m));
        f.ByOrderType.Single(l => l.Key == "MARMITA").Received.ShouldBe(M(1100m));
        f.ByPaymentMethod.Single(l => l.Key == "CASH").Received.ShouldBe(M(850m));
        f.ByPaymentMethod.Single(l => l.Key == "PIX").Received.ShouldBe(M(1680m));
        f.ByPaymentMethod.Single(l => l.Key == "CARD").Received.ShouldBe(M(900m));
        f.ExpectedCash.ShouldBe(M(750m));
    }

    [Fact]
    public void Cash_reversal_leaves_the_expected_cash_and_all_keys_are_present()
    {
        var f = ClosingCalculator.Compute(new ClosingInputs(M(80m), [], [], [new("PF", "CASH", 1, M(35m))], M(0m), M(0m)));

        f.ExpectedCash.ShouldBe(M(45m));
        f.ReversedTotal.ShouldBe(M(35m));
        f.ByOrderType.Select(l => l.Key).ShouldBe(["MARMITA", "PF", "KILO", "A_LA_CARTE"]);
        f.ByPaymentMethod.Select(l => l.Key).ShouldBe(["CASH", "PIX", "CARD"]);
    }

    [Fact]
    public void Difference_requires_justification()
    {
        var f = ClosingCalculator.Compute(new ClosingInputs(M(100m), [], [], [], M(0m), M(0m)));

        Should.Throw<BusinessException>(() =>
                DailyClosing.Finalize(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 27), f, M(90m), " ", Guid.NewGuid(), Guid.NewGuid, DateTimeOffset.UnixEpoch))
            .ErrorCode.ShouldBe(ErrorCodes.JustificationRequired);
        DailyClosing.Finalize(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 27), f, M(100m), null, Guid.NewGuid(), Guid.NewGuid, DateTimeOffset.UnixEpoch)
            .Lines.Count.ShouldBe(7); // 4 modalidades (com A_LA_CARTE) + 3 formas
    }
    /// "Vendido pelo site" (ADR-0057 item 12): só pedidos ativos com `price_list = 'SITE'`, parte do vendido; a linha
    /// `A_LA_CARTE` sai como as outras modalidades (Q-311: uma linha).
    [Fact]
    public void Site_sold_total_and_a_la_carte_line()
    {
        var f = ClosingCalculator.Compute(new ClosingInputs(
            M(0m),
            [
                new("MARMITA", false, 3, M(120m), M(10m), M(0m), M(45m)),
                new("A_LA_CARTE", false, 2, M(109m), M(0m), M(36m)),
                new("A_LA_CARTE", true, 1, M(22m), M(0m), M(22m), M(22m)),
            ],
            [new("A_LA_CARTE", "PIX", false, 1, M(73m))],
            [],
            M(0m),
            M(0m)));

        f.SiteSoldTotal.ShouldBe(M(45m));
        f.TotalSold.ShouldBe(M(229m));
        var aLaCarte = f.ByOrderType.Single(l => l.Key == "A_LA_CARTE");
        aLaCarte.Count.ShouldBe(2);
        aLaCarte.Sold.ShouldBe(M(109m));
        aLaCarte.Received.ShouldBe(M(73m));
        aLaCarte.Pending.ShouldBe(M(36m));
        DailyClosing.Finalize(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 11, 22), f, M(0m), null, Guid.NewGuid(), Guid.NewGuid, DateTimeOffset.UnixEpoch)
            .SiteSoldTotal.ShouldBe(M(45m));
    }
}
