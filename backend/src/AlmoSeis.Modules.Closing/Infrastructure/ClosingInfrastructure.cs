using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Closing.Contracts;
using AlmoSeis.Modules.Closing.Domain;
using AlmoSeis.Modules.Menu.Contracts;
using AlmoSeis.SharedKernel.Domain;
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

        modelBuilder.Entity<CashMovement>(b =>
        {
            b.ToTable("cash_movements");
            b.HasKey(x => x.Id);
            b.Property(x => x.Description).HasMaxLength(200);
        });

        modelBuilder.Entity<DailyClosing>(b =>
        {
            b.ToTable("daily_closings");
            b.HasKey(x => x.Id);
            b.Property(x => x.Justification).HasMaxLength(500);
            b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.DailyClosingId);
            b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
            // Relação "de ordenação": o UPDATE do expediente e o INSERT do fechamento na mesma transação.
            b.HasOne<BusinessDay>().WithMany().HasForeignKey(x => x.BusinessDayId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DailyClosingLine>(b =>
        {
            b.ToTable("daily_closing_lines");
            b.HasKey(x => x.Id);
        });

        modelBuilder.Entity<ClosingAdjustment>(b =>
        {
            b.ToTable("closing_adjustments");
            b.HasKey(x => x.Id);
            b.Property(x => x.Reason).HasMaxLength(500);
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

/// Somas do expediente para o ClosingCalculator (07 §7.9): uma consulta por bloco, por SQL, porque pedidos e
/// pagamentos são de módulos acima do Closing (07 §7.1). Colunas em snake_case (convenção de nomes).
internal sealed class ClosingInputsQuery(AppDbContext db, ITenantContext tenant)
{
    public async Task<ClosingInputs> LoadAsync(Guid businessDayId, Money openingBalance, CancellationToken cancellationToken)
    {
        var t = tenant.TenantId!.Value;

        var orders = await db.Database.SqlQuery<OrderRow>(
            $"""
            SELECT order_type, cancellation_status = 'CANCELED' AS canceled, count(*)::int AS count,
                   coalesce(sum(total_amount), 0) AS sold, coalesce(sum(delivery_fee), 0) AS delivery_fees,
                   coalesce(sum(total_amount - paid_amount), 0) AS pending
              FROM orders
             WHERE tenant_id = {t} AND business_day_id = {businessDayId}
             GROUP BY order_type, cancellation_status
            """).ToListAsync(cancellationToken);

        // Recebido neste expediente, de qualquer data de venda (inclui os estornados depois: o estorno conta à parte).
        var payments = await db.Database.SqlQuery<PaymentRow>(
            $"""
            SELECT o.order_type, p.payment_method, o.business_day_id <> p.business_day_id AS from_previous_day,
                   count(*)::int AS count, sum(p.amount) AS amount
              FROM payments p
              JOIN orders o ON o.tenant_id = p.tenant_id AND o.id = p.order_id
             WHERE p.tenant_id = {t} AND p.business_day_id = {businessDayId}
             GROUP BY o.order_type, p.payment_method, o.business_day_id <> p.business_day_id
            """).ToListAsync(cancellationToken);

        var reversals = await db.Database.SqlQuery<ReversalRow>(
            $"""
            SELECT o.order_type, r.payment_method, count(*)::int AS count, sum(r.amount) AS amount
              FROM payment_reversals r
              JOIN orders o ON o.tenant_id = r.tenant_id AND o.id = r.order_id
             WHERE r.tenant_id = {t} AND r.business_day_id = {businessDayId}
             GROUP BY o.order_type, r.payment_method
            """).ToListAsync(cancellationToken);

        var movements = await db.Database.SqlQuery<MovementRow>(
            $"""
            SELECT movement_type, sum(amount) AS amount
              FROM cash_movements
             WHERE tenant_id = {t} AND business_day_id = {businessDayId}
             GROUP BY movement_type
            """).ToListAsync(cancellationToken);

        return new ClosingInputs(
            openingBalance,
            orders.Select(o => new OrderTotalsRow(o.OrderType, o.Canceled, o.Count, Money.Of(o.Sold), Money.Of(o.DeliveryFees), Money.Of(o.Pending))).ToList(),
            payments.Select(p => new PaymentTotalsRow(p.OrderType, p.PaymentMethod, p.FromPreviousDay, p.Count, Money.Of(p.Amount))).ToList(),
            reversals.Select(r => new ReversalTotalsRow(r.OrderType, r.PaymentMethod, r.Count, Money.Of(r.Amount))).ToList(),
            Money.Of(movements.Where(m => m.MovementType == "SUPPLY").Sum(m => m.Amount)),
            Money.Of(movements.Where(m => m.MovementType == "WITHDRAWAL").Sum(m => m.Amount)));
    }

    private sealed record OrderRow(string OrderType, bool Canceled, int Count, decimal Sold, decimal DeliveryFees, decimal Pending);

    private sealed record PaymentRow(string OrderType, string PaymentMethod, bool FromPreviousDay, int Count, decimal Amount);

    private sealed record ReversalRow(string OrderType, string PaymentMethod, int Count, decimal Amount);

    private sealed record MovementRow(string MovementType, decimal Amount);
}

/// ClosingFinalized → `ClosingFinalized` para todo o tenant (06 §6.14).
internal sealed class ClosingFinalizedMapper(ITenantContext tenant) : IntegrationEventMapper<ClosingFinalized>
{
    protected override IEnumerable<IntegrationEnvelope> Map(ClosingFinalized e)
    {
        yield return new IntegrationEnvelope("ClosingFinalized", "DAILY_CLOSING", e.DailyClosingId, tenant.EmployeeId,
            new { businessDate = e.BusinessDate, dailyClosingId = e.DailyClosingId });
    }
}
