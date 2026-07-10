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

        /// <summary>
        /// Executes a pre-built GROUP BY aggregate query (see
        /// <see cref="QueryModel.GqlObjectQuery.GroupedAggregate"/>), applying the same
        /// filter transformers (tenant isolation, soft-delete) as row queries, and
        /// returns one <see cref="AggregateResultRow"/> per group.
        /// </summary>
        public ValueTask<IReadOnlyList<AggregateResultRow>> ResolveAggregateAsync(IBifrostFieldContext context, IDbTable table, GqlObjectQuery query);
    }

    public sealed class SqlExecutionManager : ISqlExecutionManager
    {
        private Task<List<GqlObjectQuery>>? _objectQueriesTask;
        private readonly object _objectQueriesLock = new();
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

            // Sibling root fields resolve in parallel and share this manager, so a
            // bare `??=` could parse the document (and mutate SqlContext state) twice
            // under a race. Guard the one-time init with a double-checked lock; the
            // parse is the same for every field of one request, so the first computed
            // Task is reused by all.
            if (_objectQueriesTask == null)
            {
                lock (_objectQueriesLock)
                {
                    _objectQueriesTask ??= GetAllObjectQueries(context);
                }
            }
            var objectQueries = await _objectQueriesTask;
            var table = objectQueries.FirstOrDefault(t => (alias != null && t.Alias == alias) || (alias == null && t.GraphQlName == graphqlName))
                ?? throw new BifrostExecutionError(
                    $"No parsed query node matched root field '{alias ?? graphqlName}'.");

            var userContext = context.UserContext;

            // Notify Parsed phase
            await NotifyAsync(QueryPhase.Parsed, dbTable, context, graphqlName, userContext, table.QueryType);

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
            await NotifyAsync(QueryPhase.Transformed, dbTable, context, graphqlName, userContext, table.QueryType,
                filter: table.Filter);

            var bifrost = new BifrostContextAdapter(context);
            var conFactory = bifrost.ConnFactory;

            var sw = Stopwatch.StartNew();
            var (data, sql) = await LoadDataParameterizedAsync(table, conFactory, context.CancellationToken);
            await ApplyProviderComputedColumnsAsync(table, data, context, conFactory);
            sw.Stop();

            // Notify AfterExecute phase with timing data
            if (_observers is { Count: > 0 })
            {
                var totalRows = data.Values.Sum(v => v.data.Count);
                await NotifyAsync(QueryPhase.AfterExecute, dbTable, context, graphqlName, userContext, table.QueryType,
                    filter: table.Filter, sql: sql, rowCount: totalRows, duration: sw.Elapsed);
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

        public async ValueTask<IReadOnlyList<AggregateResultRow>> ResolveAggregateAsync(IBifrostFieldContext context, IDbTable dbTable, GqlObjectQuery query)
        {
            var grouped = query.GroupedAggregate
                ?? throw new BifrostExecutionError("ResolveAggregateAsync requires a grouped-aggregate query.");

            // Apply the same fail-closed filter transformers row queries get, into a
            // per-call context overlay (sibling root fields resolve in parallel; the
            // shared UserContext is not thread-safe). Tenant/soft-delete scope thus
            // constrains the grouped rows before aggregation.
            var scopedContext = new Dictionary<string, object?>(context.UserContext);
            Modules.ModuleApiRegistry.CaptureQueryArguments(context, dbTable, scopedContext);
            _transformerService.ApplyTransformers(query, _dbModel, scopedContext);
            ApplyEnumFilterRewrite(query);

            var bifrost = new BifrostContextAdapter(context);
            var (data, _) = await LoadDataParameterizedAsync(query, bifrost.ConnFactory, context.CancellationToken);

            if (!data.TryGetValue(query.KeyName, out var tableData))
                return Array.Empty<AggregateResultRow>();

            return BuildAggregateRows(grouped, tableData.index, tableData.data);
        }

        /// <summary>
        /// Shapes the flat grouped result set into <see cref="AggregateResultRow"/>s.
        /// Group-key values are read by column GraphQL name; <c>_count</c> by its
        /// alias; each op group's values by their per-column aliases. Aggregate values
        /// are coerced to double so the <c>Float</c>-typed value fields serialize
        /// regardless of the provider's numeric CLR type (decimal/long/…).
        /// </summary>
        private static IReadOnlyList<AggregateResultRow> BuildAggregateRows(
            GroupedAggregate grouped,
            IDictionary<string, int> index,
            IList<object?[]> rows)
        {
            var valuesByOp = grouped.ValueColumns
                .GroupBy(v => v.OpGroup)
                .ToList();

            var result = new List<AggregateResultRow>(rows.Count);
            foreach (var row in rows)
            {
                var groupValues = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var group in grouped.GroupColumns)
                    groupValues[group.GraphQlName] = ReadCell(index, row, group.GraphQlName);

                int? count = null;
                if (grouped.IncludeCount && ReadCell(index, row, GroupedAggregate.CountAlias) is { } countValue)
                    count = Convert.ToInt32(countValue);

                var ops = new Dictionary<string, AggregateFields>(StringComparer.Ordinal);
                foreach (var opGroup in valuesByOp)
                {
                    var opValues = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var value in opGroup)
                        opValues[value.Column.GraphQlName] = ToDouble(ReadCell(index, row, value.SqlAlias));
                    ops[opGroup.Key] = new AggregateFields { Values = opValues };
                }

                result.Add(new AggregateResultRow { GroupValues = groupValues, Count = count, Ops = ops });
            }
            return result;
        }

        private static object? ReadCell(IDictionary<string, int> index, object?[] row, string alias)
            => index.TryGetValue(alias, out var ordinal) && ordinal < row.Length
                ? ReaderEnum.DbConvert(row[ordinal])
                : null;

        private static object? ToDouble(object? value)
            => value == null ? null : Convert.ToDouble(value);

        /// <summary>
        /// Fires one query-observer phase notification. Centralizes the
        /// <c>_observers is { Count: &gt; 0 }</c> guard, the path-string computation
        /// (falling back to the field name when the path is empty), and the shared
        /// <see cref="QueryObserverContext"/> shape, so the per-phase call sites carry
        /// only the fields that phase contributes (filter/sql/rowCount/duration are
        /// null for the phases that don't provide them — matching the record's defaults).
        /// </summary>
        private async ValueTask NotifyAsync(
            QueryPhase phase,
            IDbTable dbTable,
            IBifrostFieldContext context,
            string graphqlName,
            IDictionary<string, object?> userContext,
            QueryType queryType,
            TableFilter? filter = null,
            string? sql = null,
            int? rowCount = null,
            TimeSpan? duration = null)
        {
            if (_observers is not { Count: > 0 })
                return;

            var pathStr = string.Join(".", context.Path);
            await _observers.NotifyAsync(phase, new QueryObserverContext
            {
                Table = dbTable,
                Model = _dbModel,
                UserContext = userContext,
                QueryType = queryType,
                Path = pathStr.Length > 0 ? pathStr : graphqlName,
                Filter = filter,
                Sql = sql,
                RowCount = rowCount,
                Duration = duration,
            });
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

        private async Task<(IDictionary<string, (IDictionary<string, int> index, IList<object?[]> data)> results, string sql)> LoadDataParameterizedAsync(GqlObjectQuery query, IDbConnFactory connFactory, CancellationToken cancellationToken)
        {
            var dialect = connFactory.Dialect;
            var parameters = new QueryModel.SqlParameterCollection();
            var sqlByName = new Dictionary<string, ParameterizedSql>();
            query.AddSqlParameterized(_dbModel, dialect, sqlByName, parameters);

            // Materialize to an ordered list so the emitted statement order and the
            // positional result-set names come from ONE structural snapshot. Reading
            // .Keys and .Values as two independent Dictionary enumerations left the
            // reader's set-name alignment resting on an unspecified enumeration order.
            var sqlList = sqlByName.ToList();
            var resultNames = sqlList.Select(p => p.Key).ToArray();
            string sql = string.Join(";\r\n", sqlList.Select(p => p.Value.Sql));

            await using var conn = connFactory.GetConnection();
            try
            {
                await conn.OpenAsync(cancellationToken);
                await using var command = conn.CreateCommand();
                command.CommandText = sql;

                DbParameterBinder.AddExtraParameters(command, parameters.Parameters);

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var results = new Dictionary<string, (IDictionary<string, int> index, IList<object?[]> data)>();
                var resultIndex = 0;
                do
                {
                    var resultName = resultNames[resultIndex++];
                    var index = Enumerable.Range(0, reader.FieldCount).Select(i => (i, reader.GetName(i)))
                        .ToDictionary(x => x.Item2, x => x.i, StringComparer.OrdinalIgnoreCase);
                    var result = new List<object?[]>();
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var row = new object?[reader.FieldCount];
                        reader.GetValues(row!);
                        result.Add(row);
                    }

                    var currentResult = result.Count == 0 ?
                        (index, new List<object?[]>()) :
                        (index, result);
                    results.Add(resultName, currentResult);
                } while (await reader.NextResultAsync(cancellationToken));
                return (results, sql);
            }
            catch (OperationCanceledException)
            {
                // Propagate request aborts as-is so the pipeline can short-circuit;
                // wrapping them as execution errors would mask the cancellation.
                throw;
            }
            catch (Exception ex)
            {
                throw BifrostExecutionError.FromDatabaseException(ex);
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

                // The provider contract is per-row, so a large result set would
                // otherwise mean one sequential await per row (N+1). Compute rows
                // with bounded parallelism instead: a fixed pool of workers pulls
                // row indexes off a shared counter, capping concurrent provider/DB
                // calls while keeping every worker busy. Values land in a side
                // array (rows stay untouched during compute), then all rows are
                // expanded in one pass.
                var rowCount = tableData.data.Count;
                var values = await ComputeColumnValuesAsync(rowCount, async rowIndex =>
                {
                    var rowMap = ToRowMap(tableData.data[rowIndex], tableData.index, skipColumn: column.GraphQlDbName);
                    return await provider.ComputeAsync(new ComputedColumnContext
                    {
                        Model = _dbModel,
                        Table = query.DbTable,
                        Column = column.ComputedColumn,
                        Row = rowMap,
                        UserContext = context.UserContext,
                        Services = context.RequestServices,
                        ConnFactory = connFactory,
                    }, context.CancellationToken);
                }, context.CancellationToken);

                for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    var row = tableData.data[rowIndex];
                    var expanded = new object?[tableData.index.Count];
                    Array.Copy(row, expanded, row.Length);
                    expanded[newIndex] = values[rowIndex];
                    tableData.data[rowIndex] = expanded;
                }
            }
        }

        /// <summary>
        /// Computes one value per row with bounded parallelism: a fixed pool of at
        /// most <see cref="MaxComputeConcurrency"/> workers pulls row indexes off a
        /// shared counter and invokes <paramref name="computeFn"/>, capping concurrent
        /// provider/DB calls while keeping every worker busy. Results land in a side
        /// array indexed by row so the caller's rows stay untouched during compute.
        /// </summary>
        private static async Task<object?[]> ComputeColumnValuesAsync(
            int rowCount,
            Func<int, Task<object?>> computeFn,
            CancellationToken cancellationToken)
        {
            var values = new object?[rowCount];
            var workerCount = Math.Min(MaxComputeConcurrency, rowCount);
            if (workerCount > 0)
            {
                var nextRow = -1;
                var workers = new Task[workerCount];
                for (var w = 0; w < workerCount; w++)
                {
                    workers[w] = Task.Run(async () =>
                    {
                        int rowIndex;
                        while ((rowIndex = Interlocked.Increment(ref nextRow)) < rowCount)
                            values[rowIndex] = await computeFn(rowIndex);
                    }, cancellationToken);
                }
                await Task.WhenAll(workers);
            }
            return values;
        }

        /// <summary>
        /// Cap on concurrent per-row provider computations. Providers may run their
        /// own auxiliary queries (e.g. the EAV <c>_meta</c> provider), so unbounded
        /// parallelism could exhaust the connection pool or overwhelm the database.
        /// </summary>
        private const int MaxComputeConcurrency = 8;

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
