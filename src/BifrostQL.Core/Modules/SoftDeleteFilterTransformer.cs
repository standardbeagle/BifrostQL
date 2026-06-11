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
/// Clients control visibility through the module arguments emitted on the
/// table's query field (see <see cref="SoftDeleteModuleApi"/>):
///   users(_includeDeleted: true)  — deleted rows included
///   users(_onlyDeleted: true)     — only deleted rows (WHERE deleted_at IS NOT NULL)
///
/// Host code can also force visibility server-side via the user context,
/// globally (UserContext["include_deleted"] = true) or per table
/// (UserContext["include_deleted:dbo.users"] = true).
/// </summary>
public sealed class SoftDeleteFilterTransformer : SingleColumnFilterTransformerBase
{
    public const string MetadataKey = MetadataKeys.SoftDelete.Column;
    public const string IncludeDeletedKey = SoftDeleteModuleApi.IncludeDeletedKey;
    public const string OnlyDeletedKey = SoftDeleteModuleApi.OnlyDeletedKey;

    public SoftDeleteFilterTransformer() : base(MetadataKey, priority: 100)
    {
    }

    public override string ModuleName => MetadataKeys.SoftDelete.Column;

    public override bool AppliesTo(IDbTable table, QueryTransformContext context)
    {
        if (!base.AppliesTo(table, context))
            return false;

        // _onlyDeleted still needs a filter (IS NOT NULL), so it keeps the
        // transformer active; _includeDeleted alone removes the filter entirely.
        if (OnlyDeleted(table, context))
            return true;

        return !ModuleApiRegistry.GetFlag(context.UserContext, IncludeDeletedKey, table);
    }

    protected override TableFilter BuildFilter(IDbTable table, string columnName, QueryTransformContext context)
    {
        return OnlyDeleted(table, context)
            ? TableFilterFactory.IsNotNull(table.DbName, columnName)
            : TableFilterFactory.IsNull(table.DbName, columnName);
    }

    private static bool OnlyDeleted(IDbTable table, QueryTransformContext context) =>
        ModuleApiRegistry.GetFlag(context.UserContext, OnlyDeletedKey, table);
}
