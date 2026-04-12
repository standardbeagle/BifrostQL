using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.Storage;
using GraphQL;

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

            // Upload file to storage
            var fileMetadata = await _storageService.UploadFileAsync(
                table, column, model, recordId, fileContent, fileName, contentType, context.CancellationToken);

            // Update database record with file metadata JSON
            var metadataJson = fileMetadata.ToJson();
            await UpdateDatabaseRecord(conFactory, table, column, recordId, metadataJson);

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

        private static async Task UpdateDatabaseRecord(
            IDbConnFactory conFactory,
            IDbTable table,
            ColumnDto column,
            string recordId,
            string metadataJson)
        {
            await using var conn = conFactory.GetConnection();
            try
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();

                var dialect = conFactory.Dialect;
                var tableRef = dialect.TableReference(table.TableSchema, table.DbName);

                // Build UPDATE statement
                var keyColumns = table.KeyColumns.ToList();
                if (keyColumns.Count == 0)
                    throw new BifrostExecutionError($"Table '{table.DbName}' has no primary key");

                // For single primary key
                var setClause = $"{dialect.EscapeIdentifier(column.ColumnName)} = @fileMetadata";
                var whereClause = string.Join(" AND ", keyColumns.Select(k => 
                    $"{dialect.EscapeIdentifier(k.ColumnName)} = @pk_{k.ColumnName}"));

                cmd.CommandText = $"UPDATE {tableRef} SET {setClause} WHERE {whereClause}";

                // Add parameters
                var fileParam = cmd.CreateParameter();
                fileParam.ParameterName = "@fileMetadata";
                fileParam.Value = metadataJson;
                cmd.Parameters.Add(fileParam);

                // Add PK parameter(s)
                foreach (var keyCol in keyColumns)
                {
                    var pkParam = cmd.CreateParameter();
                    pkParam.ParameterName = $"@pk_{keyCol.ColumnName}";
                    pkParam.Value = Convert.ChangeType(recordId, GetClrType(keyCol.DataType));
                    cmd.Parameters.Add(pkParam);
                }

                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                throw new BifrostExecutionError($"Failed to update database record: {ex.Message}", ex);
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
