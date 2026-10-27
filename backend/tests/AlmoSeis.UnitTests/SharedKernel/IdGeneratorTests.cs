using AlmoSeis.SharedKernel.Application;
using Shouldly;

namespace AlmoSeis.UnitTests.SharedKernel;

public sealed class IdGeneratorTests
{
    [Fact]
    public void Ids_in_the_same_millisecond_keep_creation_order()
    {
        var generator = new IdGenerator(new FixedTime(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero)));

        var ids = Enumerable.Range(0, 5000).Select(_ => generator.New()).ToList();

        ids.Select(id => id.ToString()).ShouldBe(ids.Select(id => id.ToString()).Order(StringComparer.Ordinal).ToList());
        ids.Distinct().Count().ShouldBe(ids.Count);
        ids.ShouldAllBe(id => id.Version == 7 && (id.Variant & 0b1100) == 0b1000);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
