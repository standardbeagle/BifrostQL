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

    [Fact]
    public async Task MalformedConfigJson_IsReportedAsJson()
    {
        var (exit, json) = await RunRawAsync(null, "{ \"BifrostQL\": { \"Metadata\": [ ");

        exit.Should().Be(1);
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().StartWith("Config file is not valid JSON:");
    }

    [Fact]
    public async Task NumberMetadataRule_IsReportedAsTheServerReportsIt()
    {
        // Configuration binds a JSON number as its text, so the server reads "42" as a rule and
        // refuses it at parse; config-validate reports the same refusal.
        var (exit, json) = await RunRawAsync(null, "{ \"BifrostQL\": { \"Metadata\": [ 42 ] } }");

        exit.Should().Be(1);
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        Messages(json).Should().ContainSingle().Which.Should().Contain("Invalid metadata rule '42'");
    }

    [Fact]
    public async Task ScalarMetadata_IsReportedAsAShapeError()
    {
        // The server refuses this at startup (BifrostSetupOptions); before, config-validate
        // read no rules from it and called the file valid.
        var (exit, json) = await RunRawAsync(null, "{ \"BifrostQL\": { \"Metadata\": \"dbo.orders { tenant-filter: tenant_id }\" } }");

        exit.Should().Be(1);
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("BifrostQL:Metadata").And.Contain("array of rule strings")
            .And.NotContain("tenant_id");
    }

    [Fact]
    public async Task ObjectEntryInMetadata_IsReportedAsAShapeError()
    {
        var (exit, json) = await RunRawAsync(null,
            "{ \"BifrostQL\": { \"Metadata\": [ \"dbo.orders { soft-delete: deleted_at }\", { \"rule\": \"dbo.items { tenant-filter: tenant_id }\" } ] } }");

        exit.Should().Be(1);
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Contain("BifrostQL:Metadata:1").And.Contain("rule string")
            .And.NotContain("tenant_id");
    }

    [Fact]
    public async Task NonObjectRoot_IsReportedAsAShapeError()
    {
        var (exit, json) = await RunRawAsync(null, "[ \"dbo.orders { soft-delete: deleted_at }\" ]");

        exit.Should().Be(1);
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().NotContain("not valid JSON")
            .And.Contain("root must be a JSON object").And.Contain("Array");
    }

    [Fact]
    public async Task NonObjectBifrostSection_IsReportedAsAShapeError()
    {
        var (exit, json) = await RunRawAsync(null, "{ \"BifrostQL\": 5 }");

        exit.Should().Be(1);
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().NotContain("not valid JSON")
            .And.Contain("BifrostQL section must be a JSON object").And.Contain("Number");
    }

    [Fact]
    public async Task MalformedConnectionString_IsReportedWithoutEchoingIt()
    {
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        var connectionString = $"Data Source={Path.Combine(_dir, "model.db")};Password=hunter2-secret;Bogus Keyword=1";

        var (exit, json, text) = await RunWithTextAsync(connectionString, "main.orders { soft-delete: deleted_at }");

        exit.Should().Be(1);
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("modelValidated").GetBoolean().Should().BeFalse();
        Messages(json).Should().ContainSingle().Which.Should().Be(CliErrorText.MalformedConnectionString);
        text.Should().NotContain("hunter2-secret").And.NotContain(_dir);
    }

    [Fact]
    public async Task ParseErrors_SayTheModelWasNotValidated()
    {
        var connectionString = await CreateDatabaseAsync();

        var (exit, json) = await RunAsync(connectionString, "main.orders { tenant-filter }");

        exit.Should().Be(1);
        json.GetProperty("modelValidated").GetBoolean().Should().BeFalse();
        json.GetProperty("modelNotValidatedReason").GetString().Should().Contain("model not validated");
    }

    [Fact]
    public async Task ParseErrors_TextOutputSaysTheModelWasNotValidated()
    {
        var connectionString = await CreateDatabaseAsync();
        var configPath = Path.Combine(_dir, "bifrostql.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new { BifrostQL = new { Metadata = new[] { "main.orders { tenant-filter }" } } }));

        var writer = new StringWriter();
        var exit = await new ConfigValidateCommand().ExecuteAsync(
            ToolConfig.Parse(new[] { "config-validate", "--config", configPath, "--connection-string", connectionString }),
            new OutputFormatter(writer, jsonMode: false, supportsAnsi: false));

        exit.Should().Be(1);
        writer.ToString().Should().Contain("model not validated");
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

    private Task<(int Exit, JsonElement Json)> RunAsync(string? connectionString, params string[] rules)
        => RunRawAsync(connectionString, JsonSerializer.Serialize(new { BifrostQL = new { Metadata = rules } }));

    private async Task<(int Exit, JsonElement Json)> RunRawAsync(string? connectionString, string configJson)
    {
        var (exit, json, _) = await RunConfigAsync(connectionString, configJson);
        return (exit, json);
    }

    private Task<(int Exit, JsonElement Json, string Text)> RunWithTextAsync(string? connectionString, params string[] rules)
        => RunConfigAsync(connectionString, JsonSerializer.Serialize(new { BifrostQL = new { Metadata = rules } }));

    private async Task<(int Exit, JsonElement Json, string Text)> RunConfigAsync(string? connectionString, string configJson)
    {
        var configPath = Path.Combine(_dir, "bifrostql.json");
        await File.WriteAllTextAsync(configPath, configJson);

        var args = new List<string> { "config-validate", "--config", configPath, "--json" };
        if (connectionString != null)
            args.AddRange(new[] { "--connection-string", connectionString });

        var writer = new StringWriter();
        var exit = await new ConfigValidateCommand().ExecuteAsync(ToolConfig.Parse(args.ToArray()), new OutputFormatter(writer, jsonMode: true));
        var text = writer.ToString();
        return (exit, JsonDocument.Parse(text).RootElement.Clone(), text);
    }

    private static IReadOnlyList<string> Messages(JsonElement json)
        => json.TryGetProperty("issues", out var issues)
            ? issues.EnumerateArray().Select(i => i.GetProperty("message").GetString()!).ToList()
            : new[] { json.GetProperty("error").GetString()! };
}
