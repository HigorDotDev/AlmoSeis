using System.Reflection;

namespace AlmoSeis.Infrastructure.Persistence.Migrations;

/// Lê os scripts SQL embutidos (Persistence/Migrations/Sql/*.sql), gerados a partir do 05 (ADR-0042).
public static class EmbeddedSql
{
    public static string Read(string fileName)
    {
        var assembly = typeof(EmbeddedSql).Assembly;
        var resource = assembly.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith("." + fileName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Script SQL embutido não encontrado: {fileName}");

        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
