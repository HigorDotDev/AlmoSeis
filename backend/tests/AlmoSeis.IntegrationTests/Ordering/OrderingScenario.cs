using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Ordering.Application;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.Modules.Ordering.Jobs;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AlmoSeis.IntegrationTests.Ordering;

/// Estabelecimento pronto para vender: catálogo, tamanhos, cardápios de hoje e expediente aberto.
public sealed class OrderingScenario
{
    private OrderingScenario(ApiFactory api, PostgresFixture db, JsonElement owner, HttpClient client)
    {
        Api = api;
        Db = db;
        Owner = owner;
        Client = client;
    }

    public ApiFactory Api { get; }

    public PostgresFixture Db { get; }

    public JsonElement Owner { get; }

    public HttpClient Client { get; }

    public Guid Frango { get; private set; }

    public Guid Costela { get; private set; }

    public Guid Arroz { get; private set; }

    public Guid Feijao { get; private set; }

    public Guid Coca { get; private set; }

    public Guid Ovo { get; private set; }

    public Guid SizeM { get; private set; }

    public Guid TenantId => Guid.Parse(Owner.TenantId());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static DateOnly Today =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")).DateTime);

    /// `openDay: false` deixa o expediente fechado (para testar BusinessDayRequired).
    public static async Task<OrderingScenario> CreateAsync(ApiFactory api, PostgresFixture db, bool openDay = true, string defaultFee = "5.00")
    {
        var owner = await api.SignUpAsync();
        var scenario = new OrderingScenario(api, db, owner, api.Authenticated(owner.AccessToken()));
        await scenario.SeedAsync(defaultFee, openDay);
        return scenario;
    }

    private async Task SeedAsync(string defaultFee, bool openDay)
    {
        Frango = await ProductAsync("MAIN_DISH", "Frango grelhado", "22.00");
        Costela = await ProductAsync("MAIN_DISH", "Costela", null);
        Arroz = await ProductAsync("SIDE_DISH", "Arroz", null);
        Feijao = await ProductAsync("SIDE_DISH", "Feijão", null);
        Coca = await ProductAsync("BEVERAGE", "Coca-Cola 350ml", "6.00");
        Ovo = await ProductAsync("ADDON", "Ovo frito", "2.00");

        var size = await (await Client.PostAsJsonAsync("/api/v1/marmita-sizes", new { name = "M", price = "20.00", sortOrder = 2, sellsOnSite = true }, Ct))
            .ExpectAsync(HttpStatusCode.Created, Api);
        SizeM = Guid.Parse(size.GetProperty("id").GetString()!);

        var date = Today.ToString("yyyy-MM-dd");
        await MenuAsync("MARMITA", date, (Frango, null, true), (Costela, null, false), (Arroz, null, true), (Feijao, null, true));
        await MenuAsync("PF", date, (Frango, "24.00", true), (Arroz, null, true));

        await SqlAsync("UPDATE tenant_settings SET default_delivery_fee = @fee::numeric WHERE tenant_id = @t", ("fee", defaultFee));
        if (openDay)
        {
            await (await Client.PostIdempotentAsync("/api/v1/business-days", new { openingBalance = "100.00", confirmSameDate = false }, Guid.NewGuid()))
                .ExpectAsync(HttpStatusCode.Created, Api);
        }
    }

    public Task SetKiloPriceAsync(string price) =>
        SqlAsync("UPDATE tenant_settings SET kilo_price_per_kg = @p::numeric WHERE tenant_id = @t", ("p", price));

    /// Payments ainda não existe: simula a quitação direto no banco.
    public Task MarkPaidAsync(Guid orderId) =>
        SqlAsync("UPDATE orders SET paid_amount = total_amount, is_paid = true, version = version + 1 WHERE tenant_id = @t AND id = @o", ("o", orderId));

    public async Task SqlAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Db.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("t", TenantId);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Db.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("t", TenantId);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    public object MarmitaDelivery(
        int quantity = 2, object? deliveryFee = null, bool saveCustomer = false, string? phone = "+5534999991234", string? expectedPaymentMethod = "PIX",
        string? changeFor = null) => new
    {
        orderType = "MARMITA",
        fulfillmentType = "DELIVERY",
        customerId = (Guid?)null,
        customerName = "João Silva",
        customerPhone = phone,
        saveCustomer,
        customerAddressId = (Guid?)null,
        deliveryAddress = new { street = "Rua das Acácias", number = "120", complement = "Ap 201", neighborhood = "Santa Mônica", reference = (string?)null },
        deliveryFee,
        tableLabel = (string?)null,
        tabNumber = (string?)null,
        notes = "Entregar até 12h30",
        expectedPaymentMethod,
        changeFor,
        items = new object[]
        {
            new
            {
                itemKind = "MARMITA", marmitaSizeId = SizeM, quantity,
                mainDishProductIds = new[] { Frango }, sideDishProductIds = new[] { Arroz, Feijao },
                notes = "Sem cebola", addons = new[] { new { productId = Ovo, quantity = 1, notes = (string?)null } },
            },
            new { itemKind = "BEVERAGE", productId = Coca, quantity = 1, notes = (string?)null },
        },
        weighings = Array.Empty<object>(),
    };

    public object Pf(string table = "5") => new
    {
        orderType = "PF",
        fulfillmentType = "DINE_IN",
        tableLabel = table,
        items = new object[] { new { itemKind = "PF", productId = Frango, quantity = 2, notes = "Um sem salada", addons = Array.Empty<object>() } },
        weighings = Array.Empty<object>(),
    };

    public object Kilo(params int[] grams) => new
    {
        orderType = "KILO",
        fulfillmentType = "DINE_IN",
        customerName = "Mesa 3",
        tableLabel = "3",
        tabNumber = "47",
        items = Array.Empty<object>(),
        weighings = grams.Select(g => new { weightGrams = g }).ToArray(),
    };

    public async Task<JsonElement> CreateOrderAsync(object body, HttpClient? client = null) =>
        await (await (client ?? Client).PostIdempotentAsync("/api/v1/orders", body, Guid.NewGuid())).ExpectAsync(HttpStatusCode.Created, Api);

    /// Pedido "pagar na entrega/retirada" da página (ADR-0046) pelo caso de uso interno (a rota pública está em
    /// PublicStorefrontFlowTests), no contexto do tenant e sem funcionário.
    /// Entrega: 2 marmitas M (40,00) + 5,00 de taxa = 45,00. Retirada: 40,00.
    public async Task<Guid> SubmitWebOrderAsync(bool delivery = true, int quantity = 2, string? phone = "+5534988887777", string name = "Carla Souza")
    {
        await using var scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().InitializeForSystem(TenantId);
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<SubmitWebOrderCommand, Guid>>();
        return await handler.HandleAsync(
            new SubmitWebOrderCommand(
                delivery ? FulfillmentType.Delivery : FulfillmentType.Pickup, name, phone,
                delivery ? new DeliveryAddressDto("Rua das Palmeiras", "45", "Casa 2", "Centro", null) : null,
                delivery ? Money.Of(5m) : Money.Zero, null,
                [new OrderItemInput(OrderItemKind.Marmita, null, SizeM, null, quantity, [Frango], [Arroz, Feijao], null, null)]),
            Ct);
    }

    /// Desistência do cliente (ADR-0046 item 17): exposta pela página nas fatias 2/3.
    public async Task WithdrawWebOrderAsync(Guid orderId)
    {
        await using var scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().InitializeForSystem(TenantId);
        await scope.ServiceProvider.GetRequiredService<ICommandHandler<WithdrawWebOrderCommand, bool>>()
            .HandleAsync(new WithdrawWebOrderCommand(orderId), Ct);
    }

    public Task<int?> RunExpiryJobAsync() => Api.Services.GetRequiredService<WebOrderExpiryJob>().RunOnceAsync(Ct);

    /// Prazo de aceite já vencido (ou daqui a `seconds`), sem mexer na versão.
    public Task SetAcceptanceDeadlineAsync(Guid orderId, DateTimeOffset deadline) =>
        SqlAsync("UPDATE orders SET acceptance_deadline = @d WHERE tenant_id = @t AND id = @o", ("d", deadline), ("o", orderId));

    public async Task<JsonElement> GetOrderAsync(Guid orderId, HttpClient? client = null) =>
        await (await (client ?? Client).GetAsync($"/api/v1/orders/{orderId}", Ct)).ExpectAsync(HttpStatusCode.OK, Api);

    private async Task<Guid> ProductAsync(string kind, string name, string? price)
    {
        var product = await (await Client.PostAsJsonAsync("/api/v1/products",
            new { kind, name, description = (string?)null, category = (string?)null, defaultPrice = price, requiresPreparation = (bool?)null }, Ct))
            .ExpectAsync(HttpStatusCode.Created, Api);
        return Guid.Parse(product.GetProperty("id").GetString()!);
    }

    private async Task MenuAsync(string menuType, string date, params (Guid Product, string? PfPrice, bool Available)[] items) =>
        await (await Client.PostAsJsonAsync("/api/v1/menus", new
        {
            menuType,
            kind = "DAILY",
            date,
            dayOfWeek = (int?)null,
            items = items.Select((i, n) => new { productId = i.Product, pfPrice = i.PfPrice, isAvailable = i.Available, sortOrder = n }).ToArray(),
        }, Ct)).ExpectAsync(HttpStatusCode.Created, Api);
}

public static class JsonIds
{
    public static Guid Id(this JsonElement element) => Guid.Parse(element.GetProperty("id").GetString()!);

    public static int Version(this JsonElement element) => element.GetProperty("version").GetInt32();
}
