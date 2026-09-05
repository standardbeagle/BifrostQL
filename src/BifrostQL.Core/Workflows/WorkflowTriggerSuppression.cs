namespace BifrostQL.Core.Workflows;

/// <summary>
/// Unforgeable marker for the workflow-trigger suppression flag
/// (<see cref="WorkflowTriggerHost.SuppressTriggersKey"/>). The value under that
/// user-context key suppresses post-commit observers ONLY when it is reference-equal
/// to <see cref="Instance"/>; any bool, string, or other scalar a claims provider or
/// wire payload can emit never trips the gate.
///
/// Mint boundary: the BifrostQL.Core assembly plus its <c>InternalsVisibleTo</c> list
/// (BifrostQL.Core.Test, BifrostQL.Server, BifrostQL.Sqlite, BifrostQL.SqlServer,
/// BifrostQL.Benchmarks). The only production mint is
/// <see cref="WorkflowTriggerHost"/>, which stamps the marker onto the scoped context
/// of a workflow-triggered run so the run's own writes don't recursively re-fire
/// triggers. No public constructor, factory, or settable member may ever be added.
/// </summary>
public sealed class WorkflowTriggerSuppression
{
    internal static readonly WorkflowTriggerSuppression Instance = new();

    private WorkflowTriggerSuppression() { }
}
