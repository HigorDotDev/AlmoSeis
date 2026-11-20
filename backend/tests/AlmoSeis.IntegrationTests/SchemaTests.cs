using AlmoSeis.Infrastructure;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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

        reader.GetInt64(0).ShouldBe(40, "tabelas de 05-ddl.md");
        reader.GetInt64(1).ShouldBe(76, "políticas de RLS de 05 §5.12 e §5.17");
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

        notices.Count(n => n.StartsWith("ok:", StringComparison.Ordinal)).ShouldBe(41, string.Join(" | ", notices));
    }

    /// 0003 (ADR-0046) num banco com pedidos: o backfill roda com a RLS `FORCE` de `orders` (o banco vazio dos outros
    /// testes não pega isso) e devolve o `FORCE` no fim.
    [Fact]
    public async Task Migration_0003_backfills_existing_orders()
    {
        var database = "almoseis_backfill_" + Guid.NewGuid().ToString("N")[..8];
        await db.ExecuteAsSuperuserAsync($"CREATE DATABASE {database} OWNER almoseis_migrator");
        var superuser = new NpgsqlConnectionStringBuilder(db.SuperuserConnectionString) { Database = database }.ConnectionString;
        var migrator = new NpgsqlConnectionStringBuilder(db.ConnectionStringFor("almoseis_migrator_login", PostgresFixture.MigratorPassword))
        {
            Database = database,
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseAlmoSeisNpgsql(migrator);
        await using (var context = new AppDbContext(options.Options, new TenantContext(), Array.Empty<IModuleModelConfiguration>()))
        {
            await context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync("20261119000000_SharedDeviceWebPlatform", Ct);
        }

        await using (var connection = new NpgsqlConnection(superuser))
        {
            await connection.OpenAsync(Ct);
            await using var seed = new NpgsqlCommand(
                """
                INSERT INTO users (id, full_name, email, normalized_email, password_hash, security_stamp, concurrency_stamp, created_at)
                VALUES ('aaaaaaaa-0000-0000-0000-0000000000a1', 'Dona A', 'a@x.com', 'A@X.COM', 'h', 's', 'c', now());
                INSERT INTO tenants (id, name, document_type, document_number, city, state, terms_version, terms_accepted_at, terms_accepted_by_user_id, created_at)
                VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'Marmitaria A', 'CNPJ', '11222333000181', 'Uberlandia', 'MG', '2026-10', now(), 'aaaaaaaa-0000-0000-0000-0000000000a1', now());
                INSERT INTO tenant_employees (id, tenant_id, user_id, display_name, status, security_stamp, joined_at)
                VALUES ('aaaaaaaa-0000-0000-0000-0000000000e1', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000a1', 'Dona A', 'ACTIVE', gen_random_uuid(), now());
                INSERT INTO business_days (id, tenant_id, business_date, status, opening_balance, opened_by_employee_id, opened_at)
                VALUES ('aaaaaaaa-0000-0000-0000-0000000000d1', 'aaaaaaaa-0000-0000-0000-000000000001', '2026-10-27', 'OPEN', 0, 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
                INSERT INTO orders (id, tenant_id, business_day_id, business_date, order_number, order_type, origin, fulfillment_type,
                                    customer_name, items_total, weighings_total, total_amount, production_status, created_by_employee_id, created_at, updated_at)
                VALUES ('aaaaaaaa-0000-0000-0000-000000000c01', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-10-27', 1,
                        'MARMITA', 'APP', 'PICKUP', 'Joao', 20.00, 0, 20.00, 'OPEN', 'aaaaaaaa-0000-0000-0000-0000000000e1', now(), now()),
                       ('aaaaaaaa-0000-0000-0000-000000000c02', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-10-27', 2,
                        'KILO', 'APP', 'DINE_IN', NULL, 0, 0, 0, NULL, 'aaaaaaaa-0000-0000-0000-0000000000e1', now(), now());
                """, connection);
            await seed.ExecuteNonQueryAsync(Ct);
        }

        await using (var context = new AppDbContext(options.Options, new TenantContext(), Array.Empty<IModuleModelConfiguration>()))
        {
            await context.Database.MigrateAsync(Ct);
        }

        await using (var connection = new NpgsqlConnection(superuser))
        {
            await connection.OpenAsync(Ct);
            await using var check = new NpgsqlCommand(
                """
                SELECT (SELECT count(*) FROM orders WHERE acceptance_status = 'ACCEPTED' AND accepted_at = created_at
                          AND accepted_by_employee_id = created_by_employee_id),
                       (SELECT fulfillment_status FROM orders WHERE order_type = 'MARMITA'),
                       (SELECT count(*) FROM orders WHERE order_type = 'KILO' AND fulfillment_status IS NULL),
                       (SELECT relforcerowsecurity FROM pg_class WHERE relname = 'orders')
                """, connection);
            await using var reader = await check.ExecuteReaderAsync(Ct);
            await reader.ReadAsync(Ct);
            reader.GetInt64(0).ShouldBe(2);
            reader.GetString(1).ShouldBe("PENDING");
            reader.GetInt64(2).ShouldBe(1);
            reader.GetBoolean(3).ShouldBeTrue("o FORCE da RLS volta depois do backfill");
        }

        NpgsqlConnection.ClearAllPools();
        await db.ExecuteAsSuperuserAsync($"DROP DATABASE {database} WITH (FORCE)");
    }

    /// 0004 (ADR-0044) num banco com tenants: cada um ganha a página inativa e o slug do nome (colisão → `-{cidade}`,
    /// depois `-2`; reservado → acrescenta a cidade), lendo `tenants` com a RLS `FORCE`, que volta no fim.
    [Fact]
    public async Task Migration_0004_backfills_storefront_and_slugs()
    {
        var database = "almoseis_slugs_" + Guid.NewGuid().ToString("N")[..8];
        await db.ExecuteAsSuperuserAsync($"CREATE DATABASE {database} OWNER almoseis_migrator");
        var superuser = new NpgsqlConnectionStringBuilder(db.SuperuserConnectionString) { Database = database }.ConnectionString;
        var migrator = new NpgsqlConnectionStringBuilder(db.ConnectionStringFor("almoseis_migrator_login", PostgresFixture.MigratorPassword))
        {
            Database = database,
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseAlmoSeisNpgsql(migrator);
        await using (var context = new AppDbContext(options.Options, new TenantContext(), Array.Empty<IModuleModelConfiguration>()))
        {
            await context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync("20261120000000_WebOrderAcceptance", Ct);
        }

        await using (var connection = new NpgsqlConnection(superuser))
        {
            await connection.OpenAsync(Ct);
            await using var seed = new NpgsqlCommand(
                """
                INSERT INTO users (id, full_name, email, normalized_email, password_hash, security_stamp, concurrency_stamp, created_at)
                VALUES ('aaaaaaaa-0000-0000-0000-0000000000a1', 'Dona A', 'a@x.com', 'A@X.COM', 'h', 's', 'c', now());
                INSERT INTO tenants (id, name, document_type, document_number, city, state, terms_version, terms_accepted_at, terms_accepted_by_user_id, created_at)
                VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'Marmitaria da Zélia', 'CNPJ', '11222333000181', 'Uberlândia', 'MG', '2026-10', now(), 'aaaaaaaa-0000-0000-0000-0000000000a1', now() - interval '3 days'),
                       ('aaaaaaaa-0000-0000-0000-000000000002', 'Marmitaria da Zélia', 'CNPJ', '11222333000182', 'Uberlândia', 'MG', '2026-10', now(), 'aaaaaaaa-0000-0000-0000-0000000000a1', now() - interval '2 days'),
                       ('aaaaaaaa-0000-0000-0000-000000000003', 'Marmitaria da Zélia', 'CNPJ', '11222333000183', 'Uberlândia', 'MG', '2026-10', now(), 'aaaaaaaa-0000-0000-0000-0000000000a1', now() - interval '1 day'),
                       ('aaaaaaaa-0000-0000-0000-000000000004', 'Admin', 'CNPJ', '11222333000184', 'Araguari', 'MG', '2026-10', now(), 'aaaaaaaa-0000-0000-0000-0000000000a1', now());
                """, connection);
            await seed.ExecuteNonQueryAsync(Ct);
        }

        await using (var context = new AppDbContext(options.Options, new TenantContext(), Array.Empty<IModuleModelConfiguration>()))
        {
            await context.Database.MigrateAsync(Ct);
        }

        await using (var connection = new NpgsqlConnection(superuser))
        {
            await connection.OpenAsync(Ct);
            await using var check = new NpgsqlCommand(
                """
                SELECT string_agg(slug, ',' ORDER BY tenant_id),
                       (SELECT count(*) FROM storefront_settings WHERE NOT is_page_active AND public_name IN ('Marmitaria da Zélia', 'Admin')),
                       (SELECT relforcerowsecurity FROM pg_class WHERE relname = 'tenants')
                  FROM store_slugs WHERE is_current
                """, connection);
            await using var reader = await check.ExecuteReaderAsync(Ct);
            await reader.ReadAsync(Ct);
            reader.GetString(0).ShouldBe("marmitaria-da-zelia,marmitaria-da-zelia-uberlandia,marmitaria-da-zelia-2,admin-araguari");
            reader.GetInt64(1).ShouldBe(4);
            reader.GetBoolean(2).ShouldBeTrue("o FORCE da RLS de tenants volta depois do preenchimento");
        }

        NpgsqlConnection.ClearAllPools();
        await db.ExecuteAsSuperuserAsync($"DROP DATABASE {database} WITH (FORCE)");
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
