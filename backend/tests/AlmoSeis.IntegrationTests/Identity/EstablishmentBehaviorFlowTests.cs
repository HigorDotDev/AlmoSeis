using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.IntegrationTests.Ordering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;

namespace AlmoSeis.IntegrationTests.Identity;

/// EP-003 fatia 1 (ADR-0054): comportamento e rótulos no cadastro, módulos no `/auth/me`, troca de rótulos, acréscimo
/// de comportamento pelo admin, `403 ModuleNotEnabled` e isolamento entre tenants, contra a API real.
[Collection(nameof(PostgresCollection))]
public sealed class EstablishmentBehaviorFlowTests(PostgresFixture db) : IDisposable
{
    private const string Password = "S3nha-forte!";
    private static readonly string[] RestaurantModules = ["MARMITA", "PF", "KILO", "DAILY_MENU"];
    private static readonly string[] SnackBarModules = ["A_LA_CARTE", "DELIVERY", "FIXED_MENU"];

    private readonly ApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Signup_as_restaurant_returns_behavior_labels_and_modules()
    {
        var signup = await _api.SignUpAsync(ApiScenarios.SignUpBody(labels: ["SELF_SERVICE", "MARMITARIA"]));

        var tenant = signup.GetProperty("me").GetProperty("tenant");
        Strings(tenant, "behaviors").ShouldBe(["RESTAURANT"]);
        Strings(tenant, "labels").ShouldBe(["MARMITARIA", "SELF_SERVICE"]); // ordem da lista fixa
        Strings(tenant, "modules").ShouldBe(RestaurantModules);

        var client = _api.Authenticated(signup.AccessToken());
        var me = await (await client.GetAsync("/api/v1/auth/me", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        Strings(me.GetProperty("tenant"), "modules").ShouldBe(RestaurantModules);
        var dto = await (await client.GetAsync("/api/v1/tenant", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        Strings(dto, "behaviors").ShouldBe(["RESTAURANT"]);
        Strings(dto, "labels").ShouldBe(["MARMITARIA", "SELF_SERVICE"]);

        var tenantId = Guid.Parse(signup.TenantId());
        var userId = Guid.Parse(signup.GetProperty("me").GetProperty("user").GetProperty("id").GetString()!);
        (await ScalarAsync<string>("SELECT origin || ':' || enabled_by_user_id FROM tenant_behaviors WHERE tenant_id = @t", tenantId))
            .ShouldBe($"SIGNUP:{userId}");
        (await ScalarAsync<string>("SELECT after->>'behaviors' FROM audit_logs WHERE tenant_id = @t AND operation_type = 'TenantSignedUp'", tenantId))
            .ShouldBe("""["RESTAURANT"]""");
    }

    [Fact]
    public async Task Signup_as_snack_bar_gets_only_the_snack_bar_modules()
    {
        var signup = await _api.SignUpAsync(ApiScenarios.SignUpBody(behavior: "SNACK_BAR", labels: ["ACAI_SHOP", "BURGER_JOINT", "OTHER"]));

        var tenant = signup.GetProperty("me").GetProperty("tenant");
        Strings(tenant, "behaviors").ShouldBe(["SNACK_BAR"]);
        Strings(tenant, "labels").ShouldBe(["BURGER_JOINT", "ACAI_SHOP", "OTHER"]);
        Strings(tenant, "modules").ShouldBe(SnackBarModules);
    }

    [Fact]
    public async Task Signup_requires_one_behavior_and_labels_of_it()
    {
        var anonymous = _api.CreateClient();

        var noBehavior = await Problem(anonymous, SignUpWith(behavior: null, labels: ["RESTAURANT"]), HttpStatusCode.UnprocessableEntity);
        noBehavior.GetProperty("errors").TryGetProperty("tenant.behavior", out _).ShouldBeTrue(noBehavior.ToString());

        var noLabel = await Problem(anonymous, SignUpWith(behavior: "SNACK_BAR", labels: []), HttpStatusCode.UnprocessableEntity);
        noLabel.GetProperty("errors").TryGetProperty("tenant.labels", out _).ShouldBeTrue(noLabel.ToString());

        // Rótulo de lanchonete num restaurante (e vice-versa) → 422 em tenant.labels.
        var wrong = await Problem(anonymous, SignUpWith(behavior: "RESTAURANT", labels: ["MARMITARIA", "BURGER_JOINT"]), HttpStatusCode.UnprocessableEntity);
        wrong.GetProperty("errorCode").GetString().ShouldBe("ValidationFailed");
        wrong.GetProperty("errors").TryGetProperty("tenant.labels", out _).ShouldBeTrue(wrong.ToString());
        var wrongSnack = await Problem(anonymous, SignUpWith(behavior: "SNACK_BAR", labels: ["SELF_SERVICE"]), HttpStatusCode.UnprocessableEntity);
        wrongSnack.GetProperty("errors").TryGetProperty("tenant.labels", out _).ShouldBeTrue(wrongSnack.ToString());

        // Código fora da lista fixa é enum desconhecido.
        (await Problem(anonymous, SignUpWith(behavior: "RESTAURANT", labels: ["CHURRASCARIA"]), HttpStatusCode.BadRequest))
            .GetProperty("errorCode").GetString().ShouldBe("MalformedRequest");
        (await Problem(anonymous, SignUpWith(behavior: "FOOD_TRUCK", labels: ["OTHER"]), HttpStatusCode.BadRequest))
            .GetProperty("errorCode").GetString().ShouldBe("MalformedRequest");
    }

    [Fact]
    public async Task Owner_replaces_labels_with_if_match_and_the_change_is_audited_and_in_the_feed()
    {
        var signup = await _api.SignUpAsync(ApiScenarios.SignUpBody(labels: ["RESTAURANT"]));
        var client = _api.Authenticated(signup.AccessToken());
        var tenantId = Guid.Parse(signup.TenantId());
        var version = (await (await client.GetAsync("/api/v1/tenant", Ct)).ExpectAsync(HttpStatusCode.OK, _api)).Version();

        await (await client.SendJsonAsync(HttpMethod.Patch, "/api/v1/tenant", new { labels = new[] { "SELF_SERVICE" } }))
            .ExpectAsync(HttpStatusCode.PreconditionRequired, _api);

        var updated = await (await client.SendJsonAsync(HttpMethod.Patch, "/api/v1/tenant", new { labels = new[] { "SELF_SERVICE", "MARMITARIA" } }, version))
            .ExpectAsync(HttpStatusCode.OK, _api);
        Strings(updated, "labels").ShouldBe(["MARMITARIA", "SELF_SERVICE"]);
        Strings(updated, "behaviors").ShouldBe(["RESTAURANT"]);
        updated.Version().ShouldBe(version + 1);

        // Mesmo conjunto: nada muda, nem a versão.
        (await (await client.SendJsonAsync(HttpMethod.Patch, "/api/v1/tenant", new { labels = new[] { "MARMITARIA", "SELF_SERVICE" } }, version + 1))
            .ExpectAsync(HttpStatusCode.OK, _api)).Version().ShouldBe(version + 1);

        // Rótulo de comportamento não habilitado, lista vazia ou nula → 422 em labels; versão velha → 409 com current.
        foreach (var labels in new object?[] { new[] { "PIZZERIA" }, Array.Empty<string>(), null })
        {
            var invalid = await (await client.SendJsonAsync(HttpMethod.Patch, "/api/v1/tenant", new { labels }, version + 1))
                .ExpectAsync(HttpStatusCode.UnprocessableEntity, _api);
            invalid.GetProperty("errors").TryGetProperty("labels", out _).ShouldBeTrue(invalid.ToString());
        }

        var conflict = await (await client.SendJsonAsync(HttpMethod.Patch, "/api/v1/tenant", new { labels = new[] { "RESTAURANT" } }, version))
            .ExpectAsync(HttpStatusCode.Conflict, _api);
        conflict.GetProperty("errorCode").GetString().ShouldBe("VersionConflict");
        Strings(conflict.GetProperty("current"), "labels").ShouldBe(["MARMITARIA", "SELF_SERVICE"]);

        // Rótulo não muda funcionamento: os módulos continuam os mesmos.
        var me = await (await client.GetAsync("/api/v1/auth/me", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        Strings(me.GetProperty("tenant"), "labels").ShouldBe(["MARMITARIA", "SELF_SERVICE"]);
        Strings(me.GetProperty("tenant"), "modules").ShouldBe(RestaurantModules);

        (await ScalarAsync<long>("SELECT count(*) FROM audit_logs WHERE tenant_id = @t AND operation_type = 'TenantLabelsChanged'", tenantId)).ShouldBe(1);
        (await ScalarAsync<string>("SELECT string_agg(label, ',' ORDER BY label) FROM tenant_labels WHERE tenant_id = @t", tenantId))
            .ShouldBe("MARMITARIA,SELF_SERVICE");
        var feed = await (await client.GetAsync("/api/v1/sync/changes?since=0", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        feed.GetProperty("changes").EnumerateArray()
            .Single(c => c.GetProperty("entityType").GetString() == "TENANT")
            .GetProperty("entityVersion").GetInt32().ShouldBe(version + 1);
    }

    [Fact]
    public async Task Changing_labels_requires_settings_manage()
    {
        var owner = await _api.SignUpAsync();
        var attendant = _api.Authenticated(await _api.EmployeeTokenAsync(db, owner, "ATTENDANT"));

        (await (await attendant.SendJsonAsync(HttpMethod.Patch, "/api/v1/tenant", new { labels = new[] { "MARMITARIA" } }, 1))
            .ExpectAsync(HttpStatusCode.Forbidden, _api)).GetProperty("errorCode").GetString().ShouldBe("PermissionDenied");
    }

    /// Critérios 4 e 6 da fatia 1: a lanchonete não cria nada dos módulos do restaurante, e abrir o expediente não cria
    /// cardápio do dia. Leitura de cadastro de módulo ausente devolve lista vazia.
    [Fact]
    public async Task Snack_bar_gets_module_not_enabled_on_restaurant_modules()
    {
        // Um restaurante com tamanho, prato e modelo semanal para hoje vira lanchonete pura (só para o teste, por SQL): o
        // modelo existe, mas não é clonado na abertura.
        var signup = await _api.SignUpAsync();
        var tenantId = Guid.Parse(signup.TenantId());
        var client = _api.Authenticated(signup.AccessToken());
        var dish = (await (await client.PostAsJsonAsync("/api/v1/products",
            new { kind = "MAIN_DISH", name = "Frango grelhado", description = (string?)null, category = (string?)null, defaultPrice = "22.00", requiresPreparation = (bool?)null }, Ct))
            .ExpectAsync(HttpStatusCode.Created, _api)).Id();
        var size = (await (await client.PostAsJsonAsync("/api/v1/marmita-sizes", new { name = "M", price = "20.00", sortOrder = 2 }, Ct))
            .ExpectAsync(HttpStatusCode.Created, _api)).Id();
        var weekday = OrderingScenario.Today.DayOfWeek is DayOfWeek.Sunday ? 7 : (int)OrderingScenario.Today.DayOfWeek;
        await (await client.PostAsJsonAsync("/api/v1/menus", new
        {
            menuType = "MARMITA",
            kind = "WEEKLY_TEMPLATE",
            date = (string?)null,
            dayOfWeek = weekday,
            items = new[] { new { productId = dish, pfPrice = (string?)null, isAvailable = true, sortOrder = 1 } },
        }, Ct)).ExpectAsync(HttpStatusCode.Created, _api);
        await SqlAsync("""
            DELETE FROM tenant_labels WHERE tenant_id = @t;
            DELETE FROM tenant_behaviors WHERE tenant_id = @t;
            INSERT INTO tenant_behaviors (tenant_id, behavior, origin, enabled_at) VALUES (@t, 'SNACK_BAR', 'SYSTEM', now());
            INSERT INTO tenant_labels (tenant_id, label, behavior, created_at) VALUES (@t, 'BURGER_JOINT', 'SNACK_BAR', now());
            """, tenantId);

        Strings((await (await client.GetAsync("/api/v1/auth/me", Ct)).ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("tenant"), "modules")
            .ShouldBe(SnackBarModules);

        var opened = await (await client.PostIdempotentAsync("/api/v1/business-days", new { openingBalance = "50.00", confirmSameDate = false }, Guid.NewGuid()))
            .ExpectAsync(HttpStatusCode.Created, _api);
        opened.GetProperty("materializedMenus").GetArrayLength().ShouldBe(0);
        (await ScalarAsync<long>("SELECT count(*) FROM menus WHERE tenant_id = @t AND kind = 'DAILY'", tenantId)).ShouldBe(0);

        var pf = new
        {
            orderType = "PF", fulfillmentType = "DINE_IN", tableLabel = "5",
            items = new object[] { new { itemKind = "PF", productId = dish, quantity = 1, notes = (string?)null, addons = Array.Empty<object>() } },
            weighings = Array.Empty<object>(),
        };
        var marmita = new
        {
            orderType = "MARMITA", fulfillmentType = "PICKUP", customerName = "João",
            items = new object[] { new { itemKind = "MARMITA", marmitaSizeId = size, quantity = 1, mainDishProductIds = new[] { dish }, sideDishProductIds = Array.Empty<Guid>(), notes = (string?)null, addons = Array.Empty<object>() } },
            weighings = Array.Empty<object>(),
        };
        var kilo = new { orderType = "KILO", fulfillmentType = "DINE_IN", tableLabel = "3", items = Array.Empty<object>(), weighings = new[] { new { weightGrams = 450 } } };
        await ExpectModule(await client.PostIdempotentAsync("/api/v1/orders", pf, Guid.NewGuid()), "PF");
        await ExpectModule(await client.PostIdempotentAsync("/api/v1/orders", marmita, Guid.NewGuid()), "MARMITA");
        await ExpectModule(await client.PostIdempotentAsync("/api/v1/orders", kilo, Guid.NewGuid()), "KILO");
        await ExpectModule(await client.PostAsJsonAsync("/api/v1/marmita-sizes", new { name = "G", price = "25.00", sortOrder = 3 }, Ct), "MARMITA");
        await ExpectModule(await client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/marmita-sizes/{size}", new { price = "21.00" }, 1), "MARMITA");
        await ExpectModule(await client.PostAsJsonAsync("/api/v1/menus", new { menuType = "PF", kind = "DAILY", date = OrderingScenario.Today.ToString("yyyy-MM-dd"), dayOfWeek = (int?)null, items = Array.Empty<object>() }, Ct), "DAILY_MENU");
        var settings = await (await client.GetAsync("/api/v1/tenant/settings", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        await ExpectModule(await client.SendJsonAsync(HttpMethod.Patch, "/api/v1/tenant/settings", new { kiloPricePerKg = "59.90" }, settings.Version()), "KILO");

        // I4: o catálogo de produtos é do restaurante (algum de MARMITA/PF/KILO/DAILY_MENU); a leitura continua.
        await ExpectModule(await client.PostAsJsonAsync("/api/v1/products",
            new { kind = "BEVERAGE", name = "Coca-Cola 350ml", description = (string?)null, category = (string?)null, defaultPrice = "6.00", requiresPreparation = false }, Ct), "MARMITA");
        await ExpectModule(await client.SendJsonAsync(HttpMethod.Patch, $"/api/v1/products/{dish}", new { defaultPrice = "23.00" }, 1), "MARMITA");
        await ExpectModule(await client.PostAsJsonAsync($"/api/v1/products/{dish}/image-upload", new { contentType = "image/jpeg", sizeBytes = 1000 }, Ct), "MARMITA");
        await ExpectModule(await client.PostAsJsonAsync("/api/v1/products", new { kind = "BEVERAGE", name = "", defaultPrice = "-1.00" }, Ct), "MARMITA");
        (await (await client.GetAsync($"/api/v1/products/{dish}", Ct)).ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("defaultPrice").GetString().ShouldBe("22.00");

        // O módulo vem antes da validação do corpo: um corpo inválido também recebe 403.
        await ExpectModule(await client.PostAsJsonAsync("/api/v1/marmita-sizes", new { name = "", price = "-1.00", sortOrder = 0 }, Ct), "MARMITA");

        (await (await client.GetAsync("/api/v1/marmita-sizes?includeInactive=true", Ct)).ExpectAsync(HttpStatusCode.OK, _api))
            .GetProperty("items").GetArrayLength().ShouldBe(0);
        (await (await client.GetAsync("/api/v1/menus?kind=WEEKLY_TEMPLATE", Ct)).ExpectAsync(HttpStatusCode.OK, _api))
            .GetProperty("items").GetArrayLength().ShouldBe(0);
        (await ScalarAsync<long>("SELECT count(*) FROM orders WHERE tenant_id = @t", tenantId)).ShouldBe(0);

        // O que não é módulo continua: configurações sem o preço do quilo e o expediente.
        await (await client.SendJsonAsync(HttpMethod.Patch, "/api/v1/tenant/settings", new { defaultDeliveryFee = "7.00" }, settings.Version()))
            .ExpectAsync(HttpStatusCode.OK, _api);
    }

    /// Critério 8: o admin acrescenta SNACK_BAR a um restaurante, com auditoria PLATFORM_ADMIN, feed e evento; o app passa
    /// a ver os módulos novos no `/auth/me` e o servidor já aceita os rótulos da lanchonete.
    [Fact]
    public async Task Platform_admin_adds_a_behavior_with_audit_feed_and_realtime_event()
    {
        var (adminEmail, adminUserId) = await AdminAsync();
        var admin = _api.Authenticated(await AdminTokenAsync(adminEmail));
        var restaurant = await _api.SignUpAsync(ApiScenarios.SignUpBody(labels: ["MARMITARIA"]));
        var other = await _api.SignUpAsync();
        var tenantId = Guid.Parse(restaurant.TenantId());
        var client = _api.Authenticated(restaurant.AccessToken());
        var before = (await (await client.GetAsync("/api/v1/tenant", Ct)).ExpectAsync(HttpStatusCode.OK, _api)).Version();
        var seqBefore = (await (await client.GetAsync("/api/v1/sync/changes?since=0", Ct)).ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("lastSeq").GetInt64();

        var url = $"/api/v1/admin/tenants/{tenantId}/behaviors";
        (await (await admin.PostAsJsonAsync(url, new { behavior = "SNACK_BAR", reason = "x" }, Ct)).ExpectAsync(HttpStatusCode.UnprocessableEntity, _api))
            .GetProperty("errors").TryGetProperty("reason", out _).ShouldBeTrue();
        await (await admin.PostAsJsonAsync($"/api/v1/admin/tenants/{Guid.NewGuid()}/behaviors", new { behavior = "SNACK_BAR", reason = "Contratou a lanchonete" }, Ct))
            .ExpectAsync(HttpStatusCode.NotFound, _api);
        await (await client.PostAsJsonAsync(url, new { behavior = "SNACK_BAR", reason = "Contratou a lanchonete" }, Ct))
            .ExpectAsync(HttpStatusCode.Forbidden, _api); // token de estabelecimento em /admin

        var added = await (await admin.PostAsJsonAsync(url, new { behavior = "SNACK_BAR", reason = "Contratou a lanchonete à noite" }, Ct))
            .ExpectAsync(HttpStatusCode.OK, _api);
        added.GetProperty("tenantId").GetString().ShouldBe(tenantId.ToString());
        Strings(added, "behaviors").ShouldBe(["RESTAURANT", "SNACK_BAR"]);
        Strings(added, "labels").ShouldBe(["MARMITARIA"]);
        Strings(added, "modules").ShouldBe([.. RestaurantModules, .. SnackBarModules]);

        // Repetir não muda nada (sem nova auditoria nem evento).
        Strings(await (await admin.PostAsJsonAsync(url, new { behavior = "SNACK_BAR", reason = "Repetição do suporte" }, Ct)).ExpectAsync(HttpStatusCode.OK, _api), "behaviors")
            .ShouldBe(["RESTAURANT", "SNACK_BAR"]);

        var me = await (await client.GetAsync("/api/v1/auth/me", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        Strings(me.GetProperty("tenant"), "modules").ShouldBe([.. RestaurantModules, .. SnackBarModules]);
        var tenantDto = await (await client.GetAsync("/api/v1/tenant", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        tenantDto.Version().ShouldBe(before + 1);

        (await ScalarAsync<string>("SELECT origin || ':' || enabled_by_user_id FROM tenant_behaviors WHERE tenant_id = @t AND behavior = 'SNACK_BAR'", tenantId))
            .ShouldBe($"PLATFORM_ADMIN:{adminUserId}");
        (await ScalarAsync<long>(
            "SELECT count(*) FROM audit_logs WHERE tenant_id = @t AND origin = 'PLATFORM_ADMIN' AND operation_type = 'TenantBehaviorAdded' AND actor_user_id = @u AND actor_employee_id IS NULL",
            tenantId, ("u", adminUserId))).ShouldBe(1);
        var outbox = await ScalarAsync<string>(
            "SELECT payload::text FROM outbox_messages WHERE tenant_id = @t AND event_type = 'TenantModulesChanged'", tenantId);
        var envelope = JsonDocument.Parse(outbox).RootElement;
        envelope.GetProperty("entityType").GetString().ShouldBe("TENANT");
        envelope.GetProperty("seq").GetInt64().ShouldBeGreaterThan(seqBefore);
        envelope.GetProperty("actorEmployeeId").ValueKind.ShouldBe(JsonValueKind.Null);
        Strings(envelope.GetProperty("data"), "modules").ShouldBe([.. RestaurantModules, .. SnackBarModules]);
        (await ScalarAsync<long>("SELECT count(*) FROM outbox_messages WHERE tenant_id = @t AND event_type = 'TenantModulesChanged'", tenantId)).ShouldBe(1);

        // Quem vê pedidos (qualquer perfil) recebe o TENANT no feed e recarrega o /auth/me.
        var kitchen = _api.Authenticated(await _api.EmployeeTokenAsync(db, restaurant, "KITCHEN"));
        var feed = await (await kitchen.GetAsync($"/api/v1/sync/changes?since={seqBefore}", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        feed.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("entityType").GetString()).ShouldContain("TENANT");

        // O proprietário já pode marcar um rótulo da lanchonete.
        var labels = await (await client.SendJsonAsync(HttpMethod.Patch, "/api/v1/tenant", new { labels = new[] { "MARMITARIA", "BURGER_JOINT" } }, before + 1))
            .ExpectAsync(HttpStatusCode.OK, _api);
        Strings(labels, "labels").ShouldBe(["MARMITARIA", "BURGER_JOINT"]);

        // Isolamento: o outro tenant continua só restaurante, sem evento nem linha nova.
        var otherMe = await (await _api.Authenticated(other.AccessToken()).GetAsync("/api/v1/auth/me", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        Strings(otherMe.GetProperty("tenant"), "modules").ShouldBe(RestaurantModules);
        (await ScalarAsync<long>("SELECT count(*) FROM tenant_behaviors WHERE tenant_id = @t", Guid.Parse(other.TenantId()))).ShouldBe(1);
    }

    /// I3 (ADR-0025, complemento de 2026-11-23): a sessão do admin dentro de um tenant não leva o `app.user_id` dele, e as
    /// políticas por usuário (05 §5.12, grupo 2) não abrem os vínculos que ele tem como proprietário de outro
    /// estabelecimento. A sessão do próprio usuário (JWT) continua vendo os vínculos dele, como no login.
    [Fact]
    public async Task Platform_admin_session_does_not_open_the_admins_own_memberships()
    {
        var (_, adminUserId) = await AdminAsync(); // o admin é proprietário do tenant do autocadastro dele
        var target = Guid.Parse((await _api.SignUpAsync()).TenantId());

        async Task<(string UserId, long Tenants, long Employees, long Roles)> VisibleAsync(Action<ITenantContext> initialize)
        {
            await using var scope = _api.Services.CreateAsyncScope();
            initialize(scope.ServiceProvider.GetRequiredService<ITenantContext>());
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await context.Database.OpenConnectionAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<ITenantSession>().ApplyAsync(Ct);
            var userId = await context.Database.SqlQueryRaw<string>("SELECT coalesce(current_setting('app.user_id', true), '') AS \"Value\"").SingleAsync(Ct);
            var tenants = await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM tenants").SingleAsync(Ct);
            var employees = await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM tenant_employees").SingleAsync(Ct);
            var roles = await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM tenant_employee_roles").SingleAsync(Ct);
            return (userId, tenants, employees, roles);
        }

        var admin = await VisibleAsync(t => t.InitializeForPlatformAdmin(target, adminUserId));
        admin.ShouldBe((string.Empty, 1L, 1L, 1L)); // só o tenant da rota e o proprietário dele

        var own = await VisibleAsync(t => t.InitializeForTenant(target, adminUserId));
        own.UserId.ShouldBe(adminUserId.ToString());
        own.Tenants.ShouldBe(2); // o da sessão + o vínculo do próprio usuário (grupo 2)
    }

    [Fact]
    public async Task Each_tenant_sees_and_changes_only_its_own_behaviors_and_labels()
    {
        var a = await _api.SignUpAsync(ApiScenarios.SignUpBody(behavior: "SNACK_BAR", labels: ["PIZZERIA"]));
        var b = await _api.SignUpAsync(ApiScenarios.SignUpBody(labels: ["MARMITARIA"]));
        var clientA = _api.Authenticated(a.AccessToken());
        var clientB = _api.Authenticated(b.AccessToken());

        var tenantB = await (await clientB.GetAsync("/api/v1/tenant", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        tenantB.GetProperty("id").GetString().ShouldBe(b.TenantId());
        Strings(tenantB, "labels").ShouldBe(["MARMITARIA"]);

        // A troca de rótulos de A não toca B; B continua com os módulos de restaurante e A com os da lanchonete.
        var versionA = (await (await clientA.GetAsync("/api/v1/tenant", Ct)).ExpectAsync(HttpStatusCode.OK, _api)).Version();
        await (await clientA.SendJsonAsync(HttpMethod.Patch, "/api/v1/tenant", new { labels = new[] { "CAFE", "BAKERY" } }, versionA))
            .ExpectAsync(HttpStatusCode.OK, _api);
        Strings(await (await clientB.GetAsync("/api/v1/tenant", Ct)).ExpectAsync(HttpStatusCode.OK, _api), "labels").ShouldBe(["MARMITARIA"]);
        Strings((await (await clientB.GetAsync("/api/v1/auth/me", Ct)).ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("tenant"), "modules")
            .ShouldBe(RestaurantModules);
        Strings((await (await clientA.GetAsync("/api/v1/auth/me", Ct)).ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("tenant"), "modules")
            .ShouldBe(SnackBarModules);

        // B (restaurante) cria tamanho de marmita; A (lanchonete) não vê nem cria.
        await (await clientB.PostAsJsonAsync("/api/v1/marmita-sizes", new { name = "P", price = "15.00", sortOrder = 1 }, Ct)).ExpectAsync(HttpStatusCode.Created, _api);
        (await (await clientA.GetAsync("/api/v1/marmita-sizes", Ct)).ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("items").GetArrayLength().ShouldBe(0);
        await ExpectModule(await clientA.PostAsJsonAsync("/api/v1/marmita-sizes", new { name = "P", price = "15.00", sortOrder = 1 }, Ct), "MARMITA");

        // RLS: com a sessão de B, só as linhas de B aparecem.
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using (var session = new NpgsqlCommand("SELECT set_config('app.tenant_id', @b, false)", connection))
        {
            session.Parameters.AddWithValue("b", b.TenantId());
            await session.ExecuteScalarAsync(Ct);
        }

        await using var command = new NpgsqlCommand(
            "SELECT (SELECT string_agg(DISTINCT tenant_id::text, ',') FROM tenant_behaviors) || '|' || (SELECT string_agg(DISTINCT tenant_id::text, ',') FROM tenant_labels)",
            connection);
        (await command.ExecuteScalarAsync(Ct) as string).ShouldBe($"{b.TenantId()}|{b.TenantId()}");
    }

    /// Janela do deploy da 0008 (decisão do usuário, 07 §7.13): tenant sem comportamento é restaurante, e a primeira
    /// escrita grava o que a correção da migration gravaria.
    [Fact]
    public async Task Tenant_without_behavior_rows_works_as_restaurant_and_is_backfilled_on_write()
    {
        var signup = await _api.SignUpAsync(ApiScenarios.SignUpBody(labels: ["MARMITARIA"]));
        var tenantId = Guid.Parse(signup.TenantId());
        await SqlAsync("DELETE FROM tenant_labels WHERE tenant_id = @t; DELETE FROM tenant_behaviors WHERE tenant_id = @t;", tenantId);
        var client = _api.Authenticated(signup.AccessToken());

        var me = await (await client.GetAsync("/api/v1/auth/me", Ct)).ExpectAsync(HttpStatusCode.OK, _api);
        Strings(me.GetProperty("tenant"), "behaviors").ShouldBe(["RESTAURANT"]);
        Strings(me.GetProperty("tenant"), "labels").ShouldBe(["RESTAURANT"]);
        Strings(me.GetProperty("tenant"), "modules").ShouldBe(RestaurantModules);
        await (await client.PostAsJsonAsync("/api/v1/marmita-sizes", new { name = "M", price = "20.00", sortOrder = 2 }, Ct)).ExpectAsync(HttpStatusCode.Created, _api);

        var version = (await (await client.GetAsync("/api/v1/tenant", Ct)).ExpectAsync(HttpStatusCode.OK, _api)).Version();
        var updated = await (await client.SendJsonAsync(HttpMethod.Patch, "/api/v1/tenant", new { labels = new[] { "SELF_SERVICE" } }, version))
            .ExpectAsync(HttpStatusCode.OK, _api);
        Strings(updated, "labels").ShouldBe(["SELF_SERVICE"]);
        (await ScalarAsync<string>("SELECT behavior || ':' || origin || ':' || (enabled_by_user_id IS NULL) FROM tenant_behaviors WHERE tenant_id = @t", tenantId))
            .ShouldBe("RESTAURANT:SYSTEM:true");
    }

    private async Task ExpectModule(HttpResponseMessage response, string module)
    {
        var problem = await response.ExpectAsync(HttpStatusCode.Forbidden, _api);
        problem.GetProperty("errorCode").GetString().ShouldBe("ModuleNotEnabled");
        problem.GetProperty("module").GetString().ShouldBe(module);
    }

    private async Task<JsonElement> Problem(HttpClient client, object body, HttpStatusCode status) =>
        await (await client.PostAsJsonAsync("/api/v1/auth/signup", body, Ct)).ExpectAsync(status, _api);

    private static object SignUpWith(string? behavior, string[] labels) => new
    {
        newAccount = new { fullName = "Ana Souza", email = $"ana.{Guid.NewGuid():N}@example.com", phoneNumber = (string?)null, password = Password },
        existingAccount = (object?)null,
        tenant = new
        {
            name = "Lanchonete da Ana",
            documentType = "CNPJ",
            documentNumber = ApiScenarios.RandomDigits(14),
            city = "Uberlândia",
            state = "MG",
            timeZone = "America/Sao_Paulo",
            behavior,
            labels,
        },
        acceptedTermsVersion = "2026-10",
        installationId = $"inst_{Guid.NewGuid():N}"[..20],
    };

    private static List<string?> Strings(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(e => e.GetString()).ToList();

    private async Task<(string Email, Guid UserId)> AdminAsync()
    {
        var email = $"ops.{Guid.NewGuid():N}@almoseis.app";
        var session = await _api.SignUpAsync(ApiScenarios.SignUpBody(email: email));
        await SqlAsync("UPDATE users SET is_platform_admin = true WHERE normalized_email = upper(@e)", Guid.Empty, ("e", email));
        return (email, Guid.Parse(session.GetProperty("me").GetProperty("user").GetProperty("id").GetString()!));
    }

    private async Task<string> AdminTokenAsync(string email) =>
        (await (await _api.CreateClient().PostAsJsonAsync("/api/v1/auth/admin/login", new { login = email, password = Password }, Ct))
            .ExpectAsync(HttpStatusCode.OK, _api)).GetProperty("accessToken").GetString()!;

    private async Task SqlAsync(string sql, Guid tenantId, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(db.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("t", tenantId);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<T> ScalarAsync<T>(string sql, Guid tenantId, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(db.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("t", tenantId);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
