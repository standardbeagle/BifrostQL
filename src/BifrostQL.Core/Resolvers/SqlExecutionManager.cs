using System.Data.Common;
using System.Diagnostics;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.QueryModel;
using GraphQL.Types;
using GraphQL.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
            // Capture module arguments (e.g. _includeDeleted, _onlyDeleted) into the
            // user context under table-scoped keys for the matching transformers.
            //
            // Write into a per-call copy, not the shared request UserContext:
            // GraphQL.NET resolves sibling root fields in parallel, so mutating
            // the shared (non-thread-safe) Dictionary from concurrent resolvers
            // races. The scoped keys are ephemeral to this node's transform pass,
            // so a private overlay is the correct scope regardless.
            var scopedContext = new Dictionary<string, object?>(userContext);
            Modules.ModuleApiRegistry.CaptureQueryArguments(context, dbTable, scopedContext);

            _transformerService.ApplyTransformers(table, _dbModel, scopedContext);

            // Rewrite enum-name filter operands to their stored DB values before
            // SQL is generated, for the root table and every nested join.
            ApplyEnumFilterRewrite(table);

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
            var (data, sql) = await LoadDataParameterizedAsync(table, conFactory);
            await ApplyProviderComputedColumnsAsync(table, data, context, conFactory);
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

            var enumColumns = _dbModel.EnumColumns;
            var logger = context.RequestServices?.GetService<ILogger<SqlExecutionManager>>();

            if (table.IncludeResult)
            {
                // The "=>count" result set exists only when IncludeResult is set.
                // Look it up defensively — an unconditional .First()/[0][0] threw
                // when the count row was absent or empty.
                var total = 0;
                if (data.TryGetValue(table.KeyName + "=>count", out var countEntry)
                    && countEntry.data.Count > 0 && countEntry.data[0].Length > 0
                    && countEntry.data[0][0] is { } countObj)
                {
                    total = Convert.ToInt32(countObj);
                }

                return new TableResult
                {
                    Total = total,
                    Offset = table.Offset,
                    Limit = table.Limit,
                    Data = new ReaderEnum(table, data, enumColumns, logger)
                };
            }
            return new ReaderEnum(table, data, enumColumns, logger);
        }

        /// <summary>
        /// Rewrites enum-name filter operands (e.g. <c>status: { _eq: ACTIVE }</c>) to
        /// their stored database values across the query tree — the root table and
        /// every nested join's connected table (<see cref="GqlObjectQuery.RecurseJoins"/>
        /// flattens the join tree). No-op when the model carries no enum columns.
        /// </summary>
        private void ApplyEnumFilterRewrite(GqlObjectQuery table)
        {
            if (_dbModel.EnumColumns is not { } enumCols)
                return;

            enumCols.RewriteFilterValues(table.Filter, table.DbTable.DbName);
            foreach (var join in table.RecurseJoins)
            {
                var connected = join.ConnectedTable;
                enumCols.RewriteFilterValues(connected.Filter, connected.DbTable.DbName);
            }
        }

        private async Task<List<GqlObjectQuery>> GetAllObjectQueries(IBifrostFieldContext context)
        {
            var visitor = new SqlVisitor();
            var sqlContext = new SqlContext() { Variables = (Variables)context.Variables };
            await visitor.VisitAsync((GraphQLParser.AST.GraphQLDocument)context.Document, sqlContext);

            var newTables = sqlContext.GetFinalQueries(_dbModel);
            return newTables;
        }

        private async Task<(IDictionary<string, (IDictionary<string, int> index, IList<object?[]> data)> results, string sql)> LoadDataParameterizedAsync(GqlObjectQuery query, IDbConnFactory connFactory)
        {
            var dialect = connFactory.Dialect;
            var parameters = new QueryModel.SqlParameterCollection();
            var sqlList = new Dictionary<string, ParameterizedSql>();
            query.AddSqlParameterized(_dbModel, dialect, sqlList, parameters);

            var resultNames = sqlList.Keys.ToArray();
            string sql = string.Join(";\r\n", sqlList.Values.Select(p => p.Sql));

            await using var conn = connFactory.GetConnection();
            try
            {
                await conn.OpenAsync();
                await using var command = conn.CreateCommand();
                command.CommandText = sql;

                DbParameterBinder.AddExtraParameters(command, parameters.Parameters);

                await using var reader = await command.ExecuteReaderAsync();
                var results = new Dictionary<string, (IDictionary<string, int> index, IList<object?[]> data)>();
                var resultIndex = 0;
                do
                {
                    var resultName = resultNames[resultIndex++];
                    var index = Enumerable.Range(0, reader.FieldCount).Select(i => (i, reader.GetName(i)))
                        .ToDictionary(x => x.Item2, x => x.i, StringComparer.OrdinalIgnoreCase);
                    var result = new List<object?[]>();
                    while (await reader.ReadAsync())
                    {
                        var row = new object?[reader.FieldCount];
                        reader.GetValues(row!);
                        result.Add(row);
                    }

                    var currentResult = result.Count == 0 ?
                        (index, new List<object?[]>()) :
                        (index, result);
                    results.Add(resultName, currentResult);
                } while (await reader.NextResultAsync());
                return (results, sql);
            }
            catch (Exception ex)
            {
                throw new BifrostExecutionError(ex.Message, ex);
            }
        }

        private async ValueTask ApplyProviderComputedColumnsAsync(
            GqlObjectQuery query,
            IDictionary<string, (IDictionary<string, int> index, IList<object?[]> data)> results,
            IBifrostFieldContext context,
            IDbConnFactory connFactory)
        {
            var providers = context.RequestServices?.GetService<IComputedColumnProviders>() ?? ComputedColumnProviders.Empty;
            await ApplyProviderComputedColumnsForQueryAsync(query, query.KeyName, results, providers, context, connFactory);

            foreach (var join in query.RecurseJoins)
                await ApplyProviderComputedColumnsForQueryAsync(join.ConnectedTable, join.JoinName, results, providers, context, connFactory);
        }

        private async ValueTask ApplyProviderComputedColumnsForQueryAsync(
            GqlObjectQuery query,
            string resultName,
            IDictionary<string, (IDictionary<string, int> index, IList<object?[]> data)> results,
            IComputedColumnProviders providers,
            IBifrostFieldContext context,
            IDbConnFactory connFactory)
        {
            var providerColumns = query.ScalarColumns
                .Where(c => c.ComputedColumn is { Kind: ComputedColumnKind.Provider })
                .ToArray();
            if (providerColumns.Length == 0)
                return;

            if (!results.TryGetValue(resultName, out var tableData))
                return;

            foreach (var column in providerColumns)
            {
                if (!providers.TryGet(column.ComputedColumn!.ExpressionOrProvider, out var provider))
                    throw new BifrostExecutionError($"Computed column provider '{column.ComputedColumn.ExpressionOrProvider}' is not registered.");

                if (tableData.index.ContainsKey(column.GraphQlDbName))
                    continue;

                var newIndex = tableData.index.Count;
                tableData.index[column.GraphQlDbName] = newIndex;

                for (var rowIndex = 0; rowIndex < tableData.data.Count; rowIndex++)
                {
                    var row = tableData.data[rowIndex];
                    var rowMap = ToRowMap(row, tableData.index, skipColumn: column.GraphQlDbName);
                    var value = await provider.ComputeAsync(new ComputedColumnContext
                    {
                        Model = _dbModel,
                        Table = query.DbTable,
                        Column = column.ComputedColumn,
                        Row = rowMap,
                        UserContext = context.UserContext,
                        Services = context.RequestServices,
                        ConnFactory = connFactory,
                    });

                    var expanded = new object?[tableData.index.Count];
                    Array.Copy(row, expanded, row.Length);
                    expanded[newIndex] = value;
                    tableData.data[rowIndex] = expanded;
                }
            }
        }

        private static IReadOnlyDictionary<string, object?> ToRowMap(
            object?[] row,
            IDictionary<string, int> index,
            string skipColumn)
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in index)
            {
                if (string.Equals(kv.Key, skipColumn, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (kv.Value >= row.Length)
                    continue;
                result[kv.Key] = ReaderEnum.DbConvert(row[kv.Value]);
            }
            return result;
        }
    }
}
