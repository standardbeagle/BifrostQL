using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules;

/// <summary>
/// The type of sync operation inferred from tree diffing.
/// </summary>
public enum TreeSyncOperationType
{
    Insert,
    Update,
    Delete
}

/// <summary>
/// A single mutation operation inferred by the tree sync engine.
/// Contains the target table, operation type, data to apply,
/// and any foreign key assignments that depend on parent insert results.
/// </summary>
public sealed class TreeSyncOperation
{
    /// <summary>
    /// The database table this operation targets.
    /// </summary>
    public required IDbTable Table { get; init; }

    /// <summary>
    /// The inferred operation type.
    /// </summary>
    public required TreeSyncOperationType OperationType { get; init; }

    /// <summary>
    /// The data dictionary for the operation. For inserts and updates, contains column values.
    /// For deletes, contains primary key values only.
    /// </summary>
    public required Dictionary<string, object?> Data { get; init; }

    /// <summary>
    /// Foreign key assignments that must be resolved after parent inserts.
    /// Key is the child FK column name, value is the parent table's GraphQL name
    /// whose inserted PK should be assigned to this column.
    /// </summary>
    public Dictionary<string, string> ForeignKeyAssignments { get; init; } = new();

    /// <summary>
    /// The depth level in the tree (0 = root).
    /// </summary>
    public required int Depth { get; init; }
}

/// <summary>
/// Configuration options for tree sync behavior.
/// </summary>
public sealed class TreeSyncOptions
{
    /// <summary>
    /// Maximum nesting depth for tree traversal. Default is 3.
    /// A value of 1 means only the root entity (no children).
    /// </summary>
    public int MaxDepth { get; init; } = 3;

    /// <summary>
    /// When true, children present in the existing data but absent from the submitted
    /// tree are inferred as DELETE operations. Default is true.
    /// </summary>
    public bool DeleteOrphans { get; init; } = true;
}

/// <summary>
/// Compares a submitted nested object tree against existing database state
/// and infers the INSERT, UPDATE, and DELETE operations needed to synchronize them.
/// Operations are returned in dependency order: parents before children for inserts,
/// children before parents for deletes.
/// </summary>
public sealed class TreeSyncEngine
{
    private readonly TreeSyncOptions _options;

    public TreeSyncEngine(IDbModel model, TreeSyncOptions? options = null)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));
        _options = options ?? new TreeSyncOptions();

        if (_options.MaxDepth < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDepth must be at least 1.");
    }

    /// <summary>
    /// Computes the sync operations needed to make existing data match the submitted tree.
    /// </summary>
    /// <param name="table">The root table for the submitted tree.</param>
    /// <param name="submitted">The submitted nested object tree. Child collections are keyed by link name.</param>
    /// <param name="existing">
    /// The existing data for comparison. Same structure as submitted: scalar columns plus
    /// child collections keyed by link name. Null means the root record does not exist yet.
    /// </param>
    /// <returns>Ordered list of sync operations respecting FK dependencies.</returns>
    public IReadOnlyList<TreeSyncOperation> ComputeOperations(
        IDbTable table,
        Dictionary<string, object?> submitted,
        Dictionary<string, object?>? existing)
    {
        var operations = new List<TreeSyncOperation>();
        DiffNode(table, submitted, existing, depth: 0, parentTableGraphQlName: null, operations);
        return OrderOperations(operations);
    }

    private void DiffNode(
        IDbTable table,
        Dictionary<string, object?> submitted,
        Dictionary<string, object?>? existing,
        int depth,
        string? parentTableGraphQlName,
        List<TreeSyncOperation> operations)
    {
        if (depth >= _options.MaxDepth)
            return;

        var scalarData = ExtractScalarData(table, submitted);
        var hasPrimaryKey = HasPrimaryKeyValues(table, scalarData);

        if (existing != null && hasPrimaryKey)
        {
            var existingScalar = ExtractScalarData(table, existing);
            var changedData = FindChangedColumns(table, scalarData, existingScalar);

            if (changedData.Count > 0)
            {
                foreach (var keyCol in table.KeyColumns)
                {
                    if (scalarData.TryGetValue(keyCol.ColumnName, out var keyVal))
                        changedData[keyCol.ColumnName] = keyVal;
                }

                operations.Add(new TreeSyncOperation
                {
                    Table = table,
                    OperationType = TreeSyncOperationType.Update,
                    Data = changedData,
                    Depth = depth,
                });
            }
        }
        else
        {
            AddInsertOperation(table, scalarData, parentTableGraphQlName, depth, operations);
        }

        DiffChildren(table, submitted, existing, depth, operations);
    }

    private static void AddInsertOperation(
        IDbTable table,
        Dictionary<string, object?> scalarData,
        string? parentTableGraphQlName,
        int depth,
        List<TreeSyncOperation> operations)
    {
        var fkAssignments = new Dictionary<string, string>();
        if (parentTableGraphQlName != null)
        {
            var fkColumn = FindForeignKeyColumn(table, parentTableGraphQlName);
            if (fkColumn != null)
                fkAssignments[fkColumn] = parentTableGraphQlName;
        }

        operations.Add(new TreeSyncOperation
        {
            Table = table,
            OperationType = TreeSyncOperationType.Insert,
            Data = new Dictionary<string, object?>(scalarData),
            ForeignKeyAssignments = fkAssignments,
            Depth = depth,
        });
    }

    private void DiffChildren(
        IDbTable table,
        Dictionary<string, object?> submitted,
        Dictionary<string, object?>? existing,
        int depth,
        List<TreeSyncOperation> operations)
    {
        foreach (var multiLink in table.MultiLinks)
        {
            var linkName = multiLink.Key;
            var link = multiLink.Value;
            var childTable = link.ChildTable;

            var submittedChildren = ExtractChildList(submitted, linkName);
            var existingChildren = existing != null ? ExtractChildList(existing, linkName) : new List<Dictionary<string, object?>>();

            if (submittedChildren == null)
                continue;

            var existingByKey = IndexByPrimaryKey(childTable, existingChildren);

            foreach (var submittedChild in submittedChildren)
            {
                var childKey = GetPrimaryKeyValue(childTable, submittedChild);
                Dictionary<string, object?>? matchingExisting = null;

                if (childKey != null && existingByKey.TryGetValue(childKey, out matchingExisting))
                {
                    existingByKey.Remove(childKey);
                }

                DiffNode(childTable, submittedChild, matchingExisting, depth + 1, table.GraphQlName, operations);
            }

            if (_options.DeleteOrphans)
            {
                foreach (var orphan in existingByKey.Values)
                {
                    DeleteNodeAndChildren(childTable, orphan, depth + 1, operations);
                }
            }
        }
    }

    private void DeleteNodeAndChildren(
        IDbTable table,
        Dictionary<string, object?> existing,
        int depth,
        List<TreeSyncOperation> operations)
    {
        if (depth >= _options.MaxDepth)
            return;

        foreach (var multiLink in table.MultiLinks)
        {
            var childTable = multiLink.Value.ChildTable;
            var existingChildren = ExtractChildList(existing, multiLink.Key);
            if (existingChildren == null)
                continue;

            foreach (var child in existingChildren)
            {
                DeleteNodeAndChildren(childTable, child, depth + 1, operations);
            }
        }

        var keyData = new Dictionary<string, object?>();
        foreach (var keyCol in table.KeyColumns)
        {
            if (existing.TryGetValue(keyCol.ColumnName, out var keyVal))
                keyData[keyCol.ColumnName] = keyVal;
        }

        if (keyData.Count > 0)
        {
            operations.Add(new TreeSyncOperation
            {
                Table = table,
                OperationType = TreeSyncOperationType.Delete,
                Data = keyData,
                Depth = depth,
            });
        }
    }

    private static Dictionary<string, object?> ExtractScalarData(
        IDbTable table,
        Dictionary<string, object?> data)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in data)
        {
            if (table.ColumnLookup.ContainsKey(kvp.Key))
                result[kvp.Key] = kvp.Value;
        }
        return result;
    }

    private static bool HasPrimaryKeyValues(IDbTable table, Dictionary<string, object?> data)
    {
        var keyColumns = table.KeyColumns.ToList();
        if (keyColumns.Count == 0)
            return false;

        return keyColumns.All(k =>
            data.TryGetValue(k.ColumnName, out var val) && val != null);
    }

    private static Dictionary<string, object?> FindChangedColumns(
        IDbTable table,
        Dictionary<string, object?> submitted,
        Dictionary<string, object?> existing)
    {
        var changed = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in submitted)
        {
            if (table.KeyColumns.Any(k =>
                    string.Equals(k.ColumnName, kvp.Key, StringComparison.OrdinalIgnoreCase)))
                continue;

            existing.TryGetValue(kvp.Key, out var existingVal);
            if (!Equals(kvp.Value, existingVal))
                changed[kvp.Key] = kvp.Value;
        }
        return changed;
    }

    private static string? FindForeignKeyColumn(IDbTable childTable, string parentGraphQlName)
    {
        foreach (var link in childTable.SingleLinks.Values)
        {
            if (string.Equals(link.ParentTable.GraphQlName, parentGraphQlName, StringComparison.OrdinalIgnoreCase))
                return link.ChildId.ColumnName;
        }
        return null;
    }

    private static List<Dictionary<string, object?>>? ExtractChildList(
        Dictionary<string, object?> data,
        string linkName)
    {
        if (!data.TryGetValue(linkName, out var childObj))
            return null;

        if (childObj is List<Dictionary<string, object?>> typed)
            return typed;

        if (childObj is IEnumerable<object> enumerable)
        {
            return enumerable
                .OfType<Dictionary<string, object?>>()
                .ToList();
        }

        return null;
    }

    private static string? GetPrimaryKeyValue(
        IDbTable table,
        Dictionary<string, object?> data)
    {
        var keyColumns = table.KeyColumns.ToList();
        if (keyColumns.Count == 0)
            return null;

        var parts = new List<string>();
        foreach (var keyCol in keyColumns)
        {
            if (!data.TryGetValue(keyCol.ColumnName, out var val) || val == null)
                return null;
            parts.Add(val.ToString()!);
        }
        return string.Join("|", parts);
    }

    private static Dictionary<string, Dictionary<string, object?>> IndexByPrimaryKey(
        IDbTable table,
        List<Dictionary<string, object?>>? items)
    {
        var index = new Dictionary<string, Dictionary<string, object?>>();
        if (items == null)
            return index;

        foreach (var item in items)
        {
            var key = GetPrimaryKeyValue(table, item);
            if (key != null)
                index[key] = item;
        }
        return index;
    }

    private static IReadOnlyList<TreeSyncOperation> OrderOperations(
        List<TreeSyncOperation> operations)
    {
        var inserts = operations
            .Where(op => op.OperationType == TreeSyncOperationType.Insert)
            .OrderBy(op => op.Depth)
            .ToList();

        var updates = operations
            .Where(op => op.OperationType == TreeSyncOperationType.Update)
            .OrderBy(op => op.Depth)
            .ToList();

        var deletes = operations
            .Where(op => op.OperationType == TreeSyncOperationType.Delete)
            .OrderByDescending(op => op.Depth)
            .ToList();

        var ordered = new List<TreeSyncOperation>(operations.Count);
        ordered.AddRange(inserts);
        ordered.AddRange(updates);
        ordered.AddRange(deletes);
        return ordered;
    }
}
