using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using GraphQL.Types;
using GraphQL;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BifrostQL.Core.Resolvers
{
    public interface ISqlExecutionManager
    {
        public ValueTask<object?> ResolveAsync(IResolveFieldContext context, IDbTable table);
    }

    public sealed class SqlExecutionManager : ISqlExecutionManager
    {
        private List<GqlObjectQuery>? _objectQueries = null;
        private readonly IDbModel _dbModel;
        private readonly ISchema _schema;

        public SqlExecutionManager(IDbModel dbModel, ISchema schema)
        {
            _dbModel = dbModel;
            _schema = schema;
        }
        public async ValueTask<object?> ResolveAsync(IResolveFieldContext context, IDbTable dbTable)
        {
            if (context.SubFields == null)
                throw new ArgumentNullException(nameof(context) + ".SubFields");

            _objectQueries ??= await GetAllObjectQueries(context);
            var alias = context.FieldAst.Alias?.Name.StringValue;
            var graphqlName = context.FieldAst.Name.StringValue;
            var table = _objectQueries.First(t => (alias != null && t.Alias == alias) || (alias == null && t.GraphQlName == graphqlName));
            var conFactory = (IDbConnFactory)(context.InputExtensions["connFactory"] ?? throw new InvalidDataException("connection factory is not configured"));

            var data = LoadData(table, conFactory);
            var count = data.First(kv => kv.Key == (table.KeyName +  "=>count")).Value.data[0][0] as int?;

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

        private async Task<List<GqlObjectQuery>> GetAllObjectQueries(IResolveFieldContext context)
        {
            var visitor = new SqlVisitor();
            var sqlContext = new SqlContext() { Variables = context.Variables };
            await visitor.VisitAsync(context.Document, sqlContext);

            var newTables = sqlContext.GetFinalQueries(_dbModel);
            return newTables;
        }

        private IDictionary<string, (IDictionary<string, int> index, IList<object?[]> data)> LoadData(GqlObjectQuery query, IDbConnFactory connFactory)
        {
            var sqlList = new Dictionary<string, string>();
            query.AddSql(_dbModel, sqlList);
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
