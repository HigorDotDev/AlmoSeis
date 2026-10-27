using AlmoSeis.Infrastructure.Audit;
using AlmoSeis.Infrastructure.Persistence;
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

        var appConnection = configuration.GetConnectionString("App")
            ?? throw new InvalidOperationException("ConnectionStrings:App não configurada.");
        var platformConnection = configuration.GetConnectionString("Platform")
            ?? throw new InvalidOperationException("ConnectionStrings:Platform não configurada.");

        services.AddDbContext<AppDbContext>((sp, options) => options
            .UseAlmoSeisNpgsql(appConnection)
            .AddInterceptors(
                sp.GetRequiredService<TenantSessionConnectionInterceptor>(),
                sp.GetRequiredService<TenantGuardSaveChangesInterceptor>()));

        services.AddDbContext<PlatformDbContext>(options => options.UseAlmoSeisNpgsql(platformConnection));

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IAuditWriter, AuditWriter>();

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
