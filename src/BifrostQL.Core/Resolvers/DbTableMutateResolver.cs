using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Model;
using GraphQL;
using GraphQL.Resolvers;
using Microsoft.Extensions.DependencyInjection;
using static BifrostQL.Core.Resolvers.DbParameterBinder;

namespace BifrostQL.Core.Resolvers
{
    public sealed class DbTableMutateResolver : IBifrostResolver, IFieldResolver
    {
        private readonly IDbTable _table;

        public DbTableMutateResolver(IDbTable table)
        {
            _table = table;
        }

        public async ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
        {
            var bifrost = new BifrostContextAdapter(context);
            var conFactory = bifrost.ConnFactory;
            var model = bifrost.Model;
            var dialect = conFactory.Dialect;
            var table = _table;
            var mutationTransformers = context.RequestServices!.GetRequiredService<IMutationTransformers>();

            switch (MutationActionSelector.FromContext(context))
            {
                case MutationAction.Sync:
                    return await SyncObject(context, table, mutationTransformers, model, conFactory, dialect);
                case MutationAction.Insert:
                    return await InsertObject(context, table, mutationTransformers, model, conFactory, dialect);
                case MutationAction.Update:
                    return await UpdateObject(context, table, mutationTransformers, model, conFactory, dialect);
                case MutationAction.Delete:
                    return await DeleteObject(context, mutationTransformers, table, model, conFactory, dialect);
                case MutationAction.Upsert:
                    return await UpsertObject(context, table, mutationTransformers, model, conFactory, dialect);
                default:
                    return null;
            }
        }

        private async Task<object?> UpsertObject(IBifrostFieldContext context, IDbTable table,
            IMutationTransformers mutationTransformers, IDbModel model,
            IDbConnFactory conFactory, ISqlDialect dialect)
        {
            var propertyInfo = GetPropertyInfo(context, _table, "upsert");
            if (!propertyInfo.data.Any())
                return 0;

            // A true upsert is routed through the real Insert-or-Update decision
            // rather than a native single-statement UpsertSql. A single statement
            // (ON CONFLICT / MERGE) cannot express a transformer's AdditionalFilter
            // — tenant/policy row-scope, soft-delete IS NULL — as a guard on its
            // INSERT branch, so a caller could take over a row in another tenant or
            // resurrect a soft-deleted one. It also runs the pipeline as Update with
            // no CurrentRow, which skips created-* stamps, state-machine
            // current-state validation and insert-required checks. Probing existence
            // by primary key and dispatching to InsertObject / UpdateObject makes
            // every one of those enforcements apply exactly as for a plain
            // insert/update, and rebuilds the column list from post-transform data.
            //
            // The probe is a read outside the write transaction; the database's
            // primary-key / unique constraint is the real arbiter (a lost insert
            // race fails the INSERT, a lost update race affects 0 rows), so a
            // concurrent writer cannot cause silent data corruption here.
            if (propertyInfo.keyData.Any()
                && await RowExistsAsync(conFactory, dialect, table, propertyInfo.keyData, context.CancellationToken))
                return await UpdateObject(context, table, mutationTransformers, model, conFactory, dialect, "upsert");

            return await InsertObject(context, table, mutationTransformers, model, conFactory, dialect, "upsert");
        }

        ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
        {
            return ResolveAsync(new BifrostFieldContextAdapter(context));
        }

        // Reads the mutation argument dictionary and optional positional _primaryKey
        // off the GraphQL context, then delegates the (pure) key/standard split to
        // MutationArgumentBinder — which is directly unit-tested.
        private (Dictionary<string, object?> data, Dictionary<string, object?> keyData, Dictionary<string, object?> standardData) GetPropertyInfo(IBifrostFieldContext context, IDbTable table, string parameterName)
        {
            var baseData = context.GetArgument<Dictionary<string, object?>>(parameterName) ?? new();
            return MutationArgumentBinder.SplitProperties(table, baseData, ReadPrimaryKeyValues(context));
        }

        private static Dictionary<string, object?>? ResolvePrimaryKeyArgument(IBifrostFieldContext context, IDbTable table)
            => MutationArgumentBinder.ResolvePrimaryKey(table, ReadPrimaryKeyValues(context));

        private static IReadOnlyList<object?>? ReadPrimaryKeyValues(IBifrostFieldContext context)
            => context.HasArgument("_primaryKey")
                ? context.GetArgument<List<object?>>("_primaryKey")
                : null;

        // Probes whether a row keyed by the given primary-key values already exists,
        // so the upsert path can dispatch to the safe Insert or Update executor. The
        // probe uses its own connection; the database's PK/unique constraint remains
        // the authority under a concurrent writer (see the upsert call site).
        private static async Task<bool> RowExistsAsync(
            IDbConnFactory connFactory, ISqlDialect dialect, IDbTable table,
            Dictionary<string, object?> keyData, CancellationToken cancellationToken)
        {
            if (keyData.Count == 0)
                return false;

            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var whereClause = MutationCommandExecutor.BuildKeyPredicate(dialect, keyData.Keys);
            var sql = $"SELECT 1 FROM {tableRef} WHERE {whereClause};";

            await using var conn = connFactory.GetConnection();
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            AddParameters(cmd, keyData);
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result != null && result != DBNull.Value;
        }

        private async Task<object?> DeleteObject(IBifrostFieldContext context,
            IMutationTransformers mutationTransformers, IDbTable table, IDbModel model,
            IDbConnFactory conFactory, ISqlDialect dialect)
        {
            var data = context.GetArgument<Dictionary<string, object?>>("delete") ?? new();
            if (!data.Any())
                return 0;

            var pkKeyData = ResolvePrimaryKeyArgument(context, table);
            if (pkKeyData != null)
            {
                foreach (var kv in pkKeyData)
                    data[kv.Key] = kv.Value;
            }

            // Snapshot the columns the client actually supplied (in DB-name space),
            // captured BEFORE the pipeline runs. These are the intended WHERE-predicate
            // columns (primary key + any extra predicate). Columns a transformer stamps
            // afterwards (audit updated_at/deleted_at, soft-delete deleted_at/deleted_by)
            // are NOT in this set and must never contaminate the delete predicate.
            var clientColumns = new HashSet<string>(
                data.Keys.Select(k => DbParameterBinder.ToDbColumnName(table, k)),
                StringComparer.OrdinalIgnoreCase);

            var userContext = context.UserContext;
            var transformContext = new MutationTransformContext
            {
                Model = model,
                UserContext = userContext,
                Services = context.RequestServices,
                ModuleArguments = ModuleApiRegistry.CaptureMutationArguments(context, table),
            };
            var transformResult = await mutationTransformers.TransformAsync(table, MutationType.Delete, data, transformContext);

            if (transformResult.Errors.Length > 0)
                throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));

            // The transformer's AdditionalFilter (e.g. policy row-scope, soft-delete
            // IS NULL) is ANDed onto the WHERE clause so it narrows — never
            // replaces — the primary-key predicate.
            var additionalFilter = MutationCommandExecutor.RenderAdditionalFilter(transformResult.AdditionalFilter, dialect);

            // Rekey to DB column names once so the PK split (via ColumnLookup) and
            // the emitted WHERE/SET use one consistent name space even when a
            // GraphQL field name differs from its column.
            var dbData = DbParameterBinder.ToDbColumnKeys(table, transformResult.Data);

            // A transformer may rewrite a delete into a soft-delete UPDATE (deleted_at
            // stamped); otherwise it stays a hard DELETE. Both scope their rows by the
            // same client-supplied predicate columns (see SelectPredicateColumns).
            return transformResult.MutationType == MutationType.Update
                ? await ExecuteSoftDeleteAsync(context, table, dialect, conFactory, userContext, dbData, clientColumns, additionalFilter)
                : await ExecuteHardDeleteAsync(context, table, dialect, conFactory, userContext, dbData, clientColumns, additionalFilter);
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
                .Where(kv => clientColumns.Contains(kv.Key) || DbParameterBinder.IsPrimaryKeyColumn(table, kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        // Soft-delete: the delete was transformed to UPDATE. The SET list carries ONLY
        // the columns a transformer stamped (soft-delete deleted_at/deleted_by, audit
        // updated_at/updated_by) — never a client-supplied column, or a delete predicate
        // like status:"archived" would be written into the row unconditionally. The WHERE
        // carries the client-supplied predicate columns, so a client predicate narrows
        // which rows are soft-deleted. Before-commit hooks and the write commit atomically.
        private async Task<object?> ExecuteSoftDeleteAsync(
            IBifrostFieldContext context, IDbTable table, ISqlDialect dialect, IDbConnFactory conFactory,
            IDictionary<string, object?> userContext, Dictionary<string, object?> dbData,
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
            await MutationCommandExecutor.RunInTransactionAsync(conFactory, async (conn, transaction) =>
            {
                await MutationNotifier.RunBeforeCommitHooksAsync(context.RequestServices, table, MutationType.Update, dbData, userContext);
                result = await MutationCommandExecutor.ExecuteNonQuery(conn, transaction, sql, dbData, additionalFilter.Parameters, context.CancellationToken);
            }, context.CancellationToken);
            await MutationNotifier.NotifyMutationAsync(context.RequestServices, table, MutationType.Update, dbData, result, userContext);
            return result;
        }

        // Standard hard DELETE. WHERE is built from the client-supplied predicate
        // columns only (see SelectPredicateColumns). Before-commit hooks and the write
        // commit atomically.
        private async Task<object?> ExecuteHardDeleteAsync(
            IBifrostFieldContext context, IDbTable table, ISqlDialect dialect, IDbConnFactory conFactory,
            IDictionary<string, object?> userContext, Dictionary<string, object?> dbData,
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
            await MutationCommandExecutor.RunInTransactionAsync(conFactory, async (conn, transaction) =>
            {
                await MutationNotifier.RunBeforeCommitHooksAsync(context.RequestServices, table, MutationType.Delete, deleteData, userContext);
                result = await MutationCommandExecutor.ExecuteNonQuery(conn, transaction, sql, deleteData, additionalFilter.Parameters, context.CancellationToken);
            }, context.CancellationToken);
            await MutationNotifier.NotifyMutationAsync(context.RequestServices, table, MutationType.Delete, deleteData, result, userContext);
            return result;
        }

        private async Task<object?> UpdateObject(IBifrostFieldContext context, IDbTable table,
            IMutationTransformers mutationTransformers, IDbModel model,
            IDbConnFactory conFactory, ISqlDialect dialect, string parameterName = "update")
        {
            var propertyInfo = GetPropertyInfo(context, table, parameterName);
            if (!propertyInfo.data.Any())
                return 0;

            if (!propertyInfo.keyData.Any())
                return 0;

            if (!propertyInfo.standardData.Any())
                return 0;

            // The state-machine load, mutation transformers, before-commit hooks and
            // the update run inside one transaction so the read the transformer gates
            // on and the write it produces commit atomically or roll back together.
            Dictionary<string, object?> updatedData = null!;
            int result = 0;
            StateTransitionInfo? stateTransition = null;
            await MutationCommandExecutor.RunInTransactionAsync(conFactory, async (conn, transaction) =>
            {
                // Mutation transformers (e.g. the authorization policy engine) gate
                // the update before any SQL is built; non-empty Errors abort it.
                var currentRow = await MutationCommandExecutor.LoadCurrentStateMachineRow(
                    conn,
                    transaction,
                    dialect,
                    table,
                    propertyInfo.keyData,
                    context.CancellationToken);
                var transformContext = new MutationTransformContext
                {
                    Model = model,
                    UserContext = context.UserContext,
                    CurrentRow = currentRow,
                    Services = context.RequestServices,
                };
                var transformResult = await mutationTransformers.TransformAsync(table, MutationType.Update, propertyInfo.data, transformContext);
                if (transformResult.Errors.Length > 0)
                    throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));

                // The transformer's AdditionalFilter (e.g. policy row-scope, soft-delete
                // IS NULL) is ANDed onto the WHERE clause so it narrows — never
                // replaces — the primary-key predicate.
                var additionalFilter = MutationCommandExecutor.RenderAdditionalFilter(transformResult.AdditionalFilter, dialect);

                // Adopt the (possibly rewritten) data so transformer output — e.g.
                // enum-name → DB-value mapping — reaches the SQL, rekeyed to real DB
                // column names. keyData is already DB-named (see GetPropertyInfo), so
                // the non-key split and WHERE share one name space; enum columns are
                // non-key.
                updatedData = DbParameterBinder.ToDbColumnKeys(table, transformResult.Data);
                var standardData = updatedData
                    .Where(d => !propertyInfo.keyData.ContainsKey(d.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

                var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
                var sql = MutationCommandExecutor.BuildUpdateSql(dialect, table, tableRef, standardData.Keys, propertyInfo.keyData.Keys, additionalFilter.WhereSuffix);
                await MutationNotifier.RunBeforeCommitHooksAsync(context.RequestServices, table, MutationType.Update, updatedData, context.UserContext);
                result = await MutationCommandExecutor.ExecuteNonQuery(conn, transaction, sql, updatedData, additionalFilter.Parameters, context.CancellationToken);

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

                stateTransition = transformResult.StateTransition;
            }, context.CancellationToken);
            await MutationNotifier.NotifyMutationAsync(context.RequestServices, table, MutationType.Update, updatedData, result, context.UserContext);
            await MutationNotifier.NotifyStateTransitionAsync(context.RequestServices, stateTransition, context.UserContext);
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

        // Nested ("tree") sync: accepts a parent object with nested child
        // collections and reconciles it against current database state — inserting
        // new rows, updating changed rows, and deleting orphaned rows — in one
        // transaction. When the root carries a primary key, its existing subtree is
        // loaded and diffed; otherwise the whole tree is a fresh insert.
        //
        // Child links are auto-wired: a conventional FK receives the parent's PK,
        // and a polymorphic link additionally gets its discriminator stamped, so a
        // note synced under a company resolves back through that company's notes.
        // Orphan loading/deletion is scoped per parent (and per discriminator for
        // polymorphic links), so reconciling one parent never touches another's.
        //
        // Each inferred operation is routed through the mutation-transformer
        // pipeline (TreeSyncExecutor) before its SQL is built, so soft-delete,
        // authorization policy, and audit-populate apply to nested and orphan
        // operations exactly as they do to a single-row mutation. In particular an
        // orphaned child on a soft-delete table is rewritten Delete → UPDATE
        // (deleted_at stamped) instead of being physically removed.
        //
        // NOTE (remaining sub-item): _hardDelete is not yet capturable on the sync
        // field — opting a soft-delete orphan into a real DELETE would require
        // emitting + capturing the module arg on the sync mutation field. The
        // default (soft-delete orphans become UPDATE) is correct without it.
        private static async Task<object?> SyncObject(IBifrostFieldContext context, IDbTable table,
            IMutationTransformers mutationTransformers, IDbModel model, IDbConnFactory conFactory, ISqlDialect dialect)
        {
            var tree = context.GetArgument<Dictionary<string, object?>>("sync") ?? new();
            if (tree.Count == 0)
                return null;

            var loader = new TreeSyncStateLoader(dialect);
            var existing = await loader.LoadAsync(table, tree, conFactory);

            var engine = new TreeSyncEngine(model);
            var operations = engine.ComputeOperations(table, tree, existing);

            var executor = new TreeSyncExecutor(dialect);
            var rootId = await executor.ExecuteAsync(
                operations, conFactory, mutationTransformers, model, context.UserContext, context.RequestServices);
            // On a pure insert the executor returns the generated PK; on an update
            // the root already has one, so fall back to the submitted key value.
            rootId ??= RootKeyValue(table, tree);
            await MutationNotifier.NotifyMutationAsync(context.RequestServices, table, MutationType.Insert, tree, rootId, context.UserContext);
            return rootId;
        }

        private static object? RootKeyValue(IDbTable table, Dictionary<string, object?> tree)
        {
            var keys = table.KeyColumns.ToList();
            if (keys.Count != 1)
                return null;
            var data = new Dictionary<string, object?>(tree, StringComparer.OrdinalIgnoreCase);
            return data.TryGetValue(keys[0].ColumnName, out var v) ? v : null;
        }

        private async Task<object?> InsertObject(IBifrostFieldContext context, IDbTable table,
            IMutationTransformers mutationTransformers, IDbModel model,
            IDbConnFactory conFactory, ISqlDialect dialect, string parameterName = "insert")
        {
            var data = context.GetArgument<Dictionary<string, object?>>(parameterName) ?? new();

            // Mutation transformers (e.g. the authorization policy engine) gate
            // the insert before any SQL is built; non-empty Errors abort it.
            var transformContext = new MutationTransformContext { Model = model, UserContext = context.UserContext, Services = context.RequestServices };
            var transformResult = await mutationTransformers.TransformAsync(table, MutationType.Insert, data, transformContext);
            if (transformResult.Errors.Length > 0)
                throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));

            // Adopt the (possibly rewritten) data so transformer output — e.g.
            // enum-name → DB-value mapping — actually reaches the SQL, and rekey
            // GraphQL field names to real DB column names so sanitized/prefixed
            // columns land in the right column. Mirrors the delete path.
            data = DbParameterBinder.ToDbColumnKeys(table, transformResult.Data);

            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var insertInto = MutationCommandExecutor.BuildInsertInto(dialect, table, tableRef, data.Keys);
            var returning = dialect.ReturningIdentityClauseFor(table.KeyColumns.Select(k => k.ColumnName).ToList());
            var sql = returning != null
                ? $"{insertInto}{returning};"
                : $"{insertInto};SELECT {dialect.LastInsertedIdentity} ID;";

            // Before-commit hooks and the insert (with its identity SELECT) run in
            // one transaction so a hook veto or a failed write rolls back as a unit.
            object? result = null;
            await MutationCommandExecutor.RunInTransactionAsync(conFactory, async (conn, transaction) =>
            {
                await MutationNotifier.RunBeforeCommitHooksAsync(context.RequestServices, table, MutationType.Insert, data, context.UserContext);
                result = HandleDecimals(await MutationCommandExecutor.ExecuteScalar(conn, transaction, sql, data, context.CancellationToken));
            }, context.CancellationToken);
            await MutationNotifier.NotifyMutationAsync(context.RequestServices, table, MutationType.Insert, data, result, context.UserContext);
            return result;
        }


    }
}
