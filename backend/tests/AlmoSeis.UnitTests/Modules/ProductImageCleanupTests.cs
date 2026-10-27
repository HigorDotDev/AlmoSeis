using System.Text.Json;
using AlmoSeis.Infrastructure.Storage;
using AlmoSeis.Modules.Catalog.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace AlmoSeis.UnitTests.Modules;

public sealed class ProductImageCleanupTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Product = Guid.NewGuid();

    [Theory]
    [InlineData(true, "t/{tenant}/p/{product}-20261027-ab12cd.png")]
    [InlineData(false, "t/{tenant}/p/{other}-20261027-ab12cd.png")]
    [InlineData(false, "t/{other}/p/{product}-20261027-ab12cd.png")]
    [InlineData(false, "occurrences/abc/foto.jpg")]
    public async Task Only_keys_of_the_product_are_deleted(bool deleted, string template)
    {
        var key = template.Replace("{tenant}", Tenant.ToString()).Replace("{product}", Product.ToString()).Replace("{other}", Guid.NewGuid().ToString());
        var storage = new RecordingStorage();
        var envelope = JsonSerializer.SerializeToElement(new { entityId = Product, data = new { storageKey = key } });

        await new ProductImageCleanup(storage, NullLogger<ProductImageCleanup>.Instance).HandleAsync(Tenant, envelope, CancellationToken.None);

        storage.Deleted.Contains(key).ShouldBe(deleted);
    }

    private sealed class RecordingStorage : IObjectStorage
    {
        public List<string> Deleted { get; } = [];

        public bool IsConfigured => true;

        public string? PublicUrl(string? storageKey) => storageKey;

        public UploadUrl CreateUploadUrl(string storageKey, string contentType) => throw new NotSupportedException();

        public Task<StoredObject?> GetAsync(string storageKey, CancellationToken cancellationToken) => Task.FromResult<StoredObject?>(null);

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
        {
            Deleted.Add(storageKey);
            return Task.CompletedTask;
        }
    }
}
