using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlmoSeis.Infrastructure.Persistence.Migrations;

/// 05 §5.17, alteração 0010: modalidade `A_LA_CARTE` (ADR-0058), itens do cardápio fixo e combo em três níveis em `order_items`, com `parent_item_kind` preenchido nos adicionais existentes, e fechamento com a linha `A_LA_CARTE` e o "Vendido pelo site" (ADR-0057 item 12).
[DbContext(typeof(AppDbContext))]
[Migration("20261127000000_ALaCarteOrders")]
public sealed class ALaCarteOrders : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(EmbeddedSql.Read("0010_ALaCarteOrders.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations do AlmoSeis são só para frente (expand/contract, docs/09-devops.md §9.4).");
}
