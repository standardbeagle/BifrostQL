namespace BifrostQL.Core.Workflows;

public sealed class WorkflowScheduler
{
    private readonly WorkflowTriggerHost _triggerHost;

    public WorkflowScheduler(WorkflowTriggerHost triggerHost)
    {
        _triggerHost = triggerHost;
    }

    public async Task TickAsync(DateTimeOffset now, IDictionary<string, object?>? userContext = null)
    {
        var context = userContext ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var workflow in _triggerHost.ScheduledWorkflows(now))
            await _triggerHost.RunScheduledAsync(workflow, now, context);
    }
}
