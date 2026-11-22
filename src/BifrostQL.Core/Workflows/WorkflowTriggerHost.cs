using System.Text.Json;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
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
                 MatchesTable(payload, transition.Model, transition.Table)
                 && Matches(payload, "from", transition.From)
                 && Matches(payload, "to", transition.To)))
        {
            await RunTriggeredAsync(workflow, new Dictionary<string, object?>
            {
                ["entity"] = transition.Entity,
                ["entityKey"] = new Dictionary<string, object?>(transition.EntityKey, StringComparer.OrdinalIgnoreCase),
                ["from"] = transition.From,
                ["to"] = transition.To,
                ["actor"] = transition.Actor,
                ["eventName"] = transition.EventName,
            }, userContext, CancellationToken.None);
        }
    }

    public async ValueTask OnMutationAsync(MutationObserverContext context)
    {
        var action = context.MutationType.ToString().ToLowerInvariant();
        var model = context.Model
            ?? throw new InvalidOperationException("A mutation observer context carries no model; workflow trigger tables cannot be resolved.");
        foreach (var workflow in Matching("on-mutation", payload =>
                 MatchesTable(payload, model, context.Table)
                 && Matches(payload, "action", action)))
        {
            await RunTriggeredAsync(workflow, new Dictionary<string, object?>
            {
                ["table"] = $"{context.Table.TableSchema}.{context.Table.DbName}",
                ["action"] = action,
                ["result"] = context.Result,
                ["data"] = new Dictionary<string, object?>(context.Data, StringComparer.OrdinalIgnoreCase),
            }, context.UserContext, CancellationToken.None);
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

    internal Task RunScheduledAsync(
        WorkflowDefinition workflow, DateTimeOffset now, IDictionary<string, object?> userContext, CancellationToken cancellationToken)
        => RunTriggeredAsync(workflow, new Dictionary<string, object?>
        {
            ["scheduledAt"] = now.ToString("o"),
        }, userContext, cancellationToken);

    // The transition and mutation observer hooks carry no CancellationToken, so a run they
    // trigger passes CancellationToken.None; a scheduled run takes the scheduler's token.
    private async Task RunTriggeredAsync(
        WorkflowDefinition workflow,
        IDictionary<string, object?> inputs,
        IDictionary<string, object?> userContext,
        CancellationToken cancellationToken)
    {
        var scopedContext = new Dictionary<string, object?>(userContext, StringComparer.OrdinalIgnoreCase)
        {
            [SuppressTriggersKey] = WorkflowTriggerSuppression.Instance,
        };
        await _runner.RunAsync(workflow, inputs, scopedContext, cancellationToken);
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

    /// <summary>
    /// Whether the trigger's <c>table</c> names <paramref name="table"/>. A qualified
    /// <c>schema.name</c> (split on the first dot) names exactly one table. A bare name is
    /// accepted only when it is unique in the model: an ambiguous one throws the model's
    /// ambiguity error rather than firing for every schema that defines it. Identity is the
    /// resolved <see cref="IDbTable"/>, never a name compare.
    /// </summary>
    private static bool MatchesTable(JsonElement payload, IDbModel model, IDbTable table)
    {
        if (!payload.TryGetProperty("table", out var configured) || configured.GetString() is not { } name)
            return false;

        var dot = name.IndexOf('.');
        if (dot > 0)
            return model.TryGetTableFromDbName(name[..dot], name[(dot + 1)..], out var qualified)
                && ReferenceEquals(qualified, table);

        return string.Equals(name, table.DbName, StringComparison.OrdinalIgnoreCase)
            && ReferenceEquals(model.GetTableFromDbName(name), table);
    }

    private static bool Matches(JsonElement payload, string property, string value)
        => payload.TryGetProperty(property, out var configured)
           && string.Equals(configured.GetString(), value, StringComparison.OrdinalIgnoreCase);
}
