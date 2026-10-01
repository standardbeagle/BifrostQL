// Guard for the durable template manifest: no bifrost slice template ran
// tests/BifrostQL.Mcp.Test, so Admin_SeesEveryTableAndColumn stayed red across
// S4b/S4c/S8 for a day and surfaced only through an implementer checkpoint
// (task 01M2E7J3J5E5QHM4HQZG4ZN1QH). Close the same shape as the Integration.Test
// gap: module-slice and backend-slice must gate the MCP test project with
// build-mcp then test-mcp, immediately after test-integration.
//
// .worktrack/templates.json is the durable door the daemon reconciles rows from,
// so the assertion runs against the file, not a copy. A named command step can
// drift out of the manifest while the DB rows keep serving a stale walk; this
// fails the moment the manifest loses the gate.
using System.Text.Json;
using Xunit;

namespace BifrostQL.Tool.Test;

public class WorkflowTemplateMcpGateTests
{
    [Theory]
    [InlineData("backend-slice")]
    [InlineData("module-slice")]
    public void SliceTemplates_GateMcpTestAfterIntegration(string templateName)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(ManifestPath()));
        var template = FindTemplate(manifest.RootElement, templateName);

        var commandSteps = template.GetProperty("steps")
            .EnumerateArray()
            .Where(step => step.GetProperty("kind").GetString() == "command")
            .ToList();

        var integrationIndex = commandSteps.FindIndex(
            step => step.GetProperty("name").GetString() == "test-integration");
        Assert.True(integrationIndex >= 0,
            $"'{templateName}' has no test-integration step; the MCP gate must follow it.");

        // Both MCP steps must exist and sit directly after test-integration so a
        // reader of the walk sees the integration gate then the MCP gate.
        AssertCommandStep(commandSteps, integrationIndex + 1, "build-mcp");
        AssertCommandStep(commandSteps, integrationIndex + 2, "test-mcp");
    }

    [Theory]
    [InlineData("backend-slice")]
    [InlineData("module-slice")]
    public void BuildMcpStep_BuildsTheMcpTestProject(string templateName)
    {
        var buildMcp = Spec(commandStepsFor(templateName), "build-mcp");

        Assert.Equal(
            ["dotnet", "build", "tests/BifrostQL.Mcp.Test/BifrostQL.Mcp.Test.csproj"],
            Argv(buildMcp));
        Assert.Equal(0, buildMcp.GetProperty("expect_exit").GetInt32());
    }

    [Theory]
    [InlineData("backend-slice")]
    [InlineData("module-slice")]
    public void TestMcpStep_RunsNoBuildAndRequiresPassed(string templateName)
    {
        var testMcp = Spec(commandStepsFor(templateName), "test-mcp");

        Assert.Equal(
            ["dotnet", "test", "tests/BifrostQL.Mcp.Test/BifrostQL.Mcp.Test.csproj", "--no-build"],
            Argv(testMcp));
        Assert.Equal(0, testMcp.GetProperty("expect_exit").GetInt32());
        // "--no-build" is only safe because build-mcp runs first; requiring the
        // xunit "Passed!" banner keeps a silently-empty run from passing the gate.
        Assert.Equal("Passed!", testMcp.GetProperty("expect_stdout_matches").GetString());
    }

    private static List<JsonElement> commandStepsFor(string templateName)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(ManifestPath()));
        // Clone each element so it outlives the disposed JsonDocument.
        return FindTemplate(manifest.RootElement, templateName)
            .GetProperty("steps")
            .EnumerateArray()
            .Where(step => step.GetProperty("kind").GetString() == "command")
            .Select(step => step.Clone())
            .ToList();
    }

    private static void AssertCommandStep(List<JsonElement> steps, int index, string name)
    {
        Assert.True(index < steps.Count,
            $"Expected a command step named '{name}' at position {index}, but the template has only {steps.Count} command steps.");
        Assert.Equal(name, steps[index].GetProperty("name").GetString());
    }

    private static JsonElement FindTemplate(JsonElement root, string templateName)
    {
        foreach (var template in root.GetProperty("templates").EnumerateArray())
        {
            if (template.GetProperty("name").GetString() == templateName)
                return template;
        }
        throw new Xunit.Sdk.XunitException($"Template '{templateName}' not found in the manifest.");
    }

    private static JsonElement Spec(List<JsonElement> commandSteps, string name)
    {
        var step = commandSteps.Single(s => s.GetProperty("name").GetString() == name);
        return JsonDocument.Parse(step.GetProperty("specJson").GetString()!).RootElement.Clone();
    }

    private static string[] Argv(JsonElement spec) =>
        spec.GetProperty("argv").EnumerateArray().Select(a => a.GetString()!).ToArray();

    private static string ManifestPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BifrostQL.sln")))
            dir = dir.Parent;
        Assert.True(dir != null, "Could not locate repo root (BifrostQL.sln) above the test assembly.");
        return Path.Combine(dir!.FullName, ".worktrack", "templates.json");
    }
}
