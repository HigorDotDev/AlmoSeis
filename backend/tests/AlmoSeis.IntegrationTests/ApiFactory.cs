using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AlmoSeis.IntegrationTests;

/// A API inteira sobre o PostgreSQL do fixture, conectando com os logins reais (almoseis_app / platform).
public sealed class ApiFactory(PostgresFixture db) : WebApplicationFactory<Program>
{
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
        }));
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

    public static string RandomDigits(int length) =>
        string.Concat(Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10))));
}
