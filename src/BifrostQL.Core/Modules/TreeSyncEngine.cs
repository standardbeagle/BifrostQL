using System.Globalization;
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
    /// Stable identity of THIS operation's inserted row. The executor records the
    /// produced PK under this id so a child can resolve its deferred FK to its own
    /// parent INSTANCE — not merely to the last-inserted row of the parent table.
    /// </summary>
    public string InstanceId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// When this insert defers its FK to a NEW parent (see
    /// <see cref="ForeignKeyAssignments"/>), the <see cref="InstanceId"/> of that
    /// specific parent operation. Null when there is no deferred parent (root, or a
    /// parent whose PK is already known and written directly into <see cref="Data"/>).
    /// Lets the executor disambiguate two sibling parents of the same table, each
    /// owning their own children, which a table-name-keyed lookup would collapse —
    /// silently attaching the first parent's children to the second parent's PK.
    /// </summary>
    public string? ParentInstanceId { get; init; }

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
        DiffNode(table, submitted, existing, depth: 0, parentLink: null, parentKnownId: null, parentInstanceId: null, operations);
        return OrderOperations(operations);
    }

    private void DiffNode(
        IDbTable table,
        Dictionary<string, object?> submitted,
        Dictionary<string, object?>? existing,
        int depth,
        TableLinkDto? parentLink,
        object? parentKnownId,
        string? parentInstanceId,
        List<TreeSyncOperation> operations)
    {
        if (depth >= _options.MaxDepth)
            return;

        var scalarData = ExtractScalarData(table, submitted);
        var hasPrimaryKey = HasPrimaryKeyValues(table, scalarData);

        // Instance id of THIS node's insert (null when this node is an update or is
        // skipped), handed to its children so their deferred FK resolves to this
        // specific parent instance rather than the last-inserted row of the table.
        string? thisInstanceId = null;

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
            thisInstanceId = AddInsertOperation(table, scalarData, parentLink, parentKnownId, parentInstanceId, depth, operations);
        }

        // The PK to hand down to children: a freshly inserted parent has none yet
        // (children defer their FK to it), but an existing/PK-bearing parent does.
        var thisKnownId = GetSingleKeyValue(table, scalarData)
            ?? (existing != null ? GetSingleKeyValue(table, ExtractScalarData(table, existing)) : null);

        DiffChildren(table, submitted, existing, depth, thisKnownId, thisInstanceId, operations);
    }

    private static string AddInsertOperation(
        IDbTable table,
        Dictionary<string, object?> scalarData,
        TableLinkDto? parentLink,
        object? parentKnownId,
        string? parentInstanceId,
        int depth,
        List<TreeSyncOperation> operations)
    {
        var data = new Dictionary<string, object?>(scalarData);
        var fkAssignments = new Dictionary<string, string>();
        string? deferredParentInstanceId = null;
        if (parentLink != null)
        {
            var parentGraphQlName = parentLink.ParentTable.GraphQlName;
            // The child link column receives the parent's PK. A conventional FK
            // exposes it as a SingleLink; a polymorphic link has none, so fall
            // back to the link's own child id column (e.g. notes.entity_id).
            var fkColumn = FindForeignKeyColumn(table, parentGraphQlName)
                ?? parentLink.ChildId?.ColumnName;
            if (fkColumn != null)
            {
                // Existing parent: write its known PK directly. New parent: defer
                // until the parent insert produces a PK (resolved by the executor),
                // tagging the SPECIFIC parent instance so two sibling parents of the
                // same table don't collide on a table-name-keyed lookup.
                if (parentKnownId != null)
                    data[fkColumn] = parentKnownId;
                else
                {
                    fkAssignments[fkColumn] = parentGraphQlName;
                    deferredParentInstanceId = parentInstanceId;
                }
            }

            // Polymorphic link: stamp the discriminator constant (e.g.
            // entity_type = 'company') so the child row resolves back through
            // the same polymorphic collection it was inserted under.
            if (parentLink.TypePredicate is { } predicate)
                data[predicate.Column.ColumnName] = predicate.Value;
        }

        var op = new TreeSyncOperation
        {
            Table = table,
            OperationType = TreeSyncOperationType.Insert,
            Data = data,
            ForeignKeyAssignments = fkAssignments,
            ParentInstanceId = deferredParentInstanceId,
            Depth = depth,
        };
        operations.Add(op);
        return op.InstanceId;
    }

    // Returns the single-column primary-key value if present and non-null,
    // otherwise null (composite keys and missing values yield null).
    private static object? GetSingleKeyValue(IDbTable table, Dictionary<string, object?> data)
    {
        var keys = table.KeyColumns.ToList();
        if (keys.Count != 1)
            return null;
        return data.TryGetValue(keys[0].ColumnName, out var val) ? val : null;
    }

    private void DiffChildren(
        IDbTable table,
        Dictionary<string, object?> submitted,
        Dictionary<string, object?>? existing,
        int depth,
        object? knownId,
        string? knownInstanceId,
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

                DiffNode(childTable, submittedChild, matchingExisting, depth + 1, link, knownId, knownInstanceId, operations);
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
            // Submitted input is keyed by GraphQL field name; existing DB rows are
            // keyed by column name. Resolve either to the DB column name so the
            // rest of the engine (which works in DB-name space) sees the field —
            // a ColumnLookup-only filter silently dropped sanitized/prefixed
            // columns whose GraphQL name differs. Non-column keys (child links)
            // match neither lookup and are skipped, as before.
            if (table.GraphQlLookup.TryGetValue(kvp.Key, out var byGraphQl))
                result[byGraphQl.DbName] = kvp.Value;
            else if (table.ColumnLookup.ContainsKey(kvp.Key))
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
            if (!ValuesEqual(kvp.Value, existingVal))
                changed[kvp.Key] = kvp.Value;
        }
        return changed;
    }

    /// <summary>
    /// Type-tolerant equality for change detection. Submitted values arrive
    /// GraphQL-typed (int/string) while loaded DB values are provider-typed
    /// (long/decimal/DateTime), so a plain <see cref="object.Equals(object?,object?)"/>
    /// reports an unchanged row as changed and churns audit stamps on every sync.
    /// Numeric CLR types are compared by decimal value; everything else falls back
    /// to an invariant-culture string comparison.
    /// </summary>
    private static bool ValuesEqual(object? submitted, object? existing)
    {
        if (submitted == null && existing == null)
            return true;
        if (submitted == null || existing == null)
            return false;
        if (Equals(submitted, existing))
            return true;

        if (TryToDecimal(submitted, out var a) && TryToDecimal(existing, out var b))
            return a == b;

        return string.Equals(
            Convert.ToString(submitted, CultureInfo.InvariantCulture),
            Convert.ToString(existing, CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    private static bool TryToDecimal(object value, out decimal result)
    {
        switch (value)
        {
            case byte or sbyte or short or ushort or int or uint or long or ulong or decimal or float or double:
                try { result = Convert.ToDecimal(value, CultureInfo.InvariantCulture); return true; }
                catch { result = 0m; return false; }
            default:
                result = 0m;
                return false;
        }
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
