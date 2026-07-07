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
        return string.Join(" AND ", keyValues.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{SqlParameterNames.Sanitize(kv.Key)}"));
    }

    /// <summary>
    /// Builds a SET clause for UPDATE statements from key-value pairs using the specified dialect.
    /// </summary>
    protected string BuildSetClause(IEnumerable<KeyValuePair<string, object?>> keyValues, ISqlDialect dialect)
    {
        return string.Join(",", keyValues.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{SqlParameterNames.Sanitize(kv.Key)}"));
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
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        await using var conn = connFactory.GetConnection();
        try
        {
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            AddParameters(cmd, parameters);
            return await cmd.ExecuteScalarAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Propagate request aborts as-is; wrapping would mask the cancellation.
            throw;
        }
        catch (Exception ex)
        {
            throw BifrostExecutionError.FromDatabaseException(ex);
        }
    }

    /// <summary>
    /// Executes a non-query command and returns the number of affected rows.
    /// </summary>
    protected static async ValueTask<int> ExecuteNonQueryAsync(
        IDbConnFactory connFactory,
        string sql,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        await using var conn = connFactory.GetConnection();
        try
        {
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            AddParameters(cmd, parameters);
            return await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Propagate request aborts as-is; wrapping would mask the cancellation.
            throw;
        }
        catch (Exception ex)
        {
            throw BifrostExecutionError.FromDatabaseException(ex);
        }
    }

    /// <summary>
    /// Adds <c>@columnName</c> parameters to a command from a dictionary.
    /// </summary>
    protected static void AddParameters(DbCommand cmd, Dictionary<string, object?> parameters)
        => DbParameterBinder.AddParameters(cmd, parameters);

    /// <summary>
    /// Coerces decimal identity values to long.
    /// </summary>
    protected static object? HandleDecimals(object? obj)
        => DbParameterBinder.HandleDecimals(obj);
}
