using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Sqlite;
using BifrostQL.Tool.Commands;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Tool.Test;

/// <summary>
/// `bifrost config-validate` parses with the runtime MetadataLoader and, given a connection,
/// loads the model so ModelConfigValidator runs. A config the server would refuse must fail
/// here, and one the server accepts must pass.
/// </summary>
public sealed class ConfigValidateCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bifrost-config-validate-{Guid.NewGuid():N}");

    public ConfigValidateCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task CommaMultiSelectorAndSemicolonInVerbatimValue_AreValid()
    {
        var (exit, json) = await RunAsync(null,
            "dbo.orders, dbo.invoices { soft-delete: deleted_at }",
            "dbo.orders { computed-sql: `a:String:{x}; b:String:{y}` }");

        exit.Should().Be(0, json.ToString());
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("errorCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task DuplicateKey_IsAnError()
    {
        var (exit, json) = await RunAsync(null, "dbo.orders { soft-delete: deleted_at; soft-delete: removed_at }");

        exit.Should().Be(1);
        Messages(json).Should().ContainSingle().Which.Should().Contain("Duplicate metadata key 'soft-delete'");
    }

    [Fact]
    public async Task MalformedSegment_IsAnError()
    {
        var (exit, json) = await RunAsync(null, "dbo.orders { soft-delete: deleted_at; tenant-filter }");

        exit.Should().Be(1);
        Messages(json).Should().ContainSingle().Which.Should().Contain("Invalid metadata property 'tenant-filter'");
    }

    [Fact]
    public async Task EveryMalformedRule_IsReported()
    {
        var (exit, json) = await RunAsync(null,
            "no braces",
            "dbo.orders { soft-delete: deleted_at }",
            "dbo.orders { tenant-filter }");

        exit.Should().Be(1);
        Messages(json).Should().HaveCount(2);
    }

    [Fact]
    public async Task WithConnection_ModelConfigValidatorRuns()
    {
        var connectionString = await CreateDatabaseAsync();

        var (exit, json) = await RunAsync(connectionString, "main.orders.created_by { populate: created-by }");

        exit.Should().Be(1);
        Messages(json).Should().ContainSingle().Which.Should().Contain("user-audit-key");
    }

    [Fact]
    public async Task WithConnection_AValidModelPasses()
    {
        var connectionString = await CreateDatabaseAsync();

        var (exit, json) = await RunAsync(connectionString,
            ":root { user-audit-key: id }",
            "main.orders.created_by { populate: created-by }");

        exit.Should().Be(0, json.ToString());
        json.GetProperty("modelValidated").GetBoolean().Should().BeTrue();
    }

    private async Task<string> CreateDatabaseAsync()
    {
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        var connectionString = $"Data Source={Path.Combine(_dir, "model.db")}";
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE orders (id INTEGER PRIMARY KEY, title TEXT NOT NULL, created_by TEXT NULL)";
        await cmd.ExecuteNonQueryAsync();
        return connectionString;
    }

    private async Task<(int Exit, JsonElement Json)> RunAsync(string? connectionString, params string[] rules)
    {
        var configPath = Path.Combine(_dir, "bifrostql.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new { BifrostQL = new { Metadata = rules } }));

        var args = new List<string> { "config-validate", "--config", configPath, "--json" };
        if (connectionString != null)
            args.AddRange(new[] { "--connection-string", connectionString });

        var writer = new StringWriter();
        var exit = await new ConfigValidateCommand().ExecuteAsync(ToolConfig.Parse(args.ToArray()), new OutputFormatter(writer, jsonMode: true));
        return (exit, JsonDocument.Parse(writer.ToString()).RootElement.Clone());
    }

    private static IReadOnlyList<string> Messages(JsonElement json)
        => json.TryGetProperty("issues", out var issues)
            ? issues.EnumerateArray().Select(i => i.GetProperty("message").GetString()!).ToList()
            : new[] { json.GetProperty("error").GetString()! };
}
