using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using GraphQL;
using GraphQL.Resolvers;

namespace BifrostQL.Core.Modules.Eav;

/// <summary>
/// GraphQL resolver for flattened EAV table fields.
/// Handles both the _flattened_{meta} field on parent tables and root-level queries.
/// </summary>
public sealed class EavResolver : IBifrostResolver, IFieldResolver
{
    private readonly EavModule _module;
    private readonly EavFlattenedTable _flattenedTable;

    public EavResolver(EavModule module, EavFlattenedTable flattenedTable)
    {
        _module = module;
        _flattenedTable = flattenedTable;
    }

    public ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
    {
        // Get query parameters
        var limit = context.HasArgument("limit") ? context.GetArgument<int?>("limit") : null;
        var offset = context.HasArgument("offset") ? context.GetArgument<int?>("offset") : null;

        // Build and execute query
        var query = new EavFlattenedQuery
        {
            FlattenedTable = _flattenedTable,
            Columns = null, // All columns
            Limit = limit,
            Offset = offset,
            IncludeTotal = true,
        };

        var bifrost = new BifrostContextAdapter(context);
        using var connection = bifrost.ConnFactory.GetConnection();
        connection.Open();

        var result = _module.ExecuteQuery(query, connection);

        // Return paged result
        return ValueTask.FromResult<object?>(new EavPagedResult
        {
            Data = result.Rows,
            Total = result.TotalCount ?? 0,
            Offset = offset,
            Limit = limit,
        });
    }

    async ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
    {
        return await ResolveAsync(new BifrostFieldContextAdapter(context));
    }
}

/// <summary>
/// Resolver for single flattened EAV row (when accessed via parent entity).
/// </summary>
public sealed class EavSingleResolver : IBifrostResolver, IFieldResolver
{
    private readonly EavModule _module;
    private readonly EavFlattenedTable _flattenedTable;

    public EavSingleResolver(EavModule module, EavFlattenedTable flattenedTable)
    {
        _module = module;
        _flattenedTable = flattenedTable;
    }

    public ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
    {
        // Get the parent entity's primary key from context
        var parentPk = GetParentPrimaryKey(context);
        if (parentPk == null)
            return ValueTask.FromResult<object?>(null);

        // Build filter for specific entity
        var filter = new TableFilter
        {
            TableName = _flattenedTable.ParentTable.DbName,
            ColumnName = _flattenedTable.ParentTable.KeyColumns.First().ColumnName,
            FilterType = FilterType.Join,
            Next = new TableFilter
            {
                RelationName = "_eq",
                Value = parentPk,
                FilterType = FilterType.Relation,
            }
        };

        var query = new EavFlattenedQuery
        {
            FlattenedTable = _flattenedTable,
            Columns = null,
            Filter = filter,
            Limit = 1,
            IncludeTotal = false,
        };

        var bifrost = new BifrostContextAdapter(context);
        using var connection = bifrost.ConnFactory.GetConnection();
        connection.Open();

        var result = _module.ExecuteQuery(query, connection);

        return ValueTask.FromResult<object?>(result.Rows.FirstOrDefault());
    }

    async ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
    {
        return await ResolveAsync(new BifrostFieldContextAdapter(context));
    }

    private object? GetParentPrimaryKey(IBifrostFieldContext context)
    {
        // Try to get PK from context source
        if (context.Source is Dictionary<string, object> dict)
        {
            var pkName = _flattenedTable.ParentTable.KeyColumns.First().GraphQlName;
            if (dict.TryGetValue(pkName, out var pkValue))
                return pkValue;
        }

        return null;
    }
}

/// <summary>
/// Resolver for individual EAV column values within a flattened row.
/// </summary>
public sealed class EavColumnResolver : IBifrostResolver, IFieldResolver
{
    private readonly string _columnName;

    public EavColumnResolver(string columnName)
    {
        _columnName = columnName;
    }

    public ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
    {
        if (context.Source is Dictionary<string, object?> row)
        {
            if (row.TryGetValue(_columnName, out var value))
            {
                // Convert DBNull to null
                return ValueTask.FromResult(value == DBNull.Value ? null : value);
            }

            // Try case-insensitive lookup
            var key = row.Keys.FirstOrDefault(k =>
                string.Equals(k, _columnName, StringComparison.OrdinalIgnoreCase));
            if (key != null)
            {
                var val = row[key];
                return ValueTask.FromResult(val == DBNull.Value ? null : val);
            }
        }

        return ValueTask.FromResult<object?>(null);
    }

    async ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
    {
        return await ResolveAsync(new BifrostFieldContextAdapter(context));
    }
}

/// <summary>
/// Paged result for flattened EAV queries.
/// </summary>
public sealed class EavPagedResult
{
    public required IReadOnlyList<Dictionary<string, object?>> Data { get; init; }
    public required int Total { get; init; }
    public int? Offset { get; init; }
    public int? Limit { get; init; }
}

/// <summary>
/// Extension resolver for adding _flattened_{meta} fields to existing table types.
/// </summary>
public sealed class EavFieldExtensionResolver : IBifrostResolver
{
    private readonly EavModule _module;

    public EavFieldExtensionResolver(EavModule module)
    {
        _module = module;
    }

    public ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
    {
        // Get the parent table from context
        var parentTableName = GetParentTableName(context);
        if (parentTableName == null)
            return ValueTask.FromResult<object?>(null);

        var flattenedTable = _module.GetFlattenedTable(parentTableName);
        if (flattenedTable == null)
            return ValueTask.FromResult<object?>(null);

        // Get the parent entity's primary key
        var parentPk = GetParentPrimaryKey(context, flattenedTable.ParentTable);
        if (parentPk == null)
            return ValueTask.FromResult<object?>(null);

        // Query for this specific entity's EAV data
        var query = new EavFlattenedQuery
        {
            FlattenedTable = flattenedTable,
            Columns = null,
            Limit = 1,
            IncludeTotal = false,
        };

        var bifrost = new BifrostContextAdapter(context);
        using var connection = bifrost.ConnFactory.GetConnection();
        connection.Open();

        var result = _module.ExecuteQuery(query, connection);

        // Find the row matching the parent PK
        var pkColumnName = flattenedTable.ParentTable.KeyColumns.First().ColumnName;
        var row = result.Rows.FirstOrDefault(r =>
        {
            if (r.TryGetValue(pkColumnName, out var pkValue))
                return Equals(pkValue, parentPk);
            return false;
        });

        return ValueTask.FromResult<object?>(row);
    }

    private string? GetParentTableName(IBifrostFieldContext context)
    {
        // Extract parent table name from path
        // The path contains the field hierarchy
        var path = context.Path;
        if (path.Count > 0)
        {
            // First element in path is typically the parent type name
            return path[0]?.ToString();
        }
        return null;
    }

    private object? GetParentPrimaryKey(IBifrostFieldContext context, IDbTable parentTable)
    {
        if (context.Source is Dictionary<string, object?> source)
        {
            var pkColumn = parentTable.KeyColumns.FirstOrDefault();
            if (pkColumn == null)
                return null;

            if (source.TryGetValue(pkColumn.GraphQlName, out var pkValue))
                return pkValue;

            if (source.TryGetValue(pkColumn.ColumnName, out pkValue))
                return pkValue;
        }

        return null;
    }
}
