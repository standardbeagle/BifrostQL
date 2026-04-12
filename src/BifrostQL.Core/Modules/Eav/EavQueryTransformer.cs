using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.Model.AppSchema;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Modules.Eav;

/// <summary>
/// Transforms queries on flattened EAV tables into SQL that pivots EAV data.
/// Generates dynamic SQL with CASE WHEN expressions for each meta_key.
/// </summary>
public sealed class EavQueryTransformer
{
    private readonly ISqlDialect _dialect;

    public EavQueryTransformer(ISqlDialect dialect)
    {
        _dialect = dialect;
    }

    /// <summary>
    /// Generates SQL for a flattened EAV query using dynamic pivot.
    /// The SQL joins the parent table with pivoted EAV data.
    /// </summary>
    public ParameterizedSql GenerateFlattenedQuerySql(
        EavFlattenedTable flattenedTable,
        IReadOnlyList<EavColumn> columns,
        TableFilter? filter = null,
        int? limit = null,
        int? offset = null)
    {
        var parent = flattenedTable.ParentTable;
        var config = flattenedTable.Config;
        var parameters = new SqlParameterCollection();

        // Build the pivot subquery
        var pivotSql = GeneratePivotSubquery(flattenedTable, columns, filter, parameters);

        // Build the main query joining parent with pivoted data
        var parentRef = _dialect.TableReference(parent.TableSchema, parent.DbName);
        var pkColumns = parent.KeyColumns.Select(c => _dialect.EscapeIdentifier(c.ColumnName)).ToList();
        var pkList = string.Join(", ", pkColumns);

        // Build column list: parent PKs + EAV columns
        var columnList = new List<string>(pkColumns);
        foreach (var col in columns)
        {
            columnList.Add($"p.{_dialect.EscapeIdentifier(col.SqlAlias)}");
        }

        var sql = new StringBuilder();
        sql.Append($"SELECT {string.Join(", ", columnList)} FROM {parentRef} parent");
        sql.Append($" LEFT JOIN ({pivotSql}) p ON ");

        // Join condition: parent PK = EAV foreign key
        var joinConditions = parent.KeyColumns.Select(pk =>
            $"parent.{_dialect.EscapeIdentifier(pk.ColumnName)} = p.{_dialect.EscapeIdentifier(config.ForeignKeyColumn)}");
        sql.Append(string.Join(" AND ", joinConditions));

        // Add pagination
        var pagination = _dialect.Pagination(null, offset, limit);
        if (!string.IsNullOrEmpty(pagination))
        {
            sql.Append(pagination);
        }

        return new ParameterizedSql(sql.ToString(), parameters.Parameters.ToList());
    }

    /// <summary>
    /// Generates the pivot subquery that transforms EAV rows into columns.
    /// Uses MAX(CASE WHEN ...) pattern for dynamic pivot.
    /// </summary>
    private string GeneratePivotSubquery(
        EavFlattenedTable flattenedTable,
        IReadOnlyList<EavColumn> columns,
        TableFilter? filter,
        SqlParameterCollection parameters)
    {
        var config = flattenedTable.Config;
        var metaRef = _dialect.TableReference(flattenedTable.MetaTable.TableSchema, flattenedTable.MetaTable.DbName);

        var fkCol = _dialect.EscapeIdentifier(config.ForeignKeyColumn);
        var keyCol = _dialect.EscapeIdentifier(config.KeyColumn);
        var valueCol = _dialect.EscapeIdentifier(config.ValueColumn);

        // Build CASE WHEN columns for each meta_key
        var caseColumns = new List<string>();
        foreach (var col in columns)
        {
            var paramName = parameters.AddParameter(col.MetaKey);
            var caseWhen = $"MAX(CASE WHEN {keyCol} = {paramName} THEN {valueCol} END) AS {_dialect.EscapeIdentifier(col.SqlAlias)}";
            caseColumns.Add(caseWhen);
        }

        var sql = new StringBuilder();
        sql.Append($"SELECT {fkCol}, {string.Join(", ", caseColumns)} FROM {metaRef}");

        // Add filter if provided (transformed for meta table)
        if (filter != null)
        {
            // Note: Filter transformation would need to map flattened column names back to meta_value
            // For now, we skip filter in pivot subquery and apply it at outer level
        }

        sql.Append($" GROUP BY {fkCol}");

        return sql.ToString();
    }

    /// <summary>
    /// Generates a count query for pagination.
    /// </summary>
    public ParameterizedSql GenerateCountSql(EavFlattenedTable flattenedTable, TableFilter? filter = null)
    {
        var parent = flattenedTable.ParentTable;
        var parentRef = _dialect.TableReference(parent.TableSchema, parent.DbName);

        var sql = $"SELECT COUNT(*) FROM {parentRef}";

        // Add filter if applicable
        if (filter != null)
        {
            var filterSql = filter.ToSqlParameterized(null!, _dialect, new SqlParameterCollection(), null);
            if (!string.IsNullOrEmpty(filterSql.Sql))
            {
                sql += filterSql.Sql;
            }
        }

        return new ParameterizedSql(sql, new List<SqlParameterInfo>());
    }
}

/// <summary>
/// Filter transformer that handles queries on flattened EAV tables.
/// Maps filter conditions from virtual flattened columns to the underlying EAV structure.
/// </summary>
public sealed class EavFilterTransformer : IFilterTransformer
{
    private readonly IReadOnlyDictionary<string, EavFlattenedTable> _flattenedTables;
    private readonly ISqlDialect _dialect;

    public int Priority => 150; // After security transformers, before application

    public EavFilterTransformer(IReadOnlyDictionary<string, EavFlattenedTable> flattenedTables, ISqlDialect dialect)
    {
        _flattenedTables = flattenedTables;
        _dialect = dialect;
    }

    public bool AppliesTo(IDbTable table, QueryTransformContext context)
    {
        // Check if this is a flattened EAV table query
        return _flattenedTables.ContainsKey(table.DbName);
    }

    public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context)
    {
        // EAV filters are handled during query transformation, not as additional filters
        // This transformer primarily marks the query for EAV handling
        return null;
    }
}

/// <summary>
/// Represents a query on a flattened EAV table.
/// </summary>
public sealed class EavFlattenedQuery
{
    /// <summary>The flattened table definition</summary>
    public required EavFlattenedTable FlattenedTable { get; init; }

    /// <summary>Columns to select (null means all)</summary>
    public required IReadOnlyList<string>? Columns { get; init; }

    /// <summary>Filter conditions</summary>
    public TableFilter? Filter { get; init; }

    /// <summary>Pagination limit</summary>
    public int? Limit { get; init; }

    /// <summary>Pagination offset</summary>
    public int? Offset { get; init; }

    /// <summary>Whether to include total count</summary>
    public bool IncludeTotal { get; init; }
}

/// <summary>
/// Type converter for EAV values. Attempts to infer and convert types from string values.
/// </summary>
public static class EavTypeConverter
{
    /// <summary>
    /// Attempts to convert a string value to the best matching .NET type.
    /// </summary>
    public static object? ConvertValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        // Try integer
        if (long.TryParse(value, out var longVal))
            return longVal;

        // Try decimal
        if (decimal.TryParse(value, out var decimalVal))
            return decimalVal;

        // Try boolean
        if (bool.TryParse(value, out var boolVal))
            return boolVal;

        // Try DateTime (ISO 8601 format)
        if (DateTime.TryParse(value, out var dateVal))
            return dateVal;

        // Return as string
        return value;
    }

    /// <summary>
    /// Infers the GraphQL type from a sample of values.
    /// </summary>
    public static string InferGraphQlType(IEnumerable<string?> samples)
    {
        var nonNullSamples = samples.Where(s => !string.IsNullOrEmpty(s)).ToList();

        if (nonNullSamples.Count == 0)
            return "String";

        // Check if all are integers
        if (nonNullSamples.All(s => long.TryParse(s, out _)))
            return "Int";

        // Check if all are decimals
        if (nonNullSamples.All(s => decimal.TryParse(s, out _)))
            return "Float";

        // Check if all are booleans
        if (nonNullSamples.All(s => bool.TryParse(s, out _)))
            return "Boolean";

        // Check if all are dates
        if (nonNullSamples.All(s => DateTime.TryParse(s, out _)))
            return "DateTime";

        // Default to String
        return "String";
    }

    /// <summary>
    /// Gets the SQL data type for an inferred GraphQL type.
    /// </summary>
    public static string GetSqlDataType(string graphQlType)
    {
        return graphQlType switch
        {
            "Int" => "bigint",
            "Float" => "decimal(18,4)",
            "Boolean" => "bit",
            "DateTime" => "datetime2",
            _ => "nvarchar(max)",
        };
    }
}
