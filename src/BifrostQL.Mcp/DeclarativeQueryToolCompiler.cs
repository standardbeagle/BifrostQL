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
        if (table.KeyColumns.Count() != 1)
            throw new InvalidOperationException(
                $"Tool '{definition.Name}' root.byId requires exactly one primary-key column on '{definition.Root.Table}'.");
        if (idParameter.Table is not null &&
            !string.Equals(idParameter.Table, definition.Root.Table, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Tool '{definition.Name}' root.byId parameter '{definition.Root.ById}' targets '{idParameter.Table}', not root table '{definition.Root.Table}'.");

        var columns = definition.Root.Fields.Select(field => ResolveColumn(table, field, definition.Name)).ToArray();
        var includes = definition.Include.Select(include => CompileInclude(definition.Name, table, include)).ToArray();
        return new CompiledDeclarativeQueryTool(
            definition.Name, definition.Root.ById, DetailDefault(definition), table, table.KeyColumns.Single(), columns, includes, model, executor, endpoint);
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
        if (link?.IsComposite == true)
            throw new InvalidOperationException($"Tool '{toolName}' include relation '{include.Relation}' uses a multi-column foreign key; declarative tool document version 1 supports only single-column relationships.");

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
    private readonly ColumnDto _keyColumn;
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
        ColumnDto keyColumn,
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
        _keyColumn = keyColumn;
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

        var value = QueryToolCompiler.CoerceKeyValue(_keyColumn, QueryToolCompiler.ToClrValue(id));
        var query = QueryToolCompiler.BuildQuery(_table, _columns);
        query.QueryType = QueryType.Single;
        query.Filter = TableFilter.FromPrimaryKey([value], [_keyColumn], _table.DbName);
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
        var rootId = QueryToolCompiler.CoerceKeyValue(_keyColumn, QueryToolCompiler.ToClrValue(id));
        var detail = arguments.TryGetValue("detail", out var detailArgument) && detailArgument.ValueKind == JsonValueKind.String
            ? detailArgument.GetString() : _defaultDetail;
        var output = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(StringComparer.Ordinal);
        foreach (var include in _includes.Where(item => item.Definition.Fields is not null &&
                     (item.Definition.DetailGate != "full" || detail == "full")))
        {
            output[include.Definition.As] = await ExecuteCollectionAsync(include, rootId, userContext, cancellationToken);
        }
        return output;
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteCollectionAsync(
        CompiledInclude include, object? rootId, IDictionary<string, object?> userContext, CancellationToken cancellationToken)
    {
        IReadOnlyList<object?> relatedIds;
        ColumnDto relatedKey;
        if (include.ManyToMany is { } many)
        {
            var junction = QueryToolCompiler.BuildQuery(many.JunctionTable, [many.JunctionTargetColumn]);
            junction.Filter = EqualityFilter(many.JunctionTable, many.JunctionSourceColumn, rootId, null);
            var junctionRows = await ExecuteQueryAsync(junction, userContext, cancellationToken);
            relatedIds = junctionRows.Select(row => row[many.JunctionTargetColumn.DbName]).ToArray();
            relatedKey = many.TargetColumn;
        }
        else if (include.Link is { } link && ReferenceEquals(link.ParentTable, _table))
        {
            relatedIds = [rootId];
            relatedKey = link.ChildId;
        }
        else
        {
            var parentLink = include.Link!;
            var rootLookup = QueryToolCompiler.BuildQuery(_table, [parentLink.ChildId]);
            rootLookup.QueryType = QueryType.Single;
            rootLookup.Filter = TableFilter.FromPrimaryKey([rootId], [_keyColumn], _table.DbName);
            var rootRows = await ExecuteQueryAsync(rootLookup, userContext, cancellationToken);
            if (rootRows.Count == 0 || !rootRows[0].TryGetValue(parentLink.ChildId.DbName, out var foreignKey))
                return [];
            relatedIds = [foreignKey];
            relatedKey = parentLink.ParentId;
        }

        if (relatedIds.Count == 0) return [];
        var query = QueryToolCompiler.BuildQuery(include.RelatedTable, include.Fields);
        query.Limit = include.Definition.Limit;
        if (include.Sort is not null) query.Sort.Add(include.Sort);
        query.Filter = RelationFilter(include.RelatedTable, relatedKey, relatedIds, include.Definition.Filter);
        return await ExecuteQueryAsync(query, userContext, cancellationToken);
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

    private static TableFilter EqualityFilter(
        IDbTable table, ColumnDto column, object? value, JsonElement? Filter) =>
        RelationFilter(table, column, [value], Filter);

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
