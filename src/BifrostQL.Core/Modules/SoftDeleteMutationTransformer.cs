using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Transforms DELETE operations into UPDATE operations for soft-delete tables.
/// Also ensures UPDATE and DELETE operations only affect non-deleted records.
///
/// Configuration via metadata:
///   "dbo.users { soft-delete: deleted_at }"
///
/// On DELETE:
///   - Converts to UPDATE
///   - Sets deleted_at = current timestamp
///   - Optionally stamps deleted_by with the audit actor (see AuditMutationTransformer.StampActor)
///
/// On UPDATE:
///   - Adds filter: deleted_at IS NULL (prevent updating deleted records)
///
/// Additional metadata options:
///   "dbo.users { soft-delete: deleted_at; soft-delete-by: deleted_by_user_id }"
/// </summary>
public sealed class SoftDeleteMutationTransformer : SoftDeleteMutationTransformerBase
{
    public const string MetadataKey = MetadataKeys.SoftDelete.Column;
    public const string DeletedByMetadataKey = MetadataKeys.SoftDelete.DeletedBy;

    public SoftDeleteMutationTransformer() : base(MetadataKey, priority: 100)
    {
    }

    public override string ModuleName => MetadataKeys.SoftDelete.Column;

    protected override MutationTransformResult TransformDelete(
        IDbTable table,
        Dictionary<string, object?> data,
        MutationTransformContext context,
        string columnName,
        TableFilter softDeleteFilter)
    {
        // Convert DELETE to UPDATE, set deleted_at
        var transformedData = new Dictionary<string, object?>(data)
        {
            [columnName] = DateTimeOffset.UtcNow
        };

        // deleted_by names the same actor as the audit columns and the history trail.
        if (table.Metadata.TryGetValue(DeletedByMetadataKey, out var deletedByCol) &&
            deletedByCol is string deletedByColumn &&
            !string.IsNullOrWhiteSpace(deletedByColumn) &&
            table.ColumnLookup.ContainsKey(deletedByColumn))
        {
            AuditMutationTransformer.StampActor(context.Model, context.UserContext, transformedData, deletedByColumn);
        }

        return new MutationTransformResult
        {
            MutationType = MutationType.Update,
            Data = transformedData,
            AdditionalFilter = softDeleteFilter
        };
    }
}
