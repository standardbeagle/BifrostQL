using System.Reflection;
using System.Text;
using BifrostQL.Core.Model;

// Embedded resource loader for quickstart schemas
public static class QuickstartSchemas
{
    private static readonly Assembly ResourceAssembly = typeof(QuickstartSchemas).Assembly;

    public static Task<string?> LoadSchemaSql(string schemaName)
    {
        return LoadEmbeddedResourceAsync($"BifrostQL.UI.Schemas.{schemaName}.sql");
    }

    public static Task<string?> LoadSeedSql(string schemaName, string dataSize)
    {
        return LoadEmbeddedResourceAsync($"BifrostQL.UI.Schemas.{schemaName}-seed-{dataSize}.sql");
    }

    /// <summary>
    /// Loads the bundled per-connection profile config (<c>&lt;schema&gt;.bifrost.json</c>)
    /// for a quickstart schema, or null when none is embedded.
    /// </summary>
    public static Task<string?> LoadSampleConfig(string schema)
    {
        return LoadEmbeddedResourceAsync($"BifrostQL.UI.Schemas.{schema}.bifrost.json");
    }

    public static async Task ExecuteStatementsAsync(IDbConnFactory factory, string[] statements, CancellationToken ct)
    {
        await using var conn = factory.GetConnection();
        await conn.OpenAsync(ct);
        await using var pragmaCmd = conn.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA foreign_keys = ON;";
        await pragmaCmd.ExecuteNonQueryAsync(ct);

        foreach (var statement in statements)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = statement;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<string?> LoadEmbeddedResourceAsync(string resourceName)
    {
        await using var stream = ResourceAssembly.GetManifestResourceStream(resourceName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}
