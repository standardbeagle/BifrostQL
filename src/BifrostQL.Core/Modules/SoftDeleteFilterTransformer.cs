using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Filters out soft-deleted records based on table metadata.
///
/// Configuration via metadata:
///   "dbo.users { soft-delete: deleted_at }"
///
/// This adds WHERE deleted_at IS NULL to all queries on the table.
///
/// To include deleted records, the query must explicitly request them:
///   UserContext["include_deleted"] = true
///
/// Or per-table:
///   UserContext["include_deleted:dbo.users"] = true
/// </summary>
public sealed class SoftDeleteFilterTransformer : SingleColumnFilterTransformerBase
{
    public const string MetadataKey = "soft-delete";
    public const string IncludeDeletedKey = "include_deleted";

    public SoftDeleteFilterTransformer() : base(MetadataKey, priority: 100)
    {
    }

    public override string ModuleName => "soft-delete";

    public override bool AppliesTo(IDbTable table, QueryTransformContext context)
    {
        if (!base.AppliesTo(table, context))
            return false;

        // Check if user explicitly wants deleted records
        if (ShouldIncludeDeleted(table, context))
            return false;

        return true;
    }

    protected override TableFilter BuildFilter(IDbTable table, string columnName, QueryTransformContext context)
    {
        // Filter: deleted_at IS NULL
        return TableFilterFactory.IsNull(table.DbName, columnName);
    }

    private static bool ShouldIncludeDeleted(IDbTable table, QueryTransformContext context)
    {
        // Check table-specific override first
        var fullTableName = $"{table.TableSchema}.{table.DbName}";
        var tableKey = $"{IncludeDeletedKey}:{fullTableName}";
        if (context.UserContext.TryGetValue(tableKey, out var tableVal) && tableVal is true)
            return true;

        // Check global include_deleted
        if (context.UserContext.TryGetValue(IncludeDeletedKey, out var globalVal) && globalVal is true)
            return true;

        return false;
    }
}
