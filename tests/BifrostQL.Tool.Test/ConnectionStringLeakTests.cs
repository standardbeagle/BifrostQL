using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using BifrostQL.SqlServer;
using BifrostQL.Tool.Commands;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Tool.Test;

/// <summary>
/// A password holding an unquoted ';' splits into a second keyword: with
/// <c>Password=ab;leakedfragment=ef</c> the provider throws "Keyword not supported:
/// 'leakedfragment'", so its message quotes part of the password. No CLI output may carry a
/// provider's connection-string parse message, and an exception no command handles must not
/// reach the runtime's unhandled-exception dump, which prints the message too.
/// </summary>
public sealed class ConnectionStringLeakTests : IDisposable
{
    private const string Fragment = "leakedfragment";
    private const string SqlServerSplitPassword = "Server=127.0.0.1;Database=appdb;User Id=sa;Password=ab;leakedfragment=ef";
    private const string PostgresSplitPassword = "Host=127.0.0.1;Database=appdb;Username=app;Password=ab;leakedfragment=ef";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bifrost-cs-leak-{Guid.NewGuid():N}");

    public ConnectionStringLeakTests()
    {
        Directory.CreateDirectory(_dir);
        DbConnFactoryResolver.Register(BifrostDbProvider.SqlServer, cs => new SqlServerDbConnFactory(cs));
        DbConnFactoryResolver.Register(BifrostDbProvider.PostgreSql, cs => new PostgresDbConnFactory(cs));
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfigValidate_SplitPassword_ReportsMalformedWithoutTheFragment(bool json)
    {
        var configPath = Path.Combine(_dir, "bifrostql.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new { BifrostQL = new { Metadata = new[] { "main.orders { soft-delete: deleted_at }" } } }));
        var connectionString = $"Data Source={Path.Combine(_dir, "model.db")};Password=ab;leakedfragment=ef";

        var (exit, text) = await RunAsync(new ConfigValidateCommand(), json,
            "config-validate", "--config", configPath, "--connection-string", connectionString);

        exit.Should().Be(1);
        text.Should().Contain("Connection string is malformed").And.NotContain(Fragment);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Doctor_SplitPassword_ReportsMalformedWithoutTheFragment(bool json)
    {
        // A well-shaped file with no connection string keeps any bifrostql.json in the working
        // directory out of the run, and leaves the flag as the only connection string.
        var configPath = Path.Combine(_dir, "bifrostql.json");
        await File.WriteAllTextAsync(configPath, """{ "BifrostQL": { "Path": "/gql" } }""");

        var (_, text) = await RunAsync(new DoctorCommand(), json,
            "doctor", "--config", configPath, "--connection-string", SqlServerSplitPassword);

        text.Should().Contain("Connection string is malformed").And.NotContain(Fragment);
        text.Should().NotContain("Config file not found").And.NotContain("Invalid Config File",
            "the config file is not a factor here");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Test_SplitPassword_ReportsMalformedWithoutTheFragment(bool json)
    {
        var (exit, text) = await RunRoutedAsync(json, "test", "--connection-string", SqlServerSplitPassword);

        exit.Should().Be(1);
        text.Should().Contain("Connection string is malformed").And.NotContain(Fragment);
    }

    [Fact]
    public async Task Watch_SplitPassword_StopsAndReportsMalformedWithoutTheFragment()
    {
        var (exit, text) = await RunAsync(new WatchCommand(), json: true, "watch", "--connection-string", SqlServerSplitPassword)
            .WaitAsync(TimeSpan.FromSeconds(30));

        exit.Should().Be(1);
        text.Should().Contain("Connection string is malformed").And.NotContain(Fragment);
    }

    // Schema loads through the provider-generic DbModelLoader, so a Postgres connection string
    // reaches Npgsql's parser. Nothing in the command handles that exception; the router must.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Schema_PostgresSplitPassword_IsCaughtByTheRouterWithoutTheFragment(bool json)
    {
        var (exit, text) = await RunRoutedAsync(json, "schema", "--connection-string", PostgresSplitPassword);

        exit.Should().Be(1);
        text.Should().Contain(nameof(ArgumentException)).And.NotContain(Fragment);
        if (json)
            JsonDocument.Parse(text).RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
    }

    // The exception families whose message can quote connection-string text: a provider's
    // connection-string parse (ArgumentException), a driver error (DbException), and either one
    // wrapped as the inner exception of something else.
    public static TheoryData<string, bool> WithheldShapes() => new()
    {
        { nameof(ArgumentException), true }, { nameof(ArgumentException), false },
        { nameof(SqliteException), true }, { nameof(SqliteException), false },
        { "WrappedArgumentException", true }, { "WrappedArgumentException", false },
        { "WrappedDbException", true }, { "WrappedDbException", false },
    };

    [Theory]
    [MemberData(nameof(WithheldShapes))]
    public async Task Router_ConnectionStringFamilyException_PrintsTheTypeNotTheMessage(string shape, bool json)
    {
        var leaking = $"Password=ab;{Fragment}=ef";
        Exception exception = shape switch
        {
            nameof(ArgumentException) => new ArgumentException(leaking),
            nameof(SqliteException) => new SqliteException(leaking, 1),
            "WrappedArgumentException" => new InvalidOperationException("outer", new ArgumentException(leaking)),
            "WrappedDbException" => new IOException("outer", new SqliteException(leaking, 1)),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        var router = new CommandRouter().Register(new ThrowingCommand(exception));

        var (exit, text) = await RunAsync(router, json, "throws");

        exit.Should().Be(1);
        text.Should().Contain(exception.GetType().Name).And.Contain(CliErrorText.UnhandledFailure)
            .And.NotContain(Fragment).And.NotContain("outer");
        if (json)
            JsonDocument.Parse(text).RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
    }

    // Port in use on serve, a bad export path: the message is the diagnosis, and it carries no
    // connection-string text, so it is printed.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Router_OtherException_PrintsTheMessage(bool json)
    {
        const string message = "Failed to bind to address http://127.0.0.1:5000: address already in use.";
        var router = new CommandRouter().Register(new ThrowingCommand(new IOException(message)));

        var (exit, text) = await RunAsync(router, json, "throws");

        exit.Should().Be(1);
        text.Should().Contain(nameof(IOException)).And.Contain(message).And.NotContain(CliErrorText.UnhandledFailure);
        if (json)
        {
            var root = JsonDocument.Parse(text).RootElement;
            root.GetProperty("success").GetBoolean().Should().BeFalse();
            root.GetProperty("error").GetString().Should().Be(message);
            root.GetProperty("exceptionType").GetString().Should().Be(typeof(IOException).FullName);
        }
    }

    private static Task<(int Exit, string Text)> RunRoutedAsync(bool json, params string[] args)
        => RunAsync(new CommandRouter().Register(new TestCommand()).Register(new SchemaCommand()), json, args);

    private static async Task<(int Exit, string Text)> RunAsync(CommandRouter router, bool json, params string[] args)
    {
        var writer = new StringWriter();
        var config = ToolConfig.Parse(json ? args.Append("--json").ToArray() : args);
        var exit = await router.ExecuteAsync(config, new OutputFormatter(writer, json, supportsAnsi: false));
        return (exit, writer.ToString());
    }

    private static async Task<(int Exit, string Text)> RunAsync(ICommand command, bool json, params string[] args)
    {
        var writer = new StringWriter();
        var config = ToolConfig.Parse(json ? args.Append("--json").ToArray() : args);
        var exit = await command.ExecuteAsync(config, new OutputFormatter(writer, json, supportsAnsi: false));
        return (exit, writer.ToString());
    }

    private sealed class ThrowingCommand(Exception exception) : ICommand
    {
        public string Name => "throws";
        public string Description => "throws";
        public Task<int> ExecuteAsync(ToolConfig config, OutputFormatter output) => throw exception;
    }
}
