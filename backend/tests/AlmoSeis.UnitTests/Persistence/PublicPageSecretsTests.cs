using AlmoSeis.Infrastructure.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;

namespace AlmoSeis.UnitTests.Persistence;

/// I2 da revisão do EP-002: fora de Development a API não sobe sem os segredos da página nem com a chave de teste do
/// Turnstile; o token de acompanhamento é derivado do pedido com o segredo.
public sealed class PublicPageSecretsTests
{
    private const string SecretA = "segredo-a-com-pelo-menos-32-caracteres";
    private const string SecretB = "segredo-b-com-pelo-menos-32-caracteres";

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void Page_secrets_are_required_outside_development(string environment)
    {
        var validator = new PublicPageOptionsValidator(new FakeEnvironment(environment));
        validator.Validate(null, new PublicPageOptions()).Failed.ShouldBeTrue();
        validator.Validate(null, new PublicPageOptions { IpHashSecret = SecretA }).Failed.ShouldBeTrue();
        validator.Validate(null, new PublicPageOptions { IpHashSecret = "curto", TrackingTokenSecret = SecretB }).Failed.ShouldBeTrue();
        validator.Validate(null, new PublicPageOptions { IpHashSecret = SecretA, TrackingTokenSecret = SecretA }).Failed.ShouldBeTrue("segredos diferentes");
        validator.Validate(null, new PublicPageOptions { IpHashSecret = SecretA, TrackingTokenSecret = SecretB }).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Development_may_run_without_page_secrets() =>
        new PublicPageOptionsValidator(new FakeEnvironment("Development")).Validate(null, new PublicPageOptions()).Succeeded.ShouldBeTrue();

    [Theory]
    [InlineData("1x0000000000000000000000000000000AA")]
    [InlineData("2x0000000000000000000000000000000AA")]
    [InlineData("3x0000000000000000000000000000000AA")]
    public void Turnstile_test_keys_only_in_development(string key)
    {
        new TurnstileOptionsValidator(new FakeEnvironment("Production")).Validate(null, new TurnstileOptions { SecretKey = key }).Failed.ShouldBeTrue();
        new TurnstileOptionsValidator(new FakeEnvironment("Staging")).Validate(null, new TurnstileOptions { SecretKey = key }).Failed.ShouldBeTrue();
        new TurnstileOptionsValidator(new FakeEnvironment("Development")).Validate(null, new TurnstileOptions { SecretKey = key }).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Turnstile_requires_a_key_when_enabled_and_nothing_when_disabled()
    {
        var production = new TurnstileOptionsValidator(new FakeEnvironment("Production"));
        production.Validate(null, new TurnstileOptions { SecretKey = "" }).Failed.ShouldBeTrue();
        production.Validate(null, new TurnstileOptions { Enabled = false }).Succeeded.ShouldBeTrue();
        production.Validate(null, new TurnstileOptions { SecretKey = "0x4AAAAAAAreal-secret-key" }).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Tracking_token_is_derived_from_the_order_and_the_secret()
    {
        static ITrackingTokenIssuer Issuer(string secret) => new TrackingTokenIssuer(Options.Create(new PublicPageOptions { TrackingTokenSecret = secret }));
        var tenant = Guid.CreateVersion7();
        var order = Guid.CreateVersion7();

        var token = Issuer(SecretA).For(tenant, order);
        TrackingTokens.LooksValid(token).ShouldBeTrue();
        Issuer(SecretA).For(tenant, order).ShouldBe(token, "o reenvio recalcula o mesmo token");
        Issuer(SecretB).For(tenant, order).ShouldNotBe(token);
        Issuer(SecretA).For(tenant, Guid.CreateVersion7()).ShouldNotBe(token);
        Issuer(SecretA).For(Guid.CreateVersion7(), order).ShouldNotBe(token);
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "AlmoSeis.Api";

        public string ContentRootPath { get; set; } = ".";

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
