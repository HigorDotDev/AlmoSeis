// Semente do teste de carga (infra/load/RESULTS.md). NÃO usa o autocadastro (POST /auth/signup exige CPF/CNPJ, e o
// teste não gera documento válido de ninguém).
//
// Fase 1 (SQL, o mínimo que o autocadastro grava), respeitando a RLS e os papéis (ADR-0026, 05 §5.1):
//   - login almoseis_app_login (o mesmo da API) com app.tenant_id/app.user_id na transação, como o SignUpHandler:
//     users, tenants, tenant_behaviors, tenant_labels, tenant_sync_counters, tenant_settings, tenant_employees(+roles),
//     storefront_settings e store_slugs;
//   - login almoseis_platform_login para a assinatura ACTIVE (quem muda o estado da assinatura é a plataforma, ADR-0017).
//   Nenhum dos dois é superusuário nem BYPASSRLS. O documento do tenant é um marcador inválido ("99" + zeros + índice,
//   dígitos verificadores errados), nunca um CPF/CNPJ real.
// Fase 2 (HTTP, pela API, como o dono faria no app): tamanhos, produtos, cardápios do dia (restaurante), cardápio fixo
//   (lanchonete), página de pedidos ativa e expediente aberto. Assim a auditoria, o feed e o outbox nascem pelo
//   caminho normal.
//
// Saída: um JSON com logins, senhas geradas e ids do catálogo, gravado SÓ em infra/load/.out/ (fora do git).
//
// Uso (ver infra/load/run.sh):
//   dotnet run --project infra/load/seed -- --app-conn "Host=127.0.0.1;Port=55432;..." --platform-conn "..." \
//     --api http://127.0.0.1:8088 --api-host api.localhost --tenants 30 --out infra/load/.out/seed.json

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Npgsql;

var opts = Options.Parse(args);
var sw = Stopwatch.StartNew();
var now = DateTimeOffset.UtcNow;
var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
var businessDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, tz).DateTime);

await using var appDb = NpgsqlDataSource.Create(opts.AppConn);
await using var platformDb = NpgsqlDataSource.Create(opts.PlatformConn);

await using (var check = await appDb.OpenConnectionAsync())
{
    await using var cmd = new NpgsqlCommand("SELECT count(*) FROM users WHERE normalized_email LIKE '%@LOADTEST.INVALID'", check);
    var existing = (long)(await cmd.ExecuteScalarAsync())!;
    if (existing > 0)
    {
        Console.Error.WriteLine($"O banco já tem {existing} usuários de teste de carga. Recrie o volume do teste (run.sh down) antes de semear de novo.");
        return 2;
    }
}

var hasher = new PasswordHasher<SeedUser>();
var plans = Enumerable.Range(1, opts.Tenants).Select(TenantPlan.Create).ToList();

Console.WriteLine($"[seed] fase 1: {plans.Count} tenants por SQL (almoseis_app_login + almoseis_platform_login)");
foreach (var plan in plans)
{
    await InsertTenantAsync(plan);
}

Console.WriteLine($"[seed] fase 2: catálogo, cardápio, página e expediente pela API ({opts.Api}, Host {opts.ApiHost})");
using var http = new HttpClient { BaseAddress = new Uri(opts.Api), Timeout = TimeSpan.FromSeconds(60) };
var output = new JsonArray();
foreach (var plan in plans)
{
    output.Add(await SetupTenantAsync(plan));
    Console.WriteLine($"[seed]   {plan.Index:00} {plan.Behavior,-10} {plan.Slug} ok ({sw.Elapsed:mm\\:ss})");
}

var result = new JsonObject
{
    ["generatedAt"] = now.ToString("O"),
    ["businessDate"] = businessDate.ToString("yyyy-MM-dd"),
    ["note"] = "Credenciais de TESTE geradas pela semente. Arquivo local, fora do git (infra/load/.out/).",
    ["tenants"] = output,
};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(opts.Out))!);
await File.WriteAllTextAsync(opts.Out, result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"[seed] concluído em {sw.Elapsed:mm\\:ss}: {opts.Out}");
return 0;

// ---------------------------------------------------------------------------------------------------------------------

async Task InsertTenantAsync(TenantPlan plan)
{
    await using var conn = await appDb.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();
    var owner = plan.Devices[0];

    // O mesmo contexto que o interceptor da API grava (05 §5.2), local à transação.
    await Exec(conn, tx, "SELECT set_config('app.tenant_id', @t, true), set_config('app.user_id', @u, true)",
        ("t", plan.TenantId.ToString()), ("u", owner.UserId.ToString()));

    foreach (var d in plan.Devices)
    {
        var hash = hasher.HashPassword(new SeedUser(), d.Password);
        await Exec(conn, tx,
            """
            INSERT INTO users (id, full_name, email, normalized_email, email_confirmed, password_hash, security_stamp,
                               concurrency_stamp, lockout_enabled, access_failed_count, is_platform_admin, created_at)
            VALUES (@id, @name, @email, upper(@email), false, @hash, @ss, @cs, true, 0, false, @now)
            """,
            ("id", d.UserId), ("name", d.FullName), ("email", d.Login), ("hash", hash),
            ("ss", Guid.NewGuid().ToString("N")), ("cs", Guid.NewGuid().ToString("N")), ("now", now));
    }

    await Exec(conn, tx,
        """
        INSERT INTO tenants (id, name, document_type, document_number, city, state, time_zone, terms_version,
                             terms_accepted_at, terms_accepted_by_user_id, created_at)
        VALUES (@id, @name, 'CNPJ', @doc, 'Uberlândia', 'MG', 'America/Sao_Paulo', '2026-10', @now, @owner, @now)
        """,
        ("id", plan.TenantId), ("name", plan.Name), ("doc", plan.DocumentPlaceholder), ("now", now), ("owner", owner.UserId));
    await Exec(conn, tx,
        "INSERT INTO tenant_behaviors (tenant_id, behavior, origin, enabled_at, enabled_by_user_id) VALUES (@t, @b, 'SIGNUP', @now, @u)",
        ("t", plan.TenantId), ("b", plan.Behavior), ("now", now), ("u", owner.UserId));
    await Exec(conn, tx,
        "INSERT INTO tenant_labels (tenant_id, label, behavior, created_at) VALUES (@t, @l, @b, @now)",
        ("t", plan.TenantId), ("l", plan.Label), ("b", plan.Behavior), ("now", now));
    await Exec(conn, tx, "INSERT INTO tenant_sync_counters (tenant_id, last_seq) VALUES (@t, 0)", ("t", plan.TenantId));
    await Exec(conn, tx,
        """
        INSERT INTO tenant_settings (tenant_id, kilo_price_per_kg, default_delivery_fee, enabled_payment_methods)
        VALUES (@t, @kilo, 5.00, ARRAY['CASH', 'PIX', 'CARD'])
        """,
        ("t", plan.TenantId), ("kilo", plan.IsRestaurant ? 69.90m : (object)DBNull.Value));

    foreach (var d in plan.Devices)
    {
        await Exec(conn, tx,
            """
            INSERT INTO tenant_employees (id, tenant_id, user_id, display_name, status, security_stamp, joined_at)
            VALUES (@id, @t, @u, @name, 'ACTIVE', @ss, @now)
            """,
            ("id", d.EmployeeId), ("t", plan.TenantId), ("u", d.UserId), ("name", d.DisplayName), ("ss", Guid.NewGuid()), ("now", now));
        await Exec(conn, tx,
            "INSERT INTO tenant_employee_roles (tenant_id, tenant_employee_id, role) VALUES (@t, @e, @r)",
            ("t", plan.TenantId), ("e", d.EmployeeId), ("r", d.Role));
    }

    await Exec(conn, tx,
        "INSERT INTO storefront_settings (tenant_id, public_name, updated_at) VALUES (@t, @name, @now)",
        ("t", plan.TenantId), ("name", plan.Name), ("now", now));
    await Exec(conn, tx,
        "INSERT INTO store_slugs (slug, tenant_id, is_current, created_at) VALUES (@s, @t, true, @now)",
        ("s", plan.Slug), ("t", plan.TenantId), ("now", now));
    await tx.CommitAsync();

    // Assinatura ACTIVE: papel da plataforma (política platform_access), como o webhook do Asaas faria.
    await using var pconn = await platformDb.OpenConnectionAsync();
    await Exec(pconn, null,
        """
        INSERT INTO subscriptions (id, tenant_id, status, plan_code, monthly_price, trial_ends_at, current_period_ends_at)
        VALUES (@id, @t, 'ACTIVE', 'PILOT', 0, @trial, @period)
        """,
        ("id", Guid.CreateVersion7()), ("t", plan.TenantId), ("trial", now.AddDays(-1)), ("period", now.AddDays(30)));
}

static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, params (string Name, object Value)[] ps)
{
    await using var cmd = new NpgsqlCommand(sql, conn, tx);
    foreach (var (name, value) in ps)
    {
        cmd.Parameters.AddWithValue(name, value);
    }

    await cmd.ExecuteNonQueryAsync();
}

async Task<JsonObject> SetupTenantAsync(TenantPlan plan)
{
    var owner = plan.Devices[0];
    var token = await LoginAsync(owner.Login, owner.Password);
    var api = new Api(http, opts.ApiHost, token);
    var catalog = new JsonObject();

    if (plan.IsRestaurant)
    {
        var sizes = new JsonArray();
        foreach (var (name, price, site, order) in new[] { ("P", "16.00", "17.00", 1), ("M", "20.00", "21.50", 2), ("G", "25.00", "26.50", 3) })
        {
            var s = await api.Send(HttpMethod.Post, "/api/v1/marmita-sizes", new { name, price, sortOrder = order, sellsOnSite = true, sitePrice = site });
            sizes.Add(new JsonObject { ["id"] = s["id"]!.GetValue<string>(), ["name"] = name, ["price"] = price });
        }

        async Task<string> Product(string kind, string name, string? price, bool prep, string? sitePrice = null) =>
            (await api.Send(HttpMethod.Post, "/api/v1/products", new
            {
                kind, name, description = (string?)null, category = kind == "BEVERAGE" ? "Bebidas" : "Cozinha",
                defaultPrice = price, requiresPreparation = prep, sitePrice,
            }))["id"]!.GetValue<string>();

        var mains = new List<string>();
        foreach (var (n, p) in new[] { ("Frango grelhado", "22.00"), ("Bife acebolado", "24.00"), ("Costela", "28.00"), ("Peixe frito", "26.00") })
        {
            mains.Add(await Product("MAIN_DISH", n, p, true));
        }

        var sides = new List<string>();
        foreach (var n in new[] { "Arroz", "Feijão", "Salada", "Farofa", "Batata frita" })
        {
            sides.Add(await Product("SIDE_DISH", n, null, true)); // acompanhamento não tem preço próprio
        }

        var beverages = new JsonArray();
        foreach (var (n, p, s) in new[] { ("Coca-Cola 350ml", "6.00", "6.50"), ("Suco de laranja", "7.00", "7.00"), ("Água", "3.00", "3.00") })
        {
            beverages.Add(new JsonObject { ["id"] = await Product("BEVERAGE", n, p, false, s), ["price"] = p });
        }

        var addons = new JsonArray();
        foreach (var (n, p) in new[] { ("Ovo frito", "2.00"), ("Bacon", "3.00") })
        {
            addons.Add(new JsonObject { ["id"] = await Product("ADDON", n, p, true), ["price"] = p });
        }

        var date = businessDate.ToString("yyyy-MM-dd");
        var marmitaItems = mains.Concat(sides).Select((id, i) => new { productId = id, pfPrice = (string?)null, isAvailable = true, sortOrder = i + 1 }).ToArray();
        await api.Send(HttpMethod.Post, "/api/v1/menus", new { menuType = "MARMITA", kind = "DAILY", date, dayOfWeek = (int?)null, items = marmitaItems });
        var pfItems = mains.Select((id, i) => new { productId = id, pfPrice = (string?)$"{24 + i * 2}.00", isAvailable = true, sortOrder = i + 1 })
            .Concat(sides.Select((id, i) => new { productId = id, pfPrice = (string?)null, isAvailable = true, sortOrder = 10 + i })).ToArray();
        await api.Send(HttpMethod.Post, "/api/v1/menus", new { menuType = "PF", kind = "DAILY", date, dayOfWeek = (int?)null, items = pfItems });

        catalog["marmitaSizes"] = sizes;
        catalog["mainDishes"] = new JsonArray(mains.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray());
        catalog["sideDishes"] = new JsonArray(sides.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray());
        catalog["beverages"] = beverages;
        catalog["addons"] = addons;
    }
    else
    {
        var lanches = (await api.Send(HttpMethod.Post, "/api/v1/fixed-menu/dish-categories", new { name = "Lanches", sortOrder = 1 }))["id"]!.GetValue<string>();
        var porcoes = (await api.Send(HttpMethod.Post, "/api/v1/fixed-menu/dish-categories", new { name = "Porções", sortOrder = 2 }))["id"]!.GetValue<string>();

        async Task<JsonObject> Dish(string category, string name, string? price, object[]? sizes, int order) =>
            await api.Send(HttpMethod.Post, "/api/v1/fixed-menu/dishes", new
            {
                dishCategoryId = category, name, description = (string?)null, requiresPreparation = true,
                hasSizes = sizes is not null, price, sellsOnSite = sizes is null, sitePrice = (string?)null, sortOrder = order,
                sizes = sizes ?? Array.Empty<object>(),
            });

        var xburguer = await Dish(lanches, "X-Burguer", "22.00", null, 1);
        await Dish(lanches, "X-Salada", "24.00", null, 2);
        await Dish(lanches, "X-Tudo", "29.00", null, 3);
        var batata = await Dish(porcoes, "Batata frita", null, [
            new { id = (string?)null, name = "P", price = "12.00", sellsOnSite = true, sitePrice = "13.00", sortOrder = 1 },
            new { id = (string?)null, name = "G", price = "18.00", sellsOnSite = true, sitePrice = "19.50", sortOrder = 2 },
        ], 1);

        var refri = await api.Send(HttpMethod.Post, "/api/v1/fixed-menu/beverage-types", new
        {
            name = "Refrigerante", sortOrder = 1,
            sizes = new object[]
            {
                new
                {
                    id = (string?)null, name = "Lata 350 ml", price = "6.00", sellsOnSite = true, sitePrice = "6.50", sortOrder = 1,
                    flavors = new object[]
                    {
                        new { id = (string?)null, name = "Coca-Cola", extraPrice = "0.00", sortOrder = 1 },
                        new { id = (string?)null, name = "Guaraná", extraPrice = "0.00", sortOrder = 2 },
                        new { id = (string?)null, name = "Coca-Cola Zero", extraPrice = "0.50", sortOrder = 3 },
                    },
                },
                new { id = (string?)null, name = "2 L", price = "14.00", sellsOnSite = false, sitePrice = (string?)null, sortOrder = 2, flavors = Array.Empty<object>() },
            },
        });
        await api.Send(HttpMethod.Post, "/api/v1/fixed-menu/beverage-types", new
        {
            name = "Suco", sortOrder = 2,
            sizes = new object[]
            {
                new
                {
                    id = (string?)null, name = "500 ml", price = "9.00", sellsOnSite = true, sitePrice = "9.50", sortOrder = 1,
                    flavors = new object[]
                    {
                        new { id = (string?)null, name = "Laranja", extraPrice = "0.00", sortOrder = 1 },
                        new { id = (string?)null, name = "Maracujá", extraPrice = "1.00", sortOrder = 2 },
                    },
                },
            },
        });

        var batataP = batata["sizes"]!.AsArray().First(s => s!["name"]!.GetValue<string>() == "P")!["id"]!.GetValue<string>();
        var lata = refri["sizes"]!.AsArray().First(s => s!["name"]!.GetValue<string>() == "Lata 350 ml")!["id"]!.GetValue<string>();
        await api.Send(HttpMethod.Post, "/api/v1/fixed-menu/combos", new
        {
            name = "X-Burguer + batata + refri", description = (string?)null, price = "32.00", sellsOnSite = true, sitePrice = "34.90", sortOrder = 1,
            components = new object[]
            {
                new { kind = "DISH", dishId = xburguer["id"]!.GetValue<string>(), dishSizeId = (string?)null, beverageSizeId = (string?)null, quantity = 1 },
                new { kind = "DISH", dishId = batata["id"]!.GetValue<string>(), dishSizeId = batataP, beverageSizeId = (string?)null, quantity = 1 },
                new { kind = "BEVERAGE", dishId = (string?)null, dishSizeId = (string?)null, beverageSizeId = lata, quantity = 1 },
            },
        });
        await api.Send(HttpMethod.Post, "/api/v1/fixed-menu/addon-groups", new
        {
            name = "Adicionais de lanche", sortOrder = 1,
            options = new object[]
            {
                new { id = (string?)null, name = "Bacon", price = "4.00", sortOrder = 1 },
                new { id = (string?)null, name = "Ovo", price = "2.50", sortOrder = 2 },
                new { id = (string?)null, name = "Sem cebola", price = "0.00", sortOrder = 3 },
            },
            dishCategoryIds = new[] { lanches },
            dishIds = Array.Empty<string>(),
        });

        // O cardápio completo (com os ids dos filhos, componentes e a disponibilidade derivada) vem do GET.
        var fixedMenu = await api.Send(HttpMethod.Get, "/api/v1/fixed-menu", null);
        catalog["dishes"] = new JsonArray(fixedMenu["dishes"]!.AsArray().Select(d => (JsonNode)new JsonObject
        {
            ["id"] = d!["id"]!.GetValue<string>(),
            ["name"] = d["name"]!.GetValue<string>(),
            ["sizeIds"] = new JsonArray(d["sizes"]!.AsArray().Select(s => (JsonNode)JsonValue.Create(s!["id"]!.GetValue<string>())!).ToArray()),
            ["addonGroupIds"] = d["addonGroupIds"]!.DeepClone(),
        }).ToArray());
        catalog["beverageSizes"] = new JsonArray(fixedMenu["beverageTypes"]!.AsArray().SelectMany(t => t!["sizes"]!.AsArray()).Select(s => (JsonNode)new JsonObject
        {
            ["id"] = s!["id"]!.GetValue<string>(),
            ["flavorIds"] = new JsonArray(s["flavors"]!.AsArray().Select(f => (JsonNode)JsonValue.Create(f!["id"]!.GetValue<string>())!).ToArray()),
        }).ToArray());
        catalog["combos"] = new JsonArray(fixedMenu["combos"]!.AsArray().Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c!["id"]!.GetValue<string>(),
            ["components"] = new JsonArray(c["components"]!.AsArray().Select(k => (JsonNode)new JsonObject
            {
                ["id"] = k!["id"]!.GetValue<string>(),
                ["kind"] = k["kind"]!.GetValue<string>(),
                ["dishId"] = k["dishId"]?.DeepClone(),
                ["beverageSizeId"] = k["beverageSizeId"]?.DeepClone(),
            }).ToArray()),
        }).ToArray());
        catalog["addonOptions"] = new JsonArray(fixedMenu["addonGroups"]!.AsArray().SelectMany(g => g!["options"]!.AsArray())
            .Select(o => (JsonNode)JsonValue.Create(o!["id"]!.GetValue<string>())!).ToArray());
    }

    // Página de pedidos ativa, só retirada (sem provedor de mapas no teste). PUT inteiro com If-Match.
    var storefront = await api.Send(HttpMethod.Get, "/api/v1/storefront", null);
    await api.Send(HttpMethod.Put, "/api/v1/storefront", new
    {
        slug = plan.Slug,
        publicName = plan.Name,
        isPageActive = true,
        isAcceptingOrders = true,
        isDeliveryEnabled = false,
        offersDelivery = false,
        offersPickup = true,
        contactPhone = (string?)null,
        hoursText = "Seg a sáb, 10h30 às 14h",
        address = new { street = "Rua do Teste de Carga", number = plan.Index.ToString(), complement = (string?)null, neighborhood = "Centro", city = "Uberlândia", state = "MG", zipCode = "38400000" },
        delivery = new { feeMode = "FIXED", pricePerKm = (string?)null, minimumFee = "0.00", maxDistanceKm = (string?)null },
    }, ifMatch: storefront["version"]!.GetValue<int>());

    var day = await api.Send(HttpMethod.Post, "/api/v1/business-days", new { openingBalance = "100.00", confirmSameDate = true }, idempotent: true);

    return new JsonObject
    {
        ["index"] = plan.Index,
        ["id"] = plan.TenantId.ToString(),
        ["name"] = plan.Name,
        ["behavior"] = plan.Behavior,
        ["label"] = plan.Label,
        ["slug"] = plan.Slug,
        ["businessDayId"] = day["businessDay"]!["id"]!.GetValue<string>(),
        ["devices"] = new JsonArray(plan.Devices.Select(d => (JsonNode)new JsonObject
        {
            ["role"] = d.Role, ["login"] = d.Login, ["password"] = d.Password, ["employeeId"] = d.EmployeeId.ToString(),
        }).ToArray()),
        ["catalog"] = catalog,
    };
}

async Task<string> LoginAsync(string login, string password)
{
    for (var attempt = 1; ; attempt++)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { login, password, installationId = $"loadseed-{Guid.NewGuid():N}"[..40], preferredTenantId = (string?)null }),
        };
        req.Headers.Host = opts.ApiHost;
        req.Headers.Add("X-App-Version", "1.0.0");
        req.Headers.Add("X-App-Platform", "ANDROID");
        using var res = await http.SendAsync(req);
        if (res.StatusCode == HttpStatusCode.TooManyRequests && attempt < 30)
        {
            // Limite do login por IP (ADR-0041: 10/min). A semente passa pelo Caddy, de um IP só.
            var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
            await Task.Delay(wait + TimeSpan.FromMilliseconds(250));
            continue;
        }

        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"login {login}: {(int)res.StatusCode} {body}");
        }

        return JsonNode.Parse(body)!["tokens"]!["accessToken"]!.GetValue<string>();
    }
}

internal sealed class SeedUser;

internal sealed record Device(string Role, string Login, string Password, string FullName, string DisplayName, Guid UserId, Guid EmployeeId);

internal sealed record TenantPlan(int Index, Guid TenantId, string Name, string Behavior, string Label, string Slug, string DocumentPlaceholder, List<Device> Devices)
{
    private static readonly string[] RestaurantLabels = ["MARMITARIA", "RESTAURANT", "SELF_SERVICE"];
    private static readonly string[] SnackLabels = ["BURGER_JOINT", "PASTEL_SHOP", "SNACK_BAR", "ACAI_SHOP"];

    public bool IsRestaurant => Behavior == "RESTAURANT";

    /// 60% restaurante (marmita, PF, quilo) e 40% lanchonete (à la carte): tenants 1..N, a cada 5, os de resto 1 e 3 são lanchonete.
    /// Aparelhos: restaurante 3 ou 4 (dono/caixa, cozinha, caixa, atendente), lanchonete 2 ou 3.
    public static TenantPlan Create(int index)
    {
        var snack = index % 5 is 1 or 3;
        var behavior = snack ? "SNACK_BAR" : "RESTAURANT";
        var label = snack ? SnackLabels[index % SnackLabels.Length] : RestaurantLabels[index % RestaurantLabels.Length];
        var name = snack ? $"Lanchonete Carga {index:00}" : $"Restaurante Carga {index:00}";
        var slug = $"carga-{index:00}-{(snack ? "lanches" : "restaurante")}";
        var deviceCount = snack ? (index % 2 == 0 ? 3 : 2) : (index % 2 == 0 ? 4 : 3);
        string[] roles = ["OWNER", "KITCHEN", "CASHIER", "ATTENDANT"];
        var devices = roles.Take(deviceCount).Select(role =>
        {
            var display = role switch { "OWNER" => "Dono", "KITCHEN" => "Cozinha", "CASHIER" => "Caixa", _ => "Atendente" };
            return new Device(role, $"t{index:00}-{role.ToLowerInvariant()}@loadtest.invalid", Secret(20), $"{display} Carga {index:00}", display,
                Guid.CreateVersion7(), Guid.CreateVersion7());
        }).ToList();
        return new TenantPlan(index, Guid.CreateVersion7(), name, behavior, label, slug, $"99{index:000000000000}", devices);
    }

    private static string Secret(int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return string.Create(length, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            }
        });
    }
}

internal sealed class Api(HttpClient http, string host, string token)
{
    public async Task<JsonObject> Send(HttpMethod method, string path, object? body, int? ifMatch = null, bool idempotent = false)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Host = host;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-App-Version", "1.0.0");
        req.Headers.Add("X-App-Platform", "ANDROID");
        if (ifMatch is { } v)
        {
            req.Headers.TryAddWithoutValidation("If-Match", $"\"{v}\"");
        }

        if (idempotent)
        {
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        using var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{method} {path}: {(int)res.StatusCode} {text}");
        }

        return JsonNode.Parse(text)!.AsObject();
    }
}

internal sealed record Options(string AppConn, string PlatformConn, string Api, string ApiHost, int Tenants, string Out)
{
    public static Options Parse(string[] args)
    {
        string Get(string name, string? fallback = null)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback ?? throw new ArgumentException($"faltou {name}");
        }

        return new Options(Get("--app-conn"), Get("--platform-conn"), Get("--api", "http://127.0.0.1:8088"),
            Get("--api-host", "api.localhost"), int.Parse(Get("--tenants", "30")), Get("--out", "infra/load/.out/seed.json"));
    }
}
