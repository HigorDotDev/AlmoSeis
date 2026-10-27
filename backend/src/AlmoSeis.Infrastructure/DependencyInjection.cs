using AlmoSeis.Infrastructure.Audit;
using AlmoSeis.Infrastructure.Concurrency;
using AlmoSeis.Infrastructure.Idempotency;
using AlmoSeis.Infrastructure.Storage;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Realtime;
using AlmoSeis.Infrastructure.Security;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.SharedKernel.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AlmoSeis.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAlmoSeisInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IIdGenerator, IdGenerator>();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<TenantSessionConnectionInterceptor>();
        services.AddScoped<TenantGuardSaveChangesInterceptor>();

        // Connection strings lidas na resolução (não no registro): Key Vault e testes sobrescrevem a configuração depois.
        services.AddDbContext<AppDbContext>((sp, options) => options
            .UseAlmoSeisNpgsql(RequiredConnectionString(sp, "App"))
            .AddInterceptors(
                sp.GetRequiredService<TenantSessionConnectionInterceptor>(),
                sp.GetRequiredService<TenantGuardSaveChangesInterceptor>()));

        services.AddDbContext<PlatformDbContext>((sp, options) => options.UseAlmoSeisNpgsql(RequiredConnectionString(sp, "Platform")));

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<ITenantSession, TenantSession>();
        services.AddScoped<IConcurrencyContext, ConcurrencyContext>();

        services.TryAddSingleton<AlmoSeis.Infrastructure.Email.IEmailSender, AlmoSeis.Infrastructure.Email.LoggingEmailSender>();

        services.AddOptions<IdempotencyOptions>().Bind(configuration.GetSection(IdempotencyOptions.Section));
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();

        // O IRealtimePublisher é registrado pelo host (SignalR na Api).
        services.AddOptions<OutboxOptions>().Bind(configuration.GetSection(OutboxOptions.Section));
        services.AddSingleton<OutboxDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());
        services.AddSingleton<SyncRetentionJob>();
        services.AddHostedService(sp => sp.GetRequiredService<SyncRetentionJob>());

        services.AddOptions<ObjectStorageOptions>().Bind(configuration.GetSection(ObjectStorageOptions.Section));
        services.AddSingleton<IObjectStorage, PublicUrlObjectStorage>();

        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.Section));
        services.AddSingleton<IJwtTokenIssuer, JwtTokenIssuer>();
        services.AddSingleton<ISecretHasher, Sha256SecretHasher>();

        return services;
    }

    /// Chamado pelo host depois de registrar os módulos: envolve todo ICommandHandler na transação (07 §7.3).
    public static IServiceCollection DecorateCommandHandlers(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType.IsGenericType && d.ServiceType.GetGenericTypeDefinition() == typeof(ICommandHandler<,>)))
        {
            services.Decorate(typeof(ICommandHandler<,>), typeof(TransactionalCommandHandlerDecorator<,>));
        }

        return services;
    }

    private static string RequiredConnectionString(IServiceProvider services, string name) =>
        services.GetRequiredService<IConfiguration>().GetConnectionString(name) is { Length: > 0 } value && value != "REPLACE_ME"
            ? value
            : throw new InvalidOperationException($"ConnectionStrings:{name} não configurada.");

    public static DbContextOptionsBuilder UseAlmoSeisNpgsql(this DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention()
            // Migrations são SQL a partir do 05 (ADR-0042); não há snapshot de modelo para comparar.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
}

/// `dotnet ef database update` com o papel de migration (docs/09-devops.md §9.2).
public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ALMOSEIS_MIGRATOR_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=almoseis;Username=almoseis_migrator_login;Password=dev-migrator";
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        builder.UseAlmoSeisNpgsql(connection);
        return new AppDbContext(builder.Options, new TenantContext(), Array.Empty<IModuleModelConfiguration>());
    }
}
