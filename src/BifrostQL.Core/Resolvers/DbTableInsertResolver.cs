using GraphQL;
using GraphQL.Resolvers;
using BifrostQL.Model;
using Microsoft.Extensions.DependencyInjection;
using System.Data.SqlClient;

namespace BifrostQL.Resolvers
{
    public sealed class DbTableMutateResolver : IFieldResolver
    {
        public async ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            var conFactory = context.RequestServices!.GetRequiredService<IDbConnFactory>();
            var model = context.RequestServices!.GetRequiredService<IDbModel>();
            var table = model.GetTable(context.FieldAst.Name.StringValue);

            if (context.HasArgument("insert"))
            {
                var data = context.GetArgument<Dictionary<string, object?>>("insert");
                var sql = $@"INSERT INTO [{table.TableSchema}].[{table.TableName}]([{string.Join("],[", data.Keys)}]) VALUES({string.Join(",", data.Keys.Select(k => $"@{k}"))});SELECT SCOPE_IDENTITY() ID;";
                var cmd = new SqlCommand(sql);
                cmd.Parameters.AddRange(data.Select(kv => new SqlParameter($"@{kv.Key}", kv.Value)).ToArray());
                return HandleDecimals(await ExecuteScalar(conFactory, cmd));
            }
            if (context.HasArgument("update"))
            {
                var data = context.GetArgument<Dictionary<string, object?>>("update");
                if (!data.Any())
                    return 0;
                var keyData = data.Where(d => table.ColumnLookup[d.Key].IsPrimaryKey).ToArray();
                if (keyData.Length == 0)
                    return 0;
                var standardData = data.Where(d => table.ColumnLookup[d.Key].IsPrimaryKey == false).ToArray();


                var sql = $@"UPDATE [{table.TableSchema}].[{table.TableName}] 
                    SET {string.Join(",", standardData.Select(kv => $"[{kv.Key}]=@{kv.Key}"))}
                    WHERE {string.Join(" AND ", keyData.Select(kv => $"[{kv.Key}]=@{kv.Key}"))};";
                var cmd = new SqlCommand(sql);
                cmd.Parameters.AddRange(data.Select(kv => new SqlParameter($"@{kv.Key}", kv.Value)).ToArray());
                return await ExecuteNonQuery(conFactory, cmd);

            }
            if (context.HasArgument("delete"))
            {
                var data = context.GetArgument<Dictionary<string, object?>>("delete");
                if (!data.Any())
                    return 0;
                var sql = $"DELETE FROM [{table.TableSchema}].[{table.TableName}] WHERE {string.Join(" AND ", data.Select(kv => $"[{kv.Key}]=@{kv.Key}"))};";
                var cmd = new SqlCommand(sql);
                cmd.Parameters.AddRange(data.Select(kv => new SqlParameter($"@{kv.Key}", kv.Value)).ToArray());
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
