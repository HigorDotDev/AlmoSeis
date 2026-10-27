using AlmoSeis.Infrastructure;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace AlmoSeis.IntegrationTests;

[Collection(nameof(PostgresCollection))]
public sealed class SchemaTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_creates_the_documented_schema()
    {
        await using var connection = new NpgsqlConnection(db.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__ef_migrations_history'),
                   (SELECT count(*) FROM pg_policies WHERE schemaname = 'public')
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);

        reader.GetInt64(0).ShouldBe(36, "tabelas de 05-ddl.md");
        reader.GetInt64(1).ShouldBe(65, "políticas de RLS de 05 §5.12");
    }

    /// O mesmo teste de fumaça do 05 §5.16, agora sobre o schema criado pela migration.
    [Fact]
    public async Task Ddl_smoke_test_passes_on_migrated_schema()
    {
        // Remove os meta-comandos do psql (\set, \echo) e roda numa conexão isolada (SET ROLE é de sessão).
        var sql = string.Join('\n', PostgresFixture.ReadSmokeTest().Split('\n').Where(l => !l.TrimStart().StartsWith('\\')));

        await using var connection = new NpgsqlConnection(db.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        var notices = new List<string>();
        connection.Notice += (_, e) => notices.Add(e.Notice.MessageText);

        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(Ct);
        }

        await transaction.RollbackAsync(Ct); // não deixa dados para os outros testes

        notices.Count(n => n.StartsWith("ok:", StringComparison.Ordinal)).ShouldBe(21, string.Join(" | ", notices));
    }

    /// Toda entidade mapeada no EF precisa existir no schema com as colunas do mapeamento.
    [Fact]
    public async Task Ef_model_matches_database_columns()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseAlmoSeisNpgsql(db.SuperuserConnectionString);
        await using var context = new AppDbContext(options.Options, new TenantContext(), Array.Empty<IModuleModelConfiguration>());

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var table = entityType.GetTableName()!;
            var columns = entityType.GetProperties().Select(p => $"\"{p.GetColumnName()}\"");
            var sql = $"SELECT {string.Join(", ", columns)} FROM \"{table}\" LIMIT 0";

            await Should.NotThrowAsync(() => context.Database.ExecuteSqlRawAsync(sql, Ct), $"{entityType.ClrType.Name} → {table}");
        }
    }
}
