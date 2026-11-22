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

    // Runs after security gating (Policy=0, Tenant=1, StateMachine=2) but BEFORE the
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
        void StampUser(string columnName) => StampActor(context.Model, context.UserContext, data, columnName);

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
                    // created-on/created-by are stamped once, on insert: an update carrying
                    // them is a client rewriting who created the row, so the value is dropped.
                    if (column.CompareMetadata(marker, MetadataKeys.AutoPopulate.CreatedOn)
                        || column.CompareMetadata(marker, MetadataKeys.AutoPopulate.CreatedBy))
                        data.Remove(column.ColumnName);
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

    /// <summary>
    /// Stamps a server-owned actor column (created-by/updated-by/deleted-by, and the
    /// soft-delete <c>soft-delete-by</c> column) so a client can never spoof it. With a
    /// <c>user-audit-key</c> the column gets <see cref="ResolveActor"/> (null when the claim is
    /// absent, still a value the client cannot influence). Without one there is no trustworthy
    /// value, so the key is STRIPPED, leaving the DB default or existing value to stand. Either
    /// way the client's value never reaches the row.
    /// </summary>
    internal static void StampActor(
        IDbModel model, IDictionary<string, object?> userContext, IDictionary<string, object?> data, string column)
    {
        if (!HasActorSource(model))
            data.Remove(column);
        else
            data[column] = ResolveActor(model, userContext);
    }
    /// <summary>
    /// Whether the model names the claim an actor column is stamped from (<c>user-audit-key</c>).
    /// Without it <see cref="StampActor"/> strips every actor column from the write, so model
    /// load refuses an actor column on a model without one (ModelConfigValidator).
    /// </summary>
    internal static bool HasActorSource(IDbModel model)
        => !string.IsNullOrWhiteSpace(model.GetMetadataValue(MetadataKeys.Audit.UserKey));

    /// <summary>
    /// Whether <paramref name="column"/> declares a populator this transformer stamps with the
    /// actor (<c>created-by</c>, <c>updated-by</c>, <c>deleted-by</c>), by the same
    /// comparisons the stamping switch makes.
    /// </summary>
    internal static bool StampsActor(ColumnDto column)
    {
        var marker = MetadataKeys.AutoPopulate.Marker;
        return column.CompareMetadata(marker, MetadataKeys.AutoPopulate.CreatedBy)
            || column.CompareMetadata(marker, MetadataKeys.AutoPopulate.UpdatedBy)
            || column.CompareMetadata(marker, MetadataKeys.AutoPopulate.DeletedBy);
    }
}
