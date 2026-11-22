using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.SqlServer;
using BifrostQL.Tool.Commands;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Tool.Test;

/// <summary>
/// watch, schema, export, test and config-generate accepted --config and ignored it: the file's
/// connection string was never read and a malformed file was never refused. Each now resolves
/// its connection through <see cref="CommandConnection"/>, as serve does: --connection-string
/// wins, the file supplies it otherwise, and a file that exists passes ReadServeConfig either way.
/// </summary>
public sealed class CommandConfigFileTests : IDisposable
{
    // Splits into a second keyword, so every provider refuses it at parse: the command reaches
    // the provider and stops there without dialing a server.
    private const string UnparseableSqlServer = "Server=127.0.0.1;Database=appdb;User Id=sa;Password=ab;leakedfragment=ef";
    private const string Fragment = "leakedfragment";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bifrost-cmd-config-{Guid.NewGuid():N}");

    public CommandConfigFileTests()
    {
        Directory.CreateDirectory(_dir);
        DbConnFactoryResolver.Register(BifrostDbProvider.SqlServer, cs => new SqlServerDbConnFactory(cs));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    public static TheoryData<string> Commands() => new() { "watch", "schema", "export", "test", "config-generate" };

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task ConfigFileConnectionString_IsUsed(string command)
    {
        var configPath = await WriteConfigAsync(JsonSerializer.Serialize(new { ConnectionStrings = new { bifrost = UnparseableSqlServer } }));

        var (exit, text) = await RunAsync(command, "--config", configPath);

        exit.Should().Be(1);
        text.Should().NotContain(Fragment);
        var root = JsonDocument.Parse(text).RootElement;
        var error = root.GetProperty("error").GetString();
        error.Should().NotStartWith("Connection string is required", "the file supplies the connection string");
        // The command's own malformed-string report, or the router's withheld report of the
        // provider's ArgumentException: either way the file's string reached the parser.
        if (error != CliErrorText.MalformedConnectionString)
        {
            error.Should().Be(CliErrorText.UnhandledFailure);
            root.GetProperty("exceptionType").GetString().Should().Be(typeof(ArgumentException).FullName);
        }
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task WrongShapedConfigFile_IsRefusedWithServesMessage_EvenWithTheFlag(string command)
    {
        const string config = """{ "BifrostQL": { "Path": 5 } }""";
        var configPath = await WriteConfigAsync(config);
        var serveRefusal = FluentActions.Invoking(() => ConfigFileReader.ReadServeConfig(config))
            .Should().Throw<ConfigFileShapeException>().Which.Message;

        foreach (var withFlag in new[] { false, true })
        {
            var args = withFlag
                ? new[] { "--connection-string", UnparseableSqlServer, "--config", configPath }
                : new[] { "--config", configPath };

            var (exit, text) = await RunAsync(command, args);

            exit.Should().Be(1);
            var root = JsonDocument.Parse(text).RootElement;
            root.GetProperty("success").GetBoolean().Should().BeFalse();
            root.GetProperty("error").GetString().Should().Be(serveRefusal, $"--connection-string given: {withFlag}");
        }
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task WellShapedFileWithoutConnectionString_IsRefusedWithServesMessage(string command)
    {
        var configPath = await WriteConfigAsync("""{ "BifrostQL": { "Path": "/gql" } }""");

        var (exit, text) = await RunAsync(command, "--config", configPath);

        exit.Should().Be(1);
        JsonDocument.Parse(text).RootElement.GetProperty("error").GetString()
            .Should().Be(CliErrorText.NoConnectionStringInConfigFile(configPath));
    }

    // An explicit --config naming no file was dropped silently, by serve too when the
    // connection came from the flag. Every command now refuses it.
    [Fact]
    public async Task Serve_MissingExplicitConfigFile_WithTheFlag_IsRefused()
    {
        // Driven at serve's resolver: an unfixed serve would go on to start Kestrel.
        var configPath = Path.Combine(_dir, "absent.json");
        var writer = new StringWriter();

        var resolved = await ServeCommand.ResolveConnectionString(
            ToolConfig.Parse(new[] { "serve", "--connection-string", "Data Source=from-flag.db", "--config", configPath, "--json" }),
            new OutputFormatter(writer, jsonMode: true));

        resolved.Should().BeNull("an explicit --config that names no file is refused, not dropped");
        JsonDocument.Parse(writer.ToString()).RootElement.GetProperty("error").GetString()
            .Should().Be($"Config file not found: {configPath}");
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task MissingExplicitConfigFile_IsRefused_EvenWithTheFlag(string command)
    {
        var configPath = Path.Combine(_dir, "absent.json");

        foreach (var withFlag in new[] { false, true })
        {
            var args = withFlag
                ? new[] { "--connection-string", UnparseableSqlServer, "--config", configPath }
                : new[] { "--config", configPath };

            var (exit, text) = await RunAsync(command, args);

            exit.Should().Be(1);
            JsonDocument.Parse(text).RootElement.GetProperty("error").GetString()
                .Should().Be($"Config file not found: {configPath}", $"--connection-string given: {withFlag}");
        }
    }

    // doctor passed an explicit --config naming no file without a word. It must report the
    // refusal every other command gives, in the same text, with and without the flag.
    [Fact]
    public async Task Doctor_MissingExplicitConfigFile_ReportsTheOtherCommandsRefusal()
    {
        var configPath = Path.Combine(_dir, "absent.json");
        var (_, schemaText) = await RunAsync("schema", "--config", configPath);
        var otherCommandsRefusal = JsonDocument.Parse(schemaText).RootElement.GetProperty("error").GetString();
        otherCommandsRefusal.Should().Contain("Config file not found");

        foreach (var withFlag in new[] { false, true })
        {
            var args = new List<string> { "doctor", "--config", configPath, "--json" };
            if (withFlag)
                args.AddRange(new[] { "--connection-string", UnparseableSqlServer });
            var writer = new StringWriter();

            var exit = await new DoctorCommand().ExecuteAsync(ToolConfig.Parse(args.ToArray()), new OutputFormatter(writer, jsonMode: true));

            exit.Should().Be(1);
            JsonDocument.Parse(writer.ToString()).RootElement.GetProperty("issues").EnumerateArray()
                .Where(i => i.GetProperty("title").GetString() == "Config File Not Found")
                .Select(i => i.GetProperty("message").GetString())
                .Should().Equal(new[] { otherCommandsRefusal }, $"--connection-string given: {withFlag}");
        }
    }

    [Fact]
    public async Task ConnectionStringFlag_OverridesTheFilesConnectionString()
    {
        var configPath = await WriteConfigAsync(JsonSerializer.Serialize(new
        {
            ConnectionStrings = new { bifrost = "Data Source=from-file.db" },
            BifrostQL = new { Path = "/gql" },
        }));
        var writer = new StringWriter();

        var resolved = await CommandConnection.ResolveAsync(
            ToolConfig.Parse(new[] { "schema", "--connection-string", "Data Source=from-flag.db", "--config", configPath }),
            new OutputFormatter(writer, jsonMode: true));

        resolved.Should().NotBeNull(writer.ToString());
        resolved!.Value.ConnectionString.Should().Be("Data Source=from-flag.db");
        resolved.Value.ConfigSection!.Path.Should().Be("/gql", "the file is still read");
    }

    [Fact]
    public async Task FileConnectionString_IsResolvedWithoutTheFlag()
    {
        var configPath = await WriteConfigAsync(JsonSerializer.Serialize(new { ConnectionStrings = new { bifrost = "Data Source=from-file.db" } }));
        var writer = new StringWriter();

        var resolved = await CommandConnection.ResolveAsync(
            ToolConfig.Parse(new[] { "schema", "--config", configPath }),
            new OutputFormatter(writer, jsonMode: true));

        resolved.Should().NotBeNull(writer.ToString());
        resolved!.Value.ConnectionString.Should().Be("Data Source=from-file.db");
    }

    private async Task<string> WriteConfigAsync(string configJson)
    {
        var configPath = Path.Combine(_dir, "bifrostql.json");
        await File.WriteAllTextAsync(configPath, configJson);
        return configPath;
    }

    private static async Task<(int Exit, string Text)> RunAsync(string command, params string[] args)
    {
        var router = new CommandRouter()
            .Register(new WatchCommand())
            .Register(new SchemaCommand())
            .Register(new ExportCommand())
            .Register(new TestCommand())
            .Register(new ConfigGenerateCommand());
        var writer = new StringWriter();
        var config = ToolConfig.Parse(args.Prepend(command).Append("--json").ToArray());
        var exit = await router.ExecuteAsync(config, new OutputFormatter(writer, jsonMode: true, supportsAnsi: false))
            .WaitAsync(TimeSpan.FromSeconds(30));
        return (exit, writer.ToString());
    }
}
