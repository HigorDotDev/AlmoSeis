using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Menu;

/// Cardápios (06 §6.7, ADR-0038) contra a API real.
[Collection(nameof(PostgresCollection))]
public sealed class MenuFlowTests(PostgresFixture db) : IDisposable
{
    private readonly ApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Pf_menu_takes_section_from_product_and_defaults_pf_price()
    {
        var (client, catalog) = await StoreWithCatalogAsync();

        var response = await client.PostAsJsonAsync("/api/v1/menus", new
        {
            menuType = "PF",
            kind = "DAILY",
            date = "2026-10-28",
            dayOfWeek = (int?)null,
            items = new object[]
            {
                new { productId = catalog.Frango, pfPrice = (string?)null, isAvailable = true, sortOrder = 1 },
                new { productId = catalog.Costela, pfPrice = "30.00", isAvailable = true, sortOrder = 2 },
                new { productId = catalog.Arroz, pfPrice = (string?)null, isAvailable = true, sortOrder = 10 },
            },
        }, Ct);
        var menu = await response.ExpectAsync(HttpStatusCode.Created, _api);

        response.Headers.ETag!.Tag.ShouldBe("\"1\"");
        menu.GetProperty("menuType").GetString().ShouldBe("PF");
        menu.GetProperty("date").GetString().ShouldBe("2026-10-28");
        menu.GetProperty("createdBy").GetProperty("displayName").GetString().ShouldNotBeNullOrEmpty();

        var items = menu.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("name").GetString()!);
        items["Frango grelhado"].GetProperty("section").GetString().ShouldBe("MAIN_DISH");
        items["Frango grelhado"].GetProperty("pfPrice").GetString().ShouldBe("22.00"); // defaultPrice do produto
        items["Costela"].GetProperty("pfPrice").GetString().ShouldBe("30.00");
        items["Arroz"].GetProperty("section").GetString().ShouldBe("SIDE_DISH");
        items["Arroz"].GetProperty("pfPrice").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Item_rules_are_reported_per_field()
    {
        var (client, catalog) = await StoreWithCatalogAsync();

        var problem = await (await client.PostAsJsonAsync("/api/v1/menus", new
        {
            menuType = "MARMITA",
            kind = "DAILY",
            date = "2026-10-28",
            dayOfWeek = (int?)null,
            items = new object[]
            {
                new { productId = catalog.Coca, pfPrice = (string?)null, isAvailable = true, sortOrder = 1 },
                new { productId = catalog.Frango, pfPrice = "20.00", isAvailable = true, sortOrder = 2 },
                new { productId = Guid.NewGuid(), pfPrice = (string?)null, isAvailable = true, sortOrder = 3 },
            },
        }, Ct)).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);

        var errors = problem.GetProperty("errors");
        errors.GetProperty("items[0].productId")[0].GetString().ShouldBe("Bebidas e adicionais não entram no cardápio.");
        errors.GetProperty("items[1].pfPrice")[0].GetString().ShouldBe("Só pratos principais do cardápio PF têm preço.");
        errors.GetProperty("items[2].productId")[0].GetString().ShouldBe("Produto não encontrado ou inativo.");
    }

    [Fact]
    public async Task Kind_and_date_must_agree()
    {
        var (client, _) = await StoreWithCatalogAsync();

        var problem = await (await client.PostAsJsonAsync("/api/v1/menus", new
        {
            menuType = "MARMITA", kind = "WEEKLY_TEMPLATE", date = "2026-10-28", dayOfWeek = 9, items = Array.Empty<object>(),
        }, Ct)).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);

        problem.GetProperty("errors").TryGetProperty("date", out _).ShouldBeTrue();
        problem.GetProperty("errors").TryGetProperty("dayOfWeek", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Second_daily_menu_for_the_same_date_and_type_is_a_conflict()
    {
        var (client, catalog) = await StoreWithCatalogAsync();
        var first = await CreateMarmitaMenuAsync(client, catalog, "2026-10-29");

        var conflict = await (await client.PostAsJsonAsync("/api/v1/menus", MarmitaMenuBody(catalog, "2026-10-29"), Ct))
            .ExpectAsync(HttpStatusCode.Conflict, _api);
        conflict.GetProperty("errorCode").GetString().ShouldBe("MenuAlreadyExists");
        conflict.GetProperty("existingMenuId").GetString().ShouldBe(first.GetProperty("id").GetString());

        // O PF da mesma data é outro cardápio (RN-003.1).
        await (await client.PostAsJsonAsync("/api/v1/menus", new
        {
            menuType = "PF", kind = "DAILY", date = "2026-10-29", dayOfWeek = (int?)null,
            items = new[] { new { productId = catalog.Frango, pfPrice = "24.00", isAvailable = true, sortOrder = 1 } },
        }, Ct)).ExpectAsync(HttpStatusCode.Created, _api);
    }

    [Fact]
    public async Task Item_availability_bumps_the_version_and_stale_writes_get_the_current_menu()
    {
        var (client, catalog) = await StoreWithCatalogAsync();
        var menu = await CreateMarmitaMenuAsync(client, catalog, "2026-10-30");
        var costela = menu.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "Costela");
        var url = $"/api/v1/menus/{menu.GetProperty("id").GetString()}/items/{costela.GetProperty("id").GetString()}";

        var updated = await (await client.SendJsonAsync(HttpMethod.Patch, url, new { isAvailable = false }, ifMatch: 1))
            .ExpectAsync(HttpStatusCode.OK, _api);
        updated.GetProperty("version").GetInt32().ShouldBe(2);
        updated.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "Costela")
            .GetProperty("isAvailable").GetBoolean().ShouldBeFalse();

        var stale = await (await client.SendJsonAsync(HttpMethod.Patch, url, new { isAvailable = true }, ifMatch: 1))
            .ExpectAsync(HttpStatusCode.Conflict, _api);
        stale.GetProperty("current").GetProperty("version").GetInt32().ShouldBe(2);
        stale.GetProperty("current").GetProperty("items").GetArrayLength().ShouldBe(3);

        var badPrice = await (await client.SendJsonAsync(HttpMethod.Patch, url, new { pfPrice = "10.00" }, ifMatch: 2))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);
        badPrice.GetProperty("errors").TryGetProperty("pfPrice", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Replacing_items_keeps_ids_of_products_that_stay()
    {
        var (client, catalog) = await StoreWithCatalogAsync();
        var menu = await CreateMarmitaMenuAsync(client, catalog, "2026-10-31");
        var frangoItemId = menu.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "Frango grelhado").GetProperty("id").GetString();

        var replaced = await (await client.SendJsonAsync(HttpMethod.Put, $"/api/v1/menus/{menu.GetProperty("id").GetString()}/items", new
        {
            items = new object[]
            {
                new { productId = catalog.Frango, pfPrice = (string?)null, isAvailable = false, sortOrder = 5 },
                new { productId = catalog.Feijao, pfPrice = (string?)null, isAvailable = true, sortOrder = 11 },
            },
        }, ifMatch: 1)).ExpectAsync(HttpStatusCode.OK, _api);

        replaced.GetProperty("version").GetInt32().ShouldBe(2);
        var items = replaced.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("name").GetString()!);
        items.Keys.ShouldBe(["Frango grelhado", "Feijão"], ignoreOrder: true);
        items["Frango grelhado"].GetProperty("id").GetString().ShouldBe(frangoItemId);
        items["Frango grelhado"].GetProperty("isAvailable").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Clone_creates_an_independent_weekly_template()
    {
        var (client, catalog) = await StoreWithCatalogAsync();
        var daily = await CreateMarmitaMenuAsync(client, catalog, "2026-11-02");

        var clone = await (await client.PostAsJsonAsync($"/api/v1/menus/{daily.GetProperty("id").GetString()}/clone",
            new { kind = "WEEKLY_TEMPLATE", date = (string?)null, dayOfWeek = 1 }, Ct)).ExpectAsync(HttpStatusCode.Created, _api);

        clone.GetProperty("kind").GetString().ShouldBe("WEEKLY_TEMPLATE");
        clone.GetProperty("dayOfWeek").GetInt32().ShouldBe(1);
        clone.GetProperty("date").ValueKind.ShouldBe(JsonValueKind.Null);
        clone.GetProperty("sourceMenuId").GetString().ShouldBe(daily.GetProperty("id").GetString());
        clone.GetProperty("items").GetArrayLength().ShouldBe(3);
        clone.GetProperty("items")[0].GetProperty("id").GetString().ShouldNotBe(daily.GetProperty("items")[0].GetProperty("id").GetString());

        var templates = await (await client.GetAsync("/api/v1/menus?menuType=MARMITA&kind=WEEKLY_TEMPLATE", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        var summary = templates.GetProperty("items").EnumerateArray().Single();
        summary.GetProperty("itemCount").GetInt32().ShouldBe(3);
        summary.TryGetProperty("items", out _).ShouldBeFalse();

        var again = await (await client.PostAsJsonAsync($"/api/v1/menus/{daily.GetProperty("id").GetString()}/clone",
            new { kind = "WEEKLY_TEMPLATE", date = (string?)null, dayOfWeek = 1 }, Ct)).ExpectAsync(HttpStatusCode.Conflict, _api);
        again.GetProperty("errorCode").GetString().ShouldBe("MenuAlreadyExists");
    }

    [Fact]
    public async Task List_filters_daily_menus_by_period()
    {
        var (client, catalog) = await StoreWithCatalogAsync();
        await CreateMarmitaMenuAsync(client, catalog, "2026-10-20");
        await CreateMarmitaMenuAsync(client, catalog, "2026-10-27");
        await CreateMarmitaMenuAsync(client, catalog, "2026-11-05");

        var list = await (await client.GetAsync("/api/v1/menus?menuType=MARMITA&kind=DAILY&from=2026-10-20&to=2026-10-27", Ct))
            .ExpectAsync(HttpStatusCode.OK, _api);

        list.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("date").GetString()).ShouldBe(["2026-10-27", "2026-10-20"]);
    }

    [Fact]
    public async Task Product_in_a_menu_cannot_change_kind()
    {
        var (client, catalog) = await StoreWithCatalogAsync();
        await CreateMarmitaMenuAsync(client, catalog, "2026-11-03");

        var problem = await (await client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/products/{catalog.Arroz}", new { kind = "MAIN_DISH" }, ifMatch: 1))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);

        problem.GetProperty("errors").TryGetProperty("kind", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Products_and_menus_of_another_tenant_are_invisible()
    {
        var (clientA, catalogA) = await StoreWithCatalogAsync();
        var (clientB, _) = await StoreWithCatalogAsync();
        var menuA = await CreateMarmitaMenuAsync(clientA, catalogA, "2026-11-04");

        await (await clientB.GetAsync($"/api/v1/menus/{menuA.GetProperty("id").GetString()}", Ct)).ExpectAsync(HttpStatusCode.NotFound, _api);

        // B tenta montar um cardápio com o produto de A: para B ele não existe.
        var problem = await (await clientB.PostAsJsonAsync("/api/v1/menus", MarmitaMenuBody(catalogA, "2026-11-04"), Ct))
            .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);
        problem.GetProperty("errors").GetProperty("items[0].productId")[0].GetString().ShouldBe("Produto não encontrado ou inativo.");
    }

    [Fact]
    public async Task Attendant_reads_menus_but_cannot_edit_them()
    {
        var owner = await _api.SignUpAsync();
        var ownerClient = _api.Authenticated(owner.AccessToken());
        var catalog = await SeedCatalogAsync(ownerClient);
        var menu = await CreateMarmitaMenuAsync(ownerClient, catalog, "2026-11-06");
        var attendant = _api.Authenticated(await _api.EmployeeTokenAsync(db, owner, "ATTENDANT"));

        await (await attendant.GetAsync($"/api/v1/menus/{menu.GetProperty("id").GetString()}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await attendant.PostAsJsonAsync("/api/v1/menus", MarmitaMenuBody(catalog, "2026-11-07"), Ct)).ExpectAsync(HttpStatusCode.Forbidden, _api);
    }

    private sealed record Catalog(Guid Frango, Guid Costela, Guid Arroz, Guid Feijao, Guid Coca);

    private async Task<(HttpClient Client, Catalog Catalog)> StoreWithCatalogAsync()
    {
        var client = _api.Authenticated((await _api.SignUpAsync()).AccessToken());
        return (client, await SeedCatalogAsync(client));
    }

    private async Task<Catalog> SeedCatalogAsync(HttpClient client)
    {
        async Task<Guid> Create(string kind, string name, string? price)
        {
            var product = await (await client.PostAsJsonAsync("/api/v1/products",
                new { kind, name, description = (string?)null, category = (string?)null, defaultPrice = price, requiresPreparation = (bool?)null }, Ct))
                .ExpectAsync(HttpStatusCode.Created, _api);
            return Guid.Parse(product.GetProperty("id").GetString()!);
        }

        return new Catalog(
            await Create("MAIN_DISH", "Frango grelhado", "22.00"),
            await Create("MAIN_DISH", "Costela", null),
            await Create("SIDE_DISH", "Arroz", null),
            await Create("SIDE_DISH", "Feijão", null),
            await Create("BEVERAGE", "Coca-Cola 350ml", "6.00"));
    }

    private static object MarmitaMenuBody(Catalog catalog, string date) => new
    {
        menuType = "MARMITA",
        kind = "DAILY",
        date,
        dayOfWeek = (int?)null,
        items = new object[]
        {
            new { productId = catalog.Frango, pfPrice = (string?)null, isAvailable = true, sortOrder = 1 },
            new { productId = catalog.Costela, pfPrice = (string?)null, isAvailable = true, sortOrder = 2 },
            new { productId = catalog.Arroz, pfPrice = (string?)null, isAvailable = true, sortOrder = 10 },
        },
    };

    private async Task<JsonElement> CreateMarmitaMenuAsync(HttpClient client, Catalog catalog, string date) =>
        await (await client.PostAsJsonAsync("/api/v1/menus", MarmitaMenuBody(catalog, date), Ct)).ExpectAsync(HttpStatusCode.Created, _api);
}
