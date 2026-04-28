using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.Storage;
using GraphQL;
using GraphQL.Resolvers;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// GraphQL resolver for file delete operations.
    /// Removes files from storage and clears database records.
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
            var conFactory = bifrost.ConnFactory;

            // Get required arguments
            var tableName = context.GetArgument<string>("table");
            var columnName = context.GetArgument<string>("column");
            var recordId = context.GetArgument<string>("recordId");

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

            // Get current file metadata from database
            var metadataJson = await GetFileMetadataFromDatabase(conFactory, table, column, recordId);
            if (!string.IsNullOrWhiteSpace(metadataJson))
            {
                var fileMetadata = FileMetadata.FromJson(metadataJson);
                if (fileMetadata != null)
                {
                    // Delete file from storage
                    await _storageService.DeleteFileAsync(fileMetadata, model, context.CancellationToken);
                }
            }

            // Clear the database record
            await ClearDatabaseRecord(conFactory, table, column, recordId);

            return true;
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

        private static async Task ClearDatabaseRecord(
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

                // Build UPDATE statement to clear the file column
                var keyColumns = table.KeyColumns.ToList();
                if (keyColumns.Count == 0)
                    throw new BifrostExecutionError($"Table '{table.DbName}' has no primary key");

                var setClause = $"{dialect.EscapeIdentifier(column.ColumnName)} = NULL";
                var whereClause = string.Join(" AND ", keyColumns.Select(k =>
                    $"{dialect.EscapeIdentifier(k.ColumnName)} = @pk_{k.ColumnName}"));

                cmd.CommandText = $"UPDATE {tableRef} SET {setClause} WHERE {whereClause}";

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
                throw new BifrostExecutionError($"Failed to clear database record: {ex.Message}", ex);
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
}
