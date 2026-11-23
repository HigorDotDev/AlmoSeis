using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlmoSeis.Infrastructure.Persistence.Migrations;

/// 05 §5.17, alteração 0011: delivery por telefone (ADR-0059). Forma prevista e "troco para" também no pedido do app,
/// só em entrega e retirada (`ck_orders_web_fields` vira `ck_orders_risk_flag`), troco só com a forma dinheiro mesmo com
/// a forma nula (`ck_orders_change_for` reescrito, com a correção dos dados) e índice do último pedido do cliente.
[DbContext(typeof(AppDbContext))]
[Migration("20261128000000_PhoneDeliveryCustomer")]
public sealed class PhoneDeliveryCustomer : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(EmbeddedSql.Read("0011_PhoneDeliveryCustomer.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations do AlmoSeis são só para frente (expand/contract, docs/09-devops.md §9.4).");
}
