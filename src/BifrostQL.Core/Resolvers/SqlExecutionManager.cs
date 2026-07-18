using System.Data.Common;
using System.Diagnostics;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.Observers;
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

        /// <summary>
        /// Executes a PIVOT: discovers the pivot column's distinct values within the
        /// caller's scope, then cross-tabulates. The same filter transformers (tenant
        /// isolation, soft-delete, policy row/column scope) as row queries constrain
        /// BOTH the distinct-value discovery and the pivot, so cross-tenant column
        /// headers and cell values can never leak. Returns the JSON payload
        /// <c>{ pivotColumn, rowKeys, columns, rows }</c>.
        /// </summary>
        public ValueTask<IReadOnlyDictionary<string, object?>> ResolvePivotAsync(IBifrostFieldContext context, IDbTable table, PivotRequest request);

        /// <summary>
        /// Executes a pre-built trail read query (the generated <c>&lt;table&gt;History</c>
        /// field) against <paramref name="trackedTable"/>'s resolved history target.
        /// The entity discriminator (<c>entity = schema.table</c>) and, for a
        /// tenant-filtered tracked table, the caller's tenant predicate on the
        /// materialized scope column are applied HERE, unconditionally, before the
        /// standard filter transformer pass and SQL generation — so no resolver or
        /// argument shape can read another table's trail rows through a shared history
        /// table, and tenant scoping fails closed (no claim ⇒ zero rows).
        /// </summary>
        public ValueTask<object?> ResolveHistoryTrailAsync(IBifrostFieldContext context, IDbTable trackedTable, GqlObjectQuery query);

        /// <summary>
        /// Executes a programmatic (adapter-built) read query with no GraphQL
        /// document: the transformer pipeline — tenant isolation, soft-delete,
        /// policy row scope, and column read guards — is applied here,
        /// unconditionally, before parameterized SQL generation. This is the
        /// single execution seam for <see cref="IQueryIntentExecutor"/>; keeping
        /// transformer application inside the manager means an adapter cannot
        /// reach SQL without it. Read-only: <see cref="GqlObjectQuery"/> emits
        /// SELECT statements exclusively. Grouped-aggregate intents
        /// (<see cref="GqlObjectQuery.GroupedAggregate"/>) are supported: the
        /// same transformer pass constrains the WHERE before grouping, and the
        /// flat grouped result set (group keys + aggregate aliases) comes back
        /// as <see cref="QueryIntentResult.Rows"/>. Encrypted columns are
        /// projected through the same decrypt/mask policy as GraphQL reads
        /// (built from <paramref name="keyManager"/> and the caller's roles);
        /// with no key manager they redact rather than leak ciphertext.
        /// </summary>
        public ValueTask<QueryIntentResult> ExecuteIntentAsync(GqlObjectQuery query, IDictionary<string, object?> userContext, IDbConnFactory connFactory, CancellationToken cancellationToken = default, BifrostQL.Core.Crypto.EnvelopeKeyManager? keyManager = null);
    }

    public sealed class SqlExecutionManager : ISqlExecutionManager
    {
        private Task<List<GqlObjectQuery>>? _objectQueriesTask;
        private readonly object _objectQueriesLock = new();
        private readonly IDbModel _dbModel;
        private readonly ISchema _schema;
        private readonly IQueryTransformerService _transformerService;
        private readonly IQueryObservers? _observers;
        private readonly EngineMetrics? _engineMetrics;

        public SqlExecutionManager(IDbModel dbModel, ISchema schema)
            : this(dbModel, schema, NullQueryTransformerService.Instance)
        {
        }

        public SqlExecutionManager(
            IDbModel dbModel,
            ISchema schema,
            IQueryTransformerService transformerService,
            IQueryObservers? observers = null,
            EngineMetrics? engineMetrics = null)
        {
            _dbModel = dbModel;
            _schema = schema;
            _transformerService = transformerService;
            _observers = observers;
            _engineMetrics = engineMetrics;
        }

        // Engine self-metric wiring (Prometheus slice-5). The read-success + SQL-duration
        // instruments are fed by EngineMetricsQueryObserver on the AfterExecute phase; what the
        // observer seam cannot express — a request's error/denied OUTCOME (no error phase) and the
        // transformer-pipeline DURATION — is recorded directly here on the read execution path.
        // The scrape marks its own collection queries with ScrapeInternalContextKey; skipping them
        // keeps a scrape from measuring itself (criterion 3, no recursive measurement). Every record
        // method also gates on EngineMetrics.Enabled, so a host with no scrape surface pays nothing.
        private bool ShouldRecordEngineMetric(IDictionary<string, object?> userContext)
            => _engineMetrics is { Enabled: true }
               && !userContext.ContainsKey(EngineMetricsQueryObserver.ScrapeInternalContextKey);

        private void RecordReadOutcome(IDictionary<string, object?> userContext, EngineRequestOutcome outcome)
        {
            if (ShouldRecordEngineMetric(userContext))
                _engineMetrics!.RecordRequest(EngineOperation.Read, outcome);
        }

        private void RecordReadTransformerDuration(IDictionary<string, object?> userContext, long startTimestamp)
        {
            if (ShouldRecordEngineMetric(userContext))
                _engineMetrics!.RecordTransformerDuration(
                    EngineOperation.Read, Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
        }

        public async ValueTask<object?> ResolveAsync(IBifrostFieldContext context, IDbTable dbTable)
        {
            // Record the request OUTCOME on the way out: read-success is the observer's (AfterExecute),
            // but error/denied never reach that phase, so they are recorded here. A cancelled request
            // is neither — the caller went away, not a failed request.
            try
            {
                return await ResolveCoreAsync(context, dbTable);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (BifrostExecutionError bex) when (bex.ErrorCode == BifrostExecutionError.AccessDeniedCode)
            {
                RecordReadOutcome(context.UserContext, EngineRequestOutcome.Denied);
                throw;
            }
            catch
            {
                RecordReadOutcome(context.UserContext, EngineRequestOutcome.Error);
                throw;
            }
        }

        private async ValueTask<object?> ResolveCoreAsync(IBifrostFieldContext context, IDbTable dbTable)
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

            var transformStart = Stopwatch.GetTimestamp();
            _transformerService.ApplyTransformers(table, _dbModel, scopedContext);
            RecordReadTransformerDuration(userContext, transformStart);

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

            // Decrypt/mask projector for field-level encryption. Built per query from the
            // caller's roles so each caller sees the plaintext or the masked value the
            // column's unmask-role grants them. Absent key manager ⇒ encrypted columns
            // read as redacted (never ciphertext). No-op for models with no encrypted
            // columns (Project passes non-encrypted columns through).
            var cryptoRead = new Modules.Crypto.CryptoReadProjector(
                _dbModel,
                context.RequestServices?.GetService<BifrostQL.Core.Crypto.EnvelopeKeyManager>(),
                ExtractRoles(userContext));

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
                    Data = new ReaderEnum(table, data, enumColumns, logger, cryptoRead)
                };
            }
            return new ReaderEnum(table, data, enumColumns, logger, cryptoRead);
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

        public async ValueTask<object?> ResolveHistoryTrailAsync(
            IBifrostFieldContext context, IDbTable trackedTable, GqlObjectQuery query)
        {
            // Re-resolve the history target through the same rule the change-history
            // writer uses. The resolver was wired against a resolved target at schema
            // build; a mismatch here means the model changed underneath the schema —
            // fail fast rather than read a table the writer is not writing.
            var config = Modules.History.HistoryConfig.FromTable(trackedTable);
            if (!config.RecordsHistory)
                throw new BifrostExecutionError(
                    $"Table '{trackedTable.TableSchema}.{trackedTable.DbName}' does not record change history; " +
                    "it has no trail to read.");

            var targetName = config.ResolveTargetName(_dbModel)
                ?? throw new BifrostExecutionError(
                    $"Table '{trackedTable.TableSchema}.{trackedTable.DbName}' records change history but no " +
                    $"'{Model.MetadataKeys.History.Table}' is configured on the table or on the model.");
            var target = ModelTableReference.Find(_dbModel, targetName)
                ?? throw new BifrostExecutionError(
                    $"The configured history table '{targetName}' was not found in the model.");
            if (!ReferenceEquals(target, query.DbTable))
                throw new BifrostExecutionError(
                    $"The trail read of '{trackedTable.TableSchema}.{trackedTable.DbName}' targets " +
                    $"'{query.DbTable.TableSchema}.{query.DbTable.DbName}', but its configured history table is " +
                    $"'{targetName}'.");

            // The entity discriminator: a shared history table holds many tables'
            // trails, and this field exposes exactly ONE of them. ANDed with the
            // client filter, so a filter can narrow within the tracked table's rows
            // but never widen to another table's.
            var forced = TableFilterFactory.Equals(
                target.DbName,
                Model.MetadataKeys.History.Column.Entity,
                $"{trackedTable.TableSchema}.{trackedTable.DbName}");

            // Tenant authorization, fail-closed. A tenant-filtered tracked table
            // materializes its tenant value into every trail row's scope column
            // (validated to exist at model load), so the trail is scoped by a plain
            // predicate on that column — derived from the SAME claim source as
            // TenantFilterTransformer, applied here where no argument shape can skip
            // it. No claim (or a null claim) means the caller's tenant is unknown:
            // zero rows, never everyone's trail. NULL-scope legacy rows are excluded
            // by the equality predicate itself — a row whose tenant is unknown fails
            // closed for scoped callers.
            var scopeColumn = Modules.History.HistoryConfig.ResolveTenantScopeColumn(trackedTable);
            if (scopeColumn is not null)
            {
                var claimKey = Modules.TenantFilterTransformer.ResolveTenantContextKey(_dbModel);
                if (!context.UserContext.TryGetValue(claimKey, out var tenant) || tenant is null)
                {
                    return new TableResult
                    {
                        Total = 0,
                        Offset = query.Offset,
                        Limit = query.Limit,
                        Data = Array.Empty<object?>(),
                    };
                }

                forced = AndFilters(forced, TableFilterFactory.Equals(target.DbName, scopeColumn, tenant));
            }

            query.Filter = query.Filter is null ? forced : AndFilters(query.Filter, forced);

            // Same fail-closed transformer pass as row queries over the TARGET table
            // (its own tenant/soft-delete/policy metadata still applies), into a
            // per-call overlay (sibling root fields resolve in parallel; the shared
            // UserContext is not thread-safe).
            var scopedContext = new Dictionary<string, object?>(context.UserContext);
            Modules.ModuleApiRegistry.CaptureQueryArguments(context, target, scopedContext);
            _transformerService.ApplyTransformers(query, _dbModel, scopedContext);
            ApplyEnumFilterRewrite(query);

            var bifrost = new BifrostContextAdapter(context);
            var (data, _) = await LoadDataParameterizedAsync(query, bifrost.ConnFactory, context.CancellationToken);

            // Decrypt/mask projector — same construction as base-table reads, so a
            // trail row's columns obey the identical per-caller policy.
            var cryptoRead = new Modules.Crypto.CryptoReadProjector(
                _dbModel,
                context.RequestServices?.GetService<BifrostQL.Core.Crypto.EnvelopeKeyManager>(),
                ExtractRoles(context.UserContext));

            // The before/after images carry the TRACKED table's column values as
            // stored — for an encrypted column, the ciphertext envelope. Route each
            // such value through the same projector a base-table read uses, so the
            // caller sees exactly what a read of the row itself would show them
            // (plaintext for the unmask role, the column's mask otherwise) and never
            // the raw ciphertext as a decryption oracle.
            ProjectEncryptedTrailImages(query, trackedTable, data, cryptoRead);

            var total = 0;
            if (data.TryGetValue(query.KeyName + "=>count", out var countEntry)
                && countEntry.data.Count > 0 && countEntry.data[0].Length > 0
                && countEntry.data[0][0] is { } countObj)
            {
                total = Convert.ToInt32(countObj);
            }

            var logger = context.RequestServices?.GetService<ILogger<SqlExecutionManager>>();
            return new TableResult
            {
                Total = total,
                Offset = query.Offset,
                Limit = query.Limit,
                Data = new ReaderEnum(query, data, _dbModel.EnumColumns, logger, cryptoRead),
            };
        }

        private static TableFilter AndFilters(TableFilter left, TableFilter right) => new()
        {
            And = new List<TableFilter> { left, right },
            FilterType = FilterType.And,
        };

        /// <summary>
        /// Rewrites the selected <c>before</c>/<c>after</c> image cells in place,
        /// projecting each encrypted tracked-table value through
        /// <paramref name="cryptoRead"/>. Image keys are the tracked table's DB
        /// column names (the writer's contract), so each key is matched against the
        /// tracked table's encrypted columns; non-encrypted entries pass through
        /// byte-for-byte. No-op when the tracked table has no encrypted column.
        /// </summary>
        private static void ProjectEncryptedTrailImages(
            GqlObjectQuery query,
            IDbTable trackedTable,
            IDictionary<string, (IDictionary<string, int> index, IList<object?[]> data)> results,
            Modules.Crypto.CryptoReadProjector cryptoRead)
        {
            var encryptedColumns = trackedTable.Columns
                .Where(c => !string.IsNullOrWhiteSpace(c.GetMetadataValue(Model.MetadataKeys.Crypto.Encrypt)))
                .Select(c => c.ColumnName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (encryptedColumns.Count == 0)
                return;

            if (!results.TryGetValue(query.KeyName, out var tableData))
                return; // No row result set selected (e.g. a total-only query).

            var imageOrdinals = query.ScalarColumns
                .Where(c => string.Equals(c.DbDbName, Model.MetadataKeys.History.Column.Before, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(c.DbDbName, Model.MetadataKeys.History.Column.After, StringComparison.OrdinalIgnoreCase))
                .Select(c => tableData.index.TryGetValue(c.GraphQlDbName, out var ordinal) ? ordinal : -1)
                .Where(ordinal => ordinal >= 0)
                .Distinct()
                .ToList();
            if (imageOrdinals.Count == 0)
                return;

            foreach (var row in tableData.data)
            {
                foreach (var ordinal in imageOrdinals)
                {
                    if (ordinal < row.Length)
                        row[ordinal] = ProjectImage(trackedTable, encryptedColumns, cryptoRead, row[ordinal]);
                }
            }
        }

        private static object? ProjectImage(
            IDbTable trackedTable,
            IReadOnlySet<string> encryptedColumns,
            Modules.Crypto.CryptoReadProjector cryptoRead,
            object? cell)
        {
            if (cell is null || cell is DBNull)
                return cell;

            var json = cell.ToString();
            if (string.IsNullOrWhiteSpace(json))
                return cell;

            Dictionary<string, System.Text.Json.JsonElement>? image;
            try
            {
                image = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json);
            }
            catch (System.Text.Json.JsonException)
            {
                // The image cannot be parsed, so its encrypted values cannot be
                // masked — refuse to return it rather than leak raw ciphertext.
                throw new BifrostExecutionError(
                    $"A history image of '{trackedTable.TableSchema}.{trackedTable.DbName}' is not valid JSON, so " +
                    "its encrypted values cannot be projected. Refusing to return the raw image.");
            }
            if (image is null)
                return cell;

            var projected = new Dictionary<string, object?>(image.Count, StringComparer.Ordinal);
            var changed = false;
            foreach (var (column, value) in image)
            {
                if (encryptedColumns.Contains(column) && value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    projected[column] = cryptoRead.Project(trackedTable.DbName, column, value.GetString());
                    changed = true;
                }
                else
                {
                    projected[column] = value;
                }
            }

            return changed ? System.Text.Json.JsonSerializer.Serialize(projected) : cell;
        }

        public async ValueTask<IReadOnlyDictionary<string, object?>> ResolvePivotAsync(
            IBifrostFieldContext context, IDbTable table, PivotRequest request)
        {
            var bifrost = new BifrostContextAdapter(context);
            var connFactory = bifrost.ConnFactory;
            var dialect = connFactory.Dialect;

            // Validate shape (column existence, pivot-not-in-rowKeys) before any SQL,
            // so a bad request fails fast with an authored message.
            var config = PivotQueryConfig.Create(request.PivotColumn, request.ValueColumn, request.Aggregate, request.RowKeys);
            config.ValidateColumns(table.ColumnLookup);

            // Fail-closed transformer pass into a per-call overlay (sibling root fields
            // resolve in parallel; the shared UserContext is not thread-safe). The
            // referenced columns ride along on the query so the column-read guard
            // asserts them — a policy-denied column cannot be pivoted or aggregated as
            // an exfiltration oracle. The resulting combined Filter scopes discovery.
            var scopedContext = new Dictionary<string, object?>(context.UserContext);
            ModuleApiRegistry.CaptureQueryArguments(context, table, scopedContext);

            var query = new GqlObjectQuery
            {
                DbTable = table,
                TableName = table.DbName,
                SchemaName = table.TableSchema,
                GraphQlName = table.GraphQlName,
                Filter = request.Filter,
                ScalarColumns = request.ReferencedColumns.Select(c => new GqlObjectColumn(c)).ToList(),
            };
            _transformerService.ApplyTransformers(query, _dbModel, scopedContext);
            ApplyEnumFilterRewrite(query);

            // Compile the combined filter (client AND tenant/policy/soft-delete) to a
            // single-table WHERE fragment, reusing the exact alias convention the row and
            // aggregate paths use (WHERE qualifies by table name; FROM uses TableReference).
            // A relationship filter would contribute a JOIN whose extra table makes the
            // pivot's unqualified CASE/GROUP BY columns ambiguous — reject it with steering
            // rather than emit ambiguous or subtly unscoped SQL.
            var parameters = new SqlParameterCollection();
            ParameterizedSql? filter = null;
            if (query.Filter != null)
            {
                var parts = query.Filter.RenderParts(_dbModel, dialect, parameters, null);
                if (!string.IsNullOrWhiteSpace(parts.Joins))
                    throw new BifrostExecutionError(
                        "Pivot filters can only reference the pivoted table's own columns, not related tables.");
                if (!string.IsNullOrWhiteSpace(parts.Where))
                    filter = new ParameterizedSql(" WHERE " + parts.Where, parts.Parameters);
            }

            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);

            // (1) Discover the distinct pivot-column values WITHIN scope, then guard
            // cardinality — a hard error above the cap, never a silent truncation.
            var distinctSql = PivotSqlGenerator.GenerateDistinctValuesSql(dialect, config.PivotColumn, tableRef, filter);
            var (_, distinctRows) = await ExecuteRawAsync(connFactory, distinctSql, context.CancellationToken);
            if (distinctRows.Count > request.MaxPivotColumns)
                throw new BifrostExecutionError(
                    $"Pivot column '{request.PivotColumnGraphQlName}' has {distinctRows.Count} distinct values in scope, " +
                    $"exceeding the limit of {request.MaxPivotColumns}. Add a filter to narrow the pivot column, " +
                    "or pivot on a lower-cardinality column.");
            var pivotValues = distinctRows.Select(r => ReaderEnum.DbConvert(r.Length > 0 ? r[0] : null)).ToList();

            // Every output column — the row keys plus one per pivot value — becomes a
            // result-set column name AND a JSON payload key, both of which must be
            // unique. Two pivot values whose labels coincide (classically a NULL, which
            // takes the null label, alongside a literal equal to that label) or a value
            // whose label matches a row-key column name would emit duplicate columns:
            // the reader can't index them and the JSON object can't hold both. Reject
            // with an actionable message BEFORE running the pivot on EITHER dialect,
            // rather than surfacing the reader's opaque duplicate-name failure.
            var labels = pivotValues.Select(v => v?.ToString() ?? config.NullLabel).ToList();
            var duplicate = config.GroupByColumns.Concat(labels)
                .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null)
                throw new BifrostExecutionError(
                    $"Pivot of '{request.PivotColumnGraphQlName}' produces two output columns named '{duplicate.Key}' " +
                    "(two pivot values sharing a label — e.g. a NULL and a literal null label — or a value matching a " +
                    "row-key column). Filter or rename one of the colliding values before pivoting.");

            // (2) Cross-tabulate with those values under the same scope filter.
            var pivotSql = PivotSqlGenerator.GeneratePivot(dialect, config, tableRef, pivotValues, filter);
            var (pivotIndex, pivotRows) = await ExecuteRawAsync(connFactory, pivotSql, context.CancellationToken);

            return BuildPivotPayload(config, request, labels, pivotIndex, pivotRows);
        }

        /// <summary>
        /// Runs one parameterized statement and materializes its single result set as
        /// (column-name → ordinal) index plus raw rows — the low-level read the GraphQL
        /// query path uses, exposed for the pivot's two raw <see cref="ParameterizedSql"/>
        /// statements (distinct discovery + cross-tab) which are not <see cref="GqlObjectQuery"/>s.
        /// </summary>
        private static async Task<(IDictionary<string, int> index, IList<object?[]> rows)> ExecuteRawAsync(
            IDbConnFactory connFactory, ParameterizedSql sql, CancellationToken cancellationToken)
        {
            await using var conn = connFactory.GetConnection();
            try
            {
                await conn.OpenAsync(cancellationToken);
                await using var command = conn.CreateCommand();
                command.CommandText = sql.Sql;
                DbParameterBinder.AddExtraParameters(command, sql.Parameters);

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var index = Enumerable.Range(0, reader.FieldCount)
                    .ToDictionary(reader.GetName, i => i, StringComparer.OrdinalIgnoreCase);
                var rows = new List<object?[]>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    var row = new object?[reader.FieldCount];
                    reader.GetValues(row!);
                    rows.Add(row);
                }
                return (index, rows);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw BifrostExecutionError.FromDatabaseException(ex);
            }
        }

        /// <summary>
        /// Shapes the cross-tab result set into the JSON pivot payload. Row-key values
        /// are read by their DB column name and keyed by GraphQL name for a stable
        /// client contract; each pivot cell is read by its column <paramref name="labels"/> —
        /// the same labels <see cref="PivotSqlGenerator"/> aliased the CASE columns with
        /// (each value's string form, or the config's null label for NULL), already
        /// verified collision-free by the caller.
        /// </summary>
        private static IReadOnlyDictionary<string, object?> BuildPivotPayload(
            PivotQueryConfig config, PivotRequest request,
            IReadOnlyList<string> labels,
            IDictionary<string, int> index, IList<object?[]> rows)
        {
            var outRows = new List<object?>(rows.Count);
            foreach (var row in rows)
            {
                var rowObj = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < config.GroupByColumns.Count; i++)
                    rowObj[request.RowKeyGraphQlNames[i]] = ReadCell(index, row, config.GroupByColumns[i]);

                var cells = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var label in labels)
                    cells[label] = ReadCell(index, row, label);
                rowObj["cells"] = cells;
                outRows.Add(rowObj);
            }

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pivotColumn"] = request.PivotColumnGraphQlName,
                ["rowKeys"] = request.RowKeyGraphQlNames,
                ["columns"] = labels,
                ["rows"] = outRows,
            };
        }

        public async ValueTask<QueryIntentResult> ExecuteIntentAsync(
            GqlObjectQuery query,
            IDictionary<string, object?> userContext,
            IDbConnFactory connFactory,
            CancellationToken cancellationToken = default,
            BifrostQL.Core.Crypto.EnvelopeKeyManager? keyManager = null)
        {
            // Same outcome recording as the GraphQL read path so adapter (intent) traffic is measured
            // too; scrape-internal collection queries carry the recursion marker and are skipped.
            try
            {
                return await ExecuteIntentCoreAsync(query, userContext, connFactory, cancellationToken, keyManager);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (BifrostExecutionError bex) when (bex.ErrorCode == BifrostExecutionError.AccessDeniedCode)
            {
                RecordReadOutcome(userContext, EngineRequestOutcome.Denied);
                throw;
            }
            catch
            {
                RecordReadOutcome(userContext, EngineRequestOutcome.Error);
                throw;
            }
        }

        private async ValueTask<QueryIntentResult> ExecuteIntentCoreAsync(
            GqlObjectQuery query,
            IDictionary<string, object?> userContext,
            IDbConnFactory connFactory,
            CancellationToken cancellationToken = default,
            BifrostQL.Core.Crypto.EnvelopeKeyManager? keyManager = null)
        {
            if (query.DbTable is null)
                throw new BifrostExecutionError(
                    "Query intent has no table: GqlObjectQuery.DbTable must be set.");

            // History targets are system tables: an adapter intent may not read
            // them directly — that would bypass the trail field's forced entity
            // discriminator, tenant scope predicate, and crypto image projection.
            // (Links cannot smuggle one in either: the model carries no
            // relationship links touching a history target.)
            if (Schema.HistorySurface.IsHistoryTarget(_dbModel, query.DbTable))
                throw new BifrostExecutionError(
                    $"Table '{query.DbTable.TableSchema}.{query.DbTable.DbName}' is a change-history table and is " +
                    "not directly queryable. Read a table's trail through its generated '<table>History' field.")
                { ErrorCode = BifrostExecutionError.AccessDeniedCode };

            // Grouped-aggregate SQL generation ignores joins entirely, so a
            // declared link on a grouped intent would be silently dropped —
            // fail fast instead of returning subtly unscoped-looking results.
            if (query.GroupedAggregate != null && query.Links.Count > 0)
                throw new BifrostExecutionError(
                    "Grouped-aggregate intents do not support linked tables; aggregate a single table.");

            // Materialize declared Links into executable Joins (idempotent for a
            // root-only query; the intent executor is the sole caller so a query
            // instance passes through exactly once).
            query.ConnectLinks(_dbModel);

            // Same fail-closed transformer pass as GraphQL row queries, into a
            // per-call overlay so a shared caller context is never mutated.
            var scopedContext = new Dictionary<string, object?>(userContext);
            var transformStart = Stopwatch.GetTimestamp();
            _transformerService.ApplyTransformers(query, _dbModel, scopedContext);
            RecordReadTransformerDuration(userContext, transformStart);
            ApplyEnumFilterRewrite(query);

            // Same lifecycle notifications as the GraphQL path (minus Parsed — an
            // intent is never parsed), so audit/metrics observers see protocol-
            // adapter traffic too instead of silently skipping it.
            await NotifyIntentAsync(QueryPhase.Transformed, query, scopedContext, filter: query.Filter);

            var sw = Stopwatch.StartNew();
            var (data, sql) = await LoadDataParameterizedAsync(query, connFactory, cancellationToken);
            sw.Stop();

            await NotifyIntentAsync(QueryPhase.AfterExecute, query, scopedContext,
                filter: query.Filter, sql: sql,
                rowCount: data.Values.Sum(v => v.data.Count), duration: sw.Elapsed);

            if (!data.TryGetValue(query.KeyName, out var tableData))
                throw new BifrostExecutionError(
                    $"Query intent result set '{query.KeyName}' is missing from the execution results.");

            // Decrypt/mask projector — same construction as GraphQL base-table reads,
            // so an adapter intent obeys the identical per-caller crypto policy: the
            // unmask role (or admin) sees plaintext, everyone else the column's mask,
            // and raw ciphertext never leaves the seam (no key manager ⇒ redaction).
            var cryptoRead = new Modules.Crypto.CryptoReadProjector(
                _dbModel, keyManager, ExtractRoles(userContext));

            var rows = new List<IReadOnlyDictionary<string, object?>>(tableData.data.Count);
            foreach (var row in tableData.data)
            {
                var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var (column, ordinal) in tableData.index)
                {
                    if (ordinal < row.Length)
                        map[column] = cryptoRead.Project(query.DbTable.DbName, column, ReaderEnum.DbConvert(row[ordinal]));
                }
                rows.Add(map);
            }

            // Flatten forward single-link (many-to-one) joined columns into each root
            // row under a table-qualified key. ExecuteIntentAsync otherwise returns only
            // the root result set; a protocol adapter that declared a single-link join
            // (e.g. pgwire `SELECT ... JOIN parent ON child.fk = parent.pk`) needs the
            // joined scalars alongside the root row. Only single-column QueryType.Single
            // links are flattened — collection (one-to-many), composite, and m2m joins
            // are not row-flat and are rejected upstream.
            FlattenSingleLinkJoins(query, data, rows, cryptoRead);

            int? total = null;
            if (query.IncludeResult
                && data.TryGetValue(query.KeyName + "=>count", out var countEntry)
                && countEntry.data.Count > 0 && countEntry.data[0].Length > 0
                && countEntry.data[0][0] is { } countObj)
            {
                total = Convert.ToInt32(countObj);
            }

            return new QueryIntentResult { Rows = rows, TotalCount = total, Sql = sql };
        }

        /// <summary>
        /// Merges forward single-link (many-to-one) joined columns into each flat root
        /// row. Each such join emitted a separate result set keyed by
        /// <see cref="TableJoin.JoinName"/> that projects the parent FK value as
        /// <see cref="JoinKeyNames.SrcIdSingle"/> plus the parent's selected columns; a
        /// single link yields at most one parent row per source key, so the merge is a
        /// well-defined per-row flatten (no cardinality fan-out). Joined columns land
        /// under a table-qualified key (<c>&lt;parentTable&gt;.&lt;col&gt;</c>) so they
        /// never overwrite a same-named root column, and go through the same crypto
        /// read projector as the root scalars.
        /// </summary>
        private static void FlattenSingleLinkJoins(
            GqlObjectQuery query,
            IDictionary<string, (IDictionary<string, int> index, IList<object?[]> data)> data,
            List<IReadOnlyDictionary<string, object?>> rows,
            Modules.Crypto.CryptoReadProjector cryptoRead)
        {
            foreach (var join in query.Joins)
            {
                if (join.QueryType != QueryType.Single || join.IsComposite)
                    continue;
                if (!data.TryGetValue(join.JoinName, out var joinData))
                    continue;
                if (!joinData.index.TryGetValue(JoinKeyNames.SrcIdSingle, out var srcIdx))
                    continue;

                var connectedTable = join.ConnectedTable.DbTable.DbName;
                var connectedCols = joinData.index
                    .Where(kv => !string.Equals(kv.Key, JoinKeyNames.SrcIdSingle, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // src_id (the parent FK value) → the single joined row.
                var bySrcId = new Dictionary<object, object?[]>();
                foreach (var jr in joinData.data)
                {
                    if (srcIdx >= jr.Length) continue;
                    var key = ReaderEnum.DbConvert(jr[srcIdx]);
                    if (key is not null) bySrcId.TryAdd(key, jr);
                }

                foreach (var row in rows)
                {
                    var map = (Dictionary<string, object?>)row;
                    if (!map.TryGetValue(join.FromColumn, out var fk) || fk is null) continue;
                    if (!bySrcId.TryGetValue(fk, out var jr)) continue;
                    foreach (var (colName, idx) in connectedCols)
                    {
                        if (idx >= jr.Length) continue;
                        map[$"{connectedTable}.{colName}"] =
                            cryptoRead.Project(connectedTable, colName, ReaderEnum.DbConvert(jr[idx]));
                    }
                }
            }
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
                {
                    // _count is a GraphQL Int (Int32). A group exceeding Int range is
                    // implausible but must surface as a clean execution error, not a
                    // raw OverflowException, to match this layer's error contract.
                    var countLong = Convert.ToInt64(countValue);
                    if (countLong > int.MaxValue)
                        throw new BifrostExecutionError(
                            $"Aggregate group count {countLong} exceeds the Int range of the _count field.");
                    count = (int)countLong;
                }

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
        /// <summary>
        /// Observer notification for the intent path, which has no
        /// <see cref="IBifrostFieldContext"/>: the query's own path/name stand in.
        /// </summary>
        private async ValueTask NotifyIntentAsync(
            QueryPhase phase,
            GqlObjectQuery query,
            IDictionary<string, object?> userContext,
            TableFilter? filter = null,
            string? sql = null,
            int? rowCount = null,
            TimeSpan? duration = null)
        {
            if (_observers is not { Count: > 0 })
                return;

            await _observers.NotifyAsync(phase, new QueryObserverContext
            {
                Table = query.DbTable!,
                Model = _dbModel,
                UserContext = userContext,
                QueryType = query.QueryType,
                Path = string.IsNullOrEmpty(query.Path) ? query.GraphQlName : query.Path,
                Filter = filter,
                Sql = sql,
                RowCount = rowCount,
                Duration = duration,
            });
        }

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

        /// <summary>
        /// Reads the caller's roles from the user context (the <c>roles</c> claim),
        /// tolerating a single string, a typed list, or an untyped sequence — mirroring
        /// how the policy engine extracts roles. Used to decide field-encryption unmask.
        /// </summary>
        private static IReadOnlyList<string> ExtractRoles(IDictionary<string, object?> userContext)
        {
            if (userContext is null
                || !userContext.TryGetValue(Model.MetadataKeys.Auth.DefaultRolesContextKey, out var rolesValue)
                || rolesValue is null)
                return Array.Empty<string>();

            if (rolesValue is string single)
                return new[] { single };

            if (rolesValue is IEnumerable<string> typed)
                return typed.ToArray();

            if (rolesValue is System.Collections.IEnumerable sequence)
            {
                var result = new List<string>();
                foreach (var item in sequence)
                {
                    var role = item?.ToString();
                    if (!string.IsNullOrWhiteSpace(role))
                        result.Add(role);
                }
                return result;
            }

            return Array.Empty<string>();
        }
    }
}
