using GraphQL;
using GraphQL.Resolvers;
using BifrostQL.Model;
using Microsoft.Extensions.DependencyInjection;
using System.Data.SqlClient;
using BifrostQL.Core.Modules;
using System;

namespace BifrostQL.Resolvers
{
    public sealed class DbTableMutateResolver : IFieldResolver
    {
        public async ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            var conFactory = (IDbConnFactory)(context.InputExtensions["connFactory"] ?? throw new InvalidDataException("connection factory is not configured"));
            var model = (IDbModel)(context.InputExtensions["model"] ?? throw new InvalidDataException("database model is not configured"));
            var table = model.GetTable(context.FieldAst.Name.StringValue);
            var modules = context.RequestServices!.GetRequiredService<IMutationModules>();
            modules.OnSave(context);

            if (context.HasArgument("insert"))
            {
                var data = context.GetArgument<Dictionary<string, object?>>("insert");
                var moduleSql = modules.Insert(data, table, context.UserContext, model);
                var sql = $@"INSERT INTO [{table.TableSchema}].[{table.DbName}]([{string.Join("],[", data.Keys)}]) VALUES({string.Join(",", data.Keys.Select(k => $"@{k}"))});SELECT SCOPE_IDENTITY() ID;";
                var cmd = new SqlCommand(Join(sql, moduleSql));
                cmd.Parameters.AddRange(data.Select(kv => new SqlParameter($"@{kv.Key}", kv.Value ?? DBNull.Value) { IsNullable = table.ColumnLookup[kv.Key].IsNullable }).ToArray());
                return HandleDecimals(await ExecuteScalar(conFactory, cmd));
            }
            if (context.HasArgument("update"))
            {
                var baseData = context.GetArgument<Dictionary<string, object?>>("update") ?? new Dictionary<string, object?>();
                if (!baseData.Any())
                    return 0;

                var data = new Dictionary<string, object?>(baseData!, StringComparer.OrdinalIgnoreCase);
                var keyData = data.Where(d => table.ColumnLookup[d.Key].IsPrimaryKey).ToArray();
                if (keyData.Length == 0)
                    return 0;
                var standardData = data
                    .Where(d => table.ColumnLookup[d.Key].IsPrimaryKey == false)
                    .ToArray();


                var moduleSql = modules.Update(data, table, context.UserContext, model);
                var sql = $@"UPDATE [{table.TableSchema}].[{table.DbName}] 
                    SET {string.Join(",", standardData.Select(kv => $"[{kv.Key}]=@{kv.Key}"))}
                    WHERE {string.Join(" AND ", keyData.Select(kv => $"[{kv.Key}]=@{kv.Key}"))};";
                var cmd = new SqlCommand(Join(sql, moduleSql));
                cmd.Parameters.AddRange(data.Select(kv => new SqlParameter($"@{kv.Key}", kv.Value ?? DBNull.Value) { IsNullable = table.ColumnLookup[kv.Key].IsNullable }).ToArray());
                return await ExecuteNonQuery(conFactory, cmd);

            }
            if (context.HasArgument("delete"))
            {
                var data = context.GetArgument<Dictionary<string, object?>>("delete");
                if (!data.Any())
                    return 0;
                var moduleSql = modules.Delete(data, table, context.UserContext, model);
                var sql = $"DELETE FROM [{table.TableSchema}].[{table.DbName}] WHERE {string.Join(" AND ", data.Select(kv => $"[{kv.Key}]=@{kv.Key}"))};";
                var cmd = new SqlCommand(Join(sql, moduleSql));
                cmd.Parameters.AddRange(data.Select(kv => new SqlParameter($"@{kv.Key}", kv.Value ?? DBNull.Value)).ToArray());
                return await ExecuteNonQuery(conFactory, cmd);
            }
            return null;
        }

        public async ValueTask<object?> ExecuteSql(IDbConnFactory connFactory, SqlCommand cmd, Func<SqlDataReader, ValueTask<object?>> read)
        {
            using var conn = connFactory.GetConnection();
            cmd.Connection = conn;
            await conn.OpenAsync();
            using var reader = await cmd.ExecuteReaderAsync();
            return await read(reader);
        }
        public async ValueTask<object?> ExecuteScalar(IDbConnFactory connFactory, SqlCommand cmd)
        {
            using var conn = connFactory.GetConnection();
            cmd.Connection = conn;
            await conn.OpenAsync();
            var result = await cmd.ExecuteScalarAsync();
            return result;
        }
        public async ValueTask<int> ExecuteNonQuery(IDbConnFactory connFactory, SqlCommand cmd)
        {
            using var conn = connFactory.GetConnection();
            cmd.Connection = conn;
            await conn.OpenAsync();
            var result = await cmd.ExecuteNonQueryAsync();
            return result;

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
