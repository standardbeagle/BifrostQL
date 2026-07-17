using System.Data.Common;
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
            var dialect = conFactory.Dialect;

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

            // Decode recordId into one value per key column (composite-key safe;
            // never the same scalar broadcast across every key column).
            var keyData = FileRecordKey.BuildKeyData(table, recordId);

            // Filter by the request's active profile so clearing the file column applies the
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

            // Clearing the file column is modeled as an UPDATE (setting the
            // column to NULL), not a row DELETE, so this runs through the same
            // tenant/soft-delete/row-scope-policy pipeline normal updates use —
            // the same machinery that protects normal row reads/writes (fix for
            // the resolver bypassing tenant-filter/soft-delete entirely).
            var clearData = new Dictionary<string, object?>(keyData, StringComparer.OrdinalIgnoreCase)
            {
                [column.ColumnName] = null,
            };

            MutationTransformResult transformResult;
            if (mutationTransformers != null)
            {
                transformResult = await mutationTransformers.TransformAsync(table, MutationType.Update, clearData, transformContext);
                if (transformResult.Errors.Length > 0)
                    throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));
            }
            else
            {
                transformResult = new MutationTransformResult { MutationType = MutationType.Update, Data = clearData };
            }

            // Get current file metadata from the database, scoped by the same
            // filter, so a caller cannot discover (or delete) another tenant's
            // or a soft-deleted row's file. A row that does not exist — or is
            // filtered out — fails here, before any storage I/O.
            var (rowFound, metadataJson) = await GetFileMetadataFromDatabase(
                conFactory, dialect, table, column, keyData, transformResult.AdditionalFilter);
            if (!rowFound)
                throw new BifrostExecutionError($"Record not found or not accessible in table '{tableName}'.");

            if (!string.IsNullOrWhiteSpace(metadataJson))
            {
                var fileMetadata = FileMetadata.FromJson(metadataJson);
                if (fileMetadata == null)
                    // The column holds a value that is not parseable file metadata. Clearing the
                    // DB pointer now would orphan whatever the value referenced while telling the
                    // client the delete succeeded. Fail fast and preserve the pointer instead.
                    throw new BifrostExecutionError(
                        $"File metadata for '{table.DbName}.{column.ColumnName}' record '{recordId}' " +
                        "could not be parsed; refusing to clear the database record and orphan the file.");

                // Delete the file from storage first; only clear the DB pointer
                // once the underlying object is gone, so a storage failure does
                // not leave an orphaned blob. The storage target is always
                // resolved from the column's configuration (never from the
                // row-persisted BucketName/ProviderType, which is an ordinary
                // writable column value an attacker could point elsewhere).
                await _storageService.DeleteFileAsync(table, column, model, fileMetadata, context.CancellationToken);
            }

            // Clear the database record with the same transformer-derived
            // filter. Zero rows affected means the row vanished (or was scoped
            // out) between the read and the write — report failure rather than
            // a false success.
            var rowsAffected = await ClearDatabaseRecord(conFactory, dialect, table, keyData, transformResult);
            if (rowsAffected == 0)
                throw new BifrostExecutionError($"Record not found or not accessible in table '{tableName}'.");

            return true;
        }

        ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
        {
            return ResolveAsync(new BifrostFieldContextAdapter(context));
        }

        private static async Task<(bool RowFound, string? MetadataJson)> GetFileMetadataFromDatabase(
            IDbConnFactory conFactory,
            ISqlDialect dialect,
            IDbTable table,
            ColumnDto column,
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

                cmd.CommandText = $"SELECT {dialect.EscapeIdentifier(column.ColumnName)} FROM {tableRef} WHERE {whereClause}{filterSuffix}";

                DbParameterBinder.AddParameters(cmd, keyData);
                DbParameterBinder.AddExtraParameters(cmd, filterParams);

                await using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                    return (false, null);

                var value = await reader.IsDBNullAsync(0) ? null : reader.GetValue(0)?.ToString();
                return (true, value);
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

        private static async Task<int> ClearDatabaseRecord(
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
                throw new BifrostExecutionError($"Failed to clear database record: {ex.Message}", ex);
            }
        }
    }
}
