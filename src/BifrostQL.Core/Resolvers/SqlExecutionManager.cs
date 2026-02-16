using System.Data.Common;
using System.Diagnostics;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using GraphQL.Types;
using GraphQL.Validation;

namespace BifrostQL.Core.Resolvers
{
    public interface ISqlExecutionManager
    {
        public ValueTask<object?> ResolveAsync(IBifrostFieldContext context, IDbTable table);
    }

    public sealed class SqlExecutionManager : ISqlExecutionManager
    {
        private List<GqlObjectQuery>? _objectQueries = null;
        private readonly IDbModel _dbModel;
        private readonly ISchema _schema;
        private readonly IQueryTransformerService _transformerService;
        private readonly IQueryObservers? _observers;

        public SqlExecutionManager(IDbModel dbModel, ISchema schema)
            : this(dbModel, schema, NullQueryTransformerService.Instance)
        {
        }

        public SqlExecutionManager(IDbModel dbModel, ISchema schema, IQueryTransformerService transformerService, IQueryObservers? observers = null)
        {
            _dbModel = dbModel;
            _schema = schema;
            _transformerService = transformerService;
            _observers = observers;
        }
        public async ValueTask<object?> ResolveAsync(IBifrostFieldContext context, IDbTable dbTable)
        {
            if (!context.HasSubFields)
                throw new ArgumentNullException(nameof(context) + ".SubFields");

            var alias = context.FieldAlias;
            var graphqlName = context.FieldName;

            _objectQueries ??= await GetAllObjectQueries(context);
            var table = _objectQueries.First(t => (alias != null && t.Alias == alias) || (alias == null && t.GraphQlName == graphqlName));

            var userContext = context.UserContext;

            // Notify Parsed phase
            if (_observers is { Count: > 0 })
            {
                var pathStr = string.Join(".", context.Path);
                await _observers.NotifyAsync(QueryPhase.Parsed, new QueryObserverContext
                {
                    Table = dbTable,
                    Model = _dbModel,
                    UserContext = userContext,
                    QueryType = table.QueryType,
                    Path = pathStr.Length > 0 ? pathStr : graphqlName,
                });
            }

            // Apply filter transformers (tenant isolation, soft-delete, etc.)
            // Pass _includeDeleted argument to UserContext for SoftDeleteFilterTransformer
            if (context.HasArgument("_includeDeleted") && context.GetArgument<bool>("_includeDeleted"))
            {
                userContext[Modules.SoftDeleteFilterTransformer.IncludeDeletedKey] = true;
            }

            _transformerService.ApplyTransformers(table, _dbModel, userContext);

            // Notify Transformed phase
            if (_observers is { Count: > 0 })
            {
                var pathStr = string.Join(".", context.Path);
                await _observers.NotifyAsync(QueryPhase.Transformed, new QueryObserverContext
                {
                    Table = dbTable,
                    Model = _dbModel,
                    UserContext = userContext,
                    QueryType = table.QueryType,
                    Path = pathStr.Length > 0 ? pathStr : graphqlName,
                    Filter = table.Filter,
                });
            }

            var bifrost = new BifrostContextAdapter(context);
            var conFactory = bifrost.ConnFactory;

            var sw = Stopwatch.StartNew();
            var (data, sql) = LoadDataParameterized(table, conFactory);
            sw.Stop();

            // Notify AfterExecute phase with timing data
            if (_observers is { Count: > 0 })
            {
                var pathStr = string.Join(".", context.Path);
                var totalRows = data.Values.Sum(v => v.data.Count);
                await _observers.NotifyAsync(QueryPhase.AfterExecute, new QueryObserverContext
                {
                    Table = dbTable,
                    Model = _dbModel,
                    UserContext = userContext,
                    QueryType = table.QueryType,
                    Path = pathStr.Length > 0 ? pathStr : graphqlName,
                    Filter = table.Filter,
                    Sql = sql,
                    RowCount = totalRows,
                    Duration = sw.Elapsed,
                });
            }

            var count = data.First(kv => kv.Key == (table.KeyName + "=>count")).Value.data[0][0] as int?;

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

        private async Task<List<GqlObjectQuery>> GetAllObjectQueries(IBifrostFieldContext context)
        {
            var visitor = new SqlVisitor();
            var sqlContext = new SqlContext() { Variables = (Variables)context.Variables };
            await visitor.VisitAsync((GraphQLParser.AST.GraphQLDocument)context.Document, sqlContext);

            var newTables = sqlContext.GetFinalQueries(_dbModel);
            return newTables;
        }

        private (IDictionary<string, (IDictionary<string, int> index, IList<object?[]> data)> results, string sql) LoadDataParameterized(GqlObjectQuery query, IDbConnFactory connFactory)
        {
            var dialect = connFactory.Dialect;
            var parameters = new QueryModel.SqlParameterCollection();
            var sqlList = new Dictionary<string, ParameterizedSql>();
            query.AddSqlParameterized(_dbModel, dialect, sqlList, parameters);

            var resultNames = sqlList.Keys.ToArray();
            string sql = string.Join(";\r\n", sqlList.Values.Select(p => p.Sql));

            using var conn = connFactory.GetConnection();
            try
            {
                conn.Open();
                var command = conn.CreateCommand();
                command.CommandText = sql;

                // Add all parameters
                foreach (var param in parameters.Parameters)
                {
                    var dbParam = command.CreateParameter();
                    dbParam.ParameterName = param.Name;
                    dbParam.Value = param.Value ?? DBNull.Value;
                    if (param.DbType != null)
                    {
                        dbParam.DbType = (System.Data.DbType)Enum.Parse(typeof(System.Data.DbType), param.DbType);
                    }
                    command.Parameters.Add(dbParam);
                }

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
                return (results, sql);
            }
            catch (Exception ex)
            {
                throw new BifrostExecutionError(ex.Message, ex);
            }
        }
    }
}
