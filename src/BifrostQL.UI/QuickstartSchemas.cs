using System.Reflection;
using System.Text;
using BifrostQL.Core.Model;

// Embedded resource loader for quickstart schemas
public static class QuickstartSchemas
{
    private static readonly Assembly ResourceAssembly = typeof(QuickstartSchemas).Assembly;
    private const string ResourcePrefix = "BifrostQL.UI.Schemas.";

    /// <summary>
    /// The quickstart schema names, derived from the embedded <c>Schemas/&lt;name&gt;.sql</c>
    /// DDL resources (seed files carry <c>-seed-</c>). The endpoint validates against this
    /// list, so a new schema file is offered without a second hand-kept list.
    /// </summary>
    public static IReadOnlyList<string> SchemaNames { get; } = ResourceAssembly
        .GetManifestResourceNames()
        .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                    && n.EndsWith(".sql", StringComparison.Ordinal)
                    && !n.Contains("-seed-", StringComparison.Ordinal))
        .Select(n => n[ResourcePrefix.Length..^".sql".Length])
        .Order(StringComparer.Ordinal)
        .ToArray();

    /// <summary>The seed sizes a quickstart accepts.</summary>
    public static IReadOnlyList<string> DataSizes { get; } = new[] { "sample", "full" };

    public static Task<string?> LoadSchemaSql(string schemaName)
    {
        return LoadEmbeddedResourceAsync($"{ResourcePrefix}{schemaName}.sql");
    }

    public static Task<string?> LoadSeedSql(string schemaName, string dataSize)
    {
        return LoadEmbeddedResourceAsync($"{ResourcePrefix}{schemaName}-seed-{dataSize}.sql");
    }

    /// <summary>
    /// Loads the bundled per-connection profile config (<c>&lt;schema&gt;.bifrost.json</c>)
    /// for a quickstart schema, or null when none is embedded.
    /// </summary>
    public static Task<string?> LoadSampleConfig(string schema)
    {
        return LoadEmbeddedResourceAsync($"{ResourcePrefix}{schema}.bifrost.json");
    }

    /// <summary>
    /// Runs a whole quickstart script as ONE command. Microsoft.Data.Sqlite executes every
    /// statement in the command text in order, and its tokenizer knows string literals,
    /// comments and trigger bodies, so the host never splits SQL itself. (A <c>Split(';')</c>
    /// here cut <c>'…the club; bypasses…'</c> in the membership-manager seed into an
    /// unterminated literal.)
    /// </summary>
    public static async Task ExecuteScriptAsync(IDbConnFactory factory, string sql, CancellationToken ct)
    {
        await using var conn = factory.GetConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON;\n" + sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string?> LoadEmbeddedResourceAsync(string resourceName)
    {
        await using var stream = ResourceAssembly.GetManifestResourceStream(resourceName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}
