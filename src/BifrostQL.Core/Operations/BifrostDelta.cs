namespace BifrostQL.Core.Operations;

/// <summary>
/// The rows one <see cref="BifrostOperationContext.DeltaAsync"/> call writes, in the shape of a
/// table's <c>delta</c> mutation argument: rows to insert, rows to update (each naming its key),
/// and rows to delete (each naming its key). Each row is keyed by column name.
/// </summary>
public sealed record BifrostDelta
{
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Inserted { get; init; } = [];
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Updated { get; init; } = [];
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Deleted { get; init; } = [];
}
