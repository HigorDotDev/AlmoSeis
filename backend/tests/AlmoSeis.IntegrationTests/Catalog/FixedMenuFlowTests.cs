using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlmoSeis.IntegrationTests.Ordering;
using AlmoSeis.Modules.Catalog.Contracts;
using Microsoft.Extensions.DependencyInjection;
using AlmoSeis.Infrastructure.Tenancy;
using Npgsql;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Catalog;

/// Cardápio fixo da lanchonete (EP-003 fatia 2, ADR-0056/0057; 06 §6.5 "Cardápio fixo") contra a API real: cadastro,
/// disponibilidade derivada dos combos, pausa por `catalog.availability`, uso em combo, If-Match, módulo, feed e isolamento.
[Collection(nameof(PostgresCollection))]
public sealed class FixedMenuFlowTests(PostgresFixture db) : IDisposable
{
    private const string Base = "/api/v1/fixed-menu";

    private readonly ApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _api.Dispose();

    /// Lanches (X-Burguer 22,00 / site 23,90; X-Salada 24,00), Porções (Batata frita P/G), Açaí (300/500 ml),
    /// Refrigerante Lata (6,00) com Coca-Cola e Coca-Cola Zero (+0,50), Suco 500 ml (9,00) com Laranja com acerola (+1,00),
    /// e três combos: X-Burguer + Batata P + Lata; Batata P + Lata; Açaí 500 ml.
    private sealed record Menu(
        JsonElement Owner, HttpClient Client, Guid Lanches, Guid XBurguer, Guid XSalada, Guid Batata, Guid BatataP, Guid Acai, Guid Acai500,
        Guid Refri, Guid Lata, Guid Coca, Guid CocaZero, Guid Combo1, Guid Combo2, Guid Combo3);

    private async Task<Menu> SeedAsync()
    {
        var owner = await _api.SignUpAsync(ApiScenarios.SignUpBody(behavior: "SNACK_BAR"));
        var client = _api.Authenticated(owner.AccessToken());

        var lanches = (await Created(client, "dish-categories", new { name = "Lanches", sortOrder = 1 })).Id();
        var porcoes = (await Created(client, "dish-categories", new { name = "Porções", sortOrder = 2 })).Id();
        var acaiCat = (await Created(client, "dish-categories", new { name = "Açaí", sortOrder = 3 })).Id();

        var xburguer = await Created(client, "dishes", Dish(lanches, "X-Burguer", "22.00", sellsOnSite: true, sitePrice: "23.90"));
        xburguer.GetProperty("price").GetString().ShouldBe("22.00");
        xburguer.GetProperty("sitePrice").GetString().ShouldBe("23.90");
        xburguer.GetProperty("isSellable").GetBoolean().ShouldBeTrue();
        var xsalada = await Created(client, "dishes", Dish(lanches, "X-Salada", "24.00"));
        var batata = await Created(client, "dishes", SizedDish(porcoes, "Batata frita", ("P", "12.00"), ("G", "18.00")));
        var acai = await Created(client, "dishes", SizedDish(acaiCat, "Açaí", ("300 ml", "14.00"), ("500 ml", "18.00")));

        var refri = await Created(client, "beverage-types", new
        {
            name = "Refrigerante", sortOrder = 1,
            sizes = new[]
            {
                new
                {
                    id = (Guid?)null, name = "Lata 350 ml", price = "6.00", sellsOnSite = true, sitePrice = (string?)null, sortOrder = 1,
                    flavors = new[]
                    {
                        new { id = (Guid?)null, name = "Coca-Cola", extraPrice = "0.00", sortOrder = 1 },
                        new { id = (Guid?)null, name = "Coca-Cola Zero", extraPrice = "0.50", sortOrder = 2 },
                    },
                },
            },
        });
        var lata = refri.GetProperty("sizes")[0];
        lata.GetProperty("sitePrice").GetString().ShouldBe("6.00", "ao ligar \"vende no site\", vem o preço de balcão");

        var batataP = SizeId(batata, "P");
        var acai500 = SizeId(acai, "500 ml");
        var lataId = lata.Id();
        var combo1 = await Created(client, "combos", Combo("X-Burguer + batata + refri", "32.00",
            DishPart(xburguer.Id(), null), DishPart(batata.Id(), batataP), BeveragePart(lataId)));
        combo1.GetProperty("isSellable").GetBoolean().ShouldBeTrue();
        combo1.GetProperty("components")[1].GetProperty("name").GetString().ShouldBe("Batata frita P");
        combo1.GetProperty("components")[2].GetProperty("name").GetString().ShouldBe("Refrigerante Lata 350 ml");
        var combo2 = await Created(client, "combos", Combo("Batata + refri", "16.00", DishPart(batata.Id(), batataP), BeveragePart(lataId)));
        var combo3 = await Created(client, "combos", Combo("Açaí grande", "17.00", DishPart(acai.Id(), acai500)));

        return new Menu(
            owner, client, lanches, xburguer.Id(), xsalada.Id(), batata.Id(), batataP, acai.Id(), acai500, refri.Id(), lataId,
            lata.GetProperty("flavors")[0].Id(), lata.GetProperty("flavors")[1].Id(), combo1.Id(), combo2.Id(), combo3.Id());
    }

    [Fact]
    public async Task Catalog_is_created_with_etag_location_and_read_in_one_call()
    {
        var m = await SeedAsync();

        var response = await m.Client.PostAsJsonAsync($"{Base}/dish-categories", new { name = "Pastéis", sortOrder = 4 }, Ct);
        var created = await response.ExpectAsync(HttpStatusCode.Created, _api);
        response.Headers.ETag!.Tag.ShouldBe("\"1\"");
        response.Headers.Location!.ToString().ShouldBe($"{Base}/dish-categories/{created.Id()}");

        var menu = await Get(m.Client);
        menu.GetProperty("categories").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ShouldBe(["Lanches", "Porções", "Açaí", "Pastéis"]);
        menu.GetProperty("dishes").GetArrayLength().ShouldBe(4);
        menu.GetProperty("beverageTypes").GetArrayLength().ShouldBe(1);
        menu.GetProperty("combos").GetArrayLength().ShouldBe(3);
        var acai = Find(menu, "dishes", m.Acai);
        acai.GetProperty("hasSizes").GetBoolean().ShouldBeTrue();
        acai.GetProperty("price").ValueKind.ShouldBe(JsonValueKind.Null);
        acai.GetProperty("sizes").EnumerateArray().Select(s => s.GetProperty("price").GetString()).ShouldBe(["14.00", "18.00"]);

        // Reordenar = PATCH do sortOrder de cada categoria.
        var lanches = Find(menu, "categories", m.Lanches);
        (await (await m.Client.SendJsonAsync(HttpMethod.Patch, $"{Base}/dish-categories/{m.Lanches}", new { sortOrder = 9 }, lanches.Version()))
            .ExpectAsync(HttpStatusCode.OK, _api)).Version().ShouldBe(2);
        (await Get(m.Client)).GetProperty("categories").EnumerateArray().Last().Id().ShouldBe(m.Lanches);
    }

    [Fact]
    public async Task Dish_with_sizes_needs_an_active_size_and_without_sizes_needs_a_price()
    {
        var m = await SeedAsync();

        var noSize = await Problem(m.Client.PostAsJsonAsync($"{Base}/dishes", SizedDish(m.Lanches, "Pizza"), Ct), HttpStatusCode.UnprocessableEntity);
        noSize.GetProperty("errors").TryGetProperty("sizes", out _).ShouldBeTrue();
        var noPrice = await Problem(m.Client.PostAsJsonAsync($"{Base}/dishes", Dish(m.Lanches, "Misto", null), Ct), HttpStatusCode.UnprocessableEntity);
        noPrice.GetProperty("errors").TryGetProperty("price", out _).ShouldBeTrue();
        var both = await Problem(m.Client.PostAsJsonAsync($"{Base}/dishes", new
        {
            dishCategoryId = m.Lanches, name = "Pizza", hasSizes = true, price = "30.00",
            sizes = new[] { new { id = (Guid?)null, name = "Grande", price = "40.00", sellsOnSite = false, sitePrice = (string?)null, sortOrder = 1 } },
        }, Ct), HttpStatusCode.UnprocessableEntity);
        both.GetProperty("errors").TryGetProperty("price", out _).ShouldBeTrue();

        // Tirar o último tamanho ativo de um prato com tamanho → 422; desligar "Tamanho?" exige o preço único e inativa os tamanhos.
        var acai = await GetDish(m.Client, m.Acai);
        await Problem(m.Client.SendJsonAsync(HttpMethod.Patch, $"{Base}/dishes/{m.Acai}", new { sizes = Array.Empty<object>() }, acai.Version()), HttpStatusCode.UnprocessableEntity);
        await Problem(m.Client.SendJsonAsync(HttpMethod.Patch, $"{Base}/dishes/{m.XSalada}", new { hasSizes = true }, 1), HttpStatusCode.UnprocessableEntity);

        var single = await (await m.Client.SendJsonAsync(HttpMethod.Patch, $"{Base}/dishes/{m.XSalada}", new { price = "25.00", sellsOnSite = true }, 1))
            .ExpectAsync(HttpStatusCode.OK, _api);
        single.GetProperty("sitePrice").GetString().ShouldBe("25.00");
        single.Version().ShouldBe(2);
    }

    /// ADR-0056 item 23 e critérios 3 e 4 da fatia 2: pausar a Batata P tira de venda os dois combos que a usam, e só eles,
    /// sem tocar no combo; reativar devolve; todos os sabores da lata pausados tiram de venda os combos com a lata.
    [Fact]
    public async Task Pausing_a_component_pauses_only_the_combos_that_use_it()
    {
        var m = await SeedAsync();
        var kitchen = _api.Authenticated(await _api.EmployeeTokenAsync(db, m.Owner, "KITCHEN"));

        var batata = await GetDish(kitchen, m.Batata);
        var paused = await (await kitchen.SendJsonAsync(HttpMethod.Patch, $"{Base}/dishes/{m.Batata}/availability",
                new { isPaused = (bool?)null, sizes = new[] { new { id = m.BatataP, isPaused = true } } }, batata.Version()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        paused.Version().ShouldBe(batata.Version() + 1);
        paused.GetProperty("isSellable").GetBoolean().ShouldBeTrue("a Batata G continua");

        var menu = await Get(m.Client);
        Sellable(menu, m.Combo1).ShouldBeFalse();
        Sellable(menu, m.Combo2).ShouldBeFalse();
        Sellable(menu, m.Combo3).ShouldBeTrue();
        var combo1 = Find(menu, "combos", m.Combo1);
        combo1.GetProperty("isPaused").GetBoolean().ShouldBeFalse();
        combo1.Version().ShouldBe(1, "a pausa derivada não grava nada no combo");
        combo1.GetProperty("components").EnumerateArray().Single(c => !c.GetProperty("isSellable").GetBoolean())
            .GetProperty("name").GetString().ShouldBe("Batata frita P");

        await (await kitchen.SendJsonAsync(HttpMethod.Patch, $"{Base}/dishes/{m.Batata}/availability",
                new { sizes = new[] { new { id = m.BatataP, isPaused = false } } }, paused.Version()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        menu = await Get(m.Client);
        Sellable(menu, m.Combo1).ShouldBeTrue();
        Sellable(menu, m.Combo2).ShouldBeTrue();

        // Um sabor pausado mantém a lata; os dois pausados tiram a lata (e os combos com ela) de venda.
        var refri = await (await kitchen.SendJsonAsync(HttpMethod.Patch, $"{Base}/beverage-types/{m.Refri}/availability",
                new { flavors = new[] { new { id = m.Coca, isPaused = true } } }, 1))
            .ExpectAsync(HttpStatusCode.OK, _api);
        refri.GetProperty("sizes")[0].GetProperty("isSellable").GetBoolean().ShouldBeTrue();
        Sellable(await Get(m.Client), m.Combo2).ShouldBeTrue();
        refri = await (await kitchen.SendJsonAsync(HttpMethod.Patch, $"{Base}/beverage-types/{m.Refri}/availability",
                new { flavors = new[] { new { id = m.CocaZero, isPaused = true } } }, refri.Version()))
            .ExpectAsync(HttpStatusCode.OK, _api);
        refri.GetProperty("sizes")[0].GetProperty("isSellable").GetBoolean().ShouldBeFalse();
        menu = await Get(m.Client);
        Sellable(menu, m.Combo1).ShouldBeFalse();
        Sellable(menu, m.Combo2).ShouldBeFalse();
        Sellable(menu, m.Combo3).ShouldBeTrue();

        // Pausar o próprio combo.
        var combo3 = await (await kitchen.SendJsonAsync(HttpMethod.Patch, $"{Base}/combos/{m.Combo3}/availability", new { isPaused = true }, 1))
            .ExpectAsync(HttpStatusCode.OK, _api);
        combo3.GetProperty("isPaused").GetBoolean().ShouldBeTrue();
        combo3.GetProperty("isSellable").GetBoolean().ShouldBeFalse();

        // A porta da fatia 3 vê o mesmo.
        await using var scope = _api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().InitializeForSystem(Guid.Parse(m.Owner.TenantId()));
        var snapshot = await scope.ServiceProvider.GetRequiredService<IFixedMenuReader>().LoadAsync(Ct);
        snapshot.Combos[m.Combo1].IsSellable.ShouldBeFalse();
        snapshot.Combos[m.Combo3].IsSellable.ShouldBeFalse();
        snapshot.Dishes[m.XBurguer].Price!.For(PriceList.Site).ToString().ShouldBe("23.90");
        snapshot.Dishes[m.XBurguer].Price!.For(PriceList.Counter).ToString().ShouldBe("22.00");
        snapshot.BeverageSizes[m.Lata].Flavors[m.CocaZero].ExtraPrice.ToString().ShouldBe("0.50");
    }

    /// Critério 4 da fatia 2: a pausa não volta sozinha com o expediente.
    [Fact]
    public async Task Pause_survives_opening_the_business_day()
    {
        var m = await SeedAsync();
        await (await m.Client.SendJsonAsync(HttpMethod.Patch, $"{Base}/dishes/{m.XBurguer}/availability", new { isPaused = true }, 1))
            .ExpectAsync(HttpStatusCode.OK, _api);
        await (await m.Client.PostIdempotentAsync("/api/v1/business-days", new { openingBalance = "100.00", confirmSameDate = false }, Guid.NewGuid()))
            .ExpectAsync(HttpStatusCode.Created, _api);

        var dish = await GetDish(m.Client, m.XBurguer);
        dish.GetProperty("isPaused").GetBoolean().ShouldBeTrue();
        Sellable(await Get(m.Client), m.Combo1).ShouldBeFalse();
    }

    /// ADR-0056 item 25 (Q-306): a cozinha e o atendente pausam, mas só OWNER/MANAGER editam; o caixa não pausa.
    [Fact]
    public async Task Kitchen_pauses_but_does_not_edit_and_cashier_does_not_pause()
    {
        var m = await SeedAsync();
        var kitchen = _api.Authenticated(await _api.EmployeeTokenAsync(db, m.Owner, "KITCHEN"));
        var attendant = _api.Authenticated(await _api.EmployeeTokenAsync(db, m.Owner, "ATTENDANT", "inst_att"));
        var cashier = _api.Authenticated(await _api.EmployeeTokenAsync(db, m.Owner, "CASHIER", "inst_cash"));

        (await Problem(kitchen.SendJsonAsync(HttpMethod.Patch, $"{Base}/dishes/{m.XBurguer}", new { price = "1.00" }, 1), HttpStatusCode.Forbidden))
            .GetProperty("permission").GetString().ShouldBe("catalog.manage");
        (await Problem(kitchen.PostAsJsonAsync($"{Base}/dish-categories", new { name = "Doces", sortOrder = 1 }, Ct), HttpStatusCode.Forbidden))
            .GetProperty("errorCode").GetString().ShouldBe("PermissionDenied");
        (await Problem(cashier.SendJsonAsync(HttpMethod.Patch, $"{Base}/combos/{m.Combo1}/availability", new { isPaused = true }, 1), HttpStatusCode.Forbidden))
            .GetProperty("permission").GetString().ShouldBe("catalog.availability");

        await (await attendant.SendJsonAsync(HttpMethod.Patch, $"{Base}/combos/{m.Combo1}/availability", new { isPaused = true }, 1))
            .ExpectAsync(HttpStatusCode.OK, _api);
        (await (await kitchen.GetAsync(Base, Ct)).ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("combos").GetArrayLength().ShouldBe(3);
    }

    /// ADR-0056 item 26: inativar o que um combo ativo usa → 409 com os combos; depois de inativar os combos, passa.
    [Fact]
    public async Task Inactivating_an_item_used_by_active_combos_is_refused_with_the_combos()
    {
        var m = await SeedAsync();

        var problem = await Problem(
            m.Client.SendJsonAsync(HttpMethod.Patch, $"{Base}/dishes/{m.Batata}", new { isActive = false }, 1), HttpStatusCode.Conflict);
        problem.GetProperty("errorCode").GetString().ShouldBe("UsedInActiveCombos");
        problem.GetProperty("combos").EnumerateArray().Select(c => c.GetProperty("name").GetString())
            .ShouldBe(["Batata + refri", "X-Burguer + batata + refri"]);

        // Tirar a Batata P da lista de tamanhos também é inativar.
        var batata = await GetDish(m.Client, m.Batata);
        var onlyG = batata.GetProperty("sizes").EnumerateArray().Where(s => s.GetProperty("name").GetString() == "G")
            .Select(s => new { id = s.Id(), name = "G", price = "18.00", sellsOnSite = false, sitePrice = (string?)null, sortOrder = 2 }).ToArray();
        (await Problem(m.Client.SendJsonAsync(HttpMethod.Patch, $"{Base}/dishes/{m.Batata}", new { sizes = onlyG }, 1), HttpStatusCode.Conflict))
            .GetProperty("errorCode").GetString().ShouldBe("UsedInActiveCombos");
        (await Problem(m.Client.SendJsonAsync(HttpMethod.Patch, $"{Base}/beverage-types/{m.Refri}", new { isActive = false }, 1), HttpStatusCode.Conflict))
            .GetProperty("combos").GetArrayLength().ShouldBe(2);

        await (await m.Client.SendJsonAsync(HttpMethod.Patch, $"{Base}/combos/{m.Combo1}", new { isActive = false }, 1)).ExpectAsync(HttpStatusCode.OK, _api);
        await (await m.Client.SendJsonAsync(HttpMethod.Patch, $"{Base}/combos/{m.Combo2}", new { isActive = false }, 1)).ExpectAsync(HttpStatusCode.OK, _api);
        var inactive = await (await m.Client.SendJsonAsync(HttpMethod.Patch, $"{Base}/dishes/{m.Batata}", new { isActive = false }, 1))
            .ExpectAsync(HttpStatusCode.OK, _api);
        inactive.GetProperty("isActive").GetBoolean().ShouldBeFalse();
        (await Get(m.Client)).GetProperty("dishes").EnumerateArray().ShouldNotContain(d => d.Id() == m.Batata);
        (await (await m.Client.GetAsync($"{Base}?includeInactive=true", Ct)).ExpectAsync(HttpStatusCode.OK, _api))
            .GetProperty("dishes").EnumerateArray().ShouldContain(d => d.Id() == m.Batata);

        // Categoria com prato ativo não é inativada.
        await Problem(m.Client.SendJsonAsync(HttpMethod.Patch, $"{Base}/dish-categories/{m.Lanches}", new { isActive = false }, 1), HttpStatusCode.UnprocessableEntity);
    }

    /// Combo: prato com tamanho exige o tamanho dele; prato sem tamanho não aceita tamanho; bebida ativa.
    [Fact]
    public async Task Combo_components_are_checked_against_the_catalog()
    {
        var m = await SeedAsync();
        (await Problem(m.Client.PostAsJsonAsync($"{Base}/combos", Combo("Sem tamanho", "20.00", DishPart(m.Batata, null)), Ct), HttpStatusCode.UnprocessableEntity))
            .GetProperty("errors").TryGetProperty("components[0].dishSizeId", out _).ShouldBeTrue();
        (await Problem(m.Client.PostAsJsonAsync($"{Base}/combos", Combo("Tamanho de outro", "20.00", DishPart(m.Batata, m.Acai500)), Ct), HttpStatusCode.UnprocessableEntity))
            .GetProperty("errors").TryGetProperty("components[0].dishSizeId", out _).ShouldBeTrue();
        (await Problem(m.Client.PostAsJsonAsync($"{Base}/combos", Combo("Burguer P", "20.00", DishPart(m.XBurguer, m.BatataP)), Ct), HttpStatusCode.UnprocessableEntity))
            .GetProperty("errors").TryGetProperty("components[0].dishSizeId", out _).ShouldBeTrue();
        (await Problem(m.Client.PostAsJsonAsync($"{Base}/combos", Combo("Vazio", "20.00"), Ct), HttpStatusCode.UnprocessableEntity))
            .GetProperty("errors").TryGetProperty("components", out _).ShouldBeTrue();
    }

    /// RN-307.2: o grupo da categoria Lanches aparece em todos os lanches; o grupo só do X-Salada não aparece no X-Burguer.
    [Fact]
    public async Task Addon_groups_are_the_union_of_category_and_dish_links()
    {
        var m = await SeedAsync();
        var lanche = await Created(m.Client, "addon-groups", new
        {
            name = "Adicionais de lanche", sortOrder = 1,
            options = new[] { new { id = (Guid?)null, name = "Bacon", price = "4.00", sortOrder = 1 }, new { id = (Guid?)null, name = "Sem cebola", price = "0.00", sortOrder = 2 } },
            dishCategoryIds = new[] { m.Lanches },
            dishIds = Array.Empty<Guid>(),
        });
        var salada = await Created(m.Client, "addon-groups", new
        {
            name = "Molhos do X-Salada", sortOrder = 2,
            options = new[] { new { id = (Guid?)null, name = "Maionese verde", price = "1.50", sortOrder = 1 } },
            dishCategoryIds = Array.Empty<Guid>(),
            dishIds = new[] { m.XSalada },
        });

        var menu = await Get(m.Client);
        Find(menu, "dishes", m.XBurguer).GetProperty("addonGroupIds").EnumerateArray().Select(e => e.GetGuid()).ShouldBe([lanche.Id()]);
        Find(menu, "dishes", m.XSalada).GetProperty("addonGroupIds").EnumerateArray().Select(e => e.GetGuid()).ShouldBe([lanche.Id(), salada.Id()]);
        Find(menu, "dishes", m.Acai).GetProperty("addonGroupIds").GetArrayLength().ShouldBe(0);

        // "Acabou o bacon".
        var bacon = lanche.GetProperty("options")[0].Id();
        var paused = await (await m.Client.SendJsonAsync(HttpMethod.Patch, $"{Base}/addon-groups/{lanche.Id()}/availability",
                new { options = new[] { new { id = bacon, isPaused = true } } }, 1))
            .ExpectAsync(HttpStatusCode.OK, _api);
        paused.GetProperty("options")[0].GetProperty("isPaused").GetBoolean().ShouldBeTrue();

        // Ligar a um prato de outro tenant (ou inexistente) → 422.
        await Problem(m.Client.SendJsonAsync(HttpMethod.Patch, $"{Base}/addon-groups/{salada.Id()}", new { dishIds = new[] { Guid.NewGuid() } }, 1), HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Writes_need_if_match_and_a_stale_version_returns_the_current_resource()
    {
        var m = await SeedAsync();
        (await Problem(m.Client.SendJsonAsync(HttpMethod.Patch, $"{Base}/dishes/{m.XBurguer}", new { price = "23.00" }), (HttpStatusCode)428))
            .GetProperty("errorCode").GetString().ShouldBe("IfMatchRequired");
        await (await m.Client.SendJsonAsync(HttpMethod.Patch, $"{Base}/dishes/{m.XBurguer}", new { price = "23.00" }, 1)).ExpectAsync(HttpStatusCode.OK, _api);

        var conflict = await Problem(m.Client.SendJsonAsync(HttpMethod.Patch, $"{Base}/dishes/{m.XBurguer}/availability", new { isPaused = true }, 1), HttpStatusCode.Conflict);
        conflict.GetProperty("errorCode").GetString().ShouldBe("VersionConflict");
        conflict.GetProperty("current").GetProperty("version").GetInt32().ShouldBe(2);
        conflict.GetProperty("current").GetProperty("price").GetString().ShouldBe("23.00");

        var combo = await Problem(m.Client.SendJsonAsync(HttpMethod.Patch, $"{Base}/combos/{m.Combo1}", new { price = "33.00" }, 7), HttpStatusCode.Conflict);
        combo.GetProperty("current").GetProperty("components").GetArrayLength().ShouldBe(3);

        // PATCH sem mudança real não sobe a versão.
        (await (await m.Client.SendJsonAsync(HttpMethod.Patch, $"{Base}/dishes/{m.XBurguer}", new { price = "23.00" }, 2))
            .ExpectAsync(HttpStatusCode.OK, _api)).Version().ShouldBe(2);
    }

    /// Auditoria, feed das raízes e `CatalogUpdated` na mesma transação (critério 8 da fatia 2).
    [Fact]
    public async Task Writes_go_to_audit_feed_and_outbox()
    {
        var m = await SeedAsync();
        await (await m.Client.SendJsonAsync(HttpMethod.Patch, $"{Base}/dishes/{m.Batata}/availability",
            new { sizes = new[] { new { id = m.BatataP, isPaused = true } } }, 1)).ExpectAsync(HttpStatusCode.OK, _api);

        var feed = await (await m.Client.GetAsync("/api/v1/sync/changes?since=0", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        var types = feed.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("entityType").GetString()).ToHashSet();
        foreach (var type in new[] { "DISH_CATEGORY", "DISH", "BEVERAGE_TYPE", "COMBO" })
        {
            types.ShouldContain(type);
        }
        feed.GetProperty("changes").EnumerateArray().Single(c => c.GetProperty("entityId").GetGuid() == m.Batata)
            .GetProperty("entityVersion").GetInt32().ShouldBe(2);

        var tenant = Guid.Parse(m.Owner.TenantId());
        (await CountAsync(tenant, "SELECT count(*) FROM audit_logs WHERE tenant_id = @t AND operation_type = 'FixedMenuAvailabilityChanged' AND entity_id = @e", m.Batata)).ShouldBe(1);
        (await CountAsync(tenant, "SELECT count(*) FROM audit_logs WHERE tenant_id = @t AND operation_type = 'FixedMenuItemCreated'", null)).ShouldBe(11);
        (await CountAsync(tenant, "SELECT count(*) FROM outbox_messages WHERE tenant_id = @t AND event_type = 'CatalogUpdated' AND payload->>'entityId' = @e::text AND payload->'data'->>'entityType' = 'DISH'", m.Batata))
            .ShouldBe(2);
    }

    /// ADR-0054 item 14: sem `FIXED_MENU`, escrita → 403 ModuleNotEnabled antes do corpo; leitura → listas vazias.
    [Fact]
    public async Task Restaurant_without_the_module_gets_403_and_empty_lists()
    {
        var owner = await _api.SignUpAsync();
        var client = _api.Authenticated(owner.AccessToken());

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Post, "dish-categories", new { name = "", sortOrder = -1 }),
                     (HttpMethod.Post, "dishes", new { dishCategoryId = Guid.NewGuid(), name = "X", hasSizes = false, price = "-1.00" }),
                     (HttpMethod.Post, "beverage-types", new { name = "Suco" }),
                     (HttpMethod.Post, "combos", new { name = "C", price = "0.00", components = Array.Empty<object>() }),
                     (HttpMethod.Post, "addon-groups", new { name = "" }),
                     (HttpMethod.Patch, $"dishes/{Guid.NewGuid()}", new { price = "1.00" }),
                     (HttpMethod.Patch, $"combos/{Guid.NewGuid()}/availability", new { isPaused = true }),
                 })
        {
            var problem = await Problem(client.SendJsonAsync(method, $"{Base}/{path}", body), HttpStatusCode.Forbidden);
            problem.GetProperty("errorCode").GetString().ShouldBe("ModuleNotEnabled", path);
            problem.GetProperty("module").GetString().ShouldBe("FIXED_MENU");
        }

        var menu = await Get(client);
        foreach (var list in new[] { "categories", "dishes", "beverageTypes", "combos", "addonGroups" })
        {
            menu.GetProperty(list).GetArrayLength().ShouldBe(0);
        }
    }

    /// Isolamento (ADR-0026): B não lê nem usa o cardápio de A, também pela RLS.
    [Fact]
    public async Task Another_tenant_cannot_read_or_use_the_fixed_menu()
    {
        var a = await SeedAsync();
        var b = await SeedAsync();

        (await Problem(b.Client.GetAsync($"{Base}/dishes/{a.XBurguer}", Ct), HttpStatusCode.NotFound)).GetProperty("errorCode").GetString().ShouldBe("NotFound");
        await Problem(b.Client.SendJsonAsync(HttpMethod.Patch, $"{Base}/combos/{a.Combo1}/availability", new { isPaused = true }, 1), HttpStatusCode.NotFound);
        (await Problem(b.Client.PostAsJsonAsync($"{Base}/combos", Combo("Roubado", "10.00", DishPart(a.XBurguer, null)), Ct), HttpStatusCode.UnprocessableEntity))
            .GetProperty("errors").TryGetProperty("components[0].dishId", out _).ShouldBeTrue();
        (await Problem(b.Client.PostAsJsonAsync($"{Base}/dishes", Dish(a.Lanches, "Intruso", "5.00"), Ct), HttpStatusCode.UnprocessableEntity))
            .GetProperty("errors").TryGetProperty("dishCategoryId", out _).ShouldBeTrue();

        var menuB = await Get(b.Client);
        menuB.GetProperty("dishes").EnumerateArray().ShouldNotContain(d => d.Id() == a.XBurguer);
        menuB.GetProperty("dishes").GetArrayLength().ShouldBe(4);

        // RLS com o login da aplicação e a sessão de B: nada de A.
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @b, false); SELECT count(*) FROM dishes d JOIN dish_sizes s ON s.dish_id = d.id WHERE d.tenant_id = @a::uuid", connection);
        command.Parameters.AddWithValue("b", b.Owner.TenantId());
        command.Parameters.AddWithValue("a", a.Owner.TenantId());
        await using var reader = await command.ExecuteReaderAsync(Ct);
        await reader.NextResultAsync(Ct);
        await reader.ReadAsync(Ct);
        reader.GetInt64(0).ShouldBe(0);
    }

    // ---------- Apoio ----------

    private static object Dish(Guid category, string name, string? price, bool sellsOnSite = false, string? sitePrice = null) => new
    {
        dishCategoryId = category, name, description = (string?)null, requiresPreparation = (bool?)null, hasSizes = false, price, sellsOnSite, sitePrice,
        sortOrder = 0, sizes = (object[]?)null,
    };

    private static object SizedDish(Guid category, string name, params (string Name, string Price)[] sizes) => new
    {
        dishCategoryId = category, name, description = (string?)null, requiresPreparation = (bool?)null, hasSizes = true, price = (string?)null,
        sellsOnSite = false, sitePrice = (string?)null, sortOrder = 0,
        sizes = sizes.Select((s, i) => new { id = (Guid?)null, name = s.Name, price = s.Price, sellsOnSite = true, sitePrice = (string?)null, sortOrder = i + 1 }).ToArray(),
    };

    private static object Combo(string name, string price, params object[] components) => new
    {
        name, description = (string?)null, price, sellsOnSite = false, sitePrice = (string?)null, sortOrder = 0, components,
    };

    private static object DishPart(Guid dish, Guid? size) =>
        new { kind = "DISH", dishId = dish, dishSizeId = size, beverageSizeId = (Guid?)null, quantity = 1 };

    private static object BeveragePart(Guid size) =>
        new { kind = "BEVERAGE", dishId = (Guid?)null, dishSizeId = (Guid?)null, beverageSizeId = size, quantity = 1 };

    private static Guid SizeId(JsonElement dish, string name) =>
        dish.GetProperty("sizes").EnumerateArray().Single(s => s.GetProperty("name").GetString() == name).Id();

    private static JsonElement Find(JsonElement menu, string list, Guid id) => menu.GetProperty(list).EnumerateArray().Single(e => e.Id() == id);

    private static bool Sellable(JsonElement menu, Guid combo) => Find(menu, "combos", combo).GetProperty("isSellable").GetBoolean();

    private async Task<JsonElement> Created(HttpClient client, string path, object body) =>
        await (await client.PostAsJsonAsync($"{Base}/{path}", body, Ct)).ExpectAsync(HttpStatusCode.Created, _api);

    private async Task<JsonElement> Get(HttpClient client) => await (await client.GetAsync(Base, Ct)).ExpectAsync(HttpStatusCode.OK, _api);

    private async Task<JsonElement> GetDish(HttpClient client, Guid id) =>
        await (await client.GetAsync($"{Base}/dishes/{id}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);

    private async Task<JsonElement> Problem(Task<HttpResponseMessage> call, HttpStatusCode status) => await (await call).ExpectAsync(status, _api);

    private async Task<long> CountAsync(Guid tenant, string sql, Guid? entity)
    {
        await using var connection = new NpgsqlConnection(db.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("t", tenant);
        if (entity is { } e)
        {
            command.Parameters.AddWithValue("e", e);
        }

        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
