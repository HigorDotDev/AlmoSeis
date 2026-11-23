using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlmoSeis.IntegrationTests.Ordering;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;

namespace AlmoSeis.IntegrationTests;

/// Isolamento entre tenants pelo catálogo de rotas (ADR-0026, 07 §7.12): toda rota autenticada com id de recurso em
/// `/fixed-menu`, `/orders`, `/customers` e `/payments` (as raízes do EP-003 e o que pendura nelas) é chamada pelo tenant B
/// com os ids do tenant A e responde `404 NotFound`, sem mudar nada em A. As rotas vêm do `EndpointDataSource` da API: uma
/// rota nova nesses prefixos sem caso na tabela abaixo faz o teste falhar.
[Collection(nameof(PostgresCollection))]
public sealed class TenantIsolationTests(PostgresFixture db) : IDisposable
{
    private const string FixedMenu = "/api/v1/fixed-menu";

    private static readonly string[] Prefixes = ["api/v1/fixed-menu/", "api/v1/orders/", "api/v1/customers/", "api/v1/payments/"];

    private readonly ApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _api.Dispose();

    /// Ids de A. Os dois tenants têm os dois comportamentos (todos os módulos) e o expediente aberto, para o `404` não
    /// ser escondido por `403 ModuleNotEnabled` ou `409 BusinessDayRequired`.
    private sealed record Seed(
        HttpClient Client, Guid TenantId, Guid Category, Guid Dish, Guid BeverageType, Guid BeverageSize, Guid Combo, Guid AddonGroup,
        Guid AddonOption, Guid Order, Guid OrderItem, Guid Customer, Guid Address, Guid Payment);

    private sealed record Case(Func<Seed, object?>? Body = null);

    /// Chave: método e template da rota, como no `EndpointDataSource`. Os corpos são válidos (a validação vem antes do
    /// handler), e `{itemId}`/`{addressId}` usam os ids reais de A; `{weighingId}` não existe em A.
    private static readonly Dictionary<string, Case> Cases = new()
    {
        ["GET api/v1/fixed-menu/dish-categories/{id:guid}"] = new(),
        ["PATCH api/v1/fixed-menu/dish-categories/{id:guid}"] = new(_ => new { sortOrder = 9 }),
        ["GET api/v1/fixed-menu/dishes/{id:guid}"] = new(),
        ["PATCH api/v1/fixed-menu/dishes/{id:guid}"] = new(_ => new { sortOrder = 9 }),
        ["PATCH api/v1/fixed-menu/dishes/{id:guid}/availability"] = new(_ => new { isPaused = true, sizes = (object[]?)null }),
        ["POST api/v1/fixed-menu/dishes/{id:guid}/image-upload"] = new(_ => new { contentType = "image/jpeg", sizeBytes = 1000 }),
        ["GET api/v1/fixed-menu/beverage-types/{id:guid}"] = new(),
        ["PATCH api/v1/fixed-menu/beverage-types/{id:guid}"] = new(_ => new { sortOrder = 9 }),
        ["PATCH api/v1/fixed-menu/beverage-types/{id:guid}/availability"] = new(a => new { sizes = new[] { new { id = a.BeverageSize, isPaused = true } }, flavors = (object[]?)null }),
        ["GET api/v1/fixed-menu/combos/{id:guid}"] = new(),
        ["PATCH api/v1/fixed-menu/combos/{id:guid}"] = new(_ => new { sortOrder = 9 }),
        ["PATCH api/v1/fixed-menu/combos/{id:guid}/availability"] = new(_ => new { isPaused = true }),
        ["POST api/v1/fixed-menu/combos/{id:guid}/image-upload"] = new(_ => new { contentType = "image/jpeg", sizeBytes = 1000 }),
        ["GET api/v1/fixed-menu/addon-groups/{id:guid}"] = new(),
        ["PATCH api/v1/fixed-menu/addon-groups/{id:guid}"] = new(_ => new { sortOrder = 9 }),
        ["PATCH api/v1/fixed-menu/addon-groups/{id:guid}/availability"] = new(a => new { options = new[] { new { id = a.AddonOption, isPaused = true } } }),

        ["GET api/v1/orders/{id:guid}"] = new(),
        ["PATCH api/v1/orders/{id:guid}"] = new(_ => new { notes = "Outro tenant" }),
        ["POST api/v1/orders/{id:guid}/items"] = new(a => new { items = new[] { Dish(a.Dish) } }),
        ["PATCH api/v1/orders/{id:guid}/items/{itemId:guid}"] = new(_ => new { quantity = 3 }),
        ["DELETE api/v1/orders/{id:guid}/items/{itemId:guid}"] = new(),
        ["POST api/v1/orders/{id:guid}/weighings"] = new(_ => new { weighings = new[] { new { weightGrams = 300 } } }),
        ["DELETE api/v1/orders/{id:guid}/weighings/{weighingId:guid}"] = new(),
        ["PATCH api/v1/orders/{id:guid}/status"] = new(_ => new { productionStatus = "IN_PROGRESS" }),
        ["POST api/v1/orders/{id:guid}/cancel"] = new(_ => new { reason = "Outro tenant" }),
        ["POST api/v1/orders/{id:guid}/accept"] = new(_ => new { estimatedReadyMinutes = (int?)null }),
        ["POST api/v1/orders/{id:guid}/reject"] = new(_ => new { reasonCode = "TOO_BUSY", message = (string?)null }),
        ["PATCH api/v1/orders/{id:guid}/fulfillment"] = new(_ => new { fulfillmentStatus = "OUT_FOR_DELIVERY", completeProduction = false }),
        ["GET api/v1/orders/{id:guid}/history"] = new(),
        ["GET api/v1/orders/{id:guid}/alerts"] = new(),
        ["POST api/v1/orders/{id:guid}/alerts/acknowledge"] = new(_ => new { eventIds = new[] { Guid.NewGuid() } }),
        ["POST api/v1/orders/{orderId:guid}/payments"] = new(_ => new { amount = "1.00", paymentMethod = "PIX", cashReceived = (string?)null, label = (string?)null }),
        ["GET api/v1/orders/{orderId:guid}/payments"] = new(),

        ["GET api/v1/payments/{id:guid}"] = new(),
        ["POST api/v1/payments/{id:guid}/reverse"] = new(_ => new { reason = "Outro tenant" }),

        ["GET api/v1/customers/{id:guid}"] = new(),
        ["PATCH api/v1/customers/{id:guid}"] = new(_ => new { notes = "Outro tenant" }),
        ["POST api/v1/customers/{id:guid}/addresses"] = new(_ => new
        {
            label = "Casa", street = "Rua B", number = "1", complement = (string?)null, neighborhood = (string?)null, reference = (string?)null, isDefault = false,
        }),
        ["PATCH api/v1/customers/{id:guid}/addresses/{addressId:guid}"] = new(_ => new { label = "Trabalho" }),
        ["DELETE api/v1/customers/{id:guid}/addresses/{addressId:guid}"] = new(),
        ["POST api/v1/customers/{id:guid}/anonymize"] = new(_ => new { reason = "Outro tenant" }),
        ["GET api/v1/customers/{id:guid}/pending-orders"] = new(),
    };

    [Fact]
    public async Task Every_route_with_a_resource_id_returns_404_for_another_tenants_resource()
    {
        var a = await SeedAsync();
        var b = await SeedAsync();
        var before = await SnapshotAsync(a);

        var routes = Routes();
        routes.ShouldNotBeEmpty();
        routes.Where(r => !Cases.ContainsKey(r)).ShouldBeEmpty("rota com id sem caso de isolamento: acrescente em Cases");
        Cases.Keys.Where(k => !routes.Contains(k)).ShouldBeEmpty("caso sem rota: a rota mudou de template");

        var failures = new List<string>();
        foreach (var route in routes)
        {
            var (method, template) = (route[..route.IndexOf(' ')], route[(route.IndexOf(' ') + 1)..]);
            var url = "/" + Resolve(template, a);
            var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (Cases[route].Body is { } body)
            {
                request.Content = JsonContent.Create(body(a), options: ApiFactory.Json);
            }

            request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
            request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
            var response = await b.Client.SendAsync(request, Ct);
            var content = await response.Content.ReadAsStringAsync(Ct);
            var errorCode = content.Length > 0 && content.TrimStart().StartsWith('{')
                ? JsonDocument.Parse(content).RootElement.TryGetProperty("errorCode", out var code) ? code.GetString() : null
                : null;
            if (response.StatusCode != HttpStatusCode.NotFound || errorCode != "NotFound")
            {
                failures.Add($"{route} → {(int)response.StatusCode} {content}");
            }
        }

        failures.ShouldBeEmpty();
        (await SnapshotAsync(a)).ShouldBe(before);
    }

    /// Rotas autenticadas com parâmetro de id nos prefixos do teste, como "MÉTODO template".
    private List<string> Routes()
    {
        using var _ = _api.CreateClient(); // sobe o host
        return _api.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } raw && raw.Contains('{') && Prefixes.Any(p => raw.TrimStart('/').StartsWith(p, StringComparison.Ordinal)))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => $"{m} {e.RoutePattern.RawText!.TrimStart('/')}"))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static string Resolve(string template, Seed a)
    {
        var root = template.StartsWith("api/v1/fixed-menu/dish-categories/", StringComparison.Ordinal) ? a.Category
            : template.StartsWith("api/v1/fixed-menu/dishes/", StringComparison.Ordinal) ? a.Dish
            : template.StartsWith("api/v1/fixed-menu/beverage-types/", StringComparison.Ordinal) ? a.BeverageType
            : template.StartsWith("api/v1/fixed-menu/combos/", StringComparison.Ordinal) ? a.Combo
            : template.StartsWith("api/v1/fixed-menu/addon-groups/", StringComparison.Ordinal) ? a.AddonGroup
            : template.StartsWith("api/v1/orders/", StringComparison.Ordinal) ? a.Order
            : template.StartsWith("api/v1/customers/", StringComparison.Ordinal) ? a.Customer
            : template.StartsWith("api/v1/payments/", StringComparison.Ordinal) ? a.Payment
            : throw new InvalidOperationException($"Sem raiz para {template}.");
        return template
            .Replace("{id:guid}", root.ToString(), StringComparison.Ordinal)
            .Replace("{orderId:guid}", root.ToString(), StringComparison.Ordinal)
            .Replace("{itemId:guid}", a.OrderItem.ToString(), StringComparison.Ordinal)
            .Replace("{addressId:guid}", a.Address.ToString(), StringComparison.Ordinal)
            .Replace("{weighingId:guid}", Guid.NewGuid().ToString(), StringComparison.Ordinal);
    }

    /// Versões e contagens de A, para provar que as chamadas de B não mudaram nada.
    private async Task<string> SnapshotAsync(Seed a)
    {
        await using var connection = new NpgsqlConnection(db.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT concat_ws('|',
              (SELECT string_agg(id::text || ':' || version, ',' ORDER BY id) FROM dish_categories WHERE tenant_id = @t),
              (SELECT string_agg(id::text || ':' || version, ',' ORDER BY id) FROM dishes WHERE tenant_id = @t),
              (SELECT string_agg(id::text || ':' || version, ',' ORDER BY id) FROM beverage_types WHERE tenant_id = @t),
              (SELECT string_agg(id::text || ':' || version, ',' ORDER BY id) FROM combos WHERE tenant_id = @t),
              (SELECT string_agg(id::text || ':' || version, ',' ORDER BY id) FROM addon_groups WHERE tenant_id = @t),
              (SELECT string_agg(id::text || ':' || version || ':' || paid_amount, ',' ORDER BY id) FROM orders WHERE tenant_id = @t),
              (SELECT count(*) FROM order_items WHERE tenant_id = @t),
              (SELECT string_agg(id::text || ':' || version || ':' || (anonymized_at IS NULL), ',' ORDER BY id) FROM customers WHERE tenant_id = @t),
              (SELECT count(*) FROM customer_addresses WHERE tenant_id = @t),
              (SELECT count(*) FROM payments WHERE tenant_id = @t),
              (SELECT count(*) FROM payment_reversals WHERE tenant_id = @t))
            """, connection);
        command.Parameters.AddWithValue("t", a.TenantId);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<Seed> SeedAsync()
    {
        var owner = await _api.SignUpAsync(ApiScenarios.SignUpBody(behavior: "SNACK_BAR"));
        var tenantId = Guid.Parse(owner.TenantId());
        await using (var connection = new NpgsqlConnection(db.SuperuserConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                "INSERT INTO tenant_behaviors (tenant_id, behavior, origin, enabled_at) VALUES (@t, 'RESTAURANT', 'SYSTEM', now())", connection);
            command.Parameters.AddWithValue("t", tenantId);
            await command.ExecuteNonQueryAsync(Ct);
        }

        var client = _api.Authenticated(owner.AccessToken());
        var category = (await Created(client, "dish-categories", new { name = "Lanches", sortOrder = 1 })).Id();
        var dish = (await Created(client, "dishes", new
        {
            dishCategoryId = category, name = "X-Burguer", description = (string?)null, requiresPreparation = (bool?)null, hasSizes = false,
            price = "22.00", sellsOnSite = false, sitePrice = (string?)null, sortOrder = 0, sizes = (object[]?)null,
        })).Id();
        var beverage = await Created(client, "beverage-types", new
        {
            name = "Refrigerante", sortOrder = 1,
            sizes = new[] { new { id = (Guid?)null, name = "Lata 350 ml", price = "6.00", sellsOnSite = false, sitePrice = (string?)null, sortOrder = 1, flavors = Array.Empty<object>() } },
        });
        var beverageSize = beverage.GetProperty("sizes")[0].Id();
        var addons = await Created(client, "addon-groups", new
        {
            name = "Adicionais", sortOrder = 1,
            options = new[] { new { id = (Guid?)null, name = "Bacon", price = "4.00", sortOrder = 1 } },
            dishCategoryIds = new[] { category }, dishIds = Array.Empty<Guid>(),
        });
        var combo = (await Created(client, "combos", new
        {
            name = "X + refri", description = (string?)null, price = "25.00", sellsOnSite = false, sitePrice = (string?)null, sortOrder = 0,
            components = new object[]
            {
                new { kind = "DISH", dishId = dish, dishSizeId = (Guid?)null, beverageSizeId = (Guid?)null, quantity = 1 },
                new { kind = "BEVERAGE", dishId = (Guid?)null, dishSizeId = (Guid?)null, beverageSizeId = (Guid?)beverageSize, quantity = 1 },
            },
        })).Id();

        var customer = await (await client.PostAsJsonAsync("/api/v1/customers", new
        {
            name = "João Silva", phoneNumber = $"+55349{Random.Shared.Next(10000000, 99999999)}", notes = (string?)null,
            addresses = new[] { new { label = "Casa", street = "Rua A", number = "1", complement = (string?)null, neighborhood = (string?)null, reference = (string?)null, isDefault = true } },
        }, Ct)).ExpectAsync(HttpStatusCode.Created, _api);

        await (await client.PostIdempotentAsync("/api/v1/business-days", new { openingBalance = "100.00", confirmSameDate = false }, Guid.NewGuid()))
            .ExpectAsync(HttpStatusCode.Created, _api);
        var order = await (await client.PostIdempotentAsync("/api/v1/orders", new
        {
            orderType = "A_LA_CARTE", fulfillmentType = "DINE_IN", tableLabel = "5",
            items = new[] { Dish(dish) }, weighings = Array.Empty<object>(),
        }, Guid.NewGuid())).ExpectAsync(HttpStatusCode.Created, _api);
        var payment = await (await client.SendWithKeyAsync(HttpMethod.Post, $"/api/v1/orders/{order.Id()}/payments",
            new { amount = "10.00", paymentMethod = "PIX", cashReceived = (string?)null, label = (string?)null }, order.Version()))
            .ExpectAsync(HttpStatusCode.Created, _api);

        return new Seed(
            client, tenantId, category, dish, beverage.Id(), beverageSize, combo, addons.Id(), addons.GetProperty("options")[0].Id(),
            order.Id(), order.GetProperty("items")[0].Id(), customer.Id(), customer.GetProperty("addresses")[0].Id(), PaymentId(payment));
    }

    private static Guid PaymentId(JsonElement response) =>
        response.TryGetProperty("payment", out var payment) ? payment.Id() : response.Id();

    private static object Dish(Guid dish) => new
    {
        itemKind = "FIXED_DISH", dishId = dish, dishSizeId = (Guid?)null, quantity = 1, notes = (string?)null, groupAddons = Array.Empty<object>(),
    };

    private async Task<JsonElement> Created(HttpClient client, string path, object body) =>
        await (await client.PostAsJsonAsync($"{FixedMenu}/{path}", body, Ct)).ExpectAsync(HttpStatusCode.Created, _api);
}
