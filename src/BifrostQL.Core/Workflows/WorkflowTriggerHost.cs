using System.Text.Json;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Modules;

namespace BifrostQL.Core.Workflows;

public sealed class WorkflowTriggerHost : IStateTransitionObserver, IMutationObserver
{
    public const string SuppressTriggersKey = "_bifrostSuppressWorkflowTriggers";

    private readonly IReadOnlyDictionary<string, WorkflowDefinition> _workflows;
    private readonly IWorkflowRunner _runner;

    public WorkflowTriggerHost(
        IReadOnlyDictionary<string, WorkflowDefinition> workflows,
        IWorkflowRunner runner)
    {
        _workflows = workflows;
        _runner = runner;
    }

    public async ValueTask OnTransitionAsync(
        StateTransitionInfo transition,
        IDictionary<string, object?> userContext)
    {
        foreach (var workflow in Matching("on-state-transition", payload =>
                 Matches(payload, "table", transition.Entity)
                 && Matches(payload, "from", transition.From)
                 && Matches(payload, "to", transition.To)))
        {
            await RunTriggeredAsync(workflow, new Dictionary<string, object?>
            {
                ["entity"] = transition.Entity,
                ["entityId"] = transition.EntityId,
                ["from"] = transition.From,
                ["to"] = transition.To,
                ["actor"] = transition.Actor,
                ["eventName"] = transition.EventName,
            }, userContext);
        }
    }

    public async ValueTask OnMutationAsync(MutationObserverContext context)
    {
        var action = context.MutationType.ToString().ToLowerInvariant();
        foreach (var workflow in Matching("on-mutation", payload =>
                 Matches(payload, "table", context.Table.DbName)
                 && Matches(payload, "action", action)))
        {
            await RunTriggeredAsync(workflow, new Dictionary<string, object?>
            {
                ["table"] = context.Table.DbName,
                ["action"] = action,
                ["result"] = context.Result,
                ["data"] = new Dictionary<string, object?>(context.Data, StringComparer.OrdinalIgnoreCase),
            }, context.UserContext);
        }
    }

    internal IEnumerable<WorkflowDefinition> ScheduledWorkflows(DateTimeOffset now)
        => Matching("scheduled", payload =>
        {
            if (payload.TryGetProperty("runOnTick", out var runOnTick)
                && runOnTick.ValueKind == JsonValueKind.True)
                return true;

            return payload.TryGetProperty("intervalSeconds", out var interval)
                   && interval.TryGetInt32(out var seconds)
                   && seconds > 0;
        });

    internal Task RunScheduledAsync(WorkflowDefinition workflow, DateTimeOffset now, IDictionary<string, object?> userContext)
        => RunTriggeredAsync(workflow, new Dictionary<string, object?>
        {
            ["scheduledAt"] = now.ToString("o"),
        }, userContext);

    private async Task RunTriggeredAsync(
        WorkflowDefinition workflow,
        IDictionary<string, object?> inputs,
        IDictionary<string, object?> userContext)
    {
        var scopedContext = new Dictionary<string, object?>(userContext, StringComparer.OrdinalIgnoreCase)
        {
            [SuppressTriggersKey] = true,
        };
        await _runner.RunAsync(workflow, inputs, scopedContext);
    }

    private IEnumerable<WorkflowDefinition> Matching(
        string triggerType,
        Func<JsonElement, bool> payloadPredicate)
    {
        foreach (var workflow in _workflows.Values)
        {
            if (!string.Equals(workflow.Trigger.Type, triggerType, StringComparison.OrdinalIgnoreCase))
                continue;

            var payload = workflow.Trigger.Payload;
            if (payload is null || payload.Value.ValueKind != JsonValueKind.Object)
                continue;

            if (payloadPredicate(payload.Value))
                yield return workflow;
        }
    }

    private static bool Matches(JsonElement payload, string property, string value)
        => payload.TryGetProperty(property, out var configured)
           && string.Equals(configured.GetString(), value, StringComparison.OrdinalIgnoreCase);
}
