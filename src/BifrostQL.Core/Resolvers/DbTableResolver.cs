using System.Data.SqlClient;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Model;
using GraphQL;
using GraphQL.Resolvers;

namespace BifrostQL.Core.Resolvers
{
    public interface IDbTableResolver : IFieldResolver
    {

    }

    public class DbTableResolver : IDbTableResolver
    {
        private readonly IDbTable _table;
        public DbTableResolver(IDbTable table)
        {
            _table = table;
        }
        public ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            var factory = (ITableReaderFactory)(context.InputExtensions["tableReaderFactory"] ?? throw new InvalidDataException("tableReaderFactory not configured"));
            return factory.ResolveAsync(context, _table);
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
        public ValueTask<object?> ResolveAsync(IResolveFieldContext context, IDbTable table);
    }

    public sealed class TableReaderFactory : ITableReaderFactory
    {
        private List<GqlObjectQuery>? _tables = null;
        private readonly IDbModel _dbModel;

        public TableReaderFactory(IDbModel dbModel)
        {
            _dbModel = dbModel;
        }
        public async ValueTask<object?> ResolveAsync(IResolveFieldContext context, IDbTable dbTable)
        {
            if (context.SubFields == null)
                throw new ArgumentNullException(nameof(context) + ".SubFields");

            _tables ??= await GetTables(context);
            var alias = context.FieldAst.Alias?.Name.StringValue;
            var graphqlName = context.FieldAst.Name.StringValue;
            var table = _tables.First(t => (alias != null && t.Alias == alias) || t.GraphQlName == graphqlName);
            //var table = _tables.First(t => t.TableName == dbTable.DbName);
            var conFactory = (IDbConnFactory)(context.InputExtensions["connFactory"] ?? throw new InvalidDataException("connection factory is not configured"));

            var data = LoadData(table, conFactory);
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

        private async Task<List<GqlObjectQuery>> GetTables(IResolveFieldContext context)
        {
            var visitor = new SqlVisitor();
            var sqlContext = new SqlContext() { Variables = context.Variables };
            await visitor.VisitAsync(context.Document, sqlContext);

            var newTables = sqlContext.GetFinalTables(_dbModel);
            return newTables;
        }

        private IDictionary<string, (IDictionary<string, int> index, IList<object?[]> data)> LoadData(GqlObjectQuery table, IDbConnFactory connFactory)
        {
            var sqlList = table.ToSql(_dbModel);
            var resultNames = sqlList.Keys.ToArray();
            string sql = string.Join(";\r\n", sqlList.Values);

            using var conn = connFactory.GetConnection();
            try
            {
                conn.Open();
                var command = new SqlCommand(sql, conn);
                using var reader = command.ExecuteReader();
                var results = new Dictionary<string, (IDictionary<string, int> index, IList<object?[]> data)>();
                var resultIndex = 0;
                do
                {
                    var resultName = resultNames[resultIndex++];
                    var index = Enumerable.Range(0, reader.FieldCount).Select(i => (i, reader.GetName(i)))
                        .ToDictionary(x => x.Item2, x => x.i, StringComparer.OrdinalIgnoreCase);
                    var result = new List<object?[]>();
                    while (reader.Read())
                    {
                        var row = new object?[reader.FieldCount];
                        reader.GetValues(row);
                        result.Add(row);
                    }

                    var currentResult = result.Count == 0 ?
                        (index, new List<object?[]>()) :
                        (index, result);
                    results.Add(resultName, currentResult);
                } while (reader.NextResult());
                return results;
            }
            catch (Exception ex)
            {
                throw new ExecutionError(ex.Message, ex);
            }
        }
    }
}
