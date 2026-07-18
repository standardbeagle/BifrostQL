using System.Text.Json;
using System.Text.Json.Nodes;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;

namespace BifrostQL.Mcp;

/// <summary>
/// Compiles declarative root selections against a database model. Compilation
/// resolves every identifier once; request-time binding supplies values only.
/// </summary>
public static class DeclarativeQueryToolCompiler
{
    public static CompiledDeclarativeQueryTool Compile(
        DeclarativeToolDefinition definition,
        IDbModel model,
        IQueryIntentExecutor executor,
        string? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(executor);

        var table = model.Tables.FirstOrDefault(candidate =>
            string.Equals($"{candidate.TableSchema}.{candidate.DbName}", definition.Root.Table,
                StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Tool '{definition.Name}' references unknown root table '{definition.Root.Table}'.");

        if (!definition.Params.TryGetValue(definition.Root.ById, out var idParameter))
            throw new InvalidOperationException(
                $"Tool '{definition.Name}' root.byId references undeclared parameter '{definition.Root.ById}'.");
        if (!string.Equals(idParameter.Type, "id", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Tool '{definition.Name}' root.byId parameter '{definition.Root.ById}' must have type 'id'.");
        var keyColumns = table.KeyColumns.ToArray();
        if (keyColumns.Length == 0)
            throw new InvalidOperationException(
                $"Tool '{definition.Name}' root.byId requires a primary key on '{definition.Root.Table}', which has none.");
        if (idParameter.Table is not null &&
            !string.Equals(idParameter.Table, definition.Root.Table, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Tool '{definition.Name}' root.byId parameter '{definition.Root.ById}' targets '{idParameter.Table}', not root table '{definition.Root.Table}'.");

        var columns = definition.Root.Fields.Select(field => ResolveColumn(table, field, definition.Name)).ToArray();
        var includes = definition.Include.Select(include => CompileInclude(definition.Name, table, include)).ToArray();
        return new CompiledDeclarativeQueryTool(
            definition.Name, definition.Root.ById, DetailDefault(definition), table, keyColumns, columns, includes, model, executor, endpoint);
    }

    private static string DetailDefault(DeclarativeToolDefinition definition) =>
        definition.Params.TryGetValue("detail", out var detail) && detail.Default is { } value && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "summary" : "summary";

    private static CompiledInclude CompileInclude(string toolName, IDbTable root, DeclarativeToolInclude include)
    {
        var link = root.SingleLinks.TryGetValue(include.Relation, out var single) ? single
            : root.MultiLinks.TryGetValue(include.Relation, out var multi) ? multi
            : null;
        var manyToMany = root.ManyToManyLinks.TryGetValue(include.Relation, out var many) ? many : null;
        if (link is null && manyToMany is null)
            throw new InvalidOperationException($"Tool '{toolName}' include relation '{include.Relation}' is not a model relationship on '{root.TableSchema}.{root.DbName}'.");

        var related = manyToMany?.TargetTable
            ?? (ReferenceEquals(link!.ParentTable, root) ? link.ChildTable : link.ParentTable);
        var fields = (include.Fields ?? []).Select(field => ResolveColumn(related, field, toolName)).ToArray();
        var sort = include.Sort is null ? null : CompileIncludeSort(related, include.Sort);
        return new CompiledInclude(include, link, manyToMany, related, fields, sort);
    }

    private static string CompileIncludeSort(IDbTable table, string sort)
    {
        var descending = sort.StartsWith("-", StringComparison.Ordinal);
        var column = QueryToolCompiler.ResolveColumn(table, descending ? sort[1..] : sort);
        return column.GraphQlName + (descending ? "_desc" : "_asc");
    }

    private static ColumnDto ResolveColumn(IDbTable table, string name, string toolName)
    {
        if (table.ColumnLookup.TryGetValue(name, out var dbColumn))
            return dbColumn;
        if (table.GraphQlLookup.TryGetValue(name, out var graphQlColumn))
            return graphQlColumn;
        throw new InvalidOperationException(
            $"Tool '{toolName}' root.fields references unknown column '{name}' on table '{table.TableSchema}.{table.DbName}'.");
    }
}

public sealed class CompiledDeclarativeQueryTool
{
    private readonly IDbTable _table;
    private readonly IReadOnlyList<ColumnDto> _keyColumns;
    private readonly IReadOnlyList<ColumnDto> _columns;
    private readonly IReadOnlyList<CompiledInclude> _includes;
    private readonly IDbModel _model;
    private readonly IQueryIntentExecutor _executor;
    private readonly string? _endpoint;
    private readonly string _defaultDetail;

    internal CompiledDeclarativeQueryTool(
        string name,
        string idParameterName,
        string defaultDetail,
        IDbTable table,
        IReadOnlyList<ColumnDto> keyColumns,
        IReadOnlyList<ColumnDto> columns,
        IReadOnlyList<CompiledInclude> includes,
        IDbModel model,
        IQueryIntentExecutor executor,
        string? endpoint)
    {
        Name = name;
        IdParameterName = idParameterName;
        _defaultDetail = defaultDetail;
        _table = table;
        _keyColumns = keyColumns;
        _columns = columns;
        _includes = includes;
        _model = model;
        _executor = executor;
        _endpoint = endpoint;
    }

    public string Name { get; }
    public string IdParameterName { get; }

    public GqlObjectQuery Bind(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        if (!arguments.TryGetValue(IdParameterName, out var id))
            throw new ToolPromptException($"Missing required parameter '{IdParameterName}' for tool '{Name}'.");

        var keyValues = ParseKeyValues(id);
        var query = QueryToolCompiler.BuildQuery(_table, _columns);
        query.QueryType = QueryType.Single;
        query.Filter = TableFilter.FromPrimaryKey(keyValues, _keyColumns, _table.DbName);
        var detail = arguments.TryGetValue("detail", out var detailArgument) && detailArgument.ValueKind == JsonValueKind.String
            ? detailArgument.GetString() : _defaultDetail;
        if (detail is not ("summary" or "full"))
            throw new ToolPromptException($"Invalid detail '{detail}'. Allowed values: summary, full.");
        foreach (var include in _includes.Where(include => include.Definition.DetailGate != "full" || detail == "full"))
            include.Apply(query);
        query.ConnectLinks(_model);
        return query;
    }

    public Task<QueryIntentResult> ExecuteAsync(
        IReadOnlyDictionary<string, JsonElement> arguments,
        IDictionary<string, object?> userContext,
        CancellationToken cancellationToken = default)
    {
        var query = Bind(arguments);

        // Bind exposes the fully connected tree for SQL inspection, while the
        // intent seam also connects Links before applying security transforms.
        // Remove the already-materialized source links so that handoff remains
        // idempotent and cannot execute each declared include twice.
        query.Links.Clear();
        return _executor.ExecuteAsync(new QueryIntent
        {
            Query = query,
            UserContext = userContext,
            Endpoint = _endpoint,
        }, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>> ExecuteCollectionIncludesAsync(
        IReadOnlyDictionary<string, JsonElement> arguments,
        IDictionary<string, object?> userContext,
        CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetValue(IdParameterName, out var id))
            throw new ToolPromptException($"Missing required parameter '{IdParameterName}' for tool '{Name}'.");
        var rootKeyValues = ParseKeyValues(id);
        var rootKeyByDbName = BuildKeyMap(rootKeyValues);
        var detail = arguments.TryGetValue("detail", out var detailArgument) && detailArgument.ValueKind == JsonValueKind.String
            ? detailArgument.GetString() : _defaultDetail;
        var output = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(StringComparer.Ordinal);
        foreach (var include in _includes.Where(item => item.Definition.Fields is not null &&
                     (item.Definition.DetailGate != "full" || detail == "full")))
        {
            output[include.Definition.As] = await ExecuteCollectionAsync(
                include, rootKeyByDbName, rootKeyValues, userContext, cancellationToken);
        }
        return output;
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteCollectionAsync(
        CompiledInclude include,
        IReadOnlyDictionary<string, object?> rootKeyByDbName,
        IReadOnlyList<object?> rootKeyValues,
        IDictionary<string, object?> userContext,
        CancellationToken cancellationToken)
    {
        var query = QueryToolCompiler.BuildQuery(include.RelatedTable, include.Fields);
        query.Limit = include.Definition.Limit;
        if (include.Sort is not null) query.Sort.Add(include.Sort);

        if (include.ManyToMany is { } many)
        {
            // Junction bridge is single-column by model design (ManyToManyLink
            // carries scalar source/target columns). A composite-PK root still
            // works: resolve the one junction-source value from the root key.
            var sourceValue = rootKeyByDbName[many.SourceColumn.DbName];
            var junction = QueryToolCompiler.BuildQuery(many.JunctionTable, [many.JunctionTargetColumn]);
            junction.Filter = RelationFilter(many.JunctionTable, many.JunctionSourceColumn, [sourceValue], null);
            var junctionRows = await ExecuteQueryAsync(junction, userContext, cancellationToken);
            var targetIds = junctionRows.Select(row => row[many.JunctionTargetColumn.DbName]).ToArray();
            if (targetIds.Length == 0) return [];
            query.Filter = RelationFilter(include.RelatedTable, many.TargetColumn, targetIds, include.Definition.Filter);
            return await ExecuteQueryAsync(query, userContext, cancellationToken);
        }

        var link = include.Link!;
        // ParentIds/ChildIds are index-aligned column pairs. When the root is the
        // parent (one → many), match children by ALL child FK columns against the
        // root's parent-key values; when the root is the child (many → one), match
        // the parent by ALL parent-key columns against the root's FK values. Every
        // pair is ANDed — never index-zero a composite FK.
        var (fromColumns, matchColumns) = ReferenceEquals(link.ParentTable, _table)
            ? (link.ParentIds, link.ChildIds)
            : (link.ChildIds, link.ParentIds);

        var fromValues = await ResolveRootValuesAsync(
            fromColumns, rootKeyByDbName, rootKeyValues, userContext, cancellationToken);
        if (fromValues is null || fromValues.Any(value => value is null))
            return [];

        query.Filter = CompositeMatchFilter(include.RelatedTable, matchColumns, fromValues, include.Definition.Filter);
        return await ExecuteQueryAsync(query, userContext, cancellationToken);
    }

    /// <summary>
    /// Resolves the root row's values for <paramref name="fromColumns"/>. Columns
    /// that are part of the primary key come straight from the parsed key (no
    /// query — single-column child collections stay a single round-trip); any
    /// remaining columns (FK columns in the many → one direction) are read off the
    /// root row within the caller's access scope. Returns null when the root row
    /// is out of scope, so the include yields no children rather than leaking.
    /// </summary>
    private async Task<IReadOnlyList<object?>?> ResolveRootValuesAsync(
        IReadOnlyList<ColumnDto> fromColumns,
        IReadOnlyDictionary<string, object?> rootKeyByDbName,
        IReadOnlyList<object?> rootKeyValues,
        IDictionary<string, object?> userContext,
        CancellationToken cancellationToken)
    {
        var missing = fromColumns.Where(column => !rootKeyByDbName.ContainsKey(column.DbName)).ToArray();
        IReadOnlyDictionary<string, object?>? rootRow = null;
        if (missing.Length > 0)
        {
            var lookup = QueryToolCompiler.BuildQuery(_table, missing);
            lookup.QueryType = QueryType.Single;
            lookup.Filter = TableFilter.FromPrimaryKey(rootKeyValues, _keyColumns, _table.DbName);
            var rows = await ExecuteQueryAsync(lookup, userContext, cancellationToken);
            if (rows.Count == 0) return null;
            rootRow = rows[0];
        }

        var values = new List<object?>(fromColumns.Count);
        foreach (var column in fromColumns)
        {
            if (rootKeyByDbName.TryGetValue(column.DbName, out var keyValue))
                values.Add(keyValue);
            else if (rootRow is not null && rootRow.TryGetValue(column.DbName, out var rowValue))
                values.Add(rowValue);
            else
                return null;
        }
        return values;
    }

    private IReadOnlyDictionary<string, object?> BuildKeyMap(IReadOnlyList<object?> keyValues)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _keyColumns.Count; i++)
            map[_keyColumns[i].DbName] = keyValues[i];
        return map;
    }

    /// <summary>
    /// Parses the id argument into the full ordered primary-key value list — an
    /// array in key order, a 'v1|v2' delimited string, or a single scalar for a
    /// single-column key — reusing the shared <see cref="QueryToolCompiler"/>
    /// coercion primitive (no bespoke key parser). Arity mismatches name the key
    /// columns and both accepted forms, mirroring bifrost_row_context.
    /// </summary>
    private IReadOnlyList<object?> ParseKeyValues(JsonElement idElement)
    {
        List<object?> raw = idElement.ValueKind switch
        {
            JsonValueKind.Array => idElement.EnumerateArray().Select(QueryToolCompiler.ToClrValue).ToList(),
            JsonValueKind.String when _keyColumns.Count > 1 =>
                idElement.GetString()!.Split('|').Select(s => (object?)s).ToList(),
            JsonValueKind.String or JsonValueKind.Number =>
                new List<object?> { QueryToolCompiler.ToClrValue(idElement) },
            _ => throw new ToolPromptException(
                $"Parameter '{IdParameterName}' must be a primary-key value: a scalar, an array in " +
                "key-column order, or a 'v1|v2' delimited string."),
        };

        if (raw.Count != _keyColumns.Count)
            throw new ToolPromptException(
                $"Table '{_table.DbName}' has a primary key of {_keyColumns.Count} column(s) " +
                $"({string.Join(", ", _keyColumns.Select(column => column.ColumnName))}) but '{IdParameterName}' " +
                $"supplied {raw.Count} value(s). Pass an array in that column order, or a '|'-delimited string.");

        return raw.Select((value, i) => QueryToolCompiler.CoerceKeyValue(_keyColumns[i], value)).ToList();
    }

    /// <summary>
    /// Builds an AND-of-equalities predicate matching <paramref name="columns"/>
    /// (a single- or multi-column FK) against <paramref name="values"/>, combined
    /// with any declared include filter. All values bind as SQL parameters through
    /// <see cref="QueryToolCompiler.CompileFilter"/>.
    /// </summary>
    private static TableFilter CompositeMatchFilter(
        IDbTable table, IReadOnlyList<ColumnDto> columns, IReadOnlyList<object?> values, JsonElement? declaredFilter)
    {
        JsonNode match;
        if (columns.Count == 1)
        {
            match = new JsonObject
            {
                [columns[0].GraphQlName] = new JsonObject { ["_eq"] = JsonSerializer.SerializeToNode(values[0]) },
            };
        }
        else
        {
            var pairs = new JsonArray();
            for (var i = 0; i < columns.Count; i++)
                pairs.Add(new JsonObject
                {
                    [columns[i].GraphQlName] = new JsonObject { ["_eq"] = JsonSerializer.SerializeToNode(values[i]) },
                });
            match = new JsonObject { ["and"] = pairs };
        }

        JsonNode filter = declaredFilter is { } declared
            ? new JsonObject { ["and"] = new JsonArray(JsonNode.Parse(declared.GetRawText()), match) }
            : match;
        return QueryToolCompiler.CompileFilter(table, JsonSerializer.SerializeToElement(filter));
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteQueryAsync(
        GqlObjectQuery query, IDictionary<string, object?> userContext, CancellationToken cancellationToken)
    {
        var result = await _executor.ExecuteAsync(new QueryIntent
        {
            Query = query, UserContext = userContext, Endpoint = _endpoint,
        }, cancellationToken);
        return result.Rows;
    }

    private static TableFilter RelationFilter(
        IDbTable table, ColumnDto column, IReadOnlyList<object?> values, JsonElement? declaredFilter)
    {
        var relation = new JsonObject
        {
            [column.GraphQlName] = new JsonObject
            {
                [values.Count == 1 ? "_eq" : "_in"] = values.Count == 1
                    ? JsonSerializer.SerializeToNode(values[0])
                    : JsonSerializer.SerializeToNode(values),
            },
        };
        JsonNode filter = declaredFilter is { } declared
            ? new JsonObject { ["and"] = new JsonArray(JsonNode.Parse(declared.GetRawText()), relation) }
            : relation;
        return QueryToolCompiler.CompileFilter(table, JsonSerializer.SerializeToElement(filter));
    }
}

internal sealed record CompiledInclude(
    DeclarativeToolInclude Definition,
    TableLinkDto? Link,
    ManyToManyLink? ManyToMany,
    IDbTable RelatedTable,
    IReadOnlyList<ColumnDto> Fields,
    string? Sort)
{
    public void Apply(GqlObjectQuery root)
    {
        if (Definition.Fields is not null)
        {
            var child = QueryToolCompiler.BuildQuery(RelatedTable, Fields);
            child.GraphQlName = Definition.Relation;
            child.FieldName = Definition.Relation;
            child.Alias = Definition.As;
            child.Limit = Definition.Limit;
            if (Sort is not null)
                child.Sort.Add(Sort);
            if (Definition.Filter is { } filter)
                child.Filter = QueryToolCompiler.CompileFilter(RelatedTable, filter);
            root.Links.Add(child);
        }

        if (Definition.Aggregate is { } aggregate)
        {
            AddAggregate(root, aggregate.Count ? AggregateOperationType.Count : null,
                RelatedTable.KeyColumns.FirstOrDefault(), "count");
            AddAggregate(root, aggregate.Sum is null ? null : AggregateOperationType.Sum,
                ResolveAggregateColumn(aggregate.Sum), "sum");
            AddAggregate(root, aggregate.Avg is null ? null : AggregateOperationType.Avg,
                ResolveAggregateColumn(aggregate.Avg), "avg");
            AddAggregate(root, aggregate.Min is null ? null : AggregateOperationType.Min,
                ResolveAggregateColumn(aggregate.Min), "min");
            AddAggregate(root, aggregate.Max is null ? null : AggregateOperationType.Max,
                ResolveAggregateColumn(aggregate.Max), "max");
        }
    }

    private ColumnDto? ResolveAggregateColumn(string? name) =>
        name is null ? null : QueryToolCompiler.ResolveColumn(RelatedTable, name);

    private void AddAggregate(GqlObjectQuery root, AggregateOperationType? operation, ColumnDto? column, string suffix)
    {
        if (operation is null)
            return;
        if (column is null)
            throw new InvalidOperationException($"Aggregate '{Definition.As}.{suffix}' requires a key column on '{RelatedTable.DbName}'.");
        List<(LinkDirection Direction, TableLinkDto Link)> links;
        if (ManyToMany is { } many)
        {
            var sourceToJunction = new TableLinkDto
            {
                Name = Definition.Relation,
                ParentTable = many.SourceTable,
                ChildTable = many.JunctionTable,
                ParentId = many.SourceColumn,
                ChildId = many.JunctionSourceColumn,
                ParentIds = [many.SourceColumn],
                ChildIds = [many.JunctionSourceColumn],
            };
            var junctionToTarget = new TableLinkDto
            {
                Name = Definition.Relation,
                ParentTable = many.TargetTable,
                ChildTable = many.JunctionTable,
                ParentId = many.TargetColumn,
                ChildId = many.JunctionTargetColumn,
                ParentIds = [many.TargetColumn],
                ChildIds = [many.JunctionTargetColumn],
            };
            links = [(LinkDirection.OneToMany, sourceToJunction), (LinkDirection.ManyToOne, junctionToTarget)];
        }
        else
        {
            var direction = ReferenceEquals(Link!.ParentTable, root.DbTable) ? LinkDirection.OneToMany : LinkDirection.ManyToOne;
            links = [(direction, Link)];
        }

        var aggregate = new GqlAggregateColumn(links, column.DbName,
            $"{Definition.As}_{suffix}", operation.Value);
        var declaredFilter = Definition.Filter is { } filter
            ? QueryToolCompiler.CompileFilter(RelatedTable, filter)
            : null;
        for (var i = 0; i < links.Count; ++i)
            aggregate.DeclaredLinkFilters.Add(i == links.Count - 1 ? declaredFilter : null);
        root.AggregateColumns.Add(aggregate);
    }
}
