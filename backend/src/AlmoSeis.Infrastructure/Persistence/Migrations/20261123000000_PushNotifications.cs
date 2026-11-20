using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlmoSeis.Infrastructure.Persistence.Migrations;

/// 05 §5.17, alteração 0006: push para pedidos da página (registro por instalação, entregas e outbox agendado; ADR-0049).
[DbContext(typeof(AppDbContext))]
[Migration("20261123000000_PushNotifications")]
public sealed class PushNotifications : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(EmbeddedSql.Read("0006_PushNotifications.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations do AlmoSeis são só para frente (expand/contract, docs/09-devops.md §9.4).");
}
