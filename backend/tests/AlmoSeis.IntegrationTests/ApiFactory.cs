using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AlmoSeis.IntegrationTests;

/// A API inteira sobre o PostgreSQL do fixture, conectando com os logins reais (almoseis_app / platform).
public sealed class ApiFactory(PostgresFixture db, Action<IServiceCollection>? services = null) : WebApplicationFactory<Program>
{
    public const string WebhookToken = "test-webhook-token";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// Erros logados pela API durante o teste: anexados às falhas para diagnóstico.
    public ConcurrentQueue<string> ServerErrors { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:App"] = db.AppConnectionString,
            ["ConnectionStrings:Platform"] = db.PlatformConnectionString,
            ["Jwt:AllowEphemeralKey"] = "true",
            ["Billing:Asaas:WebhookToken"] = WebhookToken,
        }));
        if (services is not null)
        {
            builder.ConfigureTestServices(services);
        }

        builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(ServerErrors)));
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(sink);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                sink.Enqueue(exception?.ToString() ?? formatter(state, exception));
            }
        }
    }
}

/// Cenários comuns: autocadastro de um estabelecimento novo e cliente autenticado.
public static class ApiScenarios
{
    private static int _sequence;

    public static object SignUpBody(string? email = null, string? document = null, string? phone = null)
    {
        var n = Interlocked.Increment(ref _sequence);
        var unique = Guid.NewGuid().ToString("N")[..8];
        return new
        {
            newAccount = new
            {
                fullName = $"Ana Souza {n}",
                email = email ?? $"ana.{unique}@example.com",
                phoneNumber = phone,
                password = "S3nha-forte!",
            },
            existingAccount = (object?)null,
            tenant = new
            {
                name = $"Marmitaria {n}",
                documentType = "CNPJ",
                documentNumber = document ?? RandomDigits(14),
                city = "Uberlândia",
                state = "MG",
                timeZone = "America/Sao_Paulo",
            },
            acceptedTermsVersion = "2026-10",
            installationId = $"inst_{unique}",
        };
    }

    public static async Task<JsonElement> SignUpAsync(this ApiFactory factory, object? body = null)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/signup", body ?? SignUpBody(), TestContext.Current.CancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            throw new InvalidOperationException(
                $"Signup falhou: {(int)response.StatusCode} {content}\n--- erros do servidor ---\n{string.Join("\n---\n", factory.ServerErrors)}");
        }

        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    public static HttpClient Authenticated(this ApiFactory factory, string accessToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    public static string AccessToken(this JsonElement authResult) =>
        authResult.GetProperty("tokens").GetProperty("accessToken").GetString()!;

    /// Envia JSON com `If-Match: "{version}"` opcional (rotas [M] de 06).
    public static Task<HttpResponseMessage> SendJsonAsync(this HttpClient client, HttpMethod method, string url, object? body, int? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (ifMatch is { } version)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        }

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// POST [I] com `Idempotency-Key` (rotas [I] de 06, ADR-0033).
    public static Task<HttpResponseMessage> PostIdempotentAsync(this HttpClient client, string url, object? body, Guid key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString());
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// Rotas [I][M]: `Idempotency-Key` novo + `If-Match`.
    public static Task<HttpResponseMessage> SendWithKeyAsync(this HttpClient client, HttpMethod method, string url, object? body, int ifMatch)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// Lê o corpo exigindo o status esperado; na falha, mostra o corpo e os erros do servidor.
    public static async Task<JsonElement> ExpectAsync(this HttpResponseMessage response, HttpStatusCode expected, ApiFactory factory)
    {
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        if (response.StatusCode != expected)
        {
            throw new InvalidOperationException(
                $"Esperado {(int)expected}, veio {(int)response.StatusCode}: {content}\n--- erros do servidor ---\n{string.Join("\n---\n", factory.ServerErrors)}");
        }

        return content.Length == 0 ? default : JsonDocument.Parse(content).RootElement.Clone();
    }

    public static string TenantId(this JsonElement authResult) =>
        authResult.GetProperty("me").GetProperty("tenant").GetProperty("id").GetString()!;

    /// Funcionário com outro perfil no estabelecimento de `owner`: um usuário de outro autocadastro ganha
    /// vínculo direto no banco (o fluxo de convite ainda não existe) e entra escolhendo esse tenant.
    public static async Task<string> EmployeeTokenAsync(this ApiFactory factory, PostgresFixture db, JsonElement owner, string role)
    {
        var email = $"func.{Guid.NewGuid():N}@example.com";
        var other = await factory.SignUpAsync(SignUpBody(email: email));
        var userId = Guid.Parse(other.GetProperty("me").GetProperty("user").GetProperty("id").GetString()!);
        var tenantId = Guid.Parse(owner.TenantId());
        var employeeId = Guid.CreateVersion7();

        await using (var connection = new Npgsql.NpgsqlConnection(db.SuperuserConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new Npgsql.NpgsqlCommand(
                """
                INSERT INTO tenant_employees (id, tenant_id, user_id, display_name, status, security_stamp, joined_at)
                VALUES (@e, @t, @u, @n, 'ACTIVE', gen_random_uuid(), now());
                INSERT INTO tenant_employee_roles (tenant_id, tenant_employee_id, role) VALUES (@t, @e, @r);
                """, connection);
            command.Parameters.AddWithValue("e", employeeId);
            command.Parameters.AddWithValue("t", tenantId);
            command.Parameters.AddWithValue("u", userId);
            command.Parameters.AddWithValue("n", role.ToLowerInvariant());
            command.Parameters.AddWithValue("r", role);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var login = await factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login",
            new { login = email, password = "S3nha-forte!", installationId = "inst_func", preferredTenantId = tenantId },
            TestContext.Current.CancellationToken);
        return (await login.ExpectAsync(HttpStatusCode.OK, factory)).AccessToken();
    }

    public static string RandomDigits(int length) =>
        string.Concat(Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10))));
}
