using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlmoSeis.Infrastructure.Persistence.Migrations;

/// 05 §5.17, alteração 0002: `shared_devices.platform` aceita `WEB` (ADR-0053).
[DbContext(typeof(AppDbContext))]
[Migration("20261119000000_SharedDeviceWebPlatform")]
public sealed class SharedDeviceWebPlatform : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(EmbeddedSql.Read("0002_SharedDeviceWebPlatform.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations do AlmoSeis são só para frente (expand/contract, docs/09-devops.md §9.4).");
}
