using BifrostQL.Core.Model;

namespace BifrostQL.Core.QueryModel.TestFixtures;

/// <summary>
/// Fluent builder for creating GqlObjectQuery instances for testing.
/// </summary>
public sealed class GqlObjectQueryBuilder
{
    private string _tableName = "TestTable";
    private string _schemaName = "";
    private string _graphQlName = "testTable";
    private string? _alias;
    private QueryType _queryType = QueryType.Standard;
    private readonly List<GqlObjectColumn> _columns = new();
    private readonly List<GqlAggregateColumn> _aggregateColumns = new();
    private readonly List<GqlObjectQuery> _links = new();
    private readonly List<TableJoin> _joins = new();
    private readonly List<string> _sort = new();
    private TableFilter? _filter;
    private int? _limit;
    private int? _offset;
    private bool _includeResult;
    private IDbTable? _dbTable;

    public static GqlObjectQueryBuilder Create() => new();

    public GqlObjectQueryBuilder WithTableName(string tableName)
    {
        _tableName = tableName;
        return this;
    }

    public GqlObjectQueryBuilder WithSchema(string schema)
    {
        _schemaName = schema;
        return this;
    }

    public GqlObjectQueryBuilder WithGraphQlName(string name)
    {
        _graphQlName = name;
        return this;
    }

    public GqlObjectQueryBuilder WithAlias(string alias)
    {
        _alias = alias;
        return this;
    }

    public GqlObjectQueryBuilder WithQueryType(QueryType type)
    {
        _queryType = type;
        return this;
    }

    public GqlObjectQueryBuilder WithDbTable(IDbTable table)
    {
        _dbTable = table;
        _tableName = table.DbName;
        _schemaName = table.TableSchema ?? "";
        _graphQlName = table.GraphQlName;
        return this;
    }

    public GqlObjectQueryBuilder WithColumn(string dbName, string? graphQlName = null)
    {
        _columns.Add(new GqlObjectColumn(dbName) { GraphQlDbName = graphQlName ?? dbName });
        return this;
    }

    public GqlObjectQueryBuilder WithColumns(params string[] columnNames)
    {
        foreach (var name in columnNames)
            _columns.Add(new GqlObjectColumn(name) { GraphQlDbName = name });
        return this;
    }

    public GqlObjectQueryBuilder WithAggregateColumn(GqlAggregateColumn column)
    {
        _aggregateColumns.Add(column);
        return this;
    }

    public GqlObjectQueryBuilder WithLink(GqlObjectQuery link)
    {
        _links.Add(link);
        return this;
    }

    public GqlObjectQueryBuilder WithJoin(Action<TableJoinBuilder> configure)
    {
        var builder = new TableJoinBuilder();
        configure(builder);
        _joins.Add(builder.Build());
        return this;
    }

    public GqlObjectQueryBuilder WithSort(params string[] sortColumns)
    {
        _sort.AddRange(sortColumns);
        return this;
    }

    public GqlObjectQueryBuilder WithFilter(TableFilter filter)
    {
        _filter = filter;
        return this;
    }

    public GqlObjectQueryBuilder WithFilter(Action<TableFilterBuilder> configure)
    {
        var builder = new TableFilterBuilder(_tableName);
        configure(builder);
        _filter = builder.Build();
        return this;
    }

    public GqlObjectQueryBuilder WithLimit(int limit)
    {
        _limit = limit;
        return this;
    }

    public GqlObjectQueryBuilder WithOffset(int offset)
    {
        _offset = offset;
        return this;
    }

    public GqlObjectQueryBuilder WithPagination(int offset, int limit)
    {
        _offset = offset;
        _limit = limit;
        return this;
    }

    public GqlObjectQueryBuilder IncludeResult()
    {
        _includeResult = true;
        return this;
    }

    public GqlObjectQuery Build()
    {
        return new GqlObjectQuery
        {
            DbTable = _dbTable!,
            TableName = _tableName,
            SchemaName = _schemaName,
            GraphQlName = _graphQlName,
            Alias = _alias,
            QueryType = _queryType,
            ScalarColumns = _columns.ToList(),
            AggregateColumns = _aggregateColumns.ToList(),
            Links = _links.ToList(),
            Joins = _joins.ToList(),
            Sort = _sort.ToList(),
            Filter = _filter,
            Limit = _limit,
            Offset = _offset,
            IncludeResult = _includeResult,
        };
    }
}

/// <summary>
/// Builder for TableJoin instances
/// </summary>
public sealed class TableJoinBuilder
{
    private string _name = "testJoin";
    private string? _alias;
    private string _fromColumn = "Id";
    private string _connectedColumn = "ParentId";
    private QueryType _queryType = QueryType.Join;
    private string _operator = "_eq";
    private GqlObjectQuery? _fromTable;
    private GqlObjectQuery? _connectedTable;

    public TableJoinBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    public TableJoinBuilder WithAlias(string alias)
    {
        _alias = alias;
        return this;
    }

    public TableJoinBuilder WithFromColumn(string column)
    {
        _fromColumn = column;
        return this;
    }

    public TableJoinBuilder WithConnectedColumn(string column)
    {
        _connectedColumn = column;
        return this;
    }

    public TableJoinBuilder WithQueryType(QueryType type)
    {
        _queryType = type;
        return this;
    }

    public TableJoinBuilder WithOperator(string op)
    {
        _operator = op;
        return this;
    }

    public TableJoinBuilder WithFromTable(GqlObjectQuery table)
    {
        _fromTable = table;
        return this;
    }

    public TableJoinBuilder WithConnectedTable(GqlObjectQuery table)
    {
        _connectedTable = table;
        return this;
    }

    public TableJoin Build()
    {
        return new TableJoin
        {
            Name = _name,
            Alias = _alias,
            FromColumn = _fromColumn,
            ConnectedColumn = _connectedColumn,
            QueryType = _queryType,
            Operator = _operator,
            FromTable = _fromTable!,
            ConnectedTable = _connectedTable!,
        };
    }
}

/// <summary>
/// Fluent builder for TableFilter instances
/// </summary>
public sealed class TableFilterBuilder
{
    private readonly string _tableName;
    private readonly List<(string column, string op, object? value)> _conditions = new();
    private readonly List<TableFilter> _andFilters = new();
    private readonly List<TableFilter> _orFilters = new();

    public TableFilterBuilder(string tableName)
    {
        _tableName = tableName;
    }

    public static TableFilterBuilder ForTable(string tableName) => new(tableName);

    public TableFilterBuilder Where(string column, string op, object? value)
    {
        _conditions.Add((column, op, value));
        return this;
    }

    public TableFilterBuilder WhereEquals(string column, object? value) => Where(column, "_eq", value);
    public TableFilterBuilder WhereNotEquals(string column, object? value) => Where(column, "_neq", value);
    public TableFilterBuilder WhereLessThan(string column, object? value) => Where(column, "_lt", value);
    public TableFilterBuilder WhereLessThanOrEqual(string column, object? value) => Where(column, "_lte", value);
    public TableFilterBuilder WhereGreaterThan(string column, object? value) => Where(column, "_gt", value);
    public TableFilterBuilder WhereGreaterThanOrEqual(string column, object? value) => Where(column, "_gte", value);
    public TableFilterBuilder WhereContains(string column, object? value) => Where(column, "_contains", value);
    public TableFilterBuilder WhereStartsWith(string column, object? value) => Where(column, "_starts_with", value);
    public TableFilterBuilder WhereEndsWith(string column, object? value) => Where(column, "_ends_with", value);
    public TableFilterBuilder WhereLike(string column, object? value) => Where(column, "_like", value);
    public TableFilterBuilder WhereIn(string column, params object[] values) => Where(column, "_in", values);
    public TableFilterBuilder WhereNotIn(string column, params object[] values) => Where(column, "_nin", values);
    public TableFilterBuilder WhereBetween(string column, object from, object to) => Where(column, "_between", new[] { from, to });
    public TableFilterBuilder WhereIsNull(string column) => Where(column, "_eq", null);
    public TableFilterBuilder WhereIsNotNull(string column) => Where(column, "_neq", null);

    public TableFilterBuilder And(Action<TableFilterBuilder> configure)
    {
        var builder = new TableFilterBuilder(_tableName);
        configure(builder);
        _andFilters.Add(builder.Build());
        return this;
    }

    public TableFilterBuilder Or(Action<TableFilterBuilder> configure)
    {
        var builder = new TableFilterBuilder(_tableName);
        configure(builder);
        _orFilters.Add(builder.Build());
        return this;
    }

    public TableFilter Build()
    {
        if (_conditions.Count == 1 && _andFilters.Count == 0 && _orFilters.Count == 0)
        {
            var (column, op, value) = _conditions[0];
            return TableFilter.FromObject(new Dictionary<string, object?>
            {
                { column, new Dictionary<string, object?> { { op, value } } }
            }, _tableName);
        }

        // Build AND/OR compound filter
        var filterDict = new Dictionary<string, object?>();

        if (_conditions.Count > 0 || _andFilters.Count > 0)
        {
            var andList = new List<object?>();
            foreach (var (column, op, value) in _conditions)
            {
                andList.Add(new Dictionary<string, object?>
                {
                    { column, new Dictionary<string, object?> { { op, value } } }
                });
            }
            // Note: nested AND filters would need to be serialized here if needed
            filterDict["and"] = andList;
        }

        if (_orFilters.Count > 0)
        {
            // For simplicity, if we have OR filters, build as OR at top level
            var orList = _conditions.Select(c => (object?)new Dictionary<string, object?>
            {
                { c.column, new Dictionary<string, object?> { { c.op, c.value } } }
            }).ToList();
            filterDict["or"] = orList;
        }

        return TableFilter.FromObject(filterDict, _tableName);
    }
}
