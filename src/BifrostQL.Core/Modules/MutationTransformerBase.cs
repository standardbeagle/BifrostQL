using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Base class for mutation transformers that use metadata to determine applicability.
/// Reduces boilerplate for common transformer patterns.
/// </summary>
public abstract class MetadataMutationTransformerBase : IMutationTransformer, IModuleNamed
{
    private readonly string _metadataKey;
    private readonly int _priority;

    protected MetadataMutationTransformerBase(string metadataKey, int priority)
    {
        _metadataKey = metadataKey;
        _priority = priority;
    }

    public abstract string ModuleName { get; }
    public int Priority => _priority;

    public virtual bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
    {
        return table.Metadata.TryGetValue(_metadataKey, out var val) && val != null;
    }

    public MutationTransformResult Transform(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context)
    {
        var columnName = table.Metadata[_metadataKey]?.ToString();
        if (string.IsNullOrWhiteSpace(columnName))
        {
            return PassThrough(mutationType, data);
        }

        // Verify column exists
        if (!table.ColumnLookup.ContainsKey(columnName))
        {
            var fullTableName = $"{table.TableSchema}.{table.DbName}";
            return new MutationTransformResult
            {
                MutationType = mutationType,
                Data = data,
                Errors = new[] { $"{ModuleName} column '{columnName}' not found in table '{fullTableName}'." }
            };
        }

        return TransformCore(table, mutationType, data, context, columnName);
    }

    /// <summary>
    /// Performs the actual transformation. Override to customize behavior.
    /// </summary>
    protected abstract MutationTransformResult TransformCore(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context,
        string columnName);

    /// <summary>
    /// Returns a pass-through result (no transformation).
    /// </summary>
    protected static MutationTransformResult PassThrough(MutationType mutationType, Dictionary<string, object?> data)
    {
        return new MutationTransformResult
        {
            MutationType = mutationType,
            Data = data
        };
    }
}

/// <summary>
/// Base class for soft-delete style mutation transformers that convert DELETE to UPDATE.
/// </summary>
public abstract class SoftDeleteMutationTransformerBase : MetadataMutationTransformerBase
{
    protected SoftDeleteMutationTransformerBase(string metadataKey, int priority = 100)
        : base(metadataKey, priority)
    {
    }

    public override bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
    {
        // Only applies to DELETE and UPDATE operations
        if (mutationType == MutationType.Insert)
            return false;

        return base.AppliesTo(table, mutationType, context);
    }

    protected override MutationTransformResult TransformCore(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context,
        string columnName)
    {
        // For both UPDATE and DELETE, ensure we only affect non-deleted records
        var softDeleteFilter = TableFilterFactory.IsNull(table.DbName, columnName);

        if (mutationType == MutationType.Delete)
        {
            return TransformDelete(table, data, context, columnName, softDeleteFilter);
        }

        // For UPDATE, just add the filter to prevent updating deleted records
        return new MutationTransformResult
        {
            MutationType = mutationType,
            Data = data,
            AdditionalFilter = softDeleteFilter
        };
    }

    /// <summary>
    /// Transforms a DELETE operation into an UPDATE. Override to customize the soft-delete logic.
    /// </summary>
    protected abstract MutationTransformResult TransformDelete(
        IDbTable table,
        Dictionary<string, object?> data,
        MutationTransformContext context,
        string columnName,
        TableFilter softDeleteFilter);
}
