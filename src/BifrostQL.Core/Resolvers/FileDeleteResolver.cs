using BifrostQL.Core.Storage;
using GraphQL;
using GraphQL.Resolvers;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// GraphQL resolver for file delete operations.
    /// Clears the row's file pointer through the mutation pipeline, then removes
    /// the backing object from storage.
    /// </summary>
    public sealed class FileDeleteResolver : IBifrostResolver, IFieldResolver
    {
        private readonly FileStorageService _storageService;

        public FileDeleteResolver(FileStorageService? storageService = null)
        {
            _storageService = storageService ?? new FileStorageService();
        }

        public async ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
        {
            var bifrost = new BifrostContextAdapter(context);
            var model = bifrost.Model;

            // Get required arguments
            var tableName = context.GetArgument<string>("table");
            var columnName = context.GetArgument<string>("column");
            var recordId = context.GetArgument<string>("recordId");
            // The optimistic-concurrency token the row was read at, for a table that
            // declares one. Passed straight to the pipeline, which owns the guard.
            var concurrencyToken = context.GetArgument<string>("concurrencyToken");

            if (string.IsNullOrWhiteSpace(tableName))
                throw new BifrostExecutionError("Table name is required");
            if (string.IsNullOrWhiteSpace(columnName))
                throw new BifrostExecutionError("Column name is required");
            if (string.IsNullOrWhiteSpace(recordId))
                throw new BifrostExecutionError("Record ID is required");

            // Resolve table and column. Client-name rule (M11-w): schema-qualified
            // resolves exactly, a bare name resolves only when unique across
            // schemas; ambiguity and unknown are the same sanitized miss. Positive
            // resolve: the throwing lookup's message embeds the caller-supplied
            // table name, which must never reach the wire (finding M31); the same
            // rule drops the name from the column-miss message.
            if (!model.TryGetTableFromClientName(tableName, out var table))
                throw BifrostErrorSink.LookupMiss(
                    "The requested table was not found.",
                    $"File delete table miss: '{tableName}'.",
                    nameof(FileDeleteResolver));
            if (!table.ColumnLookup.TryGetValue(columnName, out var column))
                throw new BifrostExecutionError($"Column '{columnName}' was not found on the requested table");

            // Verify this is a file storage column
            if (!_storageService.IsFileStorageColumn(table, column, model))
            {
                throw new BifrostExecutionError($"Column '{columnName}' is not configured for file storage");
            }

            // Decode recordId into one value per key column (composite-key safe;
            // never the same scalar broadcast across every key column).
            var keyData = FileRecordKey.BuildKeyData(table, recordId);

            // Read the current pointer through the read-intent seam, so the same
            // filter chain a normal read applies (tenant isolation, soft delete,
            // row-scope policy) decides whether this caller can see the row at all.
            var (rowFound, pointer) = await FilePointerAccess.ReadPointerAsync(
                bifrost, table, column, keyData, context.CancellationToken);
            if (!rowFound)
                throw new BifrostExecutionError("Record not found or not accessible on the requested table.");

            FileMetadata? fileMetadata = null;
            if (pointer != null)
            {
                fileMetadata = FileMetadata.FromJson(pointer)
                    // The column holds a value that is not parseable file metadata. Clearing the
                    // DB pointer now would orphan whatever the value referenced while telling the
                    // client the delete succeeded. Fail fast and preserve the pointer instead.
                    ?? throw new BifrostExecutionError(
                        $"File metadata for '{table.DbName}.{column.ColumnName}' record '{recordId}' " +
                        "could not be parsed; refusing to clear the database record and orphan the file.");
            }

            // Clear the pointer FIRST, through the pipeline. The pipeline is the
            // authorization gate — policy, tenant scope, the approval gate, the
            // concurrency token — and it may veto or defer, so removing the object
            // before it runs would destroy content the veto exists to protect,
            // unrecoverably. Clearing first bounds the worst case to an object no row
            // references any more (reclaimable) instead of a row advertising content
            // that is already gone. This is the ordering FileObjectSeam.DeleteAsync
            // established; the resolver's old blob-first order was the divergence.
            var affectedRows = await FilePointerAccess.WritePointerAsync(
                context, bifrost, table, column, keyData, pointerJson: null, concurrencyToken);

            // Zero rows means the write was scoped away or the row vanished between
            // the read and the write. Reported as success it would strand the object
            // and lie about the write. This is the pipeline's REAL affected-row count,
            // never its scalar return (which is the KEY on a single-key table).
            if (affectedRows == 0)
                throw new BifrostExecutionError("Record not found or not accessible on the requested table.");

            if (fileMetadata != null)
            {
                // The pointer is cleared and committed. A failing object delete now
                // leaves unreferenced residue only an operator can reclaim, so it is
                // raised as the dedicated residue type carrying the storage key —
                // never swallowed, and never mistaken for a denial. The storage target
                // is always resolved from the column's configuration, never from the
                // row-persisted BucketName/ProviderType (an ordinary writable value).
                try
                {
                    await _storageService.DeleteFileAsync(table, column, model, fileMetadata, context.CancellationToken);
                }
                catch (Exception blobFailure) when (blobFailure is not OperationCanceledException)
                {
                    throw new FileObjectResidueException(
                        fileMetadata.FileKey,
                        $"The file pointer for '{table.DbName}.{column.ColumnName}' was cleared, but the backing " +
                        $"object at storage key '{fileMetadata.FileKey}' could not be deleted: it is now " +
                        "unreferenced residue and must be reclaimed manually.",
                        blobFailure);
                }
            }

            return true;
        }

        ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
        {
            return ResolveAsync(new BifrostFieldContextAdapter(context));
        }
    }
}
