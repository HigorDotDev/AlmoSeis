using System.Text.RegularExpressions;
using AlmoSeis.Infrastructure;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AlmoSeis.IntegrationTests;

/// Um PostgreSQL 16 por execução, com os papéis de 05 §5.1, logins de teste e a migration aplicada
/// pelo papel de migration — o mesmo caminho de produção (09 §9.2).
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string AppPassword = "test-app";
    public const string PlatformPassword = "test-platform";
    public const string MigratorPassword = "test-migrator";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("almoseis")
        .Build();

    public string SuperuserConnectionString => _container.GetConnectionString();

    public string ConnectionStringFor(string login, string password) =>
        new NpgsqlConnectionStringBuilder(SuperuserConnectionString) { Username = login, Password = password }.ConnectionString;

    public string AppConnectionString => ConnectionStringFor("almoseis_app_login", AppPassword);

    public string PlatformConnectionString => ConnectionStringFor("almoseis_platform_login", PlatformPassword);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        // Papéis (bloco de 05 §5.1, que fica fora da migration) + logins de teste.
        var rolesBlock = Regex.Matches(ReadDdlDocument(), "(?s)```sql\n(.*?)```")
            .Select(m => m.Groups[1].Value)
            .Single(b => b.Contains("CREATE ROLE", StringComparison.Ordinal));

        await ExecuteAsSuperuserAsync(rolesBlock + $"""
            CREATE ROLE almoseis_migrator_login LOGIN PASSWORD '{MigratorPassword}' IN ROLE almoseis_migrator;
            ALTER ROLE almoseis_migrator_login SET role = 'almoseis_migrator';
            CREATE ROLE almoseis_app_login LOGIN PASSWORD '{AppPassword}' IN ROLE almoseis_app;
            CREATE ROLE almoseis_platform_login LOGIN PASSWORD '{PlatformPassword}' IN ROLE almoseis_platform;
            GRANT CREATE, USAGE ON SCHEMA public TO almoseis_migrator;
            ALTER DATABASE almoseis OWNER TO almoseis_migrator;
            """);

        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseAlmoSeisNpgsql(ConnectionStringFor("almoseis_migrator_login", MigratorPassword));
        await using var db = new AppDbContext(options.Options, new TenantContext(), Array.Empty<IModuleModelConfiguration>());
        await db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public async Task ExecuteAsSuperuserAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(SuperuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static string ReadDdlDocument() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Ddl", "05-ddl.md")).ReplaceLineEndings("\n");

    public static string ReadSmokeTest() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Ddl", "05-ddl-smoke-test.sql")).ReplaceLineEndings("\n");
}

[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
