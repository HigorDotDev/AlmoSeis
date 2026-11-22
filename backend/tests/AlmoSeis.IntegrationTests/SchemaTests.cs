using AlmoSeis.Infrastructure;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
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

        reader.GetInt64(0).ShouldBe(57, "tabelas de 05-ddl.md");
        reader.GetInt64(1).ShouldBe(113, "políticas de RLS de 05 §5.12 e §5.17");
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

        notices.Count(n => n.StartsWith("ok:", StringComparison.Ordinal)).ShouldBe(84, string.Join(" | ", notices));
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

    /// 0008 (ADR-0054, Q-303) num banco com tenants: cada um ganha `RESTAURANT` pela migração (sem autor) e o rótulo
    /// `RESTAURANT`, lendo `tenants` com a RLS `FORCE`, que volta no fim.
    [Fact]
    public async Task Migration_0008_backfills_restaurant_behavior_and_label()
    {
        var database = "almoseis_behaviors_" + Guid.NewGuid().ToString("N")[..8];
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
            await context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync("20261124000000_PublicPageHardening", Ct);
        }

        await using (var connection = new NpgsqlConnection(superuser))
        {
            await connection.OpenAsync(Ct);
            await using var seed = new NpgsqlCommand(
                """
                INSERT INTO users (id, full_name, email, normalized_email, password_hash, security_stamp, concurrency_stamp, created_at)
                VALUES ('aaaaaaaa-0000-0000-0000-0000000000a1', 'Dona A', 'a@x.com', 'A@X.COM', 'h', 's', 'c', now());
                INSERT INTO tenants (id, name, document_type, document_number, city, state, terms_version, terms_accepted_at, terms_accepted_by_user_id, created_at)
                VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'Marmitaria A', 'CNPJ', '11222333000181', 'Uberlandia', 'MG', '2026-10', now(), 'aaaaaaaa-0000-0000-0000-0000000000a1', now()),
                       ('aaaaaaaa-0000-0000-0000-000000000002', 'Restaurante B', 'CNPJ', '11222333000182', 'Uberlandia', 'MG', '2026-10', now(), 'aaaaaaaa-0000-0000-0000-0000000000a1', now());
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
                SELECT (SELECT count(*) FROM tenant_behaviors
                         WHERE behavior = 'RESTAURANT' AND origin = 'SYSTEM' AND enabled_by_user_id IS NULL),
                       (SELECT count(*) FROM tenant_behaviors),
                       (SELECT count(*) FROM tenant_labels WHERE label = 'RESTAURANT' AND behavior = 'RESTAURANT'),
                       (SELECT count(*) FROM tenant_labels),
                       (SELECT relforcerowsecurity FROM pg_class WHERE relname = 'tenants'),
                       (SELECT bool_and(relforcerowsecurity) FROM pg_class WHERE relname IN ('tenant_behaviors', 'tenant_labels'))
                """, connection);
            await using var reader = await check.ExecuteReaderAsync(Ct);
            await reader.ReadAsync(Ct);
            reader.GetInt64(0).ShouldBe(2);
            reader.GetInt64(1).ShouldBe(2, "um comportamento por tenant migrado");
            reader.GetInt64(2).ShouldBe(2);
            reader.GetInt64(3).ShouldBe(2, "um rótulo por tenant migrado");
            reader.GetBoolean(4).ShouldBeTrue("o FORCE da RLS de tenants volta depois do preenchimento");
            reader.GetBoolean(5).ShouldBeTrue("as tabelas novas nascem com RLS FORCE");
        }

        NpgsqlConnection.ClearAllPools();
        await db.ExecuteAsSuperuserAsync($"DROP DATABASE {database} WITH (FORCE)");
    }

    /// 0009 (ADR-0057 item 15) num banco com catálogo e pedidos: tamanho de marmita ativo e bebida visível ganham preço
    /// de site igual ao de balcão (a bebida de 0.00 sai do site), pedido do site vira `SITE`, e o `FORCE` da RLS volta.
    [Fact]
    public async Task Migration_0009_backfills_site_prices_and_price_list()
    {
        var database = "almoseis_siteprice_" + Guid.NewGuid().ToString("N")[..8];
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
            await context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync("20261125000000_EstablishmentBehaviors", Ct);
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
                INSERT INTO marmita_sizes (id, tenant_id, name, price, is_active)
                VALUES ('aaaaaaaa-0000-0000-0000-0000000000f1', 'aaaaaaaa-0000-0000-0000-000000000001', 'M', 20.00, true),
                       ('aaaaaaaa-0000-0000-0000-0000000000f2', 'aaaaaaaa-0000-0000-0000-000000000001', 'G', 25.00, false);
                INSERT INTO products (id, tenant_id, kind, name, default_price, requires_preparation, is_visible_on_page, created_at, updated_at)
                VALUES ('aaaaaaaa-0000-0000-0000-0000000000b1', 'aaaaaaaa-0000-0000-0000-000000000001', 'BEVERAGE', 'Coca', 5.00, false, true, now(), now()),
                       ('aaaaaaaa-0000-0000-0000-0000000000b2', 'aaaaaaaa-0000-0000-0000-000000000001', 'BEVERAGE', 'Agua da casa', 0.00, false, true, now(), now()),
                       ('aaaaaaaa-0000-0000-0000-0000000000b3', 'aaaaaaaa-0000-0000-0000-000000000001', 'BEVERAGE', 'Suco', 7.00, false, false, now(), now()),
                       ('aaaaaaaa-0000-0000-0000-0000000000b4', 'aaaaaaaa-0000-0000-0000-000000000001', 'ADDON', 'Ovo', 2.00, true, true, now(), now());
                INSERT INTO business_days (id, tenant_id, business_date, status, opening_balance, opened_by_employee_id, opened_at)
                VALUES ('aaaaaaaa-0000-0000-0000-0000000000d1', 'aaaaaaaa-0000-0000-0000-000000000001', '2026-11-22', 'OPEN', 0, 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
                INSERT INTO orders (id, tenant_id, business_day_id, business_date, order_number, order_type, origin, fulfillment_type,
                                    customer_name, items_total, weighings_total, total_amount, production_status, accepted_at,
                                    fulfillment_status, fulfillment_status_changed_at, created_by_employee_id, created_at, updated_at)
                VALUES ('aaaaaaaa-0000-0000-0000-000000000c01', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-11-22', 1,
                        'MARMITA', 'APP', 'PICKUP', 'Joao', 20.00, 0, 20.00, 'OPEN', now(), 'PENDING', now(), 'aaaaaaaa-0000-0000-0000-0000000000e1', now(), now()),
                       ('aaaaaaaa-0000-0000-0000-000000000c02', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-11-22', 2,
                        'MARMITA', 'WEB', 'PICKUP', 'Maria', 20.00, 0, 20.00, 'OPEN', now(), 'PENDING', now(), NULL, now(), now());
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
                SELECT (SELECT string_agg(name || ':' || sells_on_site || ':' || coalesce(site_price::text, '-'), ',' ORDER BY name) FROM marmita_sizes),
                       (SELECT string_agg(name || ':' || is_visible_on_page || ':' || coalesce(site_price::text, '-'), ',' ORDER BY name) FROM products),
                       (SELECT string_agg(origin || ':' || price_list, ',' ORDER BY origin) FROM orders),
                       (SELECT bool_and(relforcerowsecurity) FROM pg_class WHERE relname IN ('marmita_sizes', 'products', 'orders')),
                       (SELECT bool_and(relforcerowsecurity) FROM pg_class
                         WHERE relname IN ('dish_categories', 'dishes', 'dish_sizes', 'beverage_types', 'beverage_sizes', 'beverage_flavors',
                                           'combos', 'combo_components', 'addon_groups', 'addon_options', 'addon_group_links'))
                """, connection);
            await using var reader = await check.ExecuteReaderAsync(Ct);
            await reader.ReadAsync(Ct);
            reader.GetString(0).ShouldBe("G:false:-,M:true:20.00", "só o tamanho ativo vende no site, pelo preço de balcão");
            reader.GetString(1).ShouldBe("Agua da casa:false:-,Coca:true:5.00,Ovo:true:-,Suco:false:-",
                "bebida visível ganha o preço de balcão; a de 0.00 sai do site; adicional e bebida escondida sem preço de site");
            reader.GetString(2).ShouldBe("APP:COUNTER,WEB:SITE");
            reader.GetBoolean(3).ShouldBeTrue("o FORCE da RLS volta depois do preenchimento");
            reader.GetBoolean(4).ShouldBeTrue("as tabelas novas nascem com RLS FORCE");
        }

        NpgsqlConnection.ClearAllPools();
        await db.ExecuteAsSuperuserAsync($"DROP DATABASE {database} WITH (FORCE)");
    }

    /// 0010 (ADR-0058) num banco com pedidos: o adicional existente recebe o tipo do pai (`parent_item_kind`) com a RLS
    /// `FORCE` de `order_items`, as restrições novas entram sobre os dados antigos, o fechamento existente fica com o
    /// "Vendido pelo site" 0 e o `FORCE` volta.
    [Fact]
    public async Task Migration_0010_backfills_parent_item_kind()
    {
        var database = "almoseis_alacarte_" + Guid.NewGuid().ToString("N")[..8];
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
            await context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync("20261126000000_FixedMenuSitePrice", Ct);
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
                INSERT INTO marmita_sizes (id, tenant_id, name, price)
                VALUES ('aaaaaaaa-0000-0000-0000-0000000000f1', 'aaaaaaaa-0000-0000-0000-000000000001', 'M', 20.00);
                INSERT INTO products (id, tenant_id, kind, name, default_price, requires_preparation, created_at, updated_at)
                VALUES ('aaaaaaaa-0000-0000-0000-0000000000b1', 'aaaaaaaa-0000-0000-0000-000000000001', 'ADDON', 'Ovo', 2.00, true, now(), now()),
                       ('aaaaaaaa-0000-0000-0000-0000000000b2', 'aaaaaaaa-0000-0000-0000-000000000001', 'BEVERAGE', 'Coca', 5.00, false, now(), now());
                INSERT INTO business_days (id, tenant_id, business_date, status, opening_balance, opened_by_employee_id, opened_at)
                VALUES ('aaaaaaaa-0000-0000-0000-0000000000d1', 'aaaaaaaa-0000-0000-0000-000000000001', '2026-11-22', 'OPEN', 0, 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
                INSERT INTO orders (id, tenant_id, business_day_id, business_date, order_number, order_type, origin, fulfillment_type,
                                    customer_name, items_total, weighings_total, total_amount, production_status, accepted_at,
                                    fulfillment_status, fulfillment_status_changed_at, created_by_employee_id, created_at, updated_at)
                VALUES ('aaaaaaaa-0000-0000-0000-000000000c01', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-11-22', 1,
                        'MARMITA', 'APP', 'PICKUP', 'Joao', 27.00, 0, 27.00, 'OPEN', now(), 'PENDING', now(), 'aaaaaaaa-0000-0000-0000-0000000000e1', now(), now());
                INSERT INTO order_items (id, tenant_id, order_id, parent_order_item_id, item_kind, marmita_size_id, product_id, quantity, unit_price,
                                         line_total, name_snapshot, requires_preparation, status, created_by_employee_id, created_at)
                VALUES ('aaaaaaaa-0000-0000-0000-000000000101', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c01', NULL,
                        'MARMITA', 'aaaaaaaa-0000-0000-0000-0000000000f1', NULL, 1, 20.00, 20.00, 'Marmita M', true, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now()),
                       ('aaaaaaaa-0000-0000-0000-000000000103', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c01', NULL,
                        'BEVERAGE', NULL, 'aaaaaaaa-0000-0000-0000-0000000000b2', 1, 5.00, 5.00, 'Coca', false, 'ACTIVE', 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
                INSERT INTO order_items (id, tenant_id, order_id, parent_order_item_id, item_kind, marmita_size_id, product_id, quantity, unit_price,
                                         line_total, name_snapshot, requires_preparation, status, created_by_employee_id, created_at)
                VALUES ('aaaaaaaa-0000-0000-0000-000000000102', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000c01',
                        'aaaaaaaa-0000-0000-0000-000000000101', 'ADDON', NULL, 'aaaaaaaa-0000-0000-0000-0000000000b1', 1, 2.00, 2.00, 'Ovo', true, 'ACTIVE',
                        'aaaaaaaa-0000-0000-0000-0000000000e1', now());
                INSERT INTO daily_closings (id, tenant_id, business_day_id, business_date, order_count, total_sold, delivery_fees_total, total_received,
                  received_from_previous_days, total_pending, canceled_count, canceled_total, reversed_count, reversed_total, opening_balance,
                  cash_received, cash_reversed, supplies, withdrawals, expected_cash, counted_cash, cash_difference, closed_by_employee_id, closed_at)
                VALUES ('aaaaaaaa-0000-0000-0000-0000000000c9', 'aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-0000000000d1', '2026-11-22',
                  1, 27.00, 0, 0, 0, 27.00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'aaaaaaaa-0000-0000-0000-0000000000e1', now());
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
                SELECT (SELECT string_agg(item_kind || ':' || coalesce(parent_item_kind, '-'), ',' ORDER BY id) FROM order_items),
                       (SELECT site_sold_total::text FROM daily_closings),
                       (SELECT relforcerowsecurity FROM pg_class WHERE relname = 'order_items')
                """, connection);
            await using var reader = await check.ExecuteReaderAsync(Ct);
            await reader.ReadAsync(Ct);
            reader.GetString(0).ShouldBe("MARMITA:-,ADDON:MARMITA,BEVERAGE:-", "só o adicional filho recebe o tipo do pai");
            reader.GetString(1).ShouldBe("0.00", "fechamento anterior à migração sem vendido pelo site");
            reader.GetBoolean(2).ShouldBeTrue("o FORCE da RLS de order_items volta depois do preenchimento");
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
        // As configurações de todos os módulos, como a API registra (inclui o cardápio fixo da 0009).
        using var api = new ApiFactory(db);
        var modules = api.Services.GetServices<IModuleModelConfiguration>().ToList();
        modules.ShouldNotBeEmpty();
        await using var context = new AppDbContext(options.Options, new TenantContext(), modules);

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var table = entityType.GetTableName()!;
            var columns = entityType.GetProperties().Select(p => $"\"{p.GetColumnName()}\"");
            var sql = $"SELECT {string.Join(", ", columns)} FROM \"{table}\" LIMIT 0";

            await Should.NotThrowAsync(() => context.Database.ExecuteSqlRawAsync(sql, Ct), $"{entityType.ClrType.Name} → {table}");
        }
    }
}
