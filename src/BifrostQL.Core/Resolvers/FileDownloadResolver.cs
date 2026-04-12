using BifrostQL.Core.Model;
using BifrostQL.Core.Storage;
using GraphQL;
using GraphQL.Resolvers;

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

            // Get file metadata from database
            var metadataJson = await GetFileMetadataFromDatabase(bifrost.ConnFactory, table, column, recordId);
            if (string.IsNullOrWhiteSpace(metadataJson))
            {
                return null; // No file stored for this record
            }

            var fileMetadata = FileMetadata.FromJson(metadataJson);
            if (fileMetadata == null)
            {
                return null;
            }

            // Generate presigned URL for access
            var accessUrl = await _storageService.GetFileUrlAsync(
                fileMetadata, model, expirationMinutes, context.CancellationToken);

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

        private static async Task<string?> GetFileMetadataFromDatabase(
            IDbConnFactory conFactory,
            IDbTable table,
            ColumnDto column,
            string recordId)
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

                var whereClause = string.Join(" AND ", keyColumns.Select(k =>
                    $"{dialect.EscapeIdentifier(k.ColumnName)} = @pk_{k.ColumnName}"));

                cmd.CommandText = $"SELECT {dialect.EscapeIdentifier(column.ColumnName)} FROM {tableRef} WHERE {whereClause}";

                // Add PK parameter(s)
                foreach (var keyCol in keyColumns)
                {
                    var pkParam = cmd.CreateParameter();
                    pkParam.ParameterName = $"@pk_{keyCol.ColumnName}";
                    pkParam.Value = Convert.ChangeType(recordId, GetClrType(keyCol.DataType));
                    cmd.Parameters.Add(pkParam);
                }

                var result = await cmd.ExecuteScalarAsync();
                return result?.ToString();
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
