using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlmoSeis.Infrastructure.Persistence.Migrations;

/// 05 §5.17, alteração 0004: página de pedidos (perfil público, slug, acompanhamento, LGPD; ADR-0044/0045/0047/0052).
[DbContext(typeof(AppDbContext))]
[Migration("20261121000000_WebStorefront")]
public sealed class WebStorefront : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(EmbeddedSql.Read("0004_WebStorefront.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations do AlmoSeis são só para frente (expand/contract, docs/09-devops.md §9.4).");
}
