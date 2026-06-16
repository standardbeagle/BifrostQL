using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Automatically populates audit columns based on column metadata configuration.
///
/// Column metadata is configured via the metadata rules system using the "populate" property:
///
///   Timestamp columns (auto-populated with UTC server time):
///     "dbo.*.created_at { populate: created-on }"   - Set on INSERT only
///     "dbo.*.updated_at { populate: updated-on }"   - Set on INSERT and UPDATE
///     "dbo.*.deleted_at { populate: deleted-on }"   - Set on DELETE only
///
///   User columns (auto-populated from BifrostContext user claims):
///     "dbo.*.created_by { populate: created-by }"   - Set on INSERT only
///     "dbo.*.updated_by { populate: updated-by }"   - Set on INSERT and UPDATE
///     "dbo.*.deleted_by { populate: deleted-by }"   - Set on DELETE only
///
/// User columns require the "user-audit-key" database metadata to identify which
/// claim to read from the user context:
///     ":root { user-audit-key: id }"
///
/// The transformer overwrites any client-provided values for audit columns, preventing
/// clients from spoofing audit data. Audit columns are marked as optional in
/// GraphQL input types so clients do not need to supply them.
///
/// Example full configuration:
///   ":root { user-audit-key: id }"
///   "dbo.*.created_at { populate: created-on }"
///   "dbo.*.created_by_user_id { populate: created-by }"
///   "dbo.*.updated_at { populate: updated-on }"
///   "dbo.*.updated_by_user_id { populate: updated-by }"
/// </summary>
public sealed class AuditMutationTransformer : IMutationTransformer, IModuleNamed
{
    public string ModuleName => "audit";

    // Runs after security gating (Policy=1, StateMachine=2) but BEFORE the
    // soft-delete transformer (100). Soft delete rewrites DELETE into UPDATE; if
    // audit ran after that rewrite it would only ever see Update and never stamp
    // the deleted-on/deleted-by columns on a soft delete. Running at 50 means audit
    // sees the original DELETE intent and stamps deleted-* before the conversion —
    // matching the legacy BasicAuditModule, whose Delete() ran on the soft-delete path.
    public int Priority => 50;

    public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
    {
        foreach (var column in table.Columns)
        {
            if (column.CompareMetadata("populate", "created-on") ||
                column.CompareMetadata("populate", "created-by") ||
                column.CompareMetadata("populate", "updated-on") ||
                column.CompareMetadata("populate", "updated-by") ||
                column.CompareMetadata("populate", "deleted-on") ||
                column.CompareMetadata("populate", "deleted-by"))
                return true;
        }
        return false;
    }

    public ValueTask<MutationTransformResult> TransformAsync(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context)
        => new(TransformSync(table, mutationType, data, context));

    private MutationTransformResult TransformSync(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context)
    {
        var dateTime = DateTime.UtcNow;
        var auditKey = context.Model.GetMetadataValue(MetadataKeys.Audit.UserKey);
        var hasAuditKey = !string.IsNullOrWhiteSpace(auditKey);
        var auditValue = ResolveAuditUser(auditKey, context.UserContext, hasAuditKey);

        foreach (var column in table.Columns)
        {
            switch (mutationType)
            {
                case MutationType.Insert:
                    if (column.CompareMetadata("populate", "created-on")) data[column.ColumnName] = dateTime;
                    if (column.CompareMetadata("populate", "created-by") && hasAuditKey) data[column.ColumnName] = auditValue;
                    if (column.CompareMetadata("populate", "updated-on")) data[column.ColumnName] = dateTime;
                    if (column.CompareMetadata("populate", "updated-by") && hasAuditKey) data[column.ColumnName] = auditValue;
                    break;
                case MutationType.Update:
                    if (column.CompareMetadata("populate", "updated-on")) data[column.ColumnName] = dateTime;
                    if (column.CompareMetadata("populate", "updated-by") && hasAuditKey) data[column.ColumnName] = auditValue;
                    break;
                case MutationType.Delete:
                    if (column.CompareMetadata("populate", "updated-on")) data[column.ColumnName] = dateTime;
                    if (column.CompareMetadata("populate", "updated-by") && hasAuditKey) data[column.ColumnName] = auditValue;
                    if (column.CompareMetadata("populate", "deleted-on")) data[column.ColumnName] = dateTime;
                    if (column.CompareMetadata("populate", "deleted-by") && hasAuditKey) data[column.ColumnName] = auditValue;
                    break;
            }
        }

        return new MutationTransformResult
        {
            MutationType = mutationType,
            Data = data,
        };
    }

    private static object? ResolveAuditUser(string? auditKey, IDictionary<string, object?> userContext, bool hasAuditKey)
    {
        if (!hasAuditKey || auditKey == null)
            return null;

        if (userContext.TryGetValue(auditKey, out var value))
            return value;

        return null;
    }
}
