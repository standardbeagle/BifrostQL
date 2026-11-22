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
        var members = Table("members");

        await observers.NotifyAsync(
            new StateTransitionInfo(Model(members), members, new Dictionary<string, object?> { ["member_id"] = 1 }, "pending", "active", "7", "member.activated"),
            new Dictionary<string, object?> { ["tenant_id"] = 1 });

        runner.Runs.Should().ContainSingle();
        runner.Runs[0].Workflow.Name.Should().Be("member-activated");
        runner.Runs[0].Inputs["entity"].Should().Be("main.members");
        runner.Runs[0].Inputs["entityKey"].Should().BeEquivalentTo(new Dictionary<string, object?> { ["member_id"] = 1 });
        runner.Runs[0].UserContext[WorkflowTriggerHost.SuppressTriggersKey].Should().BeOfType<WorkflowTriggerSuppression>(
            "the runner stamps the engine's unforgeable marker, not a forgeable bool");
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
            Model = Model(table),
            Table = table,
            MutationType = MutationType.Update,
            Data = new Dictionary<string, object?> { ["member_id"] = 1 },
            Result = 1,
            UserContext = new Dictionary<string, object?>(),
            MutationState = MutationObserverContext.NewMutationState(),
        });

        runner.Runs.Should().ContainSingle();
        runner.Runs[0].Inputs["action"].Should().Be("update");
        runner.Runs[0].Inputs["table"].Should().Be("main.members");
    }

    // A trigger table names ONE table. "employee" exists in two schemas here: the old DbName-only
    // compare fired the hr workflow for payroll writes too, and a qualified name never matched.
    [Fact]
    public async Task MutationTrigger_QualifiedTable_FiresOnlyForThatSchema()
    {
        var runner = new CapturingWorkflowRunner();
        var host = new WorkflowTriggerHost(WorkflowMap(Workflow("hr-changed", "on-mutation",
            """{ "table": "hr.employee", "action": "update" }""")), runner);
        var hr = Table("employee", "hr");
        var payroll = Table("employee", "payroll");
        var model = Model(hr, payroll);

        await host.OnMutationAsync(Mutation(model, payroll));
        runner.Runs.Should().BeEmpty("a payroll.employee write is not an hr.employee write");

        await host.OnMutationAsync(Mutation(model, hr));
        runner.Runs.Should().ContainSingle().Which.Inputs["table"].Should().Be("hr.employee");
    }

    [Fact]
    public async Task StateTransitionTrigger_QualifiedTable_FiresOnlyForThatSchema()
    {
        var runner = new CapturingWorkflowRunner();
        var host = new WorkflowTriggerHost(WorkflowMap(Workflow("hr-activated", "on-state-transition",
            """{ "table": "hr.employee", "from": "pending", "to": "active" }""")), runner);
        var hr = Table("employee", "hr");
        var payroll = Table("employee", "payroll");
        var model = Model(hr, payroll);
        var key = new Dictionary<string, object?> { ["id"] = 3 };

        await host.OnTransitionAsync(new StateTransitionInfo(model, payroll, key, "pending", "active", "7", null), new Dictionary<string, object?>());
        runner.Runs.Should().BeEmpty();

        await host.OnTransitionAsync(new StateTransitionInfo(model, hr, key, "pending", "active", "7", null), new Dictionary<string, object?>());
        runner.Runs.Should().ContainSingle().Which.Inputs["entity"].Should().Be("hr.employee");
    }

    [Fact]
    public async Task Trigger_BareTableName_MatchesWhenUniqueInTheModel()
    {
        var runner = new CapturingWorkflowRunner();
        var host = new WorkflowTriggerHost(WorkflowMap(Workflow("hr-changed", "on-mutation",
            """{ "table": "employee", "action": "update" }""")), runner);
        var hr = Table("employee", "hr");

        await host.OnMutationAsync(Mutation(Model(hr, Table("payslip", "payroll")), hr));

        runner.Runs.Should().ContainSingle();
    }

    // An ambiguous bare name fails instead of firing for both tables.
    [Fact]
    public async Task Trigger_AmbiguousBareTableName_FailsInsteadOfMatchingBothTables()
    {
        var runner = new CapturingWorkflowRunner();
        var host = new WorkflowTriggerHost(WorkflowMap(Workflow("employee-changed", "on-mutation",
            """{ "table": "employee", "action": "update" }""")), runner);
        var hr = Table("employee", "hr");
        var payroll = Table("employee", "payroll");

        var act = async () => await host.OnMutationAsync(Mutation(Model(hr, payroll), payroll));

        (await act.Should().ThrowAsync<BifrostQL.Core.Resolvers.BifrostExecutionError>()).WithMessage("*ambiguous*");
        runner.Runs.Should().BeEmpty();
    }

    private static MutationObserverContext Mutation(IDbModel model, IDbTable table) => new()
    {
        Model = model,
        Table = table,
        MutationType = MutationType.Update,
        Data = new Dictionary<string, object?> { ["id"] = 1 },
        Result = 1,
        UserContext = new Dictionary<string, object?>(),
        MutationState = MutationObserverContext.NewMutationState(),
    };

    private static IDbModel Model(params IDbTable[] tables) => new DbModel
    {
        Tables = tables,
        Metadata = new Dictionary<string, object?>(),
    };

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

    private static IDbTable Table(string name, string schema = "main")
    {
        return new DbTable
        {
            TableSchema = schema,
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
            IDictionary<string, object?> userContext,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkflowRunResult> RunAsync(
            WorkflowDefinition workflow,
            IDictionary<string, object?> inputs,
            IDictionary<string, object?> userContext,
            CancellationToken cancellationToken = default)
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
