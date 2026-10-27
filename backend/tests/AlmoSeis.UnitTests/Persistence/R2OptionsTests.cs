using AlmoSeis.Infrastructure.Storage;
using Shouldly;

namespace AlmoSeis.UnitTests.Persistence;

public sealed class R2OptionsTests
{
    [Theory]
    [InlineData("https://conta.r2.cloudflarestorage.com", null, "https://conta.r2.cloudflarestorage.com")]
    [InlineData("https://conta.r2.cloudflarestorage.com/almoseis-images-homolog", null, "https://conta.r2.cloudflarestorage.com")]
    [InlineData("https://conta.r2.cloudflarestorage.com/", null, "https://conta.r2.cloudflarestorage.com")]
    [InlineData(null, "conta", "https://conta.r2.cloudflarestorage.com")]
    [InlineData(null, null, null)]
    public void Endpoint_is_only_scheme_and_host(string? serviceUrl, string? accountId, string? expected) =>
        new R2Options { ServiceUrl = serviceUrl, AccountId = accountId }.Endpoint.ShouldBe(expected);
}
