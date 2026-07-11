using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Storage;
using GraphQL;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// GraphQL resolver for file upload operations.
    /// Handles uploading files to storage and updating database records.
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
            var conFactory = bifrost.ConnFactory;
            var dialect = conFactory.Dialect;

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

            var keyColumns = table.KeyColumns.ToList();
            if (keyColumns.Count == 0)
                throw new BifrostExecutionError($"Table '{table.DbName}' has no primary key");

            var keyData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var keyCol in keyColumns)
                keyData[keyCol.ColumnName] = Convert.ChangeType(recordId, GetClrType(keyCol.DataType));

            // Filter by the request's active profile so the file-column write applies the
            // same per-profile module set a normal update does (fail-closed floor retained).
            var rawMutationTransformers = context.RequestServices?.GetService<IMutationTransformers>();
            var mutationTransformers = rawMutationTransformers == null
                ? null
                : BifrostProfileRegistry.FilterBy(rawMutationTransformers, context.UserContext);
            var transformContext = new MutationTransformContext
            {
                Model = model,
                UserContext = context.UserContext,
                Services = context.RequestServices,
            };

            // Pre-check (fix: verify row-writability BEFORE uploading, so we
            // never orphan a blob for a nonexistent or other-tenant row). Uses
            // primary-key-only data so the pipeline is evaluated purely for its
            // AdditionalFilter (tenant/soft-delete/row-scope policy) and any
            // table/action-level denial, without tripping column-level
            // required-value validation for the file column (whose real value
            // isn't known yet).
            TableFilter? preCheckFilter = null;
            if (mutationTransformers != null)
            {
                var preCheck = await mutationTransformers.TransformAsync(
                    table, MutationType.Update, new Dictionary<string, object?>(keyData, StringComparer.OrdinalIgnoreCase), transformContext);
                if (preCheck.Errors.Length > 0)
                    throw new BifrostExecutionError(string.Join("; ", preCheck.Errors));
                preCheckFilter = preCheck.AdditionalFilter;
            }

            var writable = await RowIsWritable(conFactory, dialect, table, keyData, preCheckFilter);
            if (!writable)
                throw new BifrostExecutionError($"Record not found or not accessible in table '{tableName}'.");

            // Upload file to storage only after confirming the row is writable.
            var fileMetadata = await _storageService.UploadFileAsync(
                table, column, model, recordId, fileContent, fileName, contentType, context.CancellationToken);

            // Re-run the transformer pipeline with the real column value so
            // policy column-write-deny and any data rewriting (e.g. audit
            // stamps) apply to the actual write, then update — checking rows
            // affected so a race between the pre-check and this write (row
            // deleted/reassigned/soft-deleted in between) is not reported as
            // success and does not leave a dangling blob.
            var updateData = new Dictionary<string, object?>(keyData, StringComparer.OrdinalIgnoreCase)
            {
                [column.ColumnName] = fileMetadata.ToJson(),
            };

            MutationTransformResult finalResult;
            if (mutationTransformers != null)
            {
                finalResult = await mutationTransformers.TransformAsync(table, MutationType.Update, updateData, transformContext);
                if (finalResult.Errors.Length > 0)
                {
                    await TryDeleteOrphanBlobAsync(table, column, model, fileMetadata, context.CancellationToken);
                    throw new BifrostExecutionError(string.Join("; ", finalResult.Errors));
                }
            }
            else
            {
                finalResult = new MutationTransformResult { MutationType = MutationType.Update, Data = updateData };
            }

            var rowsAffected = await UpdateDatabaseRecord(conFactory, dialect, table, keyData, finalResult);
            if (rowsAffected == 0)
            {
                await TryDeleteOrphanBlobAsync(table, column, model, fileMetadata, context.CancellationToken);
                throw new BifrostExecutionError($"Record not found or not accessible in table '{tableName}'.");
            }

            // Re-upload orphans the previous blob referenced by the row's prior
            // metadata; that cleanup is intentionally best-effort and
            // out-of-band (a stale row is impossible here because the UPDATE
            // above already committed the new reference), so failures are
            // swallowed rather than turning a successful upload into an error.
            // See docs: orphaned blobs from superseded uploads are expected to
            // be reclaimed by out-of-band storage GC, not by this request path.

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
        /// Checks whether a row exists under the combined tenant/soft-delete/
        /// row-scope-policy filter, without writing anything. Used to confirm
        /// writability before spending the (potentially large) storage upload.
        /// </summary>
        private static async Task<bool> RowIsWritable(
            IDbConnFactory conFactory,
            ISqlDialect dialect,
            IDbTable table,
            Dictionary<string, object?> keyData,
            TableFilter? additionalFilter)
        {
            await using var conn = conFactory.GetConnection();
            try
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();

                var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
                var whereClause = string.Join(" AND ", keyData.Keys.Select(k => $"{dialect.EscapeIdentifier(k)} = @{k}"));

                var parameters = new SqlParameterCollection();
                var filterSuffix = "";
                IReadOnlyList<SqlParameterInfo> filterParams = Array.Empty<SqlParameterInfo>();
                if (additionalFilter != null)
                {
                    var rendered = additionalFilter.RenderForMutation(dialect, parameters);
                    filterSuffix = $" AND ({rendered.Sql})";
                    filterParams = parameters.Parameters;
                }

                cmd.CommandText = $"SELECT 1 FROM {tableRef} WHERE {whereClause}{filterSuffix}";
                DbParameterBinder.AddParameters(cmd, keyData);
                DbParameterBinder.AddExtraParameters(cmd, filterParams);

                var result = await cmd.ExecuteScalarAsync();
                return result != null;
            }
            catch (BifrostExecutionError)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new BifrostExecutionError($"Failed to verify record accessibility: {ex.Message}", ex);
            }
        }

        private static async Task<int> UpdateDatabaseRecord(
            IDbConnFactory conFactory,
            ISqlDialect dialect,
            IDbTable table,
            Dictionary<string, object?> keyData,
            MutationTransformResult transformResult)
        {
            await using var conn = conFactory.GetConnection();
            try
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();

                var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
                var dbData = DbParameterBinder.ToDbColumnKeys(table, transformResult.Data);
                var setData = dbData
                    .Where(d => !keyData.ContainsKey(d.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

                if (setData.Count == 0)
                    return 0;

                var setClause = string.Join(",", setData.Select(kv => DbParameterBinder.SetAssignment(dialect, table, kv.Key)));
                var whereClause = string.Join(" AND ", keyData.Keys.Select(k => $"{dialect.EscapeIdentifier(k)} = @{k}"));

                var parameters = new SqlParameterCollection();
                var filterSuffix = "";
                IReadOnlyList<SqlParameterInfo> filterParams = Array.Empty<SqlParameterInfo>();
                if (transformResult.AdditionalFilter != null)
                {
                    var rendered = transformResult.AdditionalFilter.RenderForMutation(dialect, parameters);
                    filterSuffix = $" AND ({rendered.Sql})";
                    filterParams = parameters.Parameters;
                }

                cmd.CommandText = $"UPDATE {tableRef} SET {setClause} WHERE {whereClause}{filterSuffix}";

                DbParameterBinder.AddParameters(cmd, keyData);
                DbParameterBinder.AddParameters(cmd, setData);
                DbParameterBinder.AddExtraParameters(cmd, filterParams);

                return await cmd.ExecuteNonQueryAsync();
            }
            catch (BifrostExecutionError)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new BifrostExecutionError($"Failed to update database record: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Best-effort cleanup of a blob that was just uploaded but whose
        /// corresponding row write failed or affected zero rows, so a rejected
        /// upload does not leave an orphaned object in storage. Failures here
        /// are swallowed: the caller is already about to raise the original
        /// error, and a cleanup failure must not mask it or crash the request.
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

        private static Type GetClrType(string dataType)
        {
            var normalized = dataType.ToLowerInvariant();
            return normalized switch
            {
                "int" or "integer" => typeof(int),
                "bigint" => typeof(long),
                "smallint" => typeof(short),
                "tinyint" => typeof(byte),
                "uniqueidentifier" or "uuid" => typeof(Guid),
                _ => typeof(string)
            };
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
