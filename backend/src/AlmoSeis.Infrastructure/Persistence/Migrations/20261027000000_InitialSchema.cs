using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlmoSeis.Infrastructure.Persistence.Migrations;

/// Schema inicial: exatamente os blocos sql de docs/05-ddl.md, exceto os papéis (ADR-0042).
[DbContext(typeof(AppDbContext))]
[Migration("20261027000000_InitialSchema")]
public sealed class InitialSchema : Migration
{
    // A posse dos objetos fica com o papel de grupo almoseis_migrator porque o login de migration é
    // criado com `ALTER ROLE ... SET role = 'almoseis_migrator'` (05 §5.1, 09 §9.2).
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(EmbeddedSql.Read("0001_InitialSchema.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations do AlmoSeis são só para frente (expand/contract, docs/09-devops.md §9.4).");
}
