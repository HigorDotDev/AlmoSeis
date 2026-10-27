using System.Text.RegularExpressions;
using AlmoSeis.Infrastructure.Persistence.Migrations;
using Shouldly;

namespace AlmoSeis.UnitTests.Persistence;

/// ADR-0042: a migration SQL é exatamente o docs/05-ddl.md (sem o bloco de papéis).
/// Falhou? Rode backend/tools/Sync-DdlMigration.ps1 e revise o diff.
public sealed class DdlMigrationSyncTests
{
    [Fact]
    public void Initial_migration_matches_ddl_document()
    {
        var doc = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Ddl", "05-ddl.md")).ReplaceLineEndings("\n");
        var blocks = Regex.Matches(doc, "(?s)```sql\n(.*?)```")
            .Select(m => m.Groups[1].Value)
            .Where(b => !b.Contains("CREATE ROLE", StringComparison.Ordinal));
        var expected = string.Join("\n", blocks);

        var migration = EmbeddedSql.Read("0001_InitialSchema.sql").ReplaceLineEndings("\n");
        var withoutHeader = migration[(migration.IndexOf('\n', StringComparison.Ordinal) + 1)..];

        withoutHeader.ShouldBe(expected);
    }
}
