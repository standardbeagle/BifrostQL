using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Base class for filter transformers that use a single column from table metadata.
/// Reduces boilerplate for common transformer patterns.
/// </summary>
public abstract class SingleColumnFilterTransformerBase : IFilterTransformer, IModuleNamed
{
    private readonly string _metadataKey;
    private readonly int _priority;

    protected SingleColumnFilterTransformerBase(string metadataKey, int priority)
    {
        _metadataKey = metadataKey;
        _priority = priority;
    }

    public abstract string ModuleName { get; }
    public int Priority => _priority;

    public virtual bool AppliesTo(IDbTable table, QueryTransformContext context)
    {
        return table.Metadata.TryGetValue(_metadataKey, out var val) && val != null;
    }

    public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context)
    {
        var columnName = table.Metadata[_metadataKey]?.ToString();
        if (string.IsNullOrWhiteSpace(columnName))
            return null;

        // Verify the column exists
        if (!table.ColumnLookup.ContainsKey(columnName))
        {
            var fullTableName = $"{table.TableSchema}.{table.DbName}";
            throw new BifrostExecutionError(
                $"{ModuleName} column '{columnName}' not found in table '{fullTableName}'.");
        }

        return BuildFilter(table, columnName, context);
    }

    /// <summary>
    /// Builds the filter for the given column. Override to customize the filter logic.
    /// </summary>
    protected abstract TableFilter BuildFilter(IDbTable table, string columnName, QueryTransformContext context);
}

/// <summary>
/// Base class for filter transformers that require a value from user context.
/// </summary>
public abstract class ContextValueFilterTransformerBase : SingleColumnFilterTransformerBase
{
    private readonly string _contextKey;

    protected ContextValueFilterTransformerBase(
        string metadataKey,
        string contextKey,
        int priority) : base(metadataKey, priority)
    {
        _contextKey = contextKey;
    }

    protected override TableFilter BuildFilter(IDbTable table, string columnName, QueryTransformContext context)
    {
        var fullTableName = $"{table.TableSchema}.{table.DbName}";

        // Get value from user context - this is required
        if (!context.UserContext.TryGetValue(_contextKey, out var value))
        {
            throw new BifrostExecutionError(
                $"{ModuleName} context required but not found. " +
                $"Expected '{_contextKey}' in user context for table '{fullTableName}'.");
        }

        if (value == null)
        {
            throw new BifrostExecutionError(
                $"{ModuleName} value cannot be null for table '{fullTableName}'.");
        }

        return TableFilterFactory.Equals(table.DbName, columnName, value);
    }
}
