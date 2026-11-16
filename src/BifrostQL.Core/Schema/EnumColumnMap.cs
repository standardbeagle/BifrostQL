using BifrostQL.Core.Forms;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

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
/// Every table is addressed by its schema-qualified <see cref="EnumTableKey"/>:
/// <c>sales.status</c> and <c>hr.status</c> are different tables, and a lookup by
/// <see cref="IDbTable"/> never sees another schema's enum columns.
///
/// A column resolves to an enum table when, in priority order:
///   1. it carries <c>enum-ref</c> metadata naming an enum table: <c>schema.name</c>
///      resolves exactly; a bare <c>name</c> resolves to the table of that name in the
///      column's own schema, else to the one table of that name across schemas (a
///      bare name defined in two other schemas is refused at build), or
///   2. it has a foreign key whose target table is an enum table and whose
///      targeted column is that enum table's resolved value column.
/// Only enum tables that have non-empty sanitized values participate.
/// </summary>
public sealed class EnumColumnMap
{
    // enum table -> (GraphQL enum type name, sanitized entries)
    private readonly Dictionary<EnumTableKey, (string EnumName, IReadOnlyList<EnumValueEntry> Entries)> _enumTables;

    // table -> (column identifier [DbName or GraphQlName] -> enum table)
    private readonly Dictionary<EnumTableKey, Dictionary<string, EnumTableKey>> _columnEnums;

    private EnumColumnMap(
        Dictionary<EnumTableKey, (string EnumName, IReadOnlyList<EnumValueEntry> Entries)> enumTables,
        Dictionary<EnumTableKey, Dictionary<string, EnumTableKey>> columnEnums)
    {
        _enumTables = enumTables;
        _columnEnums = columnEnums;
    }

    /// <summary>Per enum table: the GraphQL enum type name and its sanitized entries.</summary>
    public IReadOnlyDictionary<EnumTableKey, (string EnumName, IReadOnlyList<EnumValueEntry> Entries)> EnumTables => _enumTables;

    /// <summary>
    /// Builds the map from a model and pre-loaded enum data.
    /// </summary>
    /// <param name="model">The database model (already linked, so FK relationships are present).</param>
    /// <param name="enumValues">Sanitized enum entries keyed by enum table.</param>
    /// <param name="resolvedValueColumns">Resolved value-column name keyed by enum table.</param>
    public static EnumColumnMap Build(
        IDbModel model,
        IReadOnlyDictionary<EnumTableKey, IReadOnlyList<EnumValueEntry>> enumValues,
        IReadOnlyDictionary<EnumTableKey, string> resolvedValueColumns)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(enumValues);
        ArgumentNullException.ThrowIfNull(resolvedValueColumns);

        var enumTables = new Dictionary<EnumTableKey, (string, IReadOnlyList<EnumValueEntry>)>();

        // Only enum-configured tables with non-empty sanitized values count as enum
        // tables. FromTable is the single source of truth for the enum type name —
        // the same authority schema emission uses, so column references can never
        // diverge from the emitted enum type. The values were loaded once against
        // the base model; a profile model whose metadata leaves this table without
        // an enum config does not treat it as an enum.
        foreach (var table in model.Tables)
        {
            var key = EnumTableKey.Of(table);
            if (!enumValues.TryGetValue(key, out var entries) || entries.Count == 0)
                continue;
            var config = EnumTableConfig.FromTable(table);
            if (config == null)
                continue;
            enumTables[key] = (config.GraphQlEnumName, entries);
        }

        var columnEnums = new Dictionary<EnumTableKey, Dictionary<string, EnumTableKey>>();

        foreach (var table in model.Tables)
        {
            foreach (var column in table.Columns)
            {
                var enumTable = ResolveColumnEnum(model, table, column, enumTables, resolvedValueColumns);
                if (enumTable is not { } enumTableKey)
                    continue;

                var tableKey = EnumTableKey.Of(table);
                if (!columnEnums.TryGetValue(tableKey, out var cols))
                {
                    cols = new Dictionary<string, EnumTableKey>(StringComparer.OrdinalIgnoreCase);
                    columnEnums[tableKey] = cols;
                }

                // Register under both the DB name (public API) and the GraphQL name
                // (filter trees carry GraphQL field names) so either resolves.
                cols[column.ColumnName] = enumTableKey;
                cols[column.GraphQlName] = enumTableKey;
            }
        }

        return new EnumColumnMap(enumTables, columnEnums);
    }

    private static EnumTableKey? ResolveColumnEnum(
        IDbModel model,
        IDbTable table,
        ColumnDto column,
        Dictionary<EnumTableKey, (string EnumName, IReadOnlyList<EnumValueEntry> Entries)> enumTables,
        IReadOnlyDictionary<EnumTableKey, string> valueColumns)
    {
        // Rule 1: explicit enum-ref metadata wins.
        var enumRef = column.GetMetadataValue(MetadataKeys.Enum.Ref);
        if (!string.IsNullOrWhiteSpace(enumRef))
        {
            var target = ResolveEnumRef(model, table, enumRef.Trim());
            return target is { } key && enumTables.ContainsKey(key) ? key : null;
        }

        // Rule 2: a foreign key whose target table is an enum table and whose
        // targeted column is that enum table's resolved value column.
        var referenced = ForeignKeyHandler.GetReferencedTable(column, table);
        if (referenced == null)
            return null;
        var referencedKey = EnumTableKey.Of(referenced);
        if (!enumTables.ContainsKey(referencedKey))
            return null;

        var targetColumn = ForeignKeyHandler.GetReferencedKeyColumn(column, table);
        if (targetColumn == null || !valueColumns.TryGetValue(referencedKey, out var valueColumn))
            return null;

        return string.Equals(targetColumn, valueColumn, StringComparison.OrdinalIgnoreCase)
            ? referencedKey
            : null;
    }

    /// <summary>
    /// Resolves an <c>enum-ref</c> value to a table. <c>schema.name</c> (split on the
    /// first dot) resolves exactly. A bare name resolves in the referencing table's own
    /// schema first, then to the single table of that name across the model. A bare
    /// name with no own-schema match and two matches elsewhere has no positive answer:
    /// guessing would bind the column to an arbitrary schema's values, so the build
    /// fails with an operator-facing configuration error (schema build time, never a
    /// request).
    /// </summary>
    private static EnumTableKey? ResolveEnumRef(IDbModel model, IDbTable table, string enumRef)
    {
        var dot = enumRef.IndexOf('.');
        if (dot > 0 && dot < enumRef.Length - 1)
        {
            return model.TryGetTableFromDbName(enumRef[..dot].Trim(), enumRef[(dot + 1)..].Trim(), out var qualified)
                ? EnumTableKey.Of(qualified)
                : null;
        }

        if (model.TryGetTableFromDbName(table.TableSchema, enumRef, out var sameSchema))
            return EnumTableKey.Of(sameSchema);

        var candidates = model.Tables
            .Where(t => string.Equals(t.DbName, enumRef, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count > 1)
            throw new BifrostExecutionError(
                $"enum-ref '{enumRef}' on a column of '{table.TableSchema}.{table.DbName}' is ambiguous: more than one " +
                $"schema defines a table named '{enumRef}'. Qualify it as 'schema.{enumRef}'.");
        return candidates.Count == 1 ? EnumTableKey.Of(candidates[0]) : null;
    }

    /// <summary>Returns true and the GraphQL enum type name when the column is a lookup-table enum.</summary>
    public bool TryGetEnumType(IDbTable table, string columnDbName, out string enumName)
    {
        if (TryResolveEnumTable(table, columnDbName, out var enumTableDb))
        {
            enumName = _enumTables[enumTableDb].EnumName;
            return true;
        }
        enumName = string.Empty;
        return false;
    }

    /// <summary>Translates a database value to its GraphQL enum name, or null (non-enum column or drift).</summary>
    public string? ValueToName(IDbTable table, string columnDbName, object? dbValue)
    {
        if (dbValue == null || !TryResolveEnumTable(table, columnDbName, out var enumTableDb))
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
    public string? NameToValue(IDbTable table, string columnDbName, string name)
    {
        if (name == null || !TryResolveEnumTable(table, columnDbName, out var enumTableDb))
            return null;

        return NameToValueFromTable(enumTableDb, name);
    }

    /// <summary>True when at least one column on the table is a lookup-table enum.</summary>
    public bool HasAnyFor(IDbTable table) =>
        table != null && _columnEnums.TryGetValue(EnumTableKey.Of(table), out var cols) && cols.Count > 0;

    /// <summary>
    /// True if this single-link's FK column(s) resolve to a lookup-table enum
    /// (Approach A). Under Approach A the FK column already surfaces as the enum
    /// scalar, so the parent navigation is redundant and must be suppressed both
    /// from schema emission and from resolver wiring (otherwise the navigation
    /// field collides with the enum column's field and crashes schema build).
    /// Single source of truth shared by TableSchemaGenerator and BifrostDispatcher.
    /// </summary>
    public bool IsEnumLink(IDbTable table, TableLinkDto link) =>
        link != null
        && link.ChildIds.Any(c =>
            TryResolveEnumTable(table, c.ColumnName, out var enumTable)
            && enumTable == EnumTableKey.Of(link.ParentTable));

    /// <summary>
    /// Rewrites enum-name operands to database values in place across the filter
    /// tree, recursing through <see cref="TableFilter.Next"/>,
    /// <see cref="TableFilter.And"/> and <see cref="TableFilter.Or"/>. Both scalar
    /// (string) and collection (<see cref="IEnumerable{T}"/> of object) operands —
    /// e.g. <c>_eq</c> and <c>_in</c> — are translated. Unknown names are left
    /// unchanged.
    /// </summary>
    public void RewriteFilterValues(TableFilter? node, IDbTable table)
    {
        if (node == null)
            return;

        // A column filter is a Join node carrying the column name with a single
        // relation leaf (RelationName + Value) as its Next.
        if (!string.IsNullOrEmpty(node.ColumnName)
            && node.Next is { Next: null, RelationName: { Length: > 0 } } relation
            && TryResolveEnumTable(table, node.ColumnName, out var enumTableDb))
        {
            relation.Value = TranslateOperand(relation.Value, enumTableDb);
        }

        RewriteFilterValues(node.Next, table);
        foreach (var child in node.And)
            RewriteFilterValues(child, table);
        foreach (var child in node.Or)
            RewriteFilterValues(child, table);
    }

    private object? TranslateOperand(object? value, EnumTableKey enumTableDb)
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

    private string? NameToValueFromTable(EnumTableKey enumTableDb, string name)
    {
        foreach (var entry in _enumTables[enumTableDb].Entries)
        {
            if (string.Equals(entry.GraphQlName, name, StringComparison.Ordinal))
                return entry.DatabaseValue;
        }
        return null;
    }

    private bool TryResolveEnumTable(IDbTable table, string columnIdentifier, out EnumTableKey enumTable)
    {
        if (table != null && columnIdentifier != null
            && _columnEnums.TryGetValue(EnumTableKey.Of(table), out var cols)
            && cols.TryGetValue(columnIdentifier, out var resolved))
        {
            enumTable = resolved;
            return true;
        }
        enumTable = default;
        return false;
    }
}
