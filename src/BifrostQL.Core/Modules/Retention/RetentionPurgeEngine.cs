using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using GraphQL;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Core.Modules.Retention
{
    /// <summary>
    /// Metadata-driven data-retention purge ENGINE. Runs the <c>retain</c> (hard-purge of
    /// already-soft-deleted rows) and <c>ttl</c> (expire live rows) windows parsed by
    /// <see cref="RetentionConfig"/> on a cadence, deleting expired rows STRICTLY through the
    /// mutation pipeline so the purge can neither bypass a transformer nor cross a tenant
    /// boundary.
    ///
    /// <para><b>Cross-tenant purge is impossible BY CONSTRUCTION, not by remembering to
    /// filter.</b> A background purge carries no caller identity, so for a tenant-scoped table
    /// the engine synthesizes a PER-TENANT system <c>UserContext</c> and runs every delete under
    /// it: <see cref="TenantMutationTransformer"/> (and <see cref="PolicyMutationTransformer"/>)
    /// narrow the write to that tenant, so an out-of-scope primary key matches ZERO rows — the
    /// pipeline enforces isolation, the engine never builds a WHERE clause of its own. The one
    /// legitimate cross-tenant read is <see cref="EnumerateTenantsAsync"/>, which reads ONLY the
    /// set of distinct tenant identifiers (never row data) so the engine can fan out one scoped
    /// sweep per tenant.</para>
    ///
    /// <para><b>Reads are scoped too.</b> The bounded expired-primary-key SELECT runs through
    /// <see cref="IQueryIntentExecutor"/> under the same per-tenant system context, so it only
    /// ever sees that tenant's expired rows; the cutoff predicate is expressed as a
    /// <see cref="GqlObjectQuery"/> filter (parameterized, dialect-rendered) rather than
    /// hand-built SQL. The <c>retain</c> read additionally sets the soft-delete module's
    /// <c>only_deleted</c> flag so it targets ALREADY-soft-deleted rows (a default read hides
    /// them).</para>
    ///
    /// <para><b>Delete routing.</b> <c>ttl</c> routes a plain Delete intent and lets the
    /// pipeline decide hard-vs-soft (a soft-delete table expires to a soft delete; others hard
    /// delete) — the engine never special-cases soft-delete. <c>retain</c> routes the pipeline's
    /// DECLARED hard-delete route (the <c>hard_delete</c> module argument, role-gated by the
    /// table's <c>soft-delete-hard-role</c>) so an already-soft-deleted row — which a soft-delete
    /// UPDATE could never touch — is physically removed. Because <c>retain</c> is anchored on the
    /// soft-delete column, it can never select a live row.</para>
    ///
    /// <para><b>Bounded, throttled, resumable.</b> Each pass reads at most
    /// <see cref="DefaultBatchSize"/> expired keys per (table, tenant, mode); each delete commits
    /// in its OWN pipeline transaction, so there is no long-running transaction and no partial
    /// batch. A crash mid-sweep leaves every already-committed delete in place; the next pass
    /// re-reads the shrunken expired set and continues — progress is monotonic and never
    /// re-scans from zero.</para>
    ///
    /// <para>Ownership split mirrors <see cref="Cdc.OutboxDispatcher"/> / <c>IProtocolAdapter</c>:
    /// this engine lives in Core with no hosting dependency; the host wraps
    /// <see cref="RunAsync"/> in an <see cref="Microsoft.Extensions.Hosting.IHostedService"/>.</para>
    /// </summary>
    public sealed class RetentionPurgeEngine
    {
        /// <summary>Max expired rows read (and therefore deleted) per (table, tenant, mode) per pass.</summary>
        public const int DefaultBatchSize = 100;

        // Retention runs on a slow cadence (unlike CDC drain): expired-row windows are measured
        // in hours/days, so a frequent poll would only burn cycles finding nothing.
        private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromHours(1);

        // Distinguishes "this call is a delete" DELETE args from module arg presence checks.
        private static readonly IReadOnlyDictionary<string, object?> HardDeleteArguments =
            new Dictionary<string, object?> { [SoftDeleteModuleApi.HardDeleteKey] = true };

        private readonly IQueryIntentExecutor _reader;
        private readonly IMutationIntentExecutor _writer;
        private readonly PathCache<Inputs>? _pathCache;
        private readonly Func<DateTime> _clock;
        private readonly ILogger? _logger;
        private readonly TimeSpan _pollInterval;
        private readonly int _batchSize;

        /// <param name="reader">Scoped read seam; the expired-key SELECT runs through it so tenant/
        /// soft-delete/policy transformers apply.</param>
        /// <param name="writer">Scoped write seam; every purge delete routes through it (→ the full
        /// <c>TableMutationPipeline</c>).</param>
        /// <param name="pathCache">The endpoint cache used only by <see cref="RunAsync"/> to resolve
        /// the model/connection each pass (mirrors <see cref="Cdc.OutboxDispatcher"/>). Optional:
        /// tests drive <see cref="PurgeOnceAsync"/> with an explicit model/connection.</param>
        /// <param name="clock">Injected wall clock (UTC) for deterministic cutoffs under test;
        /// defaults to <see cref="DateTime.UtcNow"/>.</param>
        /// <param name="pollInterval">Cadence between passes; defaults to one hour.</param>
        /// <param name="batchSize">Rows purged per (table, tenant, mode) per pass; bounds memory and
        /// keeps each pass short.</param>
        public RetentionPurgeEngine(
            IQueryIntentExecutor reader,
            IMutationIntentExecutor writer,
            PathCache<Inputs>? pathCache = null,
            Func<DateTime>? clock = null,
            ILogger? logger = null,
            TimeSpan? pollInterval = null,
            int batchSize = DefaultBatchSize)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _pathCache = pathCache;
            _clock = clock ?? (() => DateTime.UtcNow);
            _logger = logger;
            _pollInterval = pollInterval is { } p && p > TimeSpan.Zero ? p : DefaultPollInterval;
            _batchSize = batchSize < 1 ? DefaultBatchSize : batchSize;
        }

        /// <summary>
        /// The polling loop. Resolves the model/connection lazily each pass, stops entirely when
        /// no table opts into retention (a non-retention host pays nothing), and runs one bounded
        /// purge pass per interval. Runs until <paramref name="cancellationToken"/> is signalled;
        /// never throws to the caller.
        /// </summary>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var (model, connFactory) = await ResolveAsync();
                    if (model is not null && connFactory is not null)
                    {
                        if (!model.Tables.Any(HasRetentionSafe))
                        {
                            // No table opts into retain/ttl on this host: stop the loop so a
                            // non-retention host does no further work.
                            _logger?.LogDebug("Retention purge idle: no table declares 'retain' or 'ttl'.");
                            return;
                        }

                        await PurgeOnceAsync(model, connFactory, endpoint: null, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Fail-safe: a resolution/purge error must not kill the hosted service.
                    _logger?.LogError(ex, "Retention purge pass failed; retrying after the interval.");
                }

                try
                {
                    await Task.Delay(_pollInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// One purge pass over every retention-tracked table on the endpoint. Per table the config
        /// read is fail-closed (an invalid config skips ONLY that table — never aborts the sweep,
        /// never widens a purge), tenants are enumerated for scoped fan-out, and each (table, tenant)
        /// is swept for both <c>retain</c> and <c>ttl</c>.
        /// </summary>
        public async Task<RetentionPurgeOutcome> PurgeOnceAsync(
            IDbModel model, IDbConnFactory connFactory, string? endpoint, CancellationToken cancellationToken)
        {
            if (model is null) throw new ArgumentNullException(nameof(model));
            if (connFactory is null) throw new ArgumentNullException(nameof(connFactory));

            var now = _clock();
            int tablesSwept = 0, rowsPurged = 0, tablesSkipped = 0;

            foreach (var table in model.Tables)
            {
                cancellationToken.ThrowIfCancellationRequested();

                RetentionConfig config;
                try
                {
                    config = RetentionConfig.FromTable(table);
                }
                catch (Exception ex)
                {
                    // FAIL-CLOSED: a per-table config error (e.g. an oversized window) skips THIS
                    // table only. Never abort the whole sweep, never fall through to an unscoped
                    // purge.
                    _logger?.LogWarning(ex,
                        "Retention config for '{Schema}.{Table}' is invalid; skipping it this pass.",
                        table.TableSchema, table.DbName);
                    tablesSkipped++;
                    continue;
                }

                if (!config.HasRetention)
                    continue;

                tablesSwept++;

                var tenantColumn = NormalizeTenantColumn(table);
                IReadOnlyList<object?> tenants = tenantColumn is null
                    ? new object?[] { null }                                   // no tenant boundary: one global sweep
                    : await EnumerateTenantsAsync(table, tenantColumn, connFactory, cancellationToken);

                foreach (var tenant in tenants)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rowsPurged += await SweepTableForTenantAsync(
                        model, table, config, tenantColumn, tenant, endpoint, now, cancellationToken);
                }
            }

            return new RetentionPurgeOutcome(tablesSwept, rowsPurged, tablesSkipped);
        }

        /// <summary>
        /// Purges one (table, tenant) for both retention modes under a synthesized per-tenant system
        /// context. Internal so a test can prove tenant isolation is STRUCTURAL: running this under
        /// tenant A's synthesized context leaves tenant B's expired rows untouched because the
        /// pipeline narrows every delete to tenant A — not because the engine filtered.
        /// </summary>
        internal async Task<int> SweepTableForTenantAsync(
            IDbModel model, IDbTable table, RetentionConfig config, string? tenantColumn,
            object? tenantValue, string? endpoint, DateTime now, CancellationToken cancellationToken)
        {
            var purged = 0;

            if (config.PurgesSoftDeleted)
                purged += await PurgeRetainAsync(model, table, config, tenantColumn, tenantValue, endpoint, now, cancellationToken);

            if (config.ExpiresLiveRows)
                purged += await PurgeTtlAsync(model, table, config, tenantColumn, tenantValue, endpoint, now, cancellationToken);

            return purged;
        }

        // retain: hard-purge rows already soft-deleted before the cutoff. The scoped read targets
        // ONLY-deleted rows (default reads hide them); the delete uses the pipeline's declared
        // hard-delete route so it reaches a row a soft-delete UPDATE could never match.
        private async Task<int> PurgeRetainAsync(
            IDbModel model, IDbTable table, RetentionConfig config, string? tenantColumn,
            object? tenantValue, string? endpoint, DateTime now, CancellationToken cancellationToken)
        {
            var cutoff = now - config.RetainWindow!.Value;
            var readContext = BuildTenantContext(model, tenantColumn, tenantValue);
            // Flip the soft-delete read filter to IS NOT NULL so the scoped read returns the
            // already-soft-deleted rows retain exists to purge.
            readContext[SoftDeleteModuleApi.OnlyDeletedKey] = true;

            var rows = await ReadExpiredKeysAsync(table, config.RetainAnchorColumn!, cutoff, readContext, endpoint, cancellationToken);

            var hardRole = table.GetMetadataValue(MetadataKeys.SoftDelete.HardDeleteRole);

            var purged = 0;
            foreach (var primaryKey in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var deleteContext = BuildTenantContext(model, tenantColumn, tenantValue);
                // Grant EXACTLY the table's declared hard-delete role (nothing broader) so the
                // pipeline's role gate authorizes the system purge; a table with no declared role
                // needs none.
                if (!string.IsNullOrWhiteSpace(hardRole))
                    deleteContext["roles"] = new[] { hardRole };

                var result = await _writer.ExecuteAsync(new MutationIntent
                {
                    Table = table.DbName,
                    Action = MutationIntentAction.Delete,
                    Data = EmptyData,
                    PrimaryKey = primaryKey,
                    ModuleArguments = HardDeleteArguments,
                    UserContext = deleteContext,
                    Endpoint = endpoint,
                }, cancellationToken);

                purged += DeletedRowCount(result);
            }

            return purged;
        }

        // ttl: expire live rows anchored on the explicit ttl column. Route a PLAIN Delete intent and
        // let the pipeline decide hard-vs-soft — the engine never special-cases soft-delete.
        private async Task<int> PurgeTtlAsync(
            IDbModel model, IDbTable table, RetentionConfig config, string? tenantColumn,
            object? tenantValue, string? endpoint, DateTime now, CancellationToken cancellationToken)
        {
            var cutoff = now - config.TtlWindow!.Value;
            // Default read context: the soft-delete filter (if any) hides already-deleted rows, which
            // is correct — ttl targets LIVE rows.
            var readContext = BuildTenantContext(model, tenantColumn, tenantValue);

            var rows = await ReadExpiredKeysAsync(table, config.TtlAnchorColumn!, cutoff, readContext, endpoint, cancellationToken);

            var purged = 0;
            foreach (var primaryKey in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await _writer.ExecuteAsync(new MutationIntent
                {
                    Table = table.DbName,
                    Action = MutationIntentAction.Delete,
                    Data = EmptyData,
                    PrimaryKey = primaryKey,
                    UserContext = BuildTenantContext(model, tenantColumn, tenantValue),
                    Endpoint = endpoint,
                }, cancellationToken);

                purged += DeletedRowCount(result);
            }

            return purged;
        }

        // Reads up to _batchSize expired primary keys through the scoped read seam. The tenant/
        // soft-delete/policy transformers apply from readContext; the engine adds ONLY the cutoff
        // predicate (anchor < cutoff) as a parameterized GqlObjectQuery filter — no hand-built SQL.
        private async Task<List<IReadOnlyList<object?>>> ReadExpiredKeysAsync(
            IDbTable table, string anchorColumn, DateTime cutoff,
            IDictionary<string, object?> readContext, string? endpoint, CancellationToken cancellationToken)
        {
            var keyColumns = table.KeyColumns.ToList();
            if (keyColumns.Count == 0)
                throw new BifrostExecutionError(
                    $"Table '{table.TableSchema}.{table.DbName}' has no primary key; a retention purge cannot address its rows.");

            var query = new GqlObjectQuery
            {
                DbTable = table,
                SchemaName = table.TableSchema,
                TableName = table.DbName,
                GraphQlName = table.GraphQlName,
                Path = table.GraphQlName,
                Limit = _batchSize,
                Filter = TableFilter.FromObject(
                    new Dictionary<string, object?>
                    {
                        [anchorColumn] = new Dictionary<string, object?> { ["_lt"] = cutoff },
                    },
                    table.DbName),
            };
            // Select and order by the key columns so the bounded batch is deterministic and the
            // next resumable pass continues over the shrunken set.
            foreach (var keyColumn in keyColumns)
            {
                query.ScalarColumns.Add(new GqlObjectColumn(keyColumn.ColumnName));
                query.Sort.Add($"{keyColumn.ColumnName}_asc");
            }

            var result = await _reader.ExecuteAsync(new QueryIntent
            {
                Query = query,
                UserContext = readContext,
                Endpoint = endpoint,
            }, cancellationToken);

            var keys = new List<IReadOnlyList<object?>>(result.Rows.Count);
            foreach (var row in result.Rows)
            {
                var lookup = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase);
                var pk = new object?[keyColumns.Count];
                for (var i = 0; i < keyColumns.Count; i++)
                {
                    if (!lookup.TryGetValue(keyColumns[i].ColumnName, out var value))
                        throw new BifrostExecutionError(
                            $"Retention read for '{table.TableSchema}.{table.DbName}' did not return key column '{keyColumns[i].ColumnName}'.");
                    pk[i] = value;
                }
                keys.Add(pk);
            }
            return keys;
        }

        /// <summary>
        /// The ONE legitimate cross-tenant read: SELECT DISTINCT of the tenant column so the sweep
        /// can fan out one scoped purge per tenant. It returns ONLY tenant identifiers — never row
        /// data — and every actual purge that follows is tenant-scoped by the pipeline. Emitted via
        /// <see cref="ISqlDialect"/> (no dialect literal in the engine).
        /// </summary>
        private static async Task<IReadOnlyList<object?>> EnumerateTenantsAsync(
            IDbTable table, string tenantColumn, IDbConnFactory connFactory, CancellationToken cancellationToken)
        {
            var dialect = connFactory.Dialect;
            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var column = dialect.EscapeIdentifier(tenantColumn);
            var sql = $"SELECT DISTINCT {column} FROM {tableRef} WHERE {column} IS NOT NULL;";

            await using var conn = connFactory.GetConnection();
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            var tenants = new List<object?>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                tenants.Add(await reader.IsDBNullAsync(0, cancellationToken) ? null : reader.GetValue(0));
            return tenants;
        }

        // Synthesizes the per-tenant system context. For a tenant-scoped table it carries only the
        // tenant claim under the model's resolved context key, so TenantMutationTransformer narrows
        // every read and write to that tenant. For a non-tenant table it is empty (no tenant
        // boundary exists to scope).
        private static IDictionary<string, object?> BuildTenantContext(
            IDbModel model, string? tenantColumn, object? tenantValue)
        {
            var context = new Dictionary<string, object?>();
            if (tenantColumn is not null)
                context[TenantFilterTransformer.ResolveTenantContextKey(model)] = tenantValue;
            return context;
        }

        private static string? NormalizeTenantColumn(IDbTable table)
        {
            var tenantColumn = table.GetMetadataValue(MetadataKeys.Security.TenantFilter);
            return string.IsNullOrWhiteSpace(tenantColumn) ? null : tenantColumn.Trim();
        }

        // For a Delete the mutation field's scalar Value IS the affected-row count (unlike Update,
        // whose Value is the key — see MutationIntentResult). A tenant/policy-scoped-away delete
        // reports 0, so a cross-tenant key contributes nothing to the purge count.
        private static int DeletedRowCount(MutationIntentResult result)
            => result.Value is null ? 0 : Convert.ToInt32(result.Value, CultureInfo.InvariantCulture);

        private bool HasRetentionSafe(IDbTable table)
        {
            try
            {
                return RetentionConfig.FromTable(table).HasRetention;
            }
            catch
            {
                // A broken config is fail-closed here too: it never keeps the loop alive on its own.
                return false;
            }
        }

        private async Task<(IDbModel? Model, IDbConnFactory? ConnFactory)> ResolveAsync()
        {
            if (_pathCache is null)
                return (null, null);

            var inputs = await _pathCache.GetFirstValueAsync();
            if (inputs is null)
                return (null, null);

            var model = inputs.TryGetValue("model", out var m) ? m as IDbModel : null;
            var factory = inputs.TryGetValue("connFactory", out var f) ? f as IDbConnFactory : null;
            return (model, factory);
        }

        private static readonly IReadOnlyDictionary<string, object?> EmptyData =
            new Dictionary<string, object?>();
    }

    /// <summary>
    /// The result of one <see cref="RetentionPurgeEngine.PurgeOnceAsync"/> pass:
    /// <see cref="TablesSwept"/> tables had retention applied, <see cref="RowsPurged"/> rows were
    /// actually deleted (scoped-away no-ops do not count), and <see cref="TablesSkipped"/> tables
    /// were skipped fail-closed on an invalid config.
    /// </summary>
    public readonly record struct RetentionPurgeOutcome(int TablesSwept, int RowsPurged, int TablesSkipped);
}
