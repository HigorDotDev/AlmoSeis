using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlmoSeis.Infrastructure.Persistence.Migrations;

/// 05 §5.17, alteração 0005: taxa de entrega por distância (cotação, cache por tenant, snapshot no pedido; ADR-0048/0052).
[DbContext(typeof(AppDbContext))]
[Migration("20261122000000_DeliveryByDistance")]
public sealed class DeliveryByDistance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(EmbeddedSql.Read("0005_DeliveryByDistance.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations do AlmoSeis são só para frente (expand/contract, docs/09-devops.md §9.4).");
}
