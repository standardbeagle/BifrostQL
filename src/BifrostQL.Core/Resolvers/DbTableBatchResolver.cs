using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using GraphQL;
using GraphQL.Resolvers;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Core.Resolvers
{
    public sealed class DbTableBatchResolver : IFieldResolver
    {
        private const int DefaultMaxBatchSize = 100;
        private readonly IDbTable _table;

        public DbTableBatchResolver(IDbTable table)
        {
            _table = table;
        }

        public async ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            var conFactory = (IDbConnFactory)(context.InputExtensions["connFactory"] ?? throw new InvalidDataException("connection factory is not configured"));
            var model = (IDbModel)(context.InputExtensions["model"] ?? throw new InvalidDataException("database model is not configured"));
            var dialect = conFactory.Dialect;
            var modules = context.RequestServices!.GetRequiredService<IMutationModules>();
            var mutationTransformers = context.RequestServices!.GetRequiredService<IMutationTransformers>();
            modules.OnSave(context);

            var actions = context.GetArgument<List<Dictionary<string, object?>>>("actions");
            if (actions == null || actions.Count == 0)
                return 0;

            var maxBatchSize = GetMaxBatchSize(_table);
            if (actions.Count > maxBatchSize)
                throw new ExecutionError($"Batch size {actions.Count} exceeds maximum allowed size of {maxBatchSize}.");

            var userContext = context.UserContext as IDictionary<string, object?> ?? new Dictionary<string, object?>();
            var transformContext = new MutationTransformContext { Model = model, UserContext = userContext };

            await using var conn = conFactory.GetConnection();
            await conn.OpenAsync();
            await using var transaction = await conn.BeginTransactionAsync();
            try
            {
                var totalAffected = 0;
                foreach (var action in actions)
                {
                    totalAffected += await ExecuteAction(action, _table, modules, mutationTransformers, model, dialect, conn, transaction, userContext, transformContext);
                }
                await transaction.CommitAsync();
                return totalAffected;
            }
            catch (ExecutionError)
            {
                await transaction.RollbackAsync();
                throw;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                throw new ExecutionError(ex.Message, ex);
            }
        }

        private static int GetMaxBatchSize(IDbTable table)
        {
            var metaValue = table.GetMetadataValue("batch-max-size");
            if (metaValue != null && int.TryParse(metaValue, out var size) && size > 0)
                return size;
            return DefaultMaxBatchSize;
        }

        private static async Task<int> ExecuteAction(
            Dictionary<string, object?> action,
            IDbTable table,
            IMutationModules modules,
            IMutationTransformers mutationTransformers,
            IDbModel model,
            ISqlDialect dialect,
            DbConnection conn,
            DbTransaction transaction,
            IDictionary<string, object?> userContext,
            MutationTransformContext transformContext)
        {
            if (action.TryGetValue("insert", out var insertObj) && insertObj is Dictionary<string, object?> insertData)
            {
                return await ExecuteInsert(insertData, table, modules, model, dialect, conn, transaction, userContext);
            }
            if (action.TryGetValue("update", out var updateObj) && updateObj is Dictionary<string, object?> updateData)
            {
                return await ExecuteUpdate(updateData, table, modules, model, dialect, conn, transaction, userContext);
            }
            if (action.TryGetValue("delete", out var deleteObj) && deleteObj is Dictionary<string, object?> deleteData)
            {
                return await ExecuteDelete(deleteData, table, modules, mutationTransformers, model, dialect, conn, transaction, userContext, transformContext);
            }
            if (action.TryGetValue("upsert", out var upsertObj) && upsertObj is Dictionary<string, object?> upsertData)
            {
                return await ExecuteUpsert(upsertData, table, modules, model, dialect, conn, transaction, userContext);
            }
            return 0;
        }

        private static async Task<int> ExecuteInsert(
            Dictionary<string, object?> data,
            IDbTable table,
            IMutationModules modules,
            IDbModel model,
            ISqlDialect dialect,
            DbConnection conn,
            DbTransaction transaction,
            IDictionary<string, object?> userContext)
        {
            if (data.Count == 0) return 0;

            var moduleSql = modules.Insert(data, table, userContext, model);
            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var columns = string.Join(",", data.Keys.Select(k => dialect.EscapeIdentifier(k)));
            var values = string.Join(",", data.Keys.Select(k => $"@{k}"));
            var sql = $"INSERT INTO {tableRef}({columns}) VALUES({values});";
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = Join(sql, moduleSql);
            cmd.Transaction = transaction;
            AddParameters(cmd, data);
            return await cmd.ExecuteNonQueryAsync();
        }

        private static async Task<int> ExecuteUpdate(
            Dictionary<string, object?> data,
            IDbTable table,
            IMutationModules modules,
            IDbModel model,
            ISqlDialect dialect,
            DbConnection conn,
            DbTransaction transaction,
            IDictionary<string, object?> userContext)
        {
            if (data.Count == 0) return 0;

            var caseData = new Dictionary<string, object?>(data, StringComparer.OrdinalIgnoreCase);
            var keyData = caseData.Where(d => table.ColumnLookup[d.Key].IsPrimaryKey)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            var standardData = caseData.Where(d => !table.ColumnLookup[d.Key].IsPrimaryKey)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            if (!keyData.Any() || !standardData.Any()) return 0;

            var moduleSql = modules.Update(caseData, table, userContext, model);
            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var setClause = string.Join(",", standardData.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));
            var whereClause = string.Join(" AND ", keyData.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));
            var sql = $"UPDATE {tableRef} SET {setClause} WHERE {whereClause};";
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = Join(sql, moduleSql);
            cmd.Transaction = transaction;
            AddParameters(cmd, caseData);
            return await cmd.ExecuteNonQueryAsync();
        }

        private static async Task<int> ExecuteDelete(
            Dictionary<string, object?> data,
            IDbTable table,
            IMutationModules modules,
            IMutationTransformers mutationTransformers,
            IDbModel model,
            ISqlDialect dialect,
            DbConnection conn,
            DbTransaction transaction,
            IDictionary<string, object?> userContext,
            MutationTransformContext transformContext)
        {
            if (data.Count == 0) return 0;

            var transformResult = mutationTransformers.Transform(table, MutationType.Delete, data, transformContext);
            if (transformResult.Errors.Length > 0)
                throw new ExecutionError(string.Join("; ", transformResult.Errors));

            if (transformResult.MutationType == MutationType.Update)
            {
                var moduleSql = modules.Delete(transformResult.Data, table, userContext, model);
                var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
                var keyData = transformResult.Data.Where(d => table.ColumnLookup.ContainsKey(d.Key) && table.ColumnLookup[d.Key].IsPrimaryKey)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                var setData = transformResult.Data.Where(d => !table.ColumnLookup.ContainsKey(d.Key) || !table.ColumnLookup[d.Key].IsPrimaryKey)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                var setClause = string.Join(",", setData.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));
                var whereClause = string.Join(" AND ", keyData.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));
                var sql = $"UPDATE {tableRef} SET {setClause} WHERE {whereClause};";
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = Join(sql, moduleSql);
                cmd.Transaction = transaction;
                AddParameters(cmd, transformResult.Data);
                return await cmd.ExecuteNonQueryAsync();
            }

            var deleteModuleSql = modules.Delete(data, table, userContext, model);
            var deleteTableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var deleteWhereClause = string.Join(" AND ", data.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));
            var deleteSql = $"DELETE FROM {deleteTableRef} WHERE {deleteWhereClause};";
            await using var deleteCmd = conn.CreateCommand();
            deleteCmd.CommandText = Join(deleteSql, deleteModuleSql);
            deleteCmd.Transaction = transaction;
            AddParameters(deleteCmd, data);
            return await deleteCmd.ExecuteNonQueryAsync();
        }

        private static async Task<int> ExecuteUpsert(
            Dictionary<string, object?> data,
            IDbTable table,
            IMutationModules modules,
            IDbModel model,
            ISqlDialect dialect,
            DbConnection conn,
            DbTransaction transaction,
            IDictionary<string, object?> userContext)
        {
            if (data.Count == 0) return 0;

            var caseData = new Dictionary<string, object?>(data, StringComparer.OrdinalIgnoreCase);
            var keyData = caseData.Where(d => table.ColumnLookup[d.Key].IsPrimaryKey)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            if (keyData.Any())
                return await ExecuteUpdate(data, table, modules, model, dialect, conn, transaction, userContext);

            return await ExecuteInsert(data, table, modules, model, dialect, conn, transaction, userContext);
        }

        private static void AddParameters(DbCommand cmd, Dictionary<string, object?> data)
        {
            foreach (var kv in data)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = $"@{kv.Key}";
                p.Value = kv.Value ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }
        }

        private static string Join(string str, string[] array)
        {
            return String.Join(";", Flat(str, array));
        }

        private static IEnumerable<string> Flat(string str, string[] array)
        {
            yield return str;
            if (array != null)
            {
                foreach (var arrString in array) { yield return arrString; }
            }
        }
    }
}
