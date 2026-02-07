using Microsoft.Data.SqlClient;
using System.Reflection;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Model;
using GraphQL;
using GraphQL.Resolvers;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Core.Resolvers
{
    public sealed class DbTableMutateResolver : IFieldResolver
    {
        private readonly IDbTable _table;

        public DbTableMutateResolver(IDbTable table)
        {
            _table = table;
        }

        public async ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            var conFactory = (IDbConnFactory)(context.InputExtensions["connFactory"] ?? throw new InvalidDataException("connection factory is not configured"));
            var model = (IDbModel)(context.InputExtensions["model"] ?? throw new InvalidDataException("database model is not configured"));
            var dialect = conFactory.Dialect;
            var table = _table;
            var modules = context.RequestServices!.GetRequiredService<IMutationModules>();
            var mutationTransformers = context.RequestServices!.GetRequiredService<IMutationTransformers>();
            modules.OnSave(context);

            if (context.HasArgument("insert"))
            {
                return await InsertObject(context, table, modules, model, conFactory, dialect);
            }
            if (context.HasArgument("update"))
            {
                return await UpdateObject(context, table, modules, model, conFactory, dialect);
            }
            if (context.HasArgument("delete"))
            {
                return await DeleteObject(context, modules, mutationTransformers, table, model, conFactory, dialect);
            }

            if (context.HasArgument("upsert"))
            {
                var propertyInfo = GetPropertyInfo(context, _table, "upsert");
                if (!propertyInfo.data.Any())
                    return 0;
                if (propertyInfo.keyData.Any())
                    return await UpdateObject(context, table, modules, model, conFactory, dialect, "upsert");

                return await InsertObject(context, table, modules, model, conFactory, dialect, "upsert");
            }
            return null;
        }

        private (Dictionary<string, object?> data, Dictionary<string, object?> keyData, Dictionary<string, object?> standardData) GetPropertyInfo(IResolveFieldContext context, IDbTable table, string parameterName)
        {
            var baseData = context.GetArgument<Dictionary<string, object?>>(parameterName) ?? new();

            var data = new Dictionary<string, object?>(baseData!, StringComparer.OrdinalIgnoreCase);

            var pkKeyData = ResolvePrimaryKeyArgument(context, table);
            var keyData = pkKeyData
                ?? data.Where(d => table.ColumnLookup[d.Key].IsPrimaryKey)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

            var standardData = data
                .Where(d => !keyData.ContainsKey(d.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            var allData = new Dictionary<string, object?>(standardData, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in keyData)
                allData[kv.Key] = kv.Value;

            return (allData, keyData, standardData);
        }

        private static Dictionary<string, object?>? ResolvePrimaryKeyArgument(IResolveFieldContext context, IDbTable table)
        {
            if (!context.HasArgument("_primaryKey"))
                return null;

            var pkValues = context.GetArgument<List<object?>>("_primaryKey");
            if (pkValues == null || pkValues.Count == 0)
                return null;

            var keyColumns = table.KeyColumns.ToList();

            if (keyColumns.Count == 0)
                throw new ExecutionError($"Table '{table.DbName}' has no primary key columns.");

            if (pkValues.Count != keyColumns.Count)
                throw new ExecutionError(
                    $"_primaryKey for '{table.DbName}' expects {keyColumns.Count} value(s) " +
                    $"({string.Join(", ", keyColumns.Select(c => c.GraphQlName))}) but received {pkValues.Count}.");

            return keyColumns.Zip(pkValues, (col, val) => new { col.ColumnName, Value = val })
                .ToDictionary(x => x.ColumnName, x => x.Value);
        }

        private async Task<object?> DeleteObject(IResolveFieldContext context, IMutationModules modules,
            IMutationTransformers mutationTransformers, IDbTable table, IDbModel model,
            IDbConnFactory conFactory, ISqlDialect dialect)
        {
            var data = context.GetArgument<Dictionary<string, object?>>("delete");
            if (!data.Any())
                return 0;

            var pkKeyData = ResolvePrimaryKeyArgument(context, table);
            if (pkKeyData != null)
            {
                foreach (var kv in pkKeyData)
                    data[kv.Key] = kv.Value;
            }

            var userContext = context.UserContext as IDictionary<string, object?> ?? new Dictionary<string, object?>();
            var transformContext = new MutationTransformContext { Model = model, UserContext = userContext };
            var transformResult = mutationTransformers.Transform(table, MutationType.Delete, data, transformContext);

            if (transformResult.Errors.Length > 0)
                throw new ExecutionError(string.Join("; ", transformResult.Errors));

            if (transformResult.MutationType == MutationType.Update)
            {
                // Soft-delete: transformed to UPDATE
                var moduleSql = modules.Delete(transformResult.Data, table, context.UserContext, model);
                var tableRef = dialect.TableReference(table.TableSchema, table.DbName);

                var keyData = transformResult.Data.Where(d => table.ColumnLookup.ContainsKey(d.Key) && table.ColumnLookup[d.Key].IsPrimaryKey)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                var setData = transformResult.Data.Where(d => !table.ColumnLookup.ContainsKey(d.Key) || !table.ColumnLookup[d.Key].IsPrimaryKey)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

                var setClause = string.Join(",", setData.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));
                var whereClause = string.Join(" AND ", keyData.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));
                var sql = $"UPDATE {tableRef} SET {setClause} WHERE {whereClause};";
                var cmd = new SqlCommand(Join(sql, moduleSql));
                cmd.Parameters.AddRange(transformResult.Data.Select(kv => new SqlParameter($"@{kv.Key}", kv.Value ?? DBNull.Value)).ToArray());
                return await ExecuteNonQuery(conFactory, cmd);
            }

            // Standard DELETE (no transformation)
            var deleteModuleSql = modules.Delete(data, table, context.UserContext, model);
            var deleteTableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var deleteWhereClause = string.Join(" AND ", data.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));
            var deleteSql = $"DELETE FROM {deleteTableRef} WHERE {deleteWhereClause};";
            var deleteCmd = new SqlCommand(Join(deleteSql, deleteModuleSql));
            deleteCmd.Parameters.AddRange(data.Select(kv => new SqlParameter($"@{kv.Key}", kv.Value ?? DBNull.Value)).ToArray());
            return await ExecuteNonQuery(conFactory, deleteCmd);
        }

        private async Task<object?> UpdateObject(IResolveFieldContext context, IDbTable table, IMutationModules modules, IDbModel model,
            IDbConnFactory conFactory, ISqlDialect dialect, string parameterName = "update")
        {
            var propertyInfo = GetPropertyInfo(context, table, parameterName);
            if (!propertyInfo.data.Any())
                return 0;

            if (!propertyInfo.keyData.Any())
                return 0;

            var moduleSql = modules.Update(propertyInfo.data, table, context.UserContext, model);
            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var setClause = string.Join(",", propertyInfo.standardData.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));
            var whereClause = string.Join(" AND ", propertyInfo.keyData.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));
            var sql = $"UPDATE {tableRef} SET {setClause} WHERE {whereClause};";
            var cmd = new SqlCommand(Join(sql, moduleSql));
            cmd.Parameters.AddRange(propertyInfo.data.Select(kv => new SqlParameter($"@{kv.Key}", kv.Value ?? DBNull.Value)
            { IsNullable = table.ColumnLookup[kv.Key].IsNullable }).ToArray());
            await ExecuteNonQuery(conFactory, cmd);
            return propertyInfo.keyData.Values.First();
        }

        private async Task<object?> InsertObject(IResolveFieldContext context, IDbTable table, IMutationModules modules, IDbModel model,
            IDbConnFactory conFactory, ISqlDialect dialect, string parameterName = "insert")
        {
            var data = context.GetArgument<Dictionary<string, object?>>(parameterName);
            var moduleSql = modules.Insert(data, table, context.UserContext, model);
            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var columns = string.Join(",", data.Keys.Select(k => dialect.EscapeIdentifier(k)));
            var values = string.Join(",", data.Keys.Select(k => $"@{k}"));
            var sql = $"INSERT INTO {tableRef}({columns}) VALUES({values});SELECT SCOPE_IDENTITY() ID;";
            var cmd = new SqlCommand(Join(sql, moduleSql));
            cmd.Parameters.AddRange(data.Select(kv => new SqlParameter($"@{kv.Key}", kv.Value ?? DBNull.Value)
            { IsNullable = table.ColumnLookup[kv.Key].IsNullable }).ToArray());
            return HandleDecimals(await ExecuteScalar(conFactory, cmd));
        }

        private async ValueTask<object?> ExecuteScalar(IDbConnFactory connFactory, SqlCommand cmd)
        {
            await using var conn = connFactory.GetConnection();
            cmd.Connection = conn;
            try
            {
                await conn.OpenAsync();
                var result = await cmd.ExecuteScalarAsync();
                return result;
            }
            catch (Exception ex)
            {
                throw new ExecutionError(ex.Message, ex);
            }
        }
        private async ValueTask<int> ExecuteNonQuery(IDbConnFactory connFactory, SqlCommand cmd)
        {
            await using var conn = connFactory.GetConnection();
            cmd.Connection = conn;
            try
            {
                await conn.OpenAsync();
                var result = await cmd.ExecuteNonQueryAsync();
                return result;
            }
            catch (Exception ex)
            {
                throw new ExecutionError(ex.Message, ex);
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

        public object? HandleDecimals(object? obj)
        {
            if (obj == null)
                return obj;
            return obj switch
            {
                decimal d => Convert.ToInt64(d),
                _ => obj,
            };
        }
    }
}
