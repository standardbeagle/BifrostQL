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
            var populate = column.GetMetadataValue(MetadataKeys.AutoPopulate.Marker);
            if (!string.IsNullOrWhiteSpace(populate) && MetadataKeys.AutoPopulate.KnownPopulators.Contains(populate))
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

        // Audit user columns (created-by/updated-by/deleted-by) are server-owned and
        // must never be settable by the client, or a caller could spoof audit data.
        // When a user-audit-key is configured we overwrite the column with the
        // resolved claim (server-controlled — it may be null when the claim is absent,
        // which is still a value the client cannot influence). When NO user-audit-key
        // is configured we have no trustworthy value, so instead of leaking a
        // client-supplied value through we STRIP the key entirely, leaving the DB
        // default / existing value to stand. Either way the client's value never
        // reaches the row.
        void StampUser(string columnName)
        {
            if (hasAuditKey)
                data[columnName] = auditValue;
            else
                data.Remove(columnName);
        }

        foreach (var column in table.Columns)
        {
            var marker = MetadataKeys.AutoPopulate.Marker;
            switch (mutationType)
            {
                case MutationType.Insert:
                    if (column.CompareMetadata(marker, MetadataKeys.AutoPopulate.CreatedOn)) data[column.ColumnName] = dateTime;
                    if (column.CompareMetadata(marker, MetadataKeys.AutoPopulate.CreatedBy)) StampUser(column.ColumnName);
                    if (column.CompareMetadata(marker, MetadataKeys.AutoPopulate.UpdatedOn)) data[column.ColumnName] = dateTime;
                    if (column.CompareMetadata(marker, MetadataKeys.AutoPopulate.UpdatedBy)) StampUser(column.ColumnName);
                    break;
                case MutationType.Update:
                    if (column.CompareMetadata(marker, MetadataKeys.AutoPopulate.UpdatedOn)) data[column.ColumnName] = dateTime;
                    if (column.CompareMetadata(marker, MetadataKeys.AutoPopulate.UpdatedBy)) StampUser(column.ColumnName);
                    break;
                case MutationType.Delete:
                    if (column.CompareMetadata(marker, MetadataKeys.AutoPopulate.UpdatedOn)) data[column.ColumnName] = dateTime;
                    if (column.CompareMetadata(marker, MetadataKeys.AutoPopulate.UpdatedBy)) StampUser(column.ColumnName);
                    if (column.CompareMetadata(marker, MetadataKeys.AutoPopulate.DeletedOn)) data[column.ColumnName] = dateTime;
                    if (column.CompareMetadata(marker, MetadataKeys.AutoPopulate.DeletedBy)) StampUser(column.ColumnName);
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

    /// <summary>
    /// The actor behind a mutation: the user-context claim named by the model-level
    /// <c>user-audit-key</c>, or null when no key is configured or the claim is absent
    /// (a system or unauthenticated write). This is the single definition of "who did
    /// this" — the created-by/updated-by/deleted-by columns stamped here and the
    /// <c>actor</c> column of a change-history row resolve the same way, so a row and
    /// its trail can never disagree about their author.
    /// </summary>
    public static object? ResolveActor(IDbModel model, IDictionary<string, object?> userContext)
    {
        var auditKey = model.GetMetadataValue(MetadataKeys.Audit.UserKey);
        return ResolveAuditUser(auditKey, userContext, !string.IsNullOrWhiteSpace(auditKey));
    }
}
