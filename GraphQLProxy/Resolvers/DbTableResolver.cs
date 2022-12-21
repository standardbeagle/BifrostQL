using GraphQL.Resolvers;
using GraphQL;
using GraphQLParser.AST;
using GraphQLProxy.Model;
using GraphQLProxy.QueryModel;
using System.Data.SqlClient;
using GraphQL.Types;
using GraphQL.Validation.Complexity;
using System.Drawing;
using GraphQL.DataLoader;
using System.Collections.Concurrent;
using GraphQLProxy.Resolvers;

namespace GraphQLProxy
{
    public interface IDbTableResolver : IFieldResolver
    {

    }

    public class DbTableResolver : IDbTableResolver
    {
        public DbTableResolver()
        {
        }
        public ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            var factory = context.RequestServices!.GetRequiredService<ITableReaderFactory>();
            return factory.ResolveAsync(context);
        }
    }

    class TableResult
    {
        public int? Total { get; set; }
        public int? Offset { get; set; }
        public int? Limit { get; set; }
        public object? Data { get; set; }
    }


    public interface ITableReaderFactory
    {
        public ValueTask<object?> ResolveAsync(IResolveFieldContext context);
    }
    public sealed class TableReaderFactory : ITableReaderFactory
    {
        private List<TableSqlData>? _tables = null;
        public async ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            if (context.SubFields == null)
                throw new ArgumentNullException(nameof(context) + ".SubFields");

            _tables ??= await GetTables(context);
            var table = _tables.First(t => t.Alias == (context.FieldAst.Alias?.Name.StringValue ?? "") && t.TableName == context.FieldAst.Name.StringValue);
            var data = LoadData(table, context.RequestServices!.GetRequiredService<IDbConnFactory>());
            var count = data.First(kv => kv.Key.EndsWith("count")).Value.data[0][0] as int?;

            if (table.IncludeResult)
            {
                return new TableResult
                {
                    Total = count ?? 0,
                    Offset = table.Offset,
                    Limit = table.Limit,
                    Data = new ReaderEnum(table, data)
                };
            }
            return new ReaderEnum(table, data);
        }

        private static async Task<List<TableSqlData>> GetTables(IResolveFieldContext context)
        {
            var visitor = new SqlVisitor();
            var sqlContext = new SqlContext() { Variables = context.Variables };
            await visitor.VisitAsync(context.Document, sqlContext);

            var newTables = sqlContext.GetFinalTables();
            return newTables;
        }

        private Dictionary<string, (Dictionary<string, int> index, List<object?[]> data)> LoadData(TableSqlData table, IDbConnFactory connFactory)
        {
            var sqlList = table.ToSql();
            var resultNames = sqlList.Keys.ToArray();
            string sql = string.Join(";\r\n", sqlList.Values);

            using var conn = connFactory.GetConnection();
            conn.Open();
            var command = new SqlCommand(sql, conn);
            using var reader = command.ExecuteReader();
            var results = new Dictionary<string, (Dictionary<string, int> index, List<object?[]> data)>();
            var resultIndex = 0;
            do
            {
                var resultName = resultNames[resultIndex++];
                var index = Enumerable.Range(0, reader.FieldCount).Select(i => (i, reader.GetName(i))).ToDictionary(x => x.Item2, x => x.i, StringComparer.OrdinalIgnoreCase);
                var result = new List<object?[]>();
                while (reader.Read())
                {
                    var row = new object?[reader.FieldCount];
                    reader.GetValues(row);
                    result.Add(row);
                }
                if (result.Count == 0)
                    results.Add(resultName, (index, new List<object?[]>()));
                else
                    results.Add(resultName, (index, result));
            } while (reader.NextResult());
            return results;
        }

    }
}
