using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using GraphQL;
using GraphQL.Resolvers;

namespace BifrostQL.Core.Resolvers;

/// <summary>
/// Base class for Bifrost resolvers that provides common functionality.
/// Implements both IBifrostResolver and IFieldResolver for GraphQL integration.
/// </summary>
public abstract class ResolverBase : IBifrostResolver, IFieldResolver
{
    /// <summary>
    /// Resolves the field value using the Bifrost context.
    /// Derived classes must implement this method.
    /// </summary>
    public abstract ValueTask<object?> ResolveAsync(IBifrostFieldContext context);

    /// <summary>
    /// Adapter method that converts GraphQL's IResolveFieldContext to IBifrostFieldContext.
    /// This allows the resolver to work with both GraphQL and Bifrost abstractions.
    /// </summary>
    ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
    {
        return ResolveAsync(new BifrostFieldContextAdapter(context));
    }
}

/// <summary>
/// Base class for resolvers that operate on a specific database table.
/// Provides common table-related functionality.
/// </summary>
public abstract class TableResolverBase : ResolverBase
{
    protected IDbTable Table { get; }

    protected TableResolverBase(IDbTable table)
    {
        Table = table ?? throw new ArgumentNullException(nameof(table));
    }

    /// <summary>
    /// Gets the table reference for the current table using the specified dialect.
    /// </summary>
    protected string GetTableReference(ISqlDialect dialect) =>
        dialect.TableReference(Table.TableSchema, Table.DbName);

    /// <summary>
    /// Builds a WHERE clause from key-value pairs using the specified dialect.
    /// </summary>
    protected string BuildWhereClause(IEnumerable<KeyValuePair<string, object?>> keyValues, ISqlDialect dialect)
    {
        return string.Join(" AND ", keyValues.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));
    }

    /// <summary>
    /// Builds a SET clause for UPDATE statements from key-value pairs using the specified dialect.
    /// </summary>
    protected string BuildSetClause(IEnumerable<KeyValuePair<string, object?>> keyValues, ISqlDialect dialect)
    {
        return string.Join(",", keyValues.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));
    }
}

/// <summary>
/// Base class for mutation resolvers that provides common mutation functionality.
/// </summary>
public abstract class MutationResolverBase : TableResolverBase
{
    protected MutationResolverBase(IDbTable table) : base(table)
    {
    }

    /// <summary>
    /// Gets the primary key values from the context, either from the _primaryKey argument
    /// or by extracting PK columns from the data.
    /// </summary>
    protected Dictionary<string, object?>? ResolvePrimaryKey(
        IBifrostFieldContext context,
        Dictionary<string, object?> data)
    {
        if (context.HasArgument("_primaryKey"))
        {
            var pkValues = context.GetArgument<List<object?>>("_primaryKey");
            if (pkValues != null && pkValues.Count > 0)
            {
                var keyColumns = Table.KeyColumns.ToList();
                if (keyColumns.Count == 0)
                    throw new BifrostExecutionError($"Table '{Table.DbName}' has no primary key columns.");

                if (pkValues.Count != keyColumns.Count)
                    throw new BifrostExecutionError(
                        $"_primaryKey for '{Table.DbName}' expects {keyColumns.Count} value(s) " +
                        $"({string.Join(", ", keyColumns.Select(c => c.GraphQlName))}) but received {pkValues.Count}.");

                return keyColumns.Zip(pkValues, (col, val) => new { col.ColumnName, Value = val })
                    .ToDictionary(x => x.ColumnName, x => x.Value);
            }
        }

        // Fall back to extracting PK columns from data
        return data.Where(d => Table.ColumnLookup[d.Key].IsPrimaryKey)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>
    /// Separates data into key columns and standard (non-key) columns.
    /// </summary>
    protected (Dictionary<string, object?> keyData, Dictionary<string, object?> standardData) SeparateKeyColumns(
        Dictionary<string, object?> data)
    {
        var keyData = data.Where(d => Table.ColumnLookup.ContainsKey(d.Key) && Table.ColumnLookup[d.Key].IsPrimaryKey)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var standardData = data.Where(d => !Table.ColumnLookup.ContainsKey(d.Key) || !Table.ColumnLookup[d.Key].IsPrimaryKey)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return (keyData, standardData);
    }
}

/// <summary>
/// Base class for database execution resolvers that provides common connection and command functionality.
/// </summary>
public abstract class DatabaseResolverBase : ResolverBase
{
    /// <summary>
    /// Executes a scalar command and returns the result.
    /// </summary>
    protected static async ValueTask<object?> ExecuteScalarAsync(
        IDbConnFactory connFactory,
        string sql,
        Dictionary<string, object?> parameters)
    {
        await using var conn = connFactory.GetConnection();
        try
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            AddParameters(cmd, parameters);
            return await cmd.ExecuteScalarAsync();
        }
        catch (Exception ex)
        {
            throw new BifrostExecutionError(ex.Message, ex);
        }
    }

    /// <summary>
    /// Executes a non-query command and returns the number of affected rows.
    /// </summary>
    protected static async ValueTask<int> ExecuteNonQueryAsync(
        IDbConnFactory connFactory,
        string sql,
        Dictionary<string, object?> parameters)
    {
        await using var conn = connFactory.GetConnection();
        try
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            AddParameters(cmd, parameters);
            return await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            throw new BifrostExecutionError(ex.Message, ex);
        }
    }

    /// <summary>
    /// Adds parameters to a database command from a dictionary.
    /// </summary>
    protected static void AddParameters(DbCommand cmd, Dictionary<string, object?> parameters)
    {
        foreach (var kv in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = $"@{kv.Key}";
            p.Value = kv.Value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
    }

    /// <summary>
    /// Handles decimal to long conversion for identity values.
    /// </summary>
    protected static object? HandleDecimals(object? obj)
    {
        if (obj == null)
            return obj;
        return obj switch
        {
            decimal d => Convert.ToInt64(d),
            _ => obj,
        };
    }
}
