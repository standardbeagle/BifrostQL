using System.Text.Json;
using BifrostQL.Core.Workflows;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

public class WorkflowRunnerTests
{
    [Fact]
    public async Task RunAsync_ExecutesQueryMutationTransitionPolicyAuditBranchAndParallelStepsThroughExecutor()
    {
        var executor = new CapturingWorkflowDataExecutor();
        executor.Rows[("members", "1")] = new Dictionary<string, object?>
        {
            ["member_id"] = 1L,
            ["status"] = "pending",
            ["first_name"] = "Ada",
        };

        var workflow = new WorkflowDefinition
        {
            Name = "member-lifecycle",
            Trigger = new WorkflowTrigger { Type = "manual" },
            Inputs = Json("""{ "type": "object", "required": [ "memberId" ] }"""),
            Steps = new[]
            {
                new WorkflowStep
                {
                    Name = "load",
                    Type = "query",
                    Payload = Json("""{ "table": "members", "id": "{{ inputs.memberId }}" }"""),
                    Output = "member",
                },
                new WorkflowStep
                {
                    Name = "policy",
                    Type = "policy-check",
                    Payload = Json("""{ "table": "members", "action": "update" }"""),
                },
                new WorkflowStep
                {
                    Name = "rename",
                    Type = "mutation",
                    Payload = Json("""{ "table": "members", "action": "update", "values": { "member_id": "{{ inputs.memberId }}", "first_name": "Augusta", "status": "{{ steps.member.status }}" } }"""),
                    Output = "renameValues",
                },
                new WorkflowStep
                {
                    Name = "activate",
                    Type = "transition",
                    Payload = Json("""{ "table": "members", "id": "{{ inputs.memberId }}", "stateColumn": "status", "from": "pending", "to": "active" }"""),
                    Output = "activated",
                },
                new WorkflowStep
                {
                    Name = "branch",
                    Type = "branch",
                    Payload = Json("""{ "condition": { "equals": { "left": "{{ inputs.audit }}", "right": true } }, "then": [ { "name": "audit-branch", "type": "audit", "payload": { "action": "member.activated", "entityType": "members", "entityId": "{{ inputs.memberId }}" }, "output": "audit" } ] }"""),
                },
                new WorkflowStep
                {
                    Name = "parallel",
                    Type = "parallel",
                    Payload = Json("""{ "steps": [ { "name": "parallel-query", "type": "query", "payload": { "table": "members", "id": "{{ inputs.memberId }}" } } ] }"""),
                },
            },
        };
        var runner = new WorkflowRunner(
            new Dictionary<string, WorkflowDefinition> { [workflow.Name] = workflow },
            executor);

        var result = await runner.RunAsync(
            "member-lifecycle",
            new Dictionary<string, object?> { ["memberId"] = 1L, ["audit"] = true },
            new Dictionary<string, object?> { ["tenant_id"] = 1L });

        result.Succeeded.Should().BeTrue();
        result.Error.Should().BeNull();
        result.Trace.Should().Contain(t => t.StepName == "load" && t.Succeeded);
        result.Trace.Should().Contain(t => t.StepName == "audit-branch" && t.Succeeded);
        executor.Queries.Should().HaveCount(3, "load, transition pre-read, and parallel query all go through the executor");
        executor.Updates.Should().HaveCount(2, "mutation and transition both use executor updates");
        executor.Inserts.Should().ContainSingle(i => i.Table == "audit_log");
        result.Outputs.Should().ContainKey("activated");
    }

    [Fact]
    public async Task RunAsync_BadInputReturnsTypedErrorBeforeAnyStepRuns()
    {
        var executor = new CapturingWorkflowDataExecutor();
        var workflow = new WorkflowDefinition
        {
            Name = "requires-member",
            Trigger = new WorkflowTrigger { Type = "manual" },
            Inputs = Json("""{ "type": "object", "required": [ "memberId" ] }"""),
            Steps = new[]
            {
                new WorkflowStep
                {
                    Name = "load",
                    Type = "query",
                    Payload = Json("""{ "table": "members", "id": "{{ inputs.memberId }}" }"""),
                },
            },
        };

        var result = await new WorkflowRunner(
                new Dictionary<string, WorkflowDefinition> { [workflow.Name] = workflow },
                executor)
            .RunAsync("requires-member", new Dictionary<string, object?>(), new Dictionary<string, object?>());

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(new WorkflowRunError("invalid_input", "Invalid workflow input."));
        result.Trace.Should().BeEmpty();
        executor.Queries.Should().BeEmpty();
    }

    [Theory]
    // Unknown root ("input" vs "inputs") and a malformed one-part template must
    // abort the step rather than write the literal "{{ ... }}" text into the DB.
    [InlineData("""{ "table": "members", "id": "{{ input.memberId }}" }""")]
    [InlineData("""{ "table": "members", "id": "{{ inputs }}" }""")]
    public async Task RunAsync_MalformedTemplate_FailsStepInsteadOfWritingLiteral(string payload)
    {
        var executor = new CapturingWorkflowDataExecutor();
        var workflow = new WorkflowDefinition
        {
            Name = "bad-template",
            Trigger = new WorkflowTrigger { Type = "manual" },
            Steps = new[]
            {
                new WorkflowStep
                {
                    Name = "load",
                    Type = "query",
                    Payload = Json(payload),
                },
            },
        };

        var result = await new WorkflowRunner(
                new Dictionary<string, WorkflowDefinition> { [workflow.Name] = workflow },
                executor)
            .RunAsync("bad-template", new Dictionary<string, object?> { ["memberId"] = 1L },
                new Dictionary<string, object?>());

        result.Succeeded.Should().BeFalse();
        result.Trace.Should().Contain(t => t.StepName == "load" && !t.Succeeded);
    }

    [Fact]
    public async Task RunAsync_StepFailureAbortsAndSurfacesTypedError()
    {
        var executor = new CapturingWorkflowDataExecutor { FailQueries = true };
        var workflow = new WorkflowDefinition
        {
            Name = "failing",
            Trigger = new WorkflowTrigger { Type = "manual" },
            Steps = new[]
            {
                new WorkflowStep
                {
                    Name = "load",
                    Type = "query",
                    Payload = Json("""{ "table": "members", "id": 1 }"""),
                },
                new WorkflowStep
                {
                    Name = "never-runs",
                    Type = "audit",
                    Payload = Json("""{ "action": "x", "entityType": "members", "entityId": "1" }"""),
                },
            },
        };

        var result = await new WorkflowRunner(
                new Dictionary<string, WorkflowDefinition> { [workflow.Name] = workflow },
                executor)
            .RunAsync("failing", new Dictionary<string, object?>(), new Dictionary<string, object?>());

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(new WorkflowRunError("step_failed", "Workflow step failed.", "load"));
        result.Trace.Should().ContainSingle().Which.StepName.Should().Be("load");
        executor.Inserts.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_CrossTenantInvisibleQueryReturnsNullThroughExecutor()
    {
        var executor = new CapturingWorkflowDataExecutor();
        var workflow = new WorkflowDefinition
        {
            Name = "tenant-scoped",
            Trigger = new WorkflowTrigger { Type = "manual" },
            Steps = new[]
            {
                new WorkflowStep
                {
                    Name = "load",
                    Type = "query",
                    Payload = Json("""{ "table": "members", "id": 99 }"""),
                    Output = "member",
                },
            },
        };

        var result = await new WorkflowRunner(
                new Dictionary<string, WorkflowDefinition> { [workflow.Name] = workflow },
                executor)
            .RunAsync("tenant-scoped", new Dictionary<string, object?>(), new Dictionary<string, object?> { ["tenant_id"] = 2L });

        result.Succeeded.Should().BeTrue();
        result.Outputs["member"].Should().BeNull("the runner preserves the executor's tenant-filtered empty result");
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class CapturingWorkflowDataExecutor : IWorkflowDataExecutor
    {
        public Dictionary<(string Table, string Id), IDictionary<string, object?>> Rows { get; } = new();
        public List<(string Table, object Id)> Queries { get; } = new();
        public List<(string Table, IDictionary<string, object?> Values)> Inserts { get; } = new();
        public List<(string Table, IDictionary<string, object?> Values)> Updates { get; } = new();
        public bool FailQueries { get; init; }

        public Task<IDictionary<string, object?>?> QuerySingleAsync(
            string table,
            object id,
            IDictionary<string, object?> userContext)
        {
            if (FailQueries)
                throw new InvalidOperationException("query failed");

            Queries.Add((table, id));
            Rows.TryGetValue((table, id.ToString() ?? string.Empty), out var row);
            return Task.FromResult(row);
        }

        public Task InsertAsync(string table, object values, IDictionary<string, object?> userContext)
        {
            Inserts.Add((table, ToDictionary(values)));
            return Task.CompletedTask;
        }

        public Task UpdateAsync(string table, object values, IDictionary<string, object?> userContext)
        {
            var dictionary = ToDictionary(values);
            Updates.Add((table, dictionary));

            if (dictionary.TryGetValue("member_id", out var id))
                Rows[(table, id?.ToString() ?? string.Empty)] = dictionary;

            return Task.CompletedTask;
        }

        private static IDictionary<string, object?> ToDictionary(object values)
            => values as IDictionary<string, object?>
               ?? throw new InvalidOperationException("Expected dictionary values.");
    }
}
