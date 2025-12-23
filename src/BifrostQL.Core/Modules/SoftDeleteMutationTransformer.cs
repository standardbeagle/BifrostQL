using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using GraphQL;

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
public sealed class SoftDeleteMutationTransformer : IMutationTransformer
{
    public const string MetadataKey = "soft-delete";
    public const string DeletedByMetadataKey = "soft-delete-by";
    public const string UserIdContextKey = "user_id";

    public int Priority => 100;

    public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
    {
        // Only applies to DELETE and UPDATE operations on soft-delete tables
        if (mutationType == MutationType.Insert)
            return false;

        return table.Metadata.TryGetValue(MetadataKey, out var val) && val != null;
    }

    public MutationTransformResult Transform(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context)
    {
        var deletedAtColumn = table.Metadata[MetadataKey]?.ToString();
        if (string.IsNullOrWhiteSpace(deletedAtColumn))
        {
            return new MutationTransformResult
            {
                MutationType = mutationType,
                Data = data
            };
        }

        // Verify column exists
        if (!table.ColumnLookup.ContainsKey(deletedAtColumn))
        {
            var fullTableName = $"{table.TableSchema}.{table.DbName}";
            return new MutationTransformResult
            {
                MutationType = mutationType,
                Data = data,
                Errors = new[] { $"Soft-delete column '{deletedAtColumn}' not found in table '{fullTableName}'." }
            };
        }

        // For both UPDATE and DELETE, ensure we only affect non-deleted records
        var softDeleteFilter = TableFilterFactory.IsNull(table.DbName, deletedAtColumn);

        if (mutationType == MutationType.Delete)
        {
            // Convert DELETE to UPDATE, set deleted_at
            var transformedData = new Dictionary<string, object?>(data)
            {
                [deletedAtColumn] = DateTimeOffset.UtcNow
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

        // For UPDATE, just add the filter to prevent updating deleted records
        return new MutationTransformResult
        {
            MutationType = mutationType,
            Data = data,
            AdditionalFilter = softDeleteFilter
        };
    }
}
