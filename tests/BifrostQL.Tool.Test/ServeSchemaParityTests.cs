using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;
using BifrostQL.Sqlite;
using BifrostQL.Tool.Commands;
using FluentAssertions;
using GraphQL;
using GraphQL.Types;
using GraphQL.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Tool.Test;

/// <summary>
/// schema, export and config-generate built the model with no metadata rules, so
/// `bifrost schema --config app.json` printed a schema serve never serves: a table the file
/// hides was listed. schema and export now build through serve's own model build; config-generate,
/// the bootstrap tool, reads the database without the file's rules.
/// </summary>
public sealed class ServeSchemaParityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bifrost-schema-parity-{Guid.NewGuid():N}");

    public ServeSchemaParityTests()
    {
        Directory.CreateDirectory(_dir);
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task SchemaWithConfig_PrintsTheSchemaServeServes()
    {
        var (connectionString, configPath) = await CreateDatabaseAndConfigAsync("main.secrets { visibility: hidden }");

        var serveSdl = await ServeSdlAsync(connectionString, configPath);
        var (exit, json) = await RunAsync(new SchemaCommand(), "schema", "--config", configPath);

        exit.Should().Be(0, json.ToString());
        var sdl = json.GetProperty("sdl").GetString();
        sdl.Should().Be(serveSdl, "schema --config prints the schema serve serves for the same file");
        sdl.Should().Contain("orders", "control: a visible table is printed");
        sdl.Should().NotContain("secrets", "the file hides this table");
    }

    // The same database without the rule lists the table, so the absence above is the rule's.
    [Fact]
    public async Task SchemaWithConfig_NoRule_ListsTheTable()
    {
        var (connectionString, configPath) = await CreateDatabaseAndConfigAsync();

        var serveSdl = await ServeSdlAsync(connectionString, configPath);
        var (exit, json) = await RunAsync(new SchemaCommand(), "schema", "--config", configPath);

        exit.Should().Be(0, json.ToString());
        json.GetProperty("sdl").GetString().Should().Be(serveSdl).And.Contain("secrets");
    }

    // config-generate is the bootstrap tool: run against a database the file's rules do not match
    // yet (the column the tenant rule names is not created), it threw from ModelConfigValidator.
    // It reads the database alone, so a table the file hides is still counted and detected.
    [Fact]
    public async Task ConfigGenerate_WithRulesTheDatabaseDoesNotMatchYet_DetectsFromTheDatabaseAlone()
    {
        var (_, configPath) = await CreateDatabaseAndConfigAsync(
            "main.orders { tenant-filter: org_id }", "main.secrets { visibility: hidden }");

        var (exit, json) = await RunAsync(new ConfigGenerateCommand(), "config-generate", "--config", configPath);

        exit.Should().Be(0, json.ToString());
        json.GetProperty("tableCount").GetInt32().Should().Be(2, "the table the file hides is detected too");
    }

    [Fact]
    public async Task ConfigGenerate_WithAHidingRule_StillDetectsTheHiddenTable()
    {
        var (_, configPath) = await CreateDatabaseAndConfigAsync("main.secrets { visibility: hidden }");

        var (exit, json) = await RunAsync(new ConfigGenerateCommand(), "config-generate", "--config", configPath);

        exit.Should().Be(0, json.ToString());
        json.GetProperty("tableCount").GetInt32().Should().Be(2, "config-generate reads the database, not the file's view of it");
    }

    // schema and export keep serve's rules, so a rule the database cannot satisfy refuses the
    // command as it refuses serve's startup: exit 1 with the validator's message, not a crash.
    [Theory]
    [InlineData("schema")]
    [InlineData("export")]
    public async Task SchemaAndExport_WithARuleTheDatabaseCannotSatisfy_RefuseWithTheValidatorsMessage(string command)
    {
        var (_, configPath) = await CreateDatabaseAndConfigAsync("main.orders { tenant-filter: org_id }");
        ICommand cmd = command == "schema" ? new SchemaCommand() : new ExportCommand();

        var (exit, json) = await RunAsync(cmd, command, "--config", configPath);

        exit.Should().Be(1);
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should()
            .StartWith("Invalid BifrostQL metadata configuration:").And.Contain("org_id");
        json.GetProperty("hint").GetString().Should().Contain("bifrost config-validate");
    }

    // Only the validator's refusal points at config-validate. A provider with no registered
    // factory (this test assembly registers no MySQL factory) is an InvalidOperationException
    // too, and config-validate cannot fix it, so it must not be told to run it.
    [Theory]
    [InlineData("schema")]
    [InlineData("export")]
    public async Task SchemaAndExport_ANonValidationFailure_GetsNoConfigValidateHint(string command)
    {
        var configPath = Path.Combine(_dir, "bifrostql.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
        {
            ConnectionStrings = new { bifrost = "Server=127.0.0.1;Port=3306;Database=app;Uid=app" },
            BifrostQL = new { Path = "/graphql", Metadata = Array.Empty<string>() },
        }));
        var router = new CommandRouter().Register(new SchemaCommand()).Register(new ExportCommand());

        var writer = new StringWriter();
        var exit = await router.ExecuteAsync(
            ToolConfig.Parse(new[] { command, "--config", configPath, "--json" }),
            new OutputFormatter(writer, jsonMode: true));
        var json = JsonDocument.Parse(writer.ToString()).RootElement;

        exit.Should().Be(1);
        json.GetProperty("error").GetString().Should().Contain("No factory registered",
            "control: the build failed on the provider, not on a rule");
        json.TryGetProperty("hint", out _).Should().BeFalse(
            "config-validate cannot fix a missing provider package");
    }

    private async Task<(string ConnectionString, string ConfigPath)> CreateDatabaseAndConfigAsync(params string[] rules)
    {
        var connectionString = $"Data Source={Path.Combine(_dir, "parity.db")}";
        await using (var conn = new SqliteConnection(connectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE orders (id INTEGER PRIMARY KEY, title TEXT NOT NULL);" +
                "CREATE TABLE secrets (id INTEGER PRIMARY KEY, token TEXT NOT NULL);";
            await cmd.ExecuteNonQueryAsync();
        }

        var configPath = Path.Combine(_dir, "bifrostql.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
        {
            ConnectionStrings = new { bifrost = connectionString },
            BifrostQL = new { Path = "/graphql", Metadata = rules },
        }));
        return (connectionString, configPath);
    }

    // serve's own pipeline, wired as ServeCommand.ExecuteAsync wires it, without binding a port.
    private static async Task<string> ServeSdlAsync(string connectionString, string configPath)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddJsonFile(configPath, optional: false);
        await using var app = ServeCommand.BuildApp(builder, "127.0.0.1", connectionString);
        var inputs = await app.Services.GetRequiredService<PathCache<Inputs>>().GetValueAsync("/graphql");
#pragma warning disable CS0618 // SchemaPrinter is the printer SchemaCommand uses.
        return new SchemaPrinter((ISchema)inputs["dbSchema"]!).Print();
#pragma warning restore CS0618
    }

    private static async Task<(int Exit, JsonElement Json)> RunAsync(ICommand command, params string[] args)
    {
        var writer = new StringWriter();
        var exit = await command.ExecuteAsync(ToolConfig.Parse(args.Append("--json").ToArray()), new OutputFormatter(writer, jsonMode: true));
        return (exit, JsonDocument.Parse(writer.ToString()).RootElement.Clone());
    }
}
