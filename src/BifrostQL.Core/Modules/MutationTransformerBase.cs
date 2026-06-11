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
        // _hardDelete: bypass the soft-delete rewrite and run a real DELETE.
        // No IS NULL filter is added so already-soft-deleted rows can be purged.
        // Optionally role-gated via the soft-delete-hard-role metadata key.
        if (mutationType == MutationType.Delete && IsHardDeleteRequested(context))
        {
            var denial = GetHardDeleteDenial(table, context);
            if (denial != null)
            {
                return new MutationTransformResult
                {
                    MutationType = mutationType,
                    Data = data,
                    Errors = new[] { denial },
                };
            }
            return PassThrough(MutationType.Delete, data);
        }

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

    private static bool IsHardDeleteRequested(MutationTransformContext context) =>
        context.ModuleArguments.TryGetValue(SoftDeleteModuleApi.HardDeleteKey, out var val) && val is true;

    /// <summary>
    /// Returns an error message when the table's <c>soft-delete-hard-role</c>
    /// metadata names a role the caller does not hold; null when allowed.
    /// </summary>
    private static string? GetHardDeleteDenial(IDbTable table, MutationTransformContext context)
    {
        if (!table.Metadata.TryGetValue(MetadataKeys.SoftDelete.HardDeleteRole, out var roleVal) ||
            roleVal is not string requiredRole || string.IsNullOrWhiteSpace(requiredRole))
            return null;

        if (ExtractRoles(context.UserContext).Any(r => string.Equals(r, requiredRole, StringComparison.OrdinalIgnoreCase)))
            return null;

        return $"Hard delete on '{table.TableSchema}.{table.DbName}' requires role '{requiredRole}'.";
    }

    private static IReadOnlyList<string> ExtractRoles(IDictionary<string, object?> userContext)
    {
        if (!userContext.TryGetValue("roles", out var rolesValue) || rolesValue is null)
            return Array.Empty<string>();

        return rolesValue switch
        {
            string single => new[] { single },
            IEnumerable<string> typed => typed.ToArray(),
            System.Collections.IEnumerable sequence => sequence.Cast<object?>()
                .Select(o => o?.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToArray(),
            _ => Array.Empty<string>(),
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
