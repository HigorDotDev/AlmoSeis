using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Closing.Contracts;
using AlmoSeis.Modules.Closing.Domain;
using AlmoSeis.Modules.Menu.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Modules.Closing.Infrastructure;

/// Mapeamento de 05 §5.8.
internal sealed class ClosingModelConfiguration : IModuleModelConfiguration
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BusinessDay>(b =>
        {
            b.ToTable("business_days");
            b.HasKey(x => x.Id);
        });
    }
}

internal sealed class BusinessDayReader(AppDbContext db, ITenantContext tenant) : IBusinessDayReader
{
    public async Task<BusinessDayRef?> GetOpenAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId!.Value;
        var rows = await db.Database
            .SqlQuery<BusinessDayRow>(
                $"""
                -- Colunas em snake_case: a convenção de nomes vale também para o tipo do resultado.
                SELECT id, business_date, version
                  FROM business_days
                 WHERE tenant_id = {tenantId} AND status = 'OPEN'
                   FOR SHARE
                """)
            .ToListAsync(cancellationToken);

        return rows is [var row] ? new BusinessDayRef(row.Id, row.BusinessDate, row.Version) : null;
    }

    private sealed record BusinessDayRow(Guid Id, DateOnly BusinessDate, int Version);
}

/// Porta do Menu (GET /menus/current): a data do expediente aberto.
internal sealed class OpenBusinessDate(AppDbContext db) : IOpenBusinessDate
{
    public async Task<DateOnly?> GetAsync(CancellationToken cancellationToken) =>
        await db.Set<BusinessDay>().AsNoTracking()
            .Where(d => d.Status == BusinessDayStatus.Open)
            .Select(d => (DateOnly?)d.BusinessDate)
            .SingleOrDefaultAsync(cancellationToken);
}

/// BusinessDayOpened → `BusinessDayOpened` para todo o tenant (06 §6.14).
internal sealed class BusinessDayOpenedMapper(ITenantContext tenant) : IntegrationEventMapper<BusinessDayOpened>
{
    protected override IEnumerable<IntegrationEnvelope> Map(BusinessDayOpened domainEvent)
    {
        yield return new IntegrationEnvelope(
            "BusinessDayOpened", "BUSINESS_DAY", domainEvent.BusinessDayId, tenant.EmployeeId,
            new { businessDate = domainEvent.BusinessDate });
    }
}
