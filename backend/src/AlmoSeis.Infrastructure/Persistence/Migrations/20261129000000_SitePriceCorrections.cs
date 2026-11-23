using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlmoSeis.Infrastructure.Persistence.Migrations;

/// 05 §5.17, alteração 0012: correções da revisão do EP-003 (ADR-0057). Preço de site `0.00` aceito na bebida do
/// restaurante (`products_site_price_check` `>= 0`), com a volta à página das bebidas de `0.00` que a 0009 escondeu;
/// tamanhos de marmita inativos com a chave "vende no site" como a carga dos ativos; e `site_delivery_fee_total` no
/// fechamento, com o "Vendido pelo site" sem a taxa de entrega e os fechamentos existentes recalculados quando possível.
[DbContext(typeof(AppDbContext))]
[Migration("20261129000000_SitePriceCorrections")]
public sealed class SitePriceCorrections : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(EmbeddedSql.Read("0012_SitePriceCorrections.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations do AlmoSeis são só para frente (expand/contract, docs/09-devops.md §9.4).");
}
