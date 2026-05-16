using System.Text.Json;
using BifrostQL.Core.AppMetadata;
using BifrostQL.Core.Workflows;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

public class WorkflowDefinitionTests
{
    [Fact]
    public void WorkflowDefinition_RoundTripsThroughJson()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "activate-member",
            Trigger = new WorkflowTrigger
            {
                Type = "manual",
                Payload = Json("""{ "button": "activate" }"""),
            },
            Inputs = Json("""{ "type": "object", "required": [ "memberId" ] }"""),
            Outputs = Json("""{ "activated": "boolean" }"""),
            Steps = new[]
            {
                new WorkflowStep
                {
                    Name = "load-member",
                    Type = "query",
                    Payload = Json("""{ "table": "members", "filter": { "member_id": "{{ inputs.memberId }}" } }"""),
                    Output = "member",
                },
                new WorkflowStep
                {
                    Name = "activate",
                    Type = "transition",
                    Payload = Json("""{ "table": "members", "from": "pending", "to": "active" }"""),
                },
            },
        };

        var json = WorkflowJson.Serialize(workflow);
        var restored = WorkflowJson.Deserialize(json);

        Canonical(WorkflowJson.Serialize(restored)).Should().Be(Canonical(json));
    }

    [Fact]
    public void Collector_ParsesRepresentativeOverlay()
    {
        var overlay = new AppMetadataModel
        {
            Workflows = new[]
            {
                new WorkflowDefinition
                {
                    Name = "publish-event",
                    Trigger = new WorkflowTrigger
                    {
                        Type = "on-state-transition",
                        Payload = Json("""{ "table": "events", "from": "draft", "to": "published" }"""),
                    },
                    Inputs = Json("""{ "type": "object" }"""),
                    Outputs = Json("""{ "auditId": "number" }"""),
                    Steps = new[]
                    {
                        new WorkflowStep
                        {
                            Name = "policy",
                            Type = "policy-check",
                            Payload = Json("""{ "table": "events", "action": "update" }"""),
                        },
                        new WorkflowStep
                        {
                            Name = "audit",
                            Type = "audit",
                            Payload = Json("""{ "action": "event.published", "entityType": "events", "entityId": "{{ trigger.entityId }}" }"""),
                            Output = "audit",
                        },
                    },
                },
            },
        };

        var json = WorkflowJson.SerializeOverlay(overlay);
        var workflows = WorkflowConfigCollector.FromJson(json);

        workflows.Should().ContainKey("publish-event");
        workflows["publish-event"].Steps.Should().HaveCount(2);
        workflows["publish-event"].Trigger.Type.Should().Be("on-state-transition");
    }

    [Theory]
    [InlineData("""{ "workflows": [ { "name": "bad", "trigger": { "type": "manual" }, "steps": [ { "name": "broken", "type": "query", "payload": { "filter": {} } } ] } ] }""")]
    [InlineData("""{ "workflows": [ { "name": "bad", "trigger": { "type": "manual" }, "steps": [ { "name": "broken", "type": "not-real", "payload": {} } ] } ] }""")]
    [InlineData("""{ "workflows": [ { "name": "bad", "trigger": { "type": "manual" }, "steps": [] } ] }""")]
    public void Collector_RejectsMalformedWorkflowWithGenericError(string json)
    {
        var act = () => WorkflowConfigCollector.FromJson(json);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Invalid workflow metadata.");
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string Canonical(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement, WorkflowJson.Options);
    }
}
