using AlmoSeis.Infrastructure.Audit;
using AlmoSeis.Infrastructure.Concurrency;
using AlmoSeis.Infrastructure.Email;
using AlmoSeis.Infrastructure.Idempotency;
using AlmoSeis.Infrastructure.Observability;
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
using Microsoft.Extensions.Options;

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
        services.AddScoped<CommitCallbacks>();

        // Connection strings lidas na resolução (não no registro): os testes sobrescrevem a configuração depois do Program.
        services.AddDbContext<AppDbContext>((sp, options) => options
            .UseAlmoSeisNpgsql(RequiredConnectionString(sp, "App"))
            .AddInterceptors(
                sp.GetRequiredService<TenantSessionConnectionInterceptor>(),
                sp.GetRequiredService<TenantGuardSaveChangesInterceptor>(),
                sp.GetRequiredService<CommitCallbacks>()));

        services.AddDbContext<PlatformDbContext>((sp, options) => options.UseAlmoSeisNpgsql(RequiredConnectionString(sp, "Platform")));

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<ITenantSession, TenantSession>();
        services.AddScoped<IConcurrencyContext, ConcurrencyContext>();

        // E-mail (ADR-0040, ADR-0060): o provedor sai de Email:Provider na resolução; os testes substituem o IEmailSender.
        services.AddOptions<EmailOptions>().Bind(configuration.GetSection(EmailOptions.Section)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>();
        services.AddSingleton<LoggingEmailSender>();
        services.AddSingleton<SmtpEmailSender>();
        services.AddSingleton<AcsEmailSender>();
        services.TryAddSingleton<IEmailSender>(sp => sp.GetRequiredService<IOptions<EmailOptions>>().Value.Provider switch
        {
            EmailProvider.Smtp => sp.GetRequiredService<SmtpEmailSender>(),
            EmailProvider.Acs => sp.GetRequiredService<AcsEmailSender>(),
            _ => sp.GetRequiredService<LoggingEmailSender>(),
        });

        // Anel de chaves do Data Protection em diretório persistente (DataProtection:KeysPath, ADR-0060).
        services.AddAlmoSeisDataProtection(configuration);

        services.AddOptions<IdempotencyOptions>().Bind(configuration.GetSection(IdempotencyOptions.Section));
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();

        // O IRealtimePublisher é registrado pelo host (SignalR na Api).
        services.AddOptions<OutboxOptions>().Bind(configuration.GetSection(OutboxOptions.Section));
        // Métricas próprias (Meter `AlmoSeis`) e a contagem do outbox do gauge e do `/health/ready` (07 §7.4).
        services.AddMetrics();
        services.AddSingleton<OutboxBacklogMonitor>();
        services.AddSingleton<AlmoSeisMetrics>();
        services.AddSingleton<OutboxDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());
        services.AddSingleton<SyncRetentionJob>();
        services.AddHostedService(sp => sp.GetRequiredService<SyncRetentionJob>());

        services.AddOptions<R2Options>().Bind(configuration.GetSection(R2Options.Section));
        services.AddSingleton<IObjectStorage, R2ObjectStorage>();

        // Segredos obrigatórios conferidos ao subir (falha clara em vez de erro na primeira requisição).
        services.AddRequiredSecretsValidation();
        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.Section)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();
        services.AddSingleton<IJwtTokenIssuer, JwtTokenIssuer>();
        services.AddSingleton<ISecretHasher, Sha256SecretHasher>();

        // Página de pedidos (ADR-0045, ADR-0047): Turnstile, hash do IP e token de acompanhamento. Fora de Development, a
        // API não sobe sem os segredos nem com a chave de teste do Turnstile.
        services.AddOptions<TurnstileOptions>().Bind(configuration.GetSection(TurnstileOptions.Section)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<TurnstileOptions>, TurnstileOptionsValidator>();
        services.AddHttpClient<ITurnstileVerifier, TurnstileVerifier>();
        services.AddOptions<PublicPageOptions>().Bind(configuration.GetSection(PublicPageOptions.Section)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<PublicPageOptions>, PublicPageOptionsValidator>();
        services.AddSingleton<IClientIpHasher, ClientIpHasher>();
        services.AddSingleton<ITrackingTokenIssuer, TrackingTokenIssuer>();

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
