using BifrostQL.Core.Forms;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Schema;

/// <summary>
/// Pure (no DB/IO) resolver for lookup-table enums (Approach A: the referencing
/// column holds the value string, so there is no id translation).
///
/// Given a <see cref="IDbModel"/> plus pre-loaded, already-sanitized enum values,
/// it answers whether a <c>(table, column)</c> pair is a lookup-table enum,
/// translates between database values and GraphQL enum names in both directions,
/// and rewrites a <see cref="TableFilter"/> tree in place (enum-name operands →
/// database values).
///
/// A column resolves to an enum table when, in priority order:
///   1. it carries <c>enum-ref</c> metadata naming an enum table (an optional
///      <c>schema.</c> prefix is stripped), or
///   2. it has a foreign key whose target table is an enum table and whose
///      targeted column is that enum table's resolved value column.
/// Only enum tables that have non-empty sanitized values participate.
/// </summary>
public sealed class EnumColumnMap
{
    // enum-table DbName -> (GraphQL enum type name, sanitized entries)
    private readonly Dictionary<string, (string EnumName, IReadOnlyList<EnumValueEntry> Entries)> _enumTables;

    // table DbName -> (column identifier [DbName or GraphQlName] -> enum-table DbName)
    private readonly Dictionary<string, Dictionary<string, string>> _columnEnums;

    private EnumColumnMap(
        Dictionary<string, (string EnumName, IReadOnlyList<EnumValueEntry> Entries)> enumTables,
        Dictionary<string, Dictionary<string, string>> columnEnums)
    {
        _enumTables = enumTables;
        _columnEnums = columnEnums;
    }

    /// <summary>Per enum table: the GraphQL enum type name and its sanitized entries.</summary>
    public IReadOnlyDictionary<string, (string EnumName, IReadOnlyList<EnumValueEntry> Entries)> EnumTables => _enumTables;

    /// <summary>
    /// Builds the map from a model and pre-loaded enum data.
    /// </summary>
    /// <param name="model">The database model (already linked, so FK relationships are present).</param>
    /// <param name="enumValues">Sanitized enum entries keyed by enum-table DbName.</param>
    /// <param name="resolvedValueColumns">Resolved value-column name keyed by enum-table DbName.</param>
    public static EnumColumnMap Build(
        IDbModel model,
        IReadOnlyDictionary<string, IReadOnlyList<EnumValueEntry>> enumValues,
        IReadOnlyDictionary<string, string> resolvedValueColumns)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(enumValues);
        ArgumentNullException.ThrowIfNull(resolvedValueColumns);

        var valueColumns = new Dictionary<string, string>(resolvedValueColumns, StringComparer.OrdinalIgnoreCase);
        var enumTables = new Dictionary<string, (string, IReadOnlyList<EnumValueEntry>)>(StringComparer.OrdinalIgnoreCase);

        // Only tables with non-empty sanitized values count as enum tables.
        foreach (var table in model.Tables)
        {
            if (!enumValues.TryGetValue(table.DbName, out var entries) || entries.Count == 0)
                continue;
            enumTables[table.DbName] = ($"{table.GraphQlName}Values", entries);
        }

        var columnEnums = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in model.Tables)
        {
            foreach (var column in table.Columns)
            {
                var enumTableDb = ResolveColumnEnum(table, column, enumTables, valueColumns);
                if (enumTableDb == null)
                    continue;

                if (!columnEnums.TryGetValue(table.DbName, out var cols))
                {
                    cols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    columnEnums[table.DbName] = cols;
                }

                // Register under both the DB name (public API) and the GraphQL name
                // (filter trees carry GraphQL field names) so either resolves.
                cols[column.ColumnName] = enumTableDb;
                cols[column.GraphQlName] = enumTableDb;
            }
        }

        return new EnumColumnMap(enumTables, columnEnums);
    }

    private static string? ResolveColumnEnum(
        IDbTable table,
        ColumnDto column,
        Dictionary<string, (string EnumName, IReadOnlyList<EnumValueEntry> Entries)> enumTables,
        Dictionary<string, string> valueColumns)
    {
        // Rule 1: explicit enum-ref metadata wins. An optional "schema." prefix is stripped.
        var enumRef = column.GetMetadataValue(MetadataKeys.Enum.Ref);
        if (!string.IsNullOrWhiteSpace(enumRef))
        {
            var target = enumRef.Split('.')[^1].Trim();
            return enumTables.ContainsKey(target) ? target : null;
        }

        // Rule 2: a foreign key whose target table is an enum table and whose
        // targeted column is that enum table's resolved value column.
        var referenced = ForeignKeyHandler.GetReferencedTable(column, table);
        if (referenced == null || !enumTables.ContainsKey(referenced.DbName))
            return null;

        var targetColumn = ForeignKeyHandler.GetReferencedKeyColumn(column, table);
        if (targetColumn == null || !valueColumns.TryGetValue(referenced.DbName, out var valueColumn))
            return null;

        return string.Equals(targetColumn, valueColumn, StringComparison.OrdinalIgnoreCase)
            ? referenced.DbName
            : null;
    }

    /// <summary>Returns true and the GraphQL enum type name when the column is a lookup-table enum.</summary>
    public bool TryGetEnumType(string tableDbName, string columnDbName, out string enumName)
    {
        if (TryResolveEnumTable(tableDbName, columnDbName, out var enumTableDb))
        {
            enumName = _enumTables[enumTableDb].EnumName;
            return true;
        }
        enumName = string.Empty;
        return false;
    }

    /// <summary>Translates a database value to its GraphQL enum name, or null (non-enum column or drift).</summary>
    public string? ValueToName(string tableDbName, string columnDbName, object? dbValue)
    {
        if (dbValue == null || !TryResolveEnumTable(tableDbName, columnDbName, out var enumTableDb))
            return null;

        var text = dbValue as string ?? dbValue.ToString();
        if (text == null)
            return null;

        foreach (var entry in _enumTables[enumTableDb].Entries)
        {
            if (string.Equals(entry.DatabaseValue, text, StringComparison.Ordinal))
                return entry.GraphQlName;
        }
        return null;
    }

    /// <summary>Translates a GraphQL enum name to its database value, or null (non-enum column or drift).</summary>
    public string? NameToValue(string tableDbName, string columnDbName, string name)
    {
        if (name == null || !TryResolveEnumTable(tableDbName, columnDbName, out var enumTableDb))
            return null;

        foreach (var entry in _enumTables[enumTableDb].Entries)
        {
            if (string.Equals(entry.GraphQlName, name, StringComparison.Ordinal))
                return entry.DatabaseValue;
        }
        return null;
    }

    /// <summary>True when at least one column on the named table is a lookup-table enum.</summary>
    public bool HasAnyFor(string tableDbName) =>
        _columnEnums.TryGetValue(tableDbName, out var cols) && cols.Count > 0;

    /// <summary>
    /// Rewrites enum-name operands to database values in place across the filter
    /// tree, recursing through <see cref="TableFilter.Next"/>,
    /// <see cref="TableFilter.And"/> and <see cref="TableFilter.Or"/>. Both scalar
    /// (string) and collection (<see cref="IEnumerable{T}"/> of object) operands —
    /// e.g. <c>_eq</c> and <c>_in</c> — are translated. Unknown names are left
    /// unchanged.
    /// </summary>
    public void RewriteFilterValues(TableFilter? node, string tableDbName)
    {
        if (node == null)
            return;

        // A column filter is a Join node carrying the column name with a single
        // relation leaf (RelationName + Value) as its Next.
        if (!string.IsNullOrEmpty(node.ColumnName)
            && node.Next is { Next: null, RelationName: { Length: > 0 } } relation
            && TryResolveEnumTable(tableDbName, node.ColumnName, out var enumTableDb))
        {
            relation.Value = TranslateOperand(relation.Value, enumTableDb);
        }

        RewriteFilterValues(node.Next, tableDbName);
        foreach (var child in node.And)
            RewriteFilterValues(child, tableDbName);
        foreach (var child in node.Or)
            RewriteFilterValues(child, tableDbName);
    }

    private object? TranslateOperand(object? value, string enumTableDb)
    {
        if (value is string name)
            return NameToValueFromTable(enumTableDb, name) ?? value;

        if (value is IEnumerable<object?> sequence)
        {
            return sequence
                .Select(item => item is string s ? (object?)(NameToValueFromTable(enumTableDb, s) ?? s) : item)
                .ToList();
        }

        return value;
    }

    private string? NameToValueFromTable(string enumTableDb, string name)
    {
        foreach (var entry in _enumTables[enumTableDb].Entries)
        {
            if (string.Equals(entry.GraphQlName, name, StringComparison.Ordinal))
                return entry.DatabaseValue;
        }
        return null;
    }

    private bool TryResolveEnumTable(string tableDbName, string columnIdentifier, out string enumTableDb)
    {
        if (tableDbName != null && columnIdentifier != null
            && _columnEnums.TryGetValue(tableDbName, out var cols)
            && cols.TryGetValue(columnIdentifier, out var resolved))
        {
            enumTableDb = resolved;
            return true;
        }
        enumTableDb = string.Empty;
        return false;
    }
}
