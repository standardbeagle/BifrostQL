using BifrostQL.Core.Model;

namespace BifrostQL.Core.Workflows;

/// <summary>
/// The engine's guard on a workflow transition's write: the UPDATE must still find the row in the
/// state the step checked, so a transition that raced another write between its read and its write
/// affects no rows and fails as a conflict instead of overwriting a state nobody validated.
///
/// <para>Carried in the write's user context under <see cref="ContextKey"/> and applied by
/// <see cref="Modules.MutationTransformersWrap.TransformAsync"/>, the pre-chain seam every write
/// path runs, so no transformer registration can drop it. A sealed engine token (protocol-adapter
/// invariant 14): the constructor is internal, so only Core and its <c>InternalsVisibleTo</c> list
/// (BifrostQL.Server's workflow executor) can mint one, and a string a caller puts under the key in
/// wire context or a claim is ignored.</para>
/// </summary>
public sealed class WorkflowTransitionGuard
{
    /// <summary>The user-context key the guard travels under.</summary>
    public const string ContextKey = "_bifrost_workflow_transition_guard";

    private readonly string _tableSchema;
    private readonly string _tableName;
    private readonly string _keyColumn;
    private readonly string _keyValue;

    internal WorkflowTransitionGuard(IDbTable table, string keyColumn, object keyValue, string stateColumn, string fromState)
    {
        ArgumentNullException.ThrowIfNull(table);
        _tableSchema = table.TableSchema;
        _tableName = table.DbName;
        _keyColumn = keyColumn;
        _keyValue = Convert.ToString(keyValue, System.Globalization.CultureInfo.InvariantCulture)
            ?? throw new ArgumentNullException(nameof(keyValue));
        StateColumn = stateColumn;
        FromState = fromState;
    }

    /// <summary>The state column's database name.</summary>
    internal string StateColumn { get; }

    /// <summary>The state the step read; the write lands only while the row still holds it.</summary>
    internal string FromState { get; }

    /// <summary>
    /// The guard for an update of <paramref name="table"/> with <paramref name="data"/> (keyed by
    /// database column name), or null when the context carries none or it guards another row.
    /// Matching the row as well as the table keeps the guard off any other write the same request
    /// makes, such as one a mutation observer issues with the same user context.
    /// </summary>
    internal static WorkflowTransitionGuard? For(
        IDictionary<string, object?> userContext, IDbTable table, IReadOnlyDictionary<string, object?> data)
    {
        if (!userContext.TryGetValue(ContextKey, out var value) || value is not WorkflowTransitionGuard guard)
            return null;
        if (!string.Equals(guard._tableSchema, table.TableSchema, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(guard._tableName, table.DbName, StringComparison.OrdinalIgnoreCase))
            return null;
        return data.TryGetValue(guard._keyColumn, out var key)
               && string.Equals(Convert.ToString(key, System.Globalization.CultureInfo.InvariantCulture), guard._keyValue, StringComparison.Ordinal)
            ? guard
            : null;
    }
}
