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

    /// O SDK calcula `X-Amz-Expires` = `Expires` − o relógio dele, em segundos arredondados (`Convert.ToInt64`). Se o
    /// instante do `TimeProvider` ficar mais de 0,5 s atrás do relógio do SDK na assinatura (a criação preguiçosa do
    /// cliente na primeira chamada, uma pausa do GC), saía `899`. Aqui o relógio da aplicação chega a 3 s atrasado.
    [Theory]
    [InlineData(0)]
    [InlineData(700)]
    [InlineData(950)]
    [InlineData(3000)]
    public void Presigned_upload_url_expires_in_exactly_the_configured_minutes(int clockLagMilliseconds)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new R2Options
        {
            AccountId = "conta",
            Bucket = "almoseis-images-test",
            AccessKeyId = "AKIATESTE",
            SecretAccessKey = "segredo-de-teste",
        });
        using var storage = new R2ObjectStorage(options, new LaggingClock(TimeSpan.FromMilliseconds(clockLagMilliseconds)));

        var upload = storage.CreateUploadUrl("t/x/p/y-1.jpg", "image/jpeg");

        upload.Url.ShouldContain("X-Amz-Expires=900");
    }

    private sealed class LaggingClock(TimeSpan lag) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() - lag;
    }
}
