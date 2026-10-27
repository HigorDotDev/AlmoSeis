using System.Linq.Expressions;
using System.Reflection;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Infrastructure.Persistence;

/// Cada módulo registra uma configuração por DI; o AppDbContext não conhece os módulos (ADR-0029).
public interface IModuleModelConfiguration
{
    void Configure(ModelBuilder modelBuilder);
}

/// Contexto único da aplicação (papel almoseis_app). Sem DbSet públicos: módulos usam Set&lt;T&gt;().
public class AppDbContext : DbContext
{
    private static readonly MethodInfo ApplyTenantFilterMethod =
        typeof(AppDbContext).GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly ITenantContext _tenant;
    private readonly IEnumerable<IModuleModelConfiguration> _moduleConfigurations;

    public AppDbContext(
        DbContextOptions<AppDbContext> options,
        ITenantContext tenant,
        IEnumerable<IModuleModelConfiguration> moduleConfigurations)
        : this((DbContextOptions)options, tenant, moduleConfigurations)
    {
    }

    protected AppDbContext(
        DbContextOptions options,
        ITenantContext tenant,
        IEnumerable<IModuleModelConfiguration> moduleConfigurations)
        : base(options)
    {
        _tenant = tenant;
        _moduleConfigurations = moduleConfigurations;
    }

    public ITenantContext Tenant => _tenant;

    // Lidos pelo query filter a cada execução (parametrizados pelo EF).
    internal Guid? CurrentTenantId => _tenant.TenantId;

    internal bool BypassTenantFilter => _tenant.BypassTenantFilter;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Money>().HaveConversion<MoneyConverter>().HavePrecision(12, 2);
        configurationBuilder.Properties<Grams>().HaveConversion<GramsConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        foreach (var configuration in _moduleConfigurations)
        {
            configuration.Configure(modelBuilder);
        }

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => t.BaseType is null).ToList())
        {
            // Versão explícita como token de concorrência de todo agregado (ADR-0034).
            if (typeof(AggregateRoot).IsAssignableFrom(entityType.ClrType))
            {
                modelBuilder.Entity(entityType.ClrType).Property(nameof(AggregateRoot.Version)).IsConcurrencyToken();
            }

            // Filtro de tenant onde TenantId é coluna mapeada. Entidades cuja chave é o próprio tenant
            // (ex.: tenant_settings) ficam só com a RLS e com consulta pela chave.
            if (typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType) && entityType.FindProperty(nameof(ITenantOwned.TenantId)) is not null)
            {
                ApplyTenantFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
            }
        }
    }

    private void ApplyTenantFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantOwned
    {
        Expression<Func<TEntity, bool>> filter = e => BypassTenantFilter || e.TenantId == CurrentTenantId;
        modelBuilder.Entity<TEntity>().HasQueryFilter(filter);
    }
}

/// Papel almoseis_platform: admin, webhooks, dispatcher e jobs (ADR-0025). Ignora o filtro de tenant.
public sealed class PlatformDbContext(
    DbContextOptions<PlatformDbContext> options,
    IEnumerable<IModuleModelConfiguration> moduleConfigurations)
    : AppDbContext(options, new TenantContext { BypassTenantFilter = true }, moduleConfigurations);
