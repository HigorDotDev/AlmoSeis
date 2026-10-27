using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Catalog;

/// Produtos e tamanhos (06 §6.5) contra a API real, com RLS, If-Match/ETag, feed e outbox.
[Collection(nameof(PostgresCollection))]
public sealed class CatalogFlowTests(PostgresFixture db) : IDisposable
{
    private readonly ApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Create_and_get_product_with_etag_and_location()
    {
        var owner = await _api.SignUpAsync();
        var client = _api.Authenticated(owner.AccessToken());

        var response = await client.PostAsJsonAsync("/api/v1/products", Beverage("Coca-Cola 350ml", "6.00"), Ct);
        var product = await response.ExpectAsync(HttpStatusCode.Created, _api);

        response.Headers.ETag!.Tag.ShouldBe("\"1\"");
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/products/{product.GetProperty("id").GetString()}");
        product.GetProperty("kind").GetString().ShouldBe("BEVERAGE");
        product.GetProperty("defaultPrice").GetString().ShouldBe("6.00");
        product.GetProperty("requiresPreparation").GetBoolean().ShouldBeFalse();
        product.GetProperty("imageUrl").ValueKind.ShouldBe(JsonValueKind.Null);
        product.GetProperty("version").GetInt32().ShouldBe(1);

        var get = await client.GetAsync($"/api/v1/products/{product.GetProperty("id").GetString()}", Ct);
        (await get.ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("name").GetString().ShouldBe("Coca-Cola 350ml");
        get.Headers.ETag!.Tag.ShouldBe("\"1\"");
    }

    [Fact]
    public async Task Requires_preparation_defaults_by_kind()
    {
        var client = _api.Authenticated((await _api.SignUpAsync()).AccessToken());

        var dish = await (await client.PostAsJsonAsync("/api/v1/products", Product("MAIN_DISH", "Frango grelhado", "22.00"), Ct))
            .ExpectAsync(HttpStatusCode.Created, _api);

        dish.GetProperty("requiresPreparation").GetBoolean().ShouldBeTrue();
    }

    [Theory]
    [InlineData("SIDE_DISH", "5.00", "Acompanhamentos não têm preço próprio.")]
    [InlineData("BEVERAGE", null, "Bebidas e adicionais precisam de preço.")]
    [InlineData("ADDON", null, "Bebidas e adicionais precisam de preço.")]
    public async Task Price_must_match_kind(string kind, string? price, string message)
    {
        var client = _api.Authenticated((await _api.SignUpAsync()).AccessToken());

        var problem = await (await client.PostAsJsonAsync("/api/v1/products", Product(kind, "Item", price), Ct))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);

        problem.GetProperty("errorCode").GetString().ShouldBe("ValidationFailed");
        problem.GetProperty("errors").GetProperty("defaultPrice")[0].GetString().ShouldBe(message);
    }

    [Fact]
    public async Task Active_name_is_unique_per_kind_ignoring_case()
    {
        var client = _api.Authenticated((await _api.SignUpAsync()).AccessToken());
        await (await client.PostAsJsonAsync("/api/v1/products", Beverage("Suco de laranja", "8.00"), Ct)).ExpectAsync(HttpStatusCode.Created, _api);

        var duplicate = await (await client.PostAsJsonAsync("/api/v1/products", Beverage("SUCO DE LARANJA ", "9.00"), Ct))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);
        duplicate.GetProperty("errors").TryGetProperty("name", out _).ShouldBeTrue();

        // Mesmo nome com outro tipo é permitido.
        await (await client.PostAsJsonAsync("/api/v1/products", Product("ADDON", "Suco de laranja", "3.00"), Ct))
            .ExpectAsync(HttpStatusCode.Created, _api);
    }

    [Fact]
    public async Task Patch_requires_if_match_and_detects_stale_version()
    {
        var client = _api.Authenticated((await _api.SignUpAsync()).AccessToken());
        var product = await (await client.PostAsJsonAsync("/api/v1/products", Beverage("Guaraná", "6.00"), Ct)).ExpectAsync(HttpStatusCode.Created, _api);
        var url = $"/api/v1/products/{product.GetProperty("id").GetString()}";

        var missing = await (await client.SendJsonAsync(HttpMethod.Patch, url, new { defaultPrice = "6.50" }))
            .ExpectAsync(HttpStatusCode.PreconditionRequired, _api);
        missing.GetProperty("errorCode").GetString().ShouldBe("IfMatchRequired");

        var updated = await client.SendJsonAsync(HttpMethod.Patch, url, new { defaultPrice = "6.50" }, ifMatch: 1);
        var body = await updated.ExpectAsync(HttpStatusCode.OK, _api);
        body.GetProperty("version").GetInt32().ShouldBe(2);
        body.GetProperty("defaultPrice").GetString().ShouldBe("6.50");
        updated.Headers.ETag!.Tag.ShouldBe("\"2\"");

        // Outro aparelho ainda com a versão 1.
        var conflict = await (await client.SendJsonAsync(HttpMethod.Patch, url, new { isAvailable = false }, ifMatch: 1))
            .ExpectAsync(HttpStatusCode.Conflict, _api);
        conflict.GetProperty("errorCode").GetString().ShouldBe("VersionConflict");
        conflict.GetProperty("expectedVersion").GetInt32().ShouldBe(1);
        conflict.GetProperty("currentVersion").GetInt32().ShouldBe(2);
        conflict.GetProperty("current").GetProperty("defaultPrice").GetString().ShouldBe("6.50");
        conflict.GetProperty("current").GetProperty("isAvailable").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Patch_distinguishes_absent_from_null_and_noop_keeps_version()
    {
        var client = _api.Authenticated((await _api.SignUpAsync()).AccessToken());
        var product = await (await client.PostAsJsonAsync("/api/v1/products", new
        {
            kind = "MAIN_DISH", name = "Costela", description = "Costela no bafo", category = "Carnes", defaultPrice = "28.00", requiresPreparation = true,
        }, Ct)).ExpectAsync(HttpStatusCode.Created, _api);
        var url = $"/api/v1/products/{product.GetProperty("id").GetString()}";

        var cleared = await (await client.SendJsonAsync(HttpMethod.Patch, url, new { description = (string?)null }, ifMatch: 1))
            .ExpectAsync(HttpStatusCode.OK, _api);
        cleared.GetProperty("description").ValueKind.ShouldBe(JsonValueKind.Null);
        cleared.GetProperty("category").GetString().ShouldBe("Carnes");
        cleared.GetProperty("defaultPrice").GetString().ShouldBe("28.00");

        var noop = await (await client.SendJsonAsync(HttpMethod.Patch, url, new { category = "Carnes" }, ifMatch: 2))
            .ExpectAsync(HttpStatusCode.OK, _api);
        noop.GetProperty("version").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Image_key_must_belong_to_the_product()
    {
        var owner = await _api.SignUpAsync();
        var client = _api.Authenticated(owner.AccessToken());
        var product = await (await client.PostAsJsonAsync("/api/v1/products", Beverage("Água", "4.00"), Ct)).ExpectAsync(HttpStatusCode.Created, _api);
        var id = product.GetProperty("id").GetString();
        var url = $"/api/v1/products/{id}";

        var foreign = await (await client.SendJsonAsync(HttpMethod.Patch, url, new { imageStorageKey = $"t/{Guid.NewGuid()}/p/{id}-1.jpg" }, ifMatch: 1))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);
        foreign.GetProperty("errors").TryGetProperty("imageStorageKey", out _).ShouldBeTrue();

        var key = $"t/{owner.TenantId()}/p/{id}-20261027.jpg";
        var ok = await (await client.SendJsonAsync(HttpMethod.Patch, url, new { imageStorageKey = key }, ifMatch: 1)).ExpectAsync(HttpStatusCode.OK, _api);
        ok.GetProperty("imageUrl").GetString().ShouldBe($"https://img.almoseis.app/{key}");
    }

    [Fact]
    public async Task List_filters_by_kind_and_hides_inactive()
    {
        var client = _api.Authenticated((await _api.SignUpAsync()).AccessToken());
        await (await client.PostAsJsonAsync("/api/v1/products", Product("MAIN_DISH", "Frango", "22.00"), Ct)).ExpectAsync(HttpStatusCode.Created, _api);
        await (await client.PostAsJsonAsync("/api/v1/products", Product("SIDE_DISH", "Arroz", null), Ct)).ExpectAsync(HttpStatusCode.Created, _api);
        var old = await (await client.PostAsJsonAsync("/api/v1/products", Product("MAIN_DISH", "Bife", "20.00"), Ct)).ExpectAsync(HttpStatusCode.Created, _api);
        await (await client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/products/{old.GetProperty("id").GetString()}", new { isActive = false }, ifMatch: 1))
            .ExpectAsync(HttpStatusCode.OK, _api);

        var active = await (await client.GetAsync("/api/v1/products?kind=MAIN_DISH", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        Names(active).ShouldBe(["Frango"]);

        var all = await (await client.GetAsync("/api/v1/products?kind=MAIN_DISH&includeInactive=true", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        Names(all).ShouldBe(["Bife", "Frango"], ignoreOrder: true);

        var unknown = await (await client.GetAsync("/api/v1/products?kind=main_dish", Ct)).ExpectAsync(HttpStatusCode.BadRequest, _api);
        unknown.GetProperty("errorCode").GetString().ShouldBe("MalformedRequest");
    }

    [Fact]
    public async Task Other_tenant_cannot_see_or_change_the_product()
    {
        var a = _api.Authenticated((await _api.SignUpAsync()).AccessToken());
        var b = _api.Authenticated((await _api.SignUpAsync()).AccessToken());
        var product = await (await a.PostAsJsonAsync("/api/v1/products", Beverage("Exclusiva do A", "5.00"), Ct)).ExpectAsync(HttpStatusCode.Created, _api);
        var url = $"/api/v1/products/{product.GetProperty("id").GetString()}";

        (await (await b.GetAsync(url, Ct)).ExpectAsync(HttpStatusCode.NotFound, _api)).GetProperty("errorCode").GetString().ShouldBe("NotFound");
        await (await b.SendJsonAsync(HttpMethod.Patch, url, new { isActive = false }, ifMatch: 1)).ExpectAsync(HttpStatusCode.NotFound, _api);
        Names(await (await b.GetAsync("/api/v1/products", Ct)).ExpectAsync(HttpStatusCode.OK, _api)).ShouldNotContain("Exclusiva do A");
    }

    [Fact]
    public async Task Kitchen_reads_but_cannot_manage_the_catalog()
    {
        var owner = await _api.SignUpAsync();
        var kitchen = _api.Authenticated(await _api.EmployeeTokenAsync(db, owner, "KITCHEN"));

        await (await kitchen.GetAsync("/api/v1/products", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        var denied = await (await kitchen.PostAsJsonAsync("/api/v1/products", Beverage("Tentativa", "1.00"), Ct)).ExpectAsync(HttpStatusCode.Forbidden, _api);
        denied.GetProperty("errorCode").GetString().ShouldBe("PermissionDenied");
    }

    [Fact]
    public async Task Writes_go_to_the_change_feed_and_outbox()
    {
        var owner = await _api.SignUpAsync();
        var client = _api.Authenticated(owner.AccessToken());
        var product = await (await client.PostAsJsonAsync("/api/v1/products", Beverage("Mate", "5.00"), Ct)).ExpectAsync(HttpStatusCode.Created, _api);
        await (await client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/products/{product.GetProperty("id").GetString()}", new { isAvailable = false }, ifMatch: 1))
            .ExpectAsync(HttpStatusCode.OK, _api);

        await using var connection = new Npgsql.NpgsqlConnection(db.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(
            """
            SELECT (SELECT string_agg(entity_version::text, ',' ORDER BY seq) FROM change_feed_entries WHERE entity_id = @p),
                   (SELECT string_agg(payload->>'eventType' || ':' || (payload->>'entityVersion'), ',' ORDER BY seq) FROM outbox_messages WHERE payload->>'entityId' = @p::text),
                   (SELECT string_agg(operation_type, ',' ORDER BY occurred_at) FROM audit_logs WHERE entity_id = @p)
            """, connection);
        command.Parameters.AddWithValue("p", Guid.Parse(product.GetProperty("id").GetString()!));
        await using var reader = await command.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);

        reader.GetString(0).ShouldBe("1,2");
        reader.GetString(1).ShouldBe("CatalogUpdated:1,CatalogUpdated:2");
        reader.GetString(2).ShouldBe("ProductCreated,ProductUpdated");
    }

    [Fact]
    public async Task Marmita_sizes_are_listed_by_sort_order_and_patched_with_if_match()
    {
        var client = _api.Authenticated((await _api.SignUpAsync()).AccessToken());
        var g = await (await client.PostAsJsonAsync("/api/v1/marmita-sizes", new { name = "G", price = "25.00", sortOrder = 3 }, Ct)).ExpectAsync(HttpStatusCode.Created, _api);
        await (await client.PostAsJsonAsync("/api/v1/marmita-sizes", new { name = "P", price = "15.00", sortOrder = 1 }, Ct)).ExpectAsync(HttpStatusCode.Created, _api);

        var zero = await (await client.PostAsJsonAsync("/api/v1/marmita-sizes", new { name = "M", price = "0.00", sortOrder = 2 }, Ct))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);
        zero.GetProperty("errors").TryGetProperty("price", out _).ShouldBeTrue();

        var dup = await (await client.PostAsJsonAsync("/api/v1/marmita-sizes", new { name = "g", price = "26.00", sortOrder = 4 }, Ct))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);
        dup.GetProperty("errors").TryGetProperty("name", out _).ShouldBeTrue();

        var patched = await (await client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/marmita-sizes/{g.GetProperty("id").GetString()}", new { price = "26.00" }, ifMatch: 1))
            .ExpectAsync(HttpStatusCode.OK, _api);
        patched.GetProperty("price").GetString().ShouldBe("26.00");
        patched.GetProperty("version").GetInt32().ShouldBe(2);

        var list = await (await client.GetAsync("/api/v1/marmita-sizes", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        list.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ShouldBe(["P", "G"]);
    }

    [Fact]
    public async Task Money_as_json_number_is_malformed()
    {
        var client = _api.Authenticated((await _api.SignUpAsync()).AccessToken());

        var problem = await (await client.PostAsJsonAsync("/api/v1/marmita-sizes", new { name = "M", price = 20.0, sortOrder = 2 }, Ct))
            .ExpectAsync(HttpStatusCode.BadRequest, _api);

        problem.GetProperty("errorCode").GetString().ShouldBe("MalformedRequest");
    }

    private static object Beverage(string name, string price) => Product("BEVERAGE", name, price);

    private static object Product(string kind, string name, string? price) =>
        new { kind, name, description = (string?)null, category = (string?)null, defaultPrice = price, requiresPreparation = (bool?)null };

    private static List<string?> Names(JsonElement list) =>
        list.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
}
