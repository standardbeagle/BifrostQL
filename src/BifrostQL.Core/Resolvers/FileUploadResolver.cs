using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Approval;
using BifrostQL.Core.Storage;
using GraphQL;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// GraphQL resolver for file upload operations.
    /// Uploads the content to storage and repoints the row's file column through
    /// the mutation pipeline.
    /// </summary>
    public sealed class FileUploadResolver : BifrostResolverBase
    {
        private readonly FileStorageService _storageService;

        public FileUploadResolver(FileStorageService? storageService = null)
        {
            _storageService = storageService ?? new FileStorageService();
        }

        public override async ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
        {
            var bifrost = new BifrostContextAdapter(context);
            var model = bifrost.Model;

            // Get required arguments
            var tableName = context.GetArgument<string>("table");
            var columnName = context.GetArgument<string>("column");
            var recordId = context.GetArgument<string>("recordId");
            var fileContent = context.GetArgument<byte[]>("file");
            var fileName = context.GetArgument<string>("filename");
            var contentType = context.GetArgument<string>("contentType");

            if (string.IsNullOrWhiteSpace(tableName))
                throw new BifrostExecutionError("Table name is required");
            if (string.IsNullOrWhiteSpace(columnName))
                throw new BifrostExecutionError("Column name is required");
            if (string.IsNullOrWhiteSpace(recordId))
                throw new BifrostExecutionError("Record ID is required");
            if (fileContent == null || fileContent.Length == 0)
                throw new BifrostExecutionError("File content is required");

            // Resolve table and column
            var table = model.GetTableFromDbName(tableName);
            if (!table.ColumnLookup.TryGetValue(columnName, out var column))
                throw new BifrostExecutionError($"Column '{columnName}' not found in table '{tableName}'");

            // Verify this is a file storage column
            if (!_storageService.IsFileStorageColumn(table, column, model))
            {
                throw new BifrostExecutionError($"Column '{columnName}' is not configured for file storage");
            }

            // Decode recordId into one value per key column (composite-key safe;
            // never the same scalar broadcast across every key column).
            var keyData = FileRecordKey.BuildKeyData(table, recordId);

            // Confirm the row is visible to this caller BEFORE spending the upload,
            // through the same read chain a normal read applies. This is a cheap
            // pre-check, not the write gate: the pipeline below is the gate.
            var (rowVisible, _) = await FilePointerAccess.ReadPointerAsync(
                bifrost, table, column, keyData, context.CancellationToken);
            if (!rowVisible)
                throw new BifrostExecutionError($"Record not found or not accessible in table '{tableName}'.");

            // The content goes to a FRESH random storage key, never to an address
            // derived from the caller's input: UploadFileAsync takes no storage-key
            // parameter, so the compensating delete below can only ever remove the
            // object this call just created, and an upload the pipeline goes on to
            // veto cannot have overwritten the row's existing content in place
            // (protocol-adapter-security invariant 8a).
            var fileMetadata = await _storageService.UploadFileAsync(
                table, column, model, recordId, fileContent, fileName, contentType,
                cancellationToken: context.CancellationToken);

            int affectedRows;
            try
            {
                affectedRows = await FilePointerAccess.WritePointerAsync(
                    context, bifrost, table, column, keyData, fileMetadata.ToJson());
            }
            catch (BifrostExecutionError pending) when (pending.ErrorCode == ApprovalInterceptMutationHook.PendingApprovalCode)
            {
                // The approval gate did not reject the write, it DEFERRED it: a pending
                // change now holds this pointer as its intended payload. Reclaiming the
                // object here would leave the approver to apply a pointer to content that
                // no longer exists, so the object stays and the caller is told the change
                // is pending. An object left by a change that is later rejected is
                // unreferenced residue for out-of-band collection.
                throw;
            }
            catch
            {
                // Nothing committed, so the object this call uploaded is unreferenced.
                await TryDeleteOrphanBlobAsync(table, column, model, fileMetadata, context.CancellationToken);
                throw;
            }

            // Zero rows means the pipeline scoped the write away (row deleted,
            // reassigned or soft-deleted between the pre-check and the write). This is
            // the pipeline's REAL affected-row count, never its scalar return, which on
            // a single-key table is the KEY — inert for every nonzero key and wrong for
            // key value 0 (invariant 8b).
            if (affectedRows == 0)
            {
                await TryDeleteOrphanBlobAsync(table, column, model, fileMetadata, context.CancellationToken);
                throw new BifrostExecutionError($"Record not found or not accessible in table '{tableName}'.");
            }

            // Re-upload orphans the previous object referenced by the row's prior
            // metadata; that cleanup is intentionally best-effort and out-of-band (a
            // stale row is impossible here because the pointer write above already
            // committed the new reference). See docs: orphaned objects from superseded
            // uploads are reclaimed by storage GC, not by this request path.

            // Return file metadata
            return new FileUploadResult
            {
                Success = true,
                FileKey = fileMetadata.FileKey,
                OriginalName = fileMetadata.OriginalName,
                ContentType = fileMetadata.ContentType,
                Size = fileMetadata.Size,
                AccessUrl = fileMetadata.AccessUrl,
                UploadedAt = fileMetadata.UploadedAt
            };
        }

        /// <summary>
        /// Best-effort cleanup of an object that was just uploaded but whose
        /// corresponding row write failed or affected zero rows, so a rejected
        /// upload does not leave an orphan in storage. Failures here are swallowed:
        /// the caller is already about to raise the original error, and a cleanup
        /// failure must not mask it or crash the request.
        /// </summary>
        private async Task TryDeleteOrphanBlobAsync(
            IDbTable table, ColumnDto column, IDbModel model, FileMetadata fileMetadata, CancellationToken cancellationToken)
        {
            try
            {
                await _storageService.DeleteFileAsync(table, column, model, fileMetadata, cancellationToken);
            }
            catch
            {
                // Best-effort cleanup only; the original failure is what surfaces to the caller.
            }
        }
    }

    /// <summary>
    /// Result of a file upload operation
    /// </summary>
    public sealed class FileUploadResult
    {
        public bool Success { get; set; }
        public string? FileKey { get; set; }
        public string? OriginalName { get; set; }
        public string? ContentType { get; set; }
        public long Size { get; set; }
        public string? AccessUrl { get; set; }
        public DateTime UploadedAt { get; set; }
    }
}
