using System.Data.SqlClient;
using System.Reflection;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
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
            var table = _table;
            var modules = context.RequestServices!.GetRequiredService<IMutationModules>();
            modules.OnSave(context);

            if (context.HasArgument("insert"))
            {
                return await InsertObject(context, table, modules, model, conFactory);
            }
            if (context.HasArgument("update"))
            {
                return await UpdateObject(context, table, modules, model, conFactory);
            }
            if (context.HasArgument("delete"))
            {
                return await DeleteObject(context, modules, table, model, conFactory);
            }

            if (context.HasArgument("upsert"))
            {
                var propertyInfo = GetPropertyInfo(context, _table, "upsert");
                if (!propertyInfo.data.Any())
                    return 0;
                if (propertyInfo.keyData.Any())
                    return await UpdateObject(context, table, modules, model, conFactory, "upsert");

                return await InsertObject(context, table, modules, model, conFactory, "upsert");
            }
            return null;
        }

        private (Dictionary<string, object?> data, Dictionary<string, object?> keyData, Dictionary<string, object?> standardData) GetPropertyInfo(IResolveFieldContext context, IDbTable table, string parameterName)
        {
            var baseData = context.GetArgument<Dictionary<string, object?>>(parameterName) ?? new();

            var data = new Dictionary<string, object?>(baseData!, StringComparer.OrdinalIgnoreCase);
            var keyData = data.Where(d => table.ColumnLookup[d.Key].IsPrimaryKey)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            var standardData = data
                .Where(d => table.ColumnLookup[d.Key].IsPrimaryKey == false)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            return (data, keyData, standardData);

        }

        private async Task<object?> DeleteObject(IResolveFieldContext context, IMutationModules modules, IDbTable table, IDbModel model,
            IDbConnFactory conFactory)
        {
            var data = context.GetArgument<Dictionary<string, object?>>("delete");
            if (!data.Any())
                return 0;
            var moduleSql = modules.Delete(data, table, context.UserContext, model);
            var sql =
                $"DELETE FROM [{table.TableSchema}].[{table.DbName}] WHERE {string.Join(" AND ", data.Select(kv => $"[{kv.Key}]=@{kv.Key}"))};";
            var cmd = new SqlCommand(Join(sql, moduleSql));
            cmd.Parameters.AddRange(data.Select(kv => new SqlParameter($"@{kv.Key}", kv.Value ?? DBNull.Value)).ToArray());
            return await ExecuteNonQuery(conFactory, cmd);
        }

        private async Task<object?> UpdateObject(IResolveFieldContext context, IDbTable table, IMutationModules modules, IDbModel model,
            IDbConnFactory conFactory, string parameterName = "update")
        {
            var propertyInfo = GetPropertyInfo(context, table, parameterName);
            if (!propertyInfo.data.Any())
                return 0;

            if (!propertyInfo.keyData.Any())
                return 0;

            var moduleSql = modules.Update(propertyInfo.data, table, context.UserContext, model);
            var sql = $@"UPDATE [{table.TableSchema}].[{table.DbName}] 
                    SET {string.Join(",", propertyInfo.standardData.Select(kv => $"[{kv.Key}]=@{kv.Key}"))}
                    WHERE {string.Join(" AND ", propertyInfo.keyData.Select(kv => $"[{kv.Key}]=@{kv.Key}"))};";
            var cmd = new SqlCommand(Join(sql, moduleSql));
            cmd.Parameters.AddRange(propertyInfo.data.Select(kv => new SqlParameter($"@{kv.Key}", kv.Value ?? DBNull.Value)
            { IsNullable = table.ColumnLookup[kv.Key].IsNullable }).ToArray());
            await ExecuteNonQuery(conFactory, cmd);
            return propertyInfo.keyData.Values.First();
        }

        private async Task<object?> InsertObject(IResolveFieldContext context, IDbTable table, IMutationModules modules, IDbModel model,
            IDbConnFactory conFactory, string parameterName = "insert")
        {
            var data = context.GetArgument<Dictionary<string, object?>>(parameterName);
            var moduleSql = modules.Insert(data, table, context.UserContext, model);
            var sql =
                $@"INSERT INTO [{table.TableSchema}].[{table.DbName}]([{string.Join("],[", data.Keys)}]) VALUES({string.Join(",", data.Keys.Select(k => $"@{k}"))});SELECT SCOPE_IDENTITY() ID;";
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
