using System.Text.RegularExpressions;
using AlmoSeis.Infrastructure.Persistence.Migrations;
using Shouldly;

namespace AlmoSeis.UnitTests.Persistence;

/// ADR-0042: as migrations SQL são exatamente o docs/05-ddl.md. A inicial = os blocos antes de §5.17 (sem o de
/// papéis); cada alteração = os blocos da subseção "### NNNN_Nome" de §5.17.
/// Falhou? Rode backend/tools/Sync-DdlMigration.ps1 -Target {script} e revise o diff.
public sealed partial class DdlMigrationSyncTests
{
    private static readonly string Doc =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Ddl", "05-ddl.md")).ReplaceLineEndings("\n");

    private static int AlterationsAt => Doc.IndexOf("\n## 5.17 ", StringComparison.Ordinal);

    [Fact]
    public void Initial_migration_matches_ddl_document()
    {
        var initial = AlterationsAt >= 0 ? Doc[..AlterationsAt] : Doc;
        var expected = string.Join("\n", SqlBlocks(initial).Where(b => !b.Contains("CREATE ROLE", StringComparison.Ordinal)));

        Script("0001_InitialSchema.sql").ShouldBe(expected);
    }

    [Fact]
    public void Every_alteration_has_a_migration_script_identical_to_the_document()
    {
        var alterations = AlterationHeading().Matches(Doc[AlterationsAt..]).ToList();
        alterations.ShouldNotBeEmpty();

        foreach (var alteration in alterations)
        {
            var name = alteration.Groups["name"].Value;
            Script(name + ".sql").ShouldBe(string.Join("\n", SqlBlocks(alteration.Groups["body"].Value)), name);
        }
    }

    private static IEnumerable<string> SqlBlocks(string markdown) =>
        Regex.Matches(markdown, "(?s)```sql\n(.*?)```").Select(m => m.Groups[1].Value);

    private static string Script(string fileName)
    {
        var migration = EmbeddedSql.Read(fileName).ReplaceLineEndings("\n");
        return migration[(migration.IndexOf('\n', StringComparison.Ordinal) + 1)..];
    }

    [GeneratedRegex(@"(?s)\n### (?<name>\d{4}_\w+)(?<body>.*?)(?=\n### |\n## |\z)")]
    private static partial Regex AlterationHeading();
}
