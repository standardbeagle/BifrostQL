using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Workflows;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test;

public class WorkflowTriggerTests
{
    [Fact]
    public async Task StateTransitionTrigger_RunsMatchingWorkflowOnce()
    {
        var runner = new CapturingWorkflowRunner();
        var workflows = WorkflowMap(Workflow("member-activated", "on-state-transition",
            """{ "table": "members", "from": "pending", "to": "active" }"""));
        var host = new WorkflowTriggerHost(workflows, runner);
        var observers = new StateTransitionObservers(new IStateTransitionObserver[] { host });

        await observers.NotifyAsync(
            new StateTransitionInfo("members", 1, "pending", "active", "7", "member.activated"),
            new Dictionary<string, object?> { ["tenant_id"] = 1 });

        runner.Runs.Should().ContainSingle();
        runner.Runs[0].Workflow.Name.Should().Be("member-activated");
        runner.Runs[0].Inputs["entityId"].Should().Be(1);
        runner.Runs[0].UserContext[WorkflowTriggerHost.SuppressTriggersKey].Should().Be(true);
    }

    [Fact]
    public async Task MutationTrigger_RunsOncePerMutation()
    {
        var runner = new CapturingWorkflowRunner();
        var workflows = WorkflowMap(Workflow("member-updated", "on-mutation",
            """{ "table": "members", "action": "update" }"""));
        var host = new WorkflowTriggerHost(workflows, runner);
        var observers = new MutationObservers(new IMutationObserver[] { host });
        var table = Table("members");

        await observers.NotifyAsync(new MutationObserverContext
        {
            Table = table,
            MutationType = MutationType.Update,
            Data = new Dictionary<string, object?> { ["member_id"] = 1 },
            Result = 1,
            UserContext = new Dictionary<string, object?>(),
        });

        runner.Runs.Should().ContainSingle();
        runner.Runs[0].Inputs["action"].Should().Be("update");
    }

    [Fact]
    public async Task Scheduler_RunsScheduledWorkflowOnTick()
    {
        var runner = new CapturingWorkflowRunner();
        var workflows = WorkflowMap(Workflow("daily-rollup", "scheduled",
            """{ "runOnTick": true }"""));
        var host = new WorkflowTriggerHost(workflows, runner);
        var scheduler = new WorkflowScheduler(host);

        await scheduler.TickAsync(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));

        runner.Runs.Should().ContainSingle();
        runner.Runs[0].Workflow.Name.Should().Be("daily-rollup");
        runner.Runs[0].Inputs.Should().ContainKey("scheduledAt");
    }

    private static IReadOnlyDictionary<string, WorkflowDefinition> WorkflowMap(params WorkflowDefinition[] workflows)
        => workflows.ToDictionary(w => w.Name, StringComparer.OrdinalIgnoreCase);

    private static WorkflowDefinition Workflow(string name, string triggerType, string triggerPayload)
        => new()
        {
            Name = name,
            Trigger = new WorkflowTrigger
            {
                Type = triggerType,
                Payload = Json(triggerPayload),
            },
            Steps = Array.Empty<WorkflowStep>(),
        };

    private static IDbTable Table(string name)
    {
        return new DbTable
        {
            TableSchema = "main",
            DbName = name,
            GraphQlName = name,
            NormalizedName = name,
            TableType = "BASE TABLE",
            ColumnLookup = new Dictionary<string, ColumnDto>(),
            GraphQlLookup = new Dictionary<string, ColumnDto>(),
        };
    }

    private static System.Text.Json.JsonElement Json(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class CapturingWorkflowRunner : IWorkflowRunner
    {
        public List<Run> Runs { get; } = new();

        public Task<WorkflowRunResult> RunAsync(
            string name,
            IDictionary<string, object?> inputs,
            IDictionary<string, object?> userContext)
            => throw new NotSupportedException();

        public Task<WorkflowRunResult> RunAsync(
            WorkflowDefinition workflow,
            IDictionary<string, object?> inputs,
            IDictionary<string, object?> userContext)
        {
            Runs.Add(new Run(
                workflow,
                new Dictionary<string, object?>(inputs, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, object?>(userContext, StringComparer.OrdinalIgnoreCase)));
            return Task.FromResult(new WorkflowRunResult(
                true,
                new Dictionary<string, object?>(),
                Array.Empty<WorkflowStepTrace>()));
        }
    }

    private sealed record Run(
        WorkflowDefinition Workflow,
        IReadOnlyDictionary<string, object?> Inputs,
        IReadOnlyDictionary<string, object?> UserContext);
}
