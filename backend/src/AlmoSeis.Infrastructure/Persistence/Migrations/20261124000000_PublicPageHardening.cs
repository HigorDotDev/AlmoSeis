using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlmoSeis.Infrastructure.Persistence.Migrations;

/// 05 §5.17, alteração 0007: correções da revisão do EP-002 (token de push só da mesma instalação, token de acompanhamento só por função, cache de geocodificação com HMAC e 4 casas).
[DbContext(typeof(AppDbContext))]
[Migration("20261124000000_PublicPageHardening")]
public sealed class PublicPageHardening : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(EmbeddedSql.Read("0007_PublicPageHardening.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations do AlmoSeis são só para frente (expand/contract, docs/09-devops.md §9.4).");
}
