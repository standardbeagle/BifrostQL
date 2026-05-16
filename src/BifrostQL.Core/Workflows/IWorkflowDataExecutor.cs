namespace BifrostQL.Core.Workflows;

/// <summary>
/// Data operations available to workflow steps. Server implementations must
/// route these calls through the normal BifrostQL GraphQL pipeline.
/// </summary>
public interface IWorkflowDataExecutor
{
    Task<IDictionary<string, object?>?> QuerySingleAsync(
        string table, object id, IDictionary<string, object?> userContext);

    Task InsertAsync(string table, object values, IDictionary<string, object?> userContext);

    Task UpdateAsync(string table, object values, IDictionary<string, object?> userContext);
}
