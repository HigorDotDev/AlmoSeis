using AlmoSeis.Modules.Identity.Domain;
using Shouldly;

namespace AlmoSeis.UnitTests.Modules;

public sealed class IdentityPolicyTests
{
    [Theory]
    [InlineData("4821", true)]
    [InlineData("907135", true)]
    [InlineData("1234", false)]
    [InlineData("4321", false)]
    [InlineData("0000", false)]
    [InlineData("123", false)]
    [InlineData("1234567", false)]
    [InlineData("12a4", false)]
    public void Pin_policy_rejects_trivial_or_malformed_pins(string pin, bool ok) =>
        (PinPolicy.Problem(pin) is null).ShouldBe(ok);

    [Fact]
    public void Invitation_codes_use_the_unambiguous_alphabet()
    {
        var codes = Enumerable.Range(0, 2000).Select(_ => InvitationCodeGenerator.New()).ToList();

        codes.ShouldAllBe(c => System.Text.RegularExpressions.Regex.IsMatch(c, "^[A-HJ-NP-Z2-9]{6}$"));
        codes.Distinct().Count().ShouldBeGreaterThan(1990);
    }
}
