using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlmoSeis.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Catalog;

/// Imagem de produto no R2 (06 §6.5, ADR-0037): URL pré-assinada e conferência do objeto antes de aceitar a chave.
[Collection(nameof(PostgresCollection))]
public sealed class ProductImageFlowTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Upload_url_is_a_presigned_put_on_the_r2_bucket()
    {
        // Credenciais fictícias: assinar a URL é local, sem chamada ao R2.
        using var api = new ApiFactory(db, s => s.Configure<R2Options>(o =>
        {
            o.AccountId = "contateste";
            o.Bucket = "almoseis-images-test";
            o.AccessKeyId = "AKIATESTE";
            o.SecretAccessKey = "segredo-de-teste";
        }));
        var owner = await api.SignUpAsync();
        var client = api.Authenticated(owner.AccessToken());
        var productId = await ProductAsync(client, api);

        var upload = await (await client.PostAsJsonAsync($"/api/v1/products/{productId}/image-upload", new { contentType = "image/jpeg", sizeBytes = 184320 }, Ct))
            .ExpectAsync(HttpStatusCode.Created, api);

        var key = upload.GetProperty("storageKey").GetString()!;
        key.ShouldStartWith($"t/{owner.TenantId()}/p/{productId}-");
        key.ShouldEndWith(".jpg");
        var url = new Uri(upload.GetProperty("uploadUrl").GetString()!);
        url.Host.ShouldBe("contateste.r2.cloudflarestorage.com");
        url.AbsolutePath.ShouldBe($"/almoseis-images-test/{key}"); // path-style, como o R2 exige
        url.Query.ShouldContain("X-Amz-Signature=");
        url.Query.ShouldContain("X-Amz-Expires=900");
        upload.GetProperty("method").GetString().ShouldBe("PUT");
        upload.GetProperty("headers").GetProperty("Content-Type").GetString().ShouldBe("image/jpeg");
        upload.GetProperty("expiresAt").GetDateTimeOffset().ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));

        // Outro pedido, outra chave: a imagem atual vale até o PATCH trocar.
        var again = await (await client.PostAsJsonAsync($"/api/v1/products/{productId}/image-upload", new { contentType = "image/webp", sizeBytes = 1000 }, Ct))
            .ExpectAsync(HttpStatusCode.Created, api);
        again.GetProperty("storageKey").GetString()!.ShouldEndWith(".webp");
        again.GetProperty("storageKey").GetString().ShouldNotBe(key);

        (await (await client.PostAsJsonAsync($"/api/v1/products/{productId}/image-upload", new { contentType = "image/gif", sizeBytes = 1000 }, Ct))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, api)).GetProperty("errors").TryGetProperty("contentType", out _).ShouldBeTrue();
        (await (await client.PostAsJsonAsync($"/api/v1/products/{productId}/image-upload", new { contentType = "image/png", sizeBytes = 3 * 1024 * 1024 }, Ct))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, api)).GetProperty("errors").TryGetProperty("sizeBytes", out _).ShouldBeTrue();
        await (await client.PostAsJsonAsync($"/api/v1/products/{Guid.NewGuid()}/image-upload", new { contentType = "image/png", sizeBytes = 10 }, Ct))
            .ExpectAsync(HttpStatusCode.NotFound, api);

        var attendant = api.Authenticated(await api.EmployeeTokenAsync(db, owner, "ATTENDANT"));
        await (await attendant.PostAsJsonAsync($"/api/v1/products/{productId}/image-upload", new { contentType = "image/png", sizeBytes = 10 }, Ct))
            .ExpectAsync(HttpStatusCode.Forbidden, api);
    }

    [Fact]
    public async Task Patch_accepts_the_key_only_after_a_valid_upload()
    {
        var storage = new FakeStorage();
        using var api = new ApiFactory(db, s => s.AddSingleton<IObjectStorage>(storage));
        var owner = await api.SignUpAsync();
        var client = api.Authenticated(owner.AccessToken());
        var productId = await ProductAsync(client, api);

        var key = (await (await client.PostAsJsonAsync($"/api/v1/products/{productId}/image-upload", new { contentType = "image/png", sizeBytes = 5000 }, Ct))
            .ExpectAsync(HttpStatusCode.Created, api)).GetProperty("storageKey").GetString()!;

        // Ainda não enviado.
        (await (await client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/products/{productId}", new { imageStorageKey = key }, 1))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, api)).GetProperty("errors").TryGetProperty("imageStorageKey", out _).ShouldBeTrue();

        // Enviado maior que o permitido (a URL pré-assinada não impede).
        storage.Objects[key] = new StoredObject(3 * 1024 * 1024, "image/png");
        await (await client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/products/{productId}", new { imageStorageKey = key }, 1))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, api);

        storage.Objects[key] = new StoredObject(5000, "image/png");
        var product = await (await client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/products/{productId}", new { imageStorageKey = key }, 1))
            .ExpectAsync(HttpStatusCode.OK, api);
        product.GetProperty("imageUrl").GetString().ShouldBe($"https://img.teste/{key}");

        // Chave de outro produto continua recusada antes de olhar o bucket.
        (await (await client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/products/{productId}", new { imageStorageKey = $"t/{owner.TenantId()}/p/{Guid.NewGuid()}-x.png" }, 2))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, api)).GetProperty("errors").GetProperty("imageStorageKey")[0].GetString()
            .ShouldBe("A imagem não pertence a este produto.");
    }

    private static async Task<string> ProductAsync(HttpClient client, ApiFactory api) =>
        (await (await client.PostAsJsonAsync("/api/v1/products",
                new { kind = "BEVERAGE", name = "Suco de laranja", description = (string?)null, category = (string?)null, defaultPrice = "7.00", requiresPreparation = (bool?)null }, Ct))
            .ExpectAsync(HttpStatusCode.Created, api)).GetProperty("id").GetString()!;

    private sealed class FakeStorage : IObjectStorage
    {
        public ConcurrentDictionary<string, StoredObject> Objects { get; } = new();

        public bool IsConfigured => true;

        public string? PublicUrl(string? storageKey) => storageKey is null ? null : $"https://img.teste/{storageKey}";

        public UploadUrl CreateUploadUrl(string storageKey, string contentType) =>
            new($"https://r2.teste/{storageKey}", "PUT", new Dictionary<string, string> { ["Content-Type"] = contentType }, DateTimeOffset.UtcNow.AddMinutes(15));

        public Task<StoredObject?> GetAsync(string storageKey, CancellationToken cancellationToken) =>
            Task.FromResult(Objects.TryGetValue(storageKey, out var o) ? o : null);
    }
}
