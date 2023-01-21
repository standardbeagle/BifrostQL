using GraphQL.Resolvers;
using GraphQL;
using GraphQLParser.AST;
using BifrostQL.Model;
using BifrostQL.QueryModel;
using System.Data.SqlClient;
using GraphQL.Types;
using GraphQL.Validation.Complexity;
using System.Drawing;
using GraphQL.DataLoader;
using System.Collections.Concurrent;
using BifrostQL.Resolvers;
using Microsoft.Extensions.DependencyInjection;
using BifrostQL.Core.QueryModel;

namespace BifrostQL
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
        private readonly IDbModel _dbModel;

        public TableReaderFactory(IDbModel dbModel) {
            _dbModel = dbModel;
        }
        public async ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            if (context.SubFields == null)
                throw new ArgumentNullException(nameof(context) + ".SubFields");

            _tables ??= await GetTables(context);
            var alias = context.FieldAst.Alias?.Name.StringValue;
            var graphqlName = context.FieldAst.Name.StringValue;
            var table = _tables.First(t => (alias != null && t.Alias == alias) || t.GraphQlName == graphqlName);
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

        private IDictionary<string, (IDictionary<string, int> index, IList<object?[]> data)> LoadData(TableSqlData table, IDbConnFactory connFactory)
        {
            var sqlList = table.ToSql(_dbModel);
            var resultNames = sqlList.Keys.ToArray();
            string sql = string.Join(";\r\n", sqlList.Values);

            using var conn = connFactory.GetConnection();
            conn.Open();
            var command = new SqlCommand(sql, conn);
            using var reader = command.ExecuteReader();
            var results = new Dictionary<string, (IDictionary<string, int> index, IList<object?[]> data)>();
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
