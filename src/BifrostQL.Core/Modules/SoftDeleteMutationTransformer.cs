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
///   - Optionally sets deleted_by if configured
///
/// On UPDATE:
///   - Adds filter: deleted_at IS NULL (prevent updating deleted records)
///
/// Additional metadata options:
///   "dbo.users { soft-delete: deleted_at; soft-delete-by: deleted_by_user_id }"
/// </summary>
public sealed class SoftDeleteMutationTransformer : SoftDeleteMutationTransformerBase
{
    public const string MetadataKey = "soft-delete";
    public const string DeletedByMetadataKey = "soft-delete-by";
    public const string UserIdContextKey = "user_id";

    public SoftDeleteMutationTransformer() : base(MetadataKey, priority: 100)
    {
    }

    public override string ModuleName => "soft-delete";

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

        // Optionally set deleted_by
        if (table.Metadata.TryGetValue(DeletedByMetadataKey, out var deletedByCol) &&
            deletedByCol is string deletedByColumn &&
            !string.IsNullOrWhiteSpace(deletedByColumn) &&
            table.ColumnLookup.ContainsKey(deletedByColumn))
        {
            if (context.UserContext.TryGetValue(UserIdContextKey, out var userId))
            {
                transformedData[deletedByColumn] = userId;
            }
        }

        return new MutationTransformResult
        {
            MutationType = MutationType.Update,
            Data = transformedData,
            AdditionalFilter = softDeleteFilter
        };
    }
}
