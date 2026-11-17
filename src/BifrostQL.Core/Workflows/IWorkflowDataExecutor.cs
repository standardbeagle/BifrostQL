namespace BifrostQL.Core.Workflows;

/// <summary>
/// Data operations available to workflow steps. Server implementations must
/// route these calls through the normal BifrostQL GraphQL pipeline, and pass
/// <c>cancellationToken</c> to it so a caller that goes away stops the work.
/// </summary>
public interface IWorkflowDataExecutor
{
    Task<IDictionary<string, object?>?> QuerySingleAsync(
        string table, object id, IDictionary<string, object?> userContext, CancellationToken cancellationToken = default);

    Task InsertAsync(
        string table, object values, IDictionary<string, object?> userContext, CancellationToken cancellationToken = default);

    Task UpdateAsync(
        string table, object values, IDictionary<string, object?> userContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves one row from <see cref="WorkflowTransition.From"/> to <see cref="WorkflowTransition.To"/>.
    /// The write carries only the key and the state column, and lands only while the row still
    /// holds <see cref="WorkflowTransition.From"/>: a row another writer moved after the step read
    /// it affects no rows and fails with the CONFLICT error code.
    /// </summary>
    Task TransitionAsync(
        WorkflowTransition transition, IDictionary<string, object?> userContext, CancellationToken cancellationToken = default);
}

/// <summary>One state transition: the row <see cref="Id"/> of <see cref="Table"/>, by its single-column key.</summary>
public sealed record WorkflowTransition(string Table, object Id, string StateColumn, string From, string To);
