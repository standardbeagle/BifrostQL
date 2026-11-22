using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Workflows;
using BifrostQL.Model;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// A workflow whose trigger names an ambiguous bare table is that workflow's own fault: it
/// is logged and skipped, and every other workflow the write matches still runs.
/// </summary>
[Collection(ErrorSinkLoggerCollection.Name)]
public class WorkflowTriggerAmbiguityTests
{
    [Fact]
    public async Task AmbiguousBareTrigger_IsLoggedAndSkipped_QualifiedSiblingStillFires()
    {
        var runner = new CapturingWorkflowRunner();
        // Declared first, so the ambiguous trigger is evaluated before its sibling.
        var workflows = new[]
        {
            Workflow("employee-changed", """{ "table": "employee", "action": "update" }"""),
            Workflow("payroll-changed", """{ "table": "payroll.employee", "action": "update" }"""),
        }.ToDictionary(w => w.Name, StringComparer.OrdinalIgnoreCase);
        var host = new WorkflowTriggerHost(workflows, runner);
        var hr = Table("employee", "hr");
        var payroll = Table("employee", "payroll");

        using var log = new ErrorSinkCapture();
        await host.OnMutationAsync(new MutationObserverContext
        {
            Model = new DbModel { Tables = new[] { hr, payroll }, Metadata = new Dictionary<string, object?>() },
            Table = payroll,
            MutationType = MutationType.Update,
            Data = new Dictionary<string, object?> { ["id"] = 1 },
            Result = 1,
            UserContext = new Dictionary<string, object?>(),
            MutationState = MutationObserverContext.NewMutationState(),
        });

        runner.Runs.Should().ContainSingle().Which.Should().Be("payroll-changed");
        log.Warnings.Should().ContainSingle()
            .Which.Should().Contain("employee-changed").And.Contain("ambiguous");
    }

    private static WorkflowDefinition Workflow(string name, string triggerPayload) => new()
    {
        Name = name,
        Trigger = new WorkflowTrigger
        {
            Type = "on-mutation",
            Payload = JsonDocument.Parse(triggerPayload).RootElement.Clone(),
        },
        Steps = Array.Empty<WorkflowStep>(),
    };

    private static IDbTable Table(string name, string schema) => new DbTable
    {
        TableSchema = schema,
        DbName = name,
        GraphQlName = $"{schema}_{name}",
        NormalizedName = name,
        TableType = "BASE TABLE",
        ColumnLookup = new Dictionary<string, ColumnDto>(),
        GraphQlLookup = new Dictionary<string, ColumnDto>(),
    };

    private sealed class CapturingWorkflowRunner : IWorkflowRunner
    {
        public List<string> Runs { get; } = new();

        public Task<WorkflowRunResult> RunAsync(
            string name, IDictionary<string, object?> inputs, IDictionary<string, object?> userContext,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkflowRunResult> RunAsync(
            WorkflowDefinition workflow, IDictionary<string, object?> inputs, IDictionary<string, object?> userContext,
            CancellationToken cancellationToken = default)
        {
            Runs.Add(workflow.Name);
            return Task.FromResult(new WorkflowRunResult(true, new Dictionary<string, object?>(), Array.Empty<WorkflowStepTrace>()));
        }
    }
}
