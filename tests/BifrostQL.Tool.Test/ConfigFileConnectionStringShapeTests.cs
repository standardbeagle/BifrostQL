using System.Text.Json;
using BifrostQL.Tool.Commands;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Tool.Test;

/// <summary>
/// `doctor` and `serve` read the connection string from the config file when no
/// --connection-string is given. A wrong-shaped file (non-object root, non-object
/// connectionStrings, non-string bifrost value) or malformed JSON is reported as a config-file
/// error, never an escaped exception.
/// </summary>
public sealed class ConfigFileConnectionStringShapeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bifrost-cs-shape-{Guid.NewGuid():N}");

    public ConfigFileConnectionStringShapeTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    public static TheoryData<string, string> WrongShapes() => new()
    {
        { "[ 1 ]", "root must be a JSON object" },
        { "{ \"connectionStrings\": 5 }", "connectionStrings section must be a JSON object" },
        { "{ \"ConnectionStrings\": { \"bifrost\": 5 } }", "connectionStrings.bifrost must be a string" },
        { "{ \"connectionStrings\": ", "valid JSON" },
    };

    [Theory]
    [MemberData(nameof(WrongShapes))]
    public async Task Doctor_WrongShapedConfigFile_IsReportedOnce(string configJson, string expected)
    {
        var configPath = await WriteConfigAsync(configJson);

        var (exit, text) = await RunAsync(new DoctorCommand(), "doctor", "--config", configPath);

        exit.Should().Be(1);
        var root = JsonDocument.Parse(text).RootElement;
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("issues").EnumerateArray()
            .Count(i => i.GetProperty("title").GetString() == "Invalid Config File")
            .Should().Be(1);
        root.GetProperty("issues").EnumerateArray()
            .Single(i => i.GetProperty("title").GetString() == "Invalid Config File")
            .GetProperty("message").GetString().Should().ContainEquivalentOf(expected);
    }

    [Theory]
    [MemberData(nameof(WrongShapes))]
    public async Task Serve_WrongShapedConfigFile_IsReported(string configJson, string expected)
    {
        var configPath = await WriteConfigAsync(configJson);

        var (exit, text) = await RunAsync(new ServeCommand(), "serve", "--config", configPath);

        exit.Should().Be(1);
        var root = JsonDocument.Parse(text).RootElement;
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("error").GetString().Should().ContainEquivalentOf(expected);
    }

    private async Task<string> WriteConfigAsync(string configJson)
    {
        var configPath = Path.Combine(_dir, "bifrostql.json");
        await File.WriteAllTextAsync(configPath, configJson);
        return configPath;
    }

    private static async Task<(int Exit, string Text)> RunAsync(ICommand command, params string[] args)
    {
        var writer = new StringWriter();
        var exit = await command.ExecuteAsync(ToolConfig.Parse(args.Append("--json").ToArray()), new OutputFormatter(writer, jsonMode: true));
        return (exit, writer.ToString());
    }
}
