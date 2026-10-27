using AlmoSeis.Modules.Closing.Contracts;
using AlmoSeis.Modules.Closing.Domain;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using Shouldly;

namespace AlmoSeis.UnitTests.Modules;

public sealed class BusinessDayDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 2, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Open_starts_at_version_one_with_the_given_local_date()
    {
        // 02:30 UTC já é 28/10, mas em São Paulo ainda é 27/10: a data vem de quem abre, não do UTC.
        var day = BusinessDay.Open(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 27), Money.Of(100m), Guid.NewGuid(), Now);

        day.Status.ShouldBe(BusinessDayStatus.Open);
        day.BusinessDate.ShouldBe(new DateOnly(2026, 10, 27));
        day.Version.ShouldBe(1);
        day.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<BusinessDayOpened>();
    }

    [Fact]
    public void Negative_opening_balance_is_rejected()
    {
        var ex = Should.Throw<BusinessException>(() =>
            BusinessDay.Open(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 27), Money.Of(-0.01m), Guid.NewGuid(), Now));

        ((IReadOnlyDictionary<string, string[]>)ex.Extensions["errors"]!).Keys.ShouldBe(["openingBalance"]);
    }
}
