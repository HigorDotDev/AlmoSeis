using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlmoSeis.Infrastructure.Persistence.Migrations;

/// 05 §5.17, alteração 0008: comportamentos e rótulos do estabelecimento (EP-003, ADR-0054), com `RESTAURANT` e o rótulo `RESTAURANT` para os tenants existentes.
[DbContext(typeof(AppDbContext))]
[Migration("20261125000000_EstablishmentBehaviors")]
public sealed class EstablishmentBehaviors : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(EmbeddedSql.Read("0008_EstablishmentBehaviors.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations do AlmoSeis são só para frente (expand/contract, docs/09-devops.md §9.4).");
}
