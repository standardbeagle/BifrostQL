using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Builds the operation list for the EXPLICIT-ops graph save (<c>save:</c>): every node
/// says what happens to it via <c>_op</c> — nothing is inferred from database state and no
/// orphan is ever deleted (that reconcile semantic belongs to <c>sync:</c>). The generous
/// defaults when <c>_op</c> is absent: a node WITH its full primary key updates, a node
/// without one inserts. A <c>delete</c> node needs only its key. Unlisted children are
/// untouched, root delete is legal, and no current-state load is needed at all — the ops
/// execute via the unchanged <see cref="TreeSyncExecutor"/> (per-node transformer chain,
/// instance-scoped FK flow, one transaction), reusing <see cref="TreeSyncEngine"/>'s
/// extraction/FK/ordering machinery so the two graph writers cannot drift.
/// </summary>
public sealed class SaveTreeBuilder
{
    public const string OpField = "_op";

    private readonly TreeSyncOptions _options;

    public SaveTreeBuilder(TreeSyncOptions? options = null)
    {
        _options = options ?? new TreeSyncOptions();
        if (_options.MaxDepth < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDepth must be at least 1.");
    }

    /// <summary>The root node's effective operation — the caller uses it to shape the reply and notifications.</summary>
    public static TreeSyncOperationType RootOperation(IDbTable table, Dictionary<string, object?> tree)
        => ResolveOp(table, tree, TreeSyncEngine.ExtractScalarData(table, tree));

    public IReadOnlyList<TreeSyncOperation> BuildOperations(IDbTable table, Dictionary<string, object?> tree)
    {
        var operations = new List<TreeSyncOperation>();
        WalkNode(table, tree, depth: 0, parentLink: null, parentKnownId: null, parentInstanceId: null, operations);
        return TreeSyncEngine.OrderOperations(operations);
    }

    private void WalkNode(
        IDbTable table,
        Dictionary<string, object?> node,
        int depth,
        TableLinkDto? parentLink,
        object? parentKnownId,
        string? parentInstanceId,
        List<TreeSyncOperation> operations)
    {
        // Explicit ops must never be silently dropped: a node past the depth bound is an
        // error, unlike sync's silent truncation of an inferred diff.
        if (depth >= _options.MaxDepth)
            throw new BifrostExecutionError(
                $"Save tree exceeds the maximum depth of {_options.MaxDepth}.");

        var scalar = TreeSyncEngine.ExtractScalarData(table, node);
        var op = ResolveOp(table, node, scalar);

        string? thisInstanceId = null;
        switch (op)
        {
            case TreeSyncOperationType.Delete:
            {
                var keyData = KeyData(table, scalar)
                    ?? throw new BifrostExecutionError(
                        $"A delete node for '{table.GraphQlName}' must carry its full primary key.");
                operations.Add(new TreeSyncOperation
                {
                    Table = table,
                    OperationType = TreeSyncOperationType.Delete,
                    Data = keyData,
                    Depth = depth,
                });
                break;
            }
            case TreeSyncOperationType.Update:
            {
                if (KeyData(table, scalar) is null)
                    throw new BifrostExecutionError(
                        $"An update node for '{table.GraphQlName}' must carry its full primary key.");
                if (!scalar.Keys.Any(k => !IsKeyColumn(table, k)))
                    throw new BifrostExecutionError(
                        $"An update node for '{table.GraphQlName}' must set at least one non-key column.");
                operations.Add(new TreeSyncOperation
                {
                    Table = table,
                    OperationType = TreeSyncOperationType.Update,
                    Data = scalar,
                    Depth = depth,
                });
                break;
            }
            default:
                thisInstanceId = TreeSyncEngine.AddInsertOperation(
                    table, scalar, parentLink, parentKnownId, parentInstanceId, depth, operations);
                break;
        }

        // The PK to hand down: an inserted parent has none yet (children defer their FK
        // to this instance); an updated/deleted parent's key is known.
        var thisKnownId = TreeSyncEngine.GetSingleKeyValue(table, scalar);

        foreach (var multiLink in table.MultiLinks)
        {
            var children = TreeSyncEngine.ExtractChildList(node, multiLink.Key);
            if (children == null)
                continue;
            foreach (var child in children)
                WalkNode(multiLink.Value.ChildTable, child, depth + 1, multiLink.Value, thisKnownId, thisInstanceId, operations);
        }
    }

    private static TreeSyncOperationType ResolveOp(
        IDbTable table, Dictionary<string, object?> node, Dictionary<string, object?> scalar)
    {
        if (node.TryGetValue(OpField, out var raw) && raw is not null)
        {
            var name = Convert.ToString(raw)?.Trim().ToLowerInvariant();
            return name switch
            {
                "insert" => TreeSyncOperationType.Insert,
                "update" => TreeSyncOperationType.Update,
                "delete" => TreeSyncOperationType.Delete,
                _ => throw new BifrostExecutionError($"Unknown save operation '{raw}'."),
            };
        }
        // The generous default: a node carrying its full key updates, one without inserts.
        return TreeSyncEngine.HasPrimaryKeyValues(table, scalar)
            ? TreeSyncOperationType.Update
            : TreeSyncOperationType.Insert;
    }

    private static Dictionary<string, object?>? KeyData(IDbTable table, Dictionary<string, object?> scalar)
    {
        if (!TreeSyncEngine.HasPrimaryKeyValues(table, scalar))
            return null;
        var keyData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var keyColumn in table.KeyColumns)
            keyData[keyColumn.ColumnName] = scalar[keyColumn.ColumnName];
        return keyData;
    }

    private static bool IsKeyColumn(IDbTable table, string columnName)
        => table.KeyColumns.Any(k => string.Equals(k.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
}
