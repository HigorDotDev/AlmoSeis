using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlmoSeis.Infrastructure.Persistence.Migrations;

/// 05 §5.17, alteração 0009: cardápio fixo (ADR-0056) e preço de site (ADR-0057), com o preço de site dos tamanhos de marmita ativos e das bebidas visíveis igual ao de balcão e `orders.price_list`.
[DbContext(typeof(AppDbContext))]
[Migration("20261126000000_FixedMenuSitePrice")]
public sealed class FixedMenuSitePrice : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(EmbeddedSql.Read("0009_FixedMenuSitePrice.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations do AlmoSeis são só para frente (expand/contract, docs/09-devops.md §9.4).");
}
