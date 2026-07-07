using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Storage;
using GraphQL;
using GraphQL.Resolvers;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// GraphQL resolver for file download/query operations.
    /// Returns file metadata and URLs for accessing stored files.
    /// </summary>
    public sealed class FileDownloadResolver : IBifrostResolver, IFieldResolver
    {
        private readonly FileStorageService _storageService;

        public FileDownloadResolver(FileStorageService? storageService = null)
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
            var expirationMinutes = context.GetArgument<int?>("expirationMinutes") ?? 15;

            if (string.IsNullOrWhiteSpace(tableName))
                throw new BifrostExecutionError("Table name is required");
            if (string.IsNullOrWhiteSpace(columnName))
                throw new BifrostExecutionError("Column name is required");
            if (string.IsNullOrWhiteSpace(recordId))
                throw new BifrostExecutionError("Record ID is required");

            // Resolve table and column
            var table = model.GetTableFromDbName(tableName);
            if (!table.ColumnLookup.TryGetValue(columnName, out var column))
                throw new BifrostExecutionError($"Column '{columnName}' not found in table '{tableName}'");

            // Verify this is a file storage column
            if (!_storageService.IsFileStorageColumn(table, column, model))
            {
                throw new BifrostExecutionError($"Column '{columnName}' is not configured for file storage");
            }

            // Apply the same filter-transformer pipeline normal reads use
            // (tenant isolation, soft-delete, row-scope policy) so a caller
            // cannot download another tenant's or a soft-deleted row's file by
            // guessing/supplying its primary key.
            var additionalFilter = GetRowScopeFilter(context, table, column, model);

            // Get file metadata from database
            var metadataJson = await GetFileMetadataFromDatabase(
                bifrost.ConnFactory, table, column, recordId, additionalFilter);
            if (string.IsNullOrWhiteSpace(metadataJson))
            {
                return null; // No file stored for this record, or filtered out (not our tenant / soft-deleted)
            }

            var fileMetadata = FileMetadata.FromJson(metadataJson);
            if (fileMetadata == null)
                // The column holds a non-empty value that is not parseable file metadata.
                // Returning null here would masquerade a corrupt pointer as "no file",
                // diverging from the delete path which fails fast. Surface the corruption.
                throw new BifrostExecutionError(
                    $"File metadata for '{table.DbName}.{column.ColumnName}' record '{recordId}' " +
                    "could not be parsed; the stored file reference is corrupt.");

            // Generate presigned URL for access. The storage target always
            // comes from the column's configuration, never from the
            // row-persisted metadata (see FileStorageService).
            var accessUrl = await _storageService.GetFileUrlAsync(
                table, column, model, fileMetadata, expirationMinutes, context.CancellationToken);

            // Return file info
            return new FileDownloadResult
            {
                FileKey = fileMetadata.FileKey,
                OriginalName = fileMetadata.OriginalName,
                ContentType = fileMetadata.ContentType,
                Size = fileMetadata.Size,
                AccessUrl = accessUrl,
                UploadedAt = fileMetadata.UploadedAt,
                ExpiresAt = DateTime.UtcNow.AddMinutes(expirationMinutes)
            };
        }

        ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
        {
            return ResolveAsync(new BifrostFieldContextAdapter(context));
        }

        /// <summary>
        /// Resolves the combined tenant/soft-delete/row-scope-policy filter for
        /// this table via the same <see cref="IFilterTransformers"/> pipeline
        /// <see cref="QueryTransformerService"/> uses for ordinary reads,
        /// and enforces any column-level read-deny for the file column itself.
        /// Returns null (no extra filtering) when the pipeline isn't registered,
        /// matching how the rest of the read path degrades when the service is
        /// absent (e.g. in lightweight test hosts).
        /// </summary>
        private static TableFilter? GetRowScopeFilter(
            IBifrostFieldContext context, IDbTable table, ColumnDto column, IDbModel model)
        {
            var filterTransformers = context.RequestServices?.GetService<IFilterTransformers>();
            if (filterTransformers == null)
                return null;

            var transformContext = new QueryTransformContext
            {
                Model = model,
                UserContext = context.UserContext,
                QueryType = QueryType.Single,
            };

            foreach (var guard in filterTransformers.OfType<IColumnReadGuard>())
                guard.AssertColumnsReadable(table, new[] { column.DbName }, transformContext);

            return filterTransformers.GetCombinedFilter(table, transformContext);
        }

        private static async Task<string?> GetFileMetadataFromDatabase(
            IDbConnFactory conFactory,
            IDbTable table,
            ColumnDto column,
            string recordId,
            TableFilter? additionalFilter)
        {
            await using var conn = conFactory.GetConnection();
            try
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();

                var dialect = conFactory.Dialect;
                var tableRef = dialect.TableReference(table.TableSchema, table.DbName);

                // Build SELECT statement
                var keyColumns = table.KeyColumns.ToList();
                if (keyColumns.Count == 0)
                    throw new BifrostExecutionError($"Table '{table.DbName}' has no primary key");

                var keyData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var keyCol in keyColumns)
                    keyData[keyCol.ColumnName] = Convert.ChangeType(recordId, GetClrType(keyCol.DataType));

                var whereClause = string.Join(" AND ", keyData.Keys.Select(k =>
                    $"{dialect.EscapeIdentifier(k)} = @{k}"));

                var parameters = new SqlParameterCollection();
                var filterSuffix = "";
                IReadOnlyList<SqlParameterInfo> filterParams = Array.Empty<SqlParameterInfo>();
                if (additionalFilter != null)
                {
                    var rendered = additionalFilter.RenderForMutation(dialect, parameters);
                    filterSuffix = $" AND ({rendered.Sql})";
                    filterParams = parameters.Parameters;
                }

                cmd.CommandText = $"SELECT {dialect.EscapeIdentifier(column.ColumnName)} FROM {tableRef} WHERE {whereClause}{filterSuffix}";

                DbParameterBinder.AddParameters(cmd, keyData);
                DbParameterBinder.AddExtraParameters(cmd, filterParams);

                var result = await cmd.ExecuteScalarAsync();
                return result?.ToString();
            }
            catch (BifrostExecutionError)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new BifrostExecutionError($"Failed to retrieve file metadata: {ex.Message}", ex);
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
    /// Result of a file download/query operation
    /// </summary>
    public sealed class FileDownloadResult
    {
        public string? FileKey { get; set; }
        public string? OriginalName { get; set; }
        public string? ContentType { get; set; }
        public long Size { get; set; }
        public string? AccessUrl { get; set; }
        public DateTime UploadedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
    }
}
