using System.Data.Common;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Model;
using static BifrostQL.Core.Resolvers.DbParameterBinder;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// The dependencies a single-row mutation needs once GraphQL context has been
    /// stripped away. Built by <see cref="DbTableMutateResolver"/> from the field
    /// context and by <see cref="MutationIntentExecutor"/> from an endpoint's cached
    /// inputs, so both entry points execute the identical pipeline.
    /// </summary>
    internal sealed class MutationPipelineContext
    {
        public required IDbModel Model { get; init; }
        public required IDbConnFactory ConnFactory { get; init; }
        public required IMutationTransformers Transformers { get; init; }
        public required IDictionary<string, object?> UserContext { get; init; }
        public IServiceProvider? Services { get; init; }

        /// <summary>
        /// Module argument values (e.g. <c>_hardDelete</c>) keyed by context key;
        /// consumed by the delete path's transformers. Empty when the entry point
        /// carries none.
        /// </summary>
        public IReadOnlyDictionary<string, object?> ModuleArguments { get; init; } = ModuleApiRegistry.EmptyArguments;

        public CancellationToken CancellationToken { get; init; }
    }

    /// <summary>
    /// The shared single-row mutation execution seam: transformer chain (policy,
    /// state machine, enum mapping, validation, soft delete, tenant isolation,
    /// audit, optimistic concurrency), before-commit hooks, parameterized SQL, and
    /// post-commit notifications. Extracted from <see cref="DbTableMutateResolver"/>
    /// so the GraphQL resolver and the protocol-adapter mutation-intent path run
    /// ONE pipeline — transformer application lives inside these methods, so no
    /// caller has an API surface that reaches SQL without it.
    /// </summary>
    internal static class TableMutationPipeline
    {
        /// <summary>
        /// The per-mutation hook context shared by BOTH in-transaction hook phases (see
        /// <see cref="MutationNotifier.RunBeforeCommitHooksAsync"/>), carrying a fresh
        /// <see cref="MutationObserverContext.NewMutationState"/> bag so one mutation's
        /// before-image can never be paired with another mutation's write.
        /// </summary>
        private static MutationObserverContext HookContext(
            IDbTable table, MutationType mutationType, IDictionary<string, object?> data,
            MutationPipelineContext ctx, DbConnection conn, DbTransaction? transaction, ISqlDialect dialect)
            => new()
            {
                Table = table,
                MutationType = mutationType,
                Data = data,
                Result = null,
                UserContext = ctx.UserContext,
                Connection = conn,
                Transaction = transaction,
                Model = ctx.Model,
                Dialect = dialect,
                MutationState = MutationObserverContext.NewMutationState(),
            };

        public static async Task<object?> InsertAsync(
            IDbTable table, Dictionary<string, object?> data, MutationPipelineContext ctx)
        {
            var dialect = ctx.ConnFactory.Dialect;

            // Mutation transformers (e.g. the authorization policy engine) gate
            // the insert before any SQL is built; non-empty Errors abort it.
            var transformContext = new MutationTransformContext
            {
                Model = ctx.Model,
                UserContext = ctx.UserContext,
                Services = ctx.Services,
            };
            var transformResult = await ctx.Transformers.TransformAsync(table, MutationType.Insert, data, transformContext);
            if (transformResult.Errors.Length > 0)
                throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));

            // Adopt the (possibly rewritten) data so transformer output — e.g.
            // enum-name → DB-value mapping — actually reaches the SQL, and rekey
            // GraphQL field names to real DB column names so sanitized/prefixed
            // columns land in the right column. Mirrors the delete path.
            data = ToDbColumnKeys(table, transformResult.Data);

            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var insertInto = MutationCommandExecutor.BuildInsertInto(dialect, table, tableRef, data.Keys);
            var returning = dialect.ReturningIdentityClauseFor(table.KeyColumns.Select(k => k.ColumnName).ToList());
            var sql = returning != null
                ? $"{insertInto}{returning};"
                : $"{insertInto};SELECT {dialect.LastInsertedIdentity} ID;";

            // Before-commit hooks and the insert (with its identity SELECT) run in
            // one transaction so a hook veto or a failed write rolls back as a unit.
            object? result = null;
            await MutationCommandExecutor.RunInTransactionAsync(ctx.ConnFactory, async (conn, transaction) =>
            {
                var hookContext = HookContext(table, MutationType.Insert, data, ctx, conn, transaction, dialect);
                await MutationNotifier.RunBeforeCommitHooksAsync(ctx.Services, hookContext);
                result = HandleDecimals(await MutationCommandExecutor.ExecuteScalar(conn, transaction, sql, data, ctx.CancellationToken));
                // After-write, still in-transaction: the outbox writer runs here so the
                // event can name the generated identity (result) returned by the insert.
                await MutationNotifier.RunInTransactionHooksAsync(ctx.Services, hookContext, result);
            }, ctx.CancellationToken);
            await MutationNotifier.NotifyMutationAsync(ctx.Services, table, MutationType.Insert, data, result, ctx.UserContext);
            return result;
        }

        public static async Task<object?> UpdateAsync(
            IDbTable table,
            (Dictionary<string, object?> data, Dictionary<string, object?> keyData, Dictionary<string, object?> standardData) propertyInfo,
            MutationPipelineContext ctx)
        {
            if (!propertyInfo.data.Any())
                return 0;

            if (!propertyInfo.keyData.Any())
                return 0;

            if (!propertyInfo.standardData.Any())
                return 0;

            var dialect = ctx.ConnFactory.Dialect;

            // The state-machine load, mutation transformers, before-commit hooks and
            // the update run inside one transaction so the read the transformer gates
            // on and the write it produces commit atomically or roll back together.
            Dictionary<string, object?> updatedData = null!;
            int result = 0;
            StateTransitionInfo? stateTransition = null;
            await MutationCommandExecutor.RunInTransactionAsync(ctx.ConnFactory, async (conn, transaction) =>
            {
                // Mutation transformers (e.g. the authorization policy engine) gate
                // the update before any SQL is built; non-empty Errors abort it.
                var currentRow = await MutationCommandExecutor.LoadCurrentStateMachineRow(
                    conn,
                    transaction,
                    dialect,
                    table,
                    propertyInfo.keyData,
                    ctx.CancellationToken);
                var transformContext = new MutationTransformContext
                {
                    Model = ctx.Model,
                    UserContext = ctx.UserContext,
                    CurrentRow = currentRow,
                    Services = ctx.Services,
                };
                var transformResult = await ctx.Transformers.TransformAsync(table, MutationType.Update, propertyInfo.data, transformContext);
                if (transformResult.Errors.Length > 0)
                    throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));

                // The transformer's AdditionalFilter (e.g. policy row-scope, soft-delete
                // IS NULL) is ANDed onto the WHERE clause so it narrows — never
                // replaces — the primary-key predicate.
                var additionalFilter = MutationCommandExecutor.RenderAdditionalFilter(transformResult.AdditionalFilter, dialect);

                // Adopt the (possibly rewritten) data so transformer output — e.g.
                // enum-name → DB-value mapping — reaches the SQL, rekeyed to real DB
                // column names. keyData is already DB-named (see MutationArgumentBinder),
                // so the non-key split and WHERE share one name space; enum columns are
                // non-key.
                updatedData = ToDbColumnKeys(table, transformResult.Data);
                var standardData = updatedData
                    .Where(d => !propertyInfo.keyData.ContainsKey(d.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

                var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
                var sql = MutationCommandExecutor.BuildUpdateSql(dialect, table, tableRef, standardData.Keys, propertyInfo.keyData.Keys, additionalFilter.WhereSuffix);
                var hookContext = HookContext(table, MutationType.Update, updatedData, ctx, conn, transaction, dialect);
                await MutationNotifier.RunBeforeCommitHooksAsync(ctx.Services, hookContext);
                result = await MutationCommandExecutor.ExecuteNonQuery(conn, transaction, sql, updatedData, additionalFilter.Parameters, ctx.CancellationToken);

                // A zero-row update under a concurrency-token guard is a lost update:
                // the token predicate matched no row, so the stored version moved since
                // the client read it. Raise a CONFLICT (rolls back this transaction)
                // rather than returning a silent no-op the way an out-of-scope
                // tenant/policy update does. Gated by the transformer's flag so those
                // legitimately-zero-row cases stay silent.
                if (transformResult.ConflictOnNoRows && result == 0)
                    throw new BifrostExecutionError(
                        $"Update of '{table.TableSchema}.{table.DbName}' was rejected: the concurrency token no longer matches — the row was modified or removed since it was read. Reload and retry.")
                    { ErrorCode = "CONFLICT" };

                // After the write and the conflict check: emit the event only if a row
                // actually changed (the hook skips zero-row updates via the count result),
                // so an out-of-scope tenant/policy no-op does not fabricate an event.
                await MutationNotifier.RunInTransactionHooksAsync(ctx.Services, hookContext, result);

                stateTransition = transformResult.StateTransition;
            }, ctx.CancellationToken);
            await MutationNotifier.NotifyMutationAsync(ctx.Services, table, MutationType.Update, updatedData, result, ctx.UserContext);
            await MutationNotifier.NotifyStateTransitionAsync(ctx.Services, stateTransition, ctx.UserContext);
            // The update mutation field is typed `Int`, so its scalar return cannot
            // carry a composite key. For a single-key table we keep returning the key
            // value (identifies the affected row, back-compat). For a composite key
            // `keyData.Values.First()` would silently surface only the FIRST key
            // component — misleading — so instead return the affected row count
            // (0 or 1), consistent with the delete mutation.
            return propertyInfo.keyData.Count == 1
                ? propertyInfo.keyData.Values.First()
                : result;
        }

        public static async Task<object?> DeleteAsync(
            IDbTable table, Dictionary<string, object?> data, MutationPipelineContext ctx)
        {
            if (!data.Any())
                return 0;

            var dialect = ctx.ConnFactory.Dialect;

            // Snapshot the columns the caller actually supplied (in DB-name space),
            // captured BEFORE the pipeline runs. These are the intended WHERE-predicate
            // columns (primary key + any extra predicate). Columns a transformer stamps
            // afterwards (audit updated_at/deleted_at, soft-delete deleted_at/deleted_by)
            // are NOT in this set and must never contaminate the delete predicate.
            var clientColumns = new HashSet<string>(
                data.Keys.Select(k => ToDbColumnName(table, k)),
                StringComparer.OrdinalIgnoreCase);

            var transformContext = new MutationTransformContext
            {
                Model = ctx.Model,
                UserContext = ctx.UserContext,
                Services = ctx.Services,
                ModuleArguments = ctx.ModuleArguments,
            };
            var transformResult = await ctx.Transformers.TransformAsync(table, MutationType.Delete, data, transformContext);

            if (transformResult.Errors.Length > 0)
                throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));

            // The transformer's AdditionalFilter (e.g. policy row-scope, soft-delete
            // IS NULL) is ANDed onto the WHERE clause so it narrows — never
            // replaces — the primary-key predicate.
            var additionalFilter = MutationCommandExecutor.RenderAdditionalFilter(transformResult.AdditionalFilter, dialect);

            // Rekey to DB column names once so the PK split (via ColumnLookup) and
            // the emitted WHERE/SET use one consistent name space even when a
            // GraphQL field name differs from its column.
            var dbData = ToDbColumnKeys(table, transformResult.Data);

            // A transformer may rewrite a delete into a soft-delete UPDATE (deleted_at
            // stamped); otherwise it stays a hard DELETE. Both scope their rows by the
            // same client-supplied predicate columns (see SelectPredicateColumns).
            return transformResult.MutationType == MutationType.Update
                ? await ExecuteSoftDeleteAsync(table, dialect, ctx, dbData, clientColumns, additionalFilter)
                : await ExecuteHardDeleteAsync(table, dialect, ctx, dbData, clientColumns, additionalFilter);
        }

        /// <summary>
        /// The columns that scope a delete's WHERE clause: the client-supplied
        /// predicate columns (primary key plus any extra predicate the client sent),
        /// NOT transformer-stamped columns (audit/soft-delete). Including a stamped
        /// column would AND a never-matching term into the WHERE and silently affect
        /// zero rows. Values come from the (possibly rewritten) transformed data so an
        /// enum-name → DB-value mapping on a predicate column still reaches the WHERE.
        /// </summary>
        private static Dictionary<string, object?> SelectPredicateColumns(
            Dictionary<string, object?> dbData, HashSet<string> clientColumns, IDbTable table)
            => dbData
                .Where(kv => clientColumns.Contains(kv.Key) || IsPrimaryKeyColumn(table, kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        // Soft-delete: the delete was transformed to UPDATE. The SET list carries ONLY
        // the columns a transformer stamped (soft-delete deleted_at/deleted_by, audit
        // updated_at/updated_by) — never a client-supplied column, or a delete predicate
        // like status:"archived" would be written into the row unconditionally. The WHERE
        // carries the client-supplied predicate columns, so a client predicate narrows
        // which rows are soft-deleted. Before-commit hooks and the write commit atomically.
        private static async Task<object?> ExecuteSoftDeleteAsync(
            IDbTable table, ISqlDialect dialect, MutationPipelineContext ctx,
            Dictionary<string, object?> dbData,
            HashSet<string> clientColumns,
            (string WhereSuffix, IReadOnlyList<SqlParameterInfo> Parameters) additionalFilter)
        {
            var keyData = SelectPredicateColumns(dbData, clientColumns, table);
            var setData = dbData
                .Where(kv => !keyData.ContainsKey(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            if (keyData.Count == 0)
                throw new BifrostExecutionError(
                    "A soft delete requires a primary key or at least one predicate column to scope the affected rows.");

            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var sql = MutationCommandExecutor.BuildUpdateSql(dialect, table, tableRef, setData.Keys, keyData.Keys, additionalFilter.WhereSuffix);
            var result = 0;
            await MutationCommandExecutor.RunInTransactionAsync(ctx.ConnFactory, async (conn, transaction) =>
            {
                var hookContext = HookContext(table, MutationType.Update, dbData, ctx, conn, transaction, dialect);
                await MutationNotifier.RunBeforeCommitHooksAsync(ctx.Services, hookContext);
                result = await MutationCommandExecutor.ExecuteNonQuery(conn, transaction, sql, dbData, additionalFilter.Parameters, ctx.CancellationToken);
                // Soft delete is modeled as an UPDATE; emit an update event (hook skips zero-row).
                await MutationNotifier.RunInTransactionHooksAsync(ctx.Services, hookContext, result);
            }, ctx.CancellationToken);
            await MutationNotifier.NotifyMutationAsync(ctx.Services, table, MutationType.Update, dbData, result, ctx.UserContext);
            return result;
        }

        // Standard hard DELETE. WHERE is built from the client-supplied predicate
        // columns only (see SelectPredicateColumns). Before-commit hooks and the write
        // commit atomically.
        private static async Task<object?> ExecuteHardDeleteAsync(
            IDbTable table, ISqlDialect dialect, MutationPipelineContext ctx,
            Dictionary<string, object?> dbData,
            HashSet<string> clientColumns,
            (string WhereSuffix, IReadOnlyList<SqlParameterInfo> Parameters) additionalFilter)
        {
            var deleteData = SelectPredicateColumns(dbData, clientColumns, table);

            if (deleteData.Count == 0)
                throw new BifrostExecutionError(
                    "A delete requires a primary key or at least one predicate column to scope the affected rows.");

            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var sql = MutationCommandExecutor.BuildDeleteSql(dialect, tableRef, deleteData.Keys, additionalFilter.WhereSuffix);
            var result = 0;
            await MutationCommandExecutor.RunInTransactionAsync(ctx.ConnFactory, async (conn, transaction) =>
            {
                var hookContext = HookContext(table, MutationType.Delete, deleteData, ctx, conn, transaction, dialect);
                await MutationNotifier.RunBeforeCommitHooksAsync(ctx.Services, hookContext);
                result = await MutationCommandExecutor.ExecuteNonQuery(conn, transaction, sql, deleteData, additionalFilter.Parameters, ctx.CancellationToken);
                await MutationNotifier.RunInTransactionHooksAsync(ctx.Services, hookContext, result);
            }, ctx.CancellationToken);
            await MutationNotifier.NotifyMutationAsync(ctx.Services, table, MutationType.Delete, deleteData, result, ctx.UserContext);
            return result;
        }
    }
}
